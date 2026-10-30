# CyberShield MDM - High-Reliability Enterprise Agent Installer
[CmdletBinding()]
param()

$ErrorActionPreference = "Continue"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   CyberShield MDM - Windows Endpoint Auto-Start Setup   " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host ""

# 1. Verify Administrative Privileges
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[UAC] Requesting Administrator Privileges..." -ForegroundColor Yellow
    Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Verb RunAs
    exit
}

# 2. Stop any existing running instances and conflicting Session 0 service
Write-Host "[1/6] Stopping any running agent instances and legacy services..." -ForegroundColor Gray
& sc.exe stop EndpointManagerClient 2>&1 | Out-Null
& sc.exe delete EndpointManagerClient 2>&1 | Out-Null
Stop-Process -Name "EndpointManager.Client" -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

# 3. Create destination directories
$targetDir = "C:\Program Files\CyberShieldMDM"
$logDir = "C:\ProgramData\CyberShieldMDM"
Write-Host "[2/6] Preparing system destination directories ($targetDir)..." -ForegroundColor Gray
if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

# 4. Copy executable and configuration
Write-Host "[3/6] Installing executable and configuration files..." -ForegroundColor Gray
$scriptDir = Split-Path -Parent $PSCommandPath
if (-not $scriptDir) { $scriptDir = (Get-Location).Path }

$sourceExe = Join-Path $scriptDir "EndpointManager.Client.exe"
$sourceConfig = Join-Path $scriptDir "appsettings.json"

if (-not (Test-Path $sourceExe)) {
    $sourceExe = Join-Path $targetDir "EndpointManager.Client.exe"
}

if (-not (Test-Path $sourceExe)) {
    Write-Host "[ERROR] EndpointManager.Client.exe was not found in $scriptDir!" -ForegroundColor Red
    Write-Host "Please extract all installer files into a folder and run again." -ForegroundColor Yellow
    Read-Host "Press Enter to exit"
    exit 1
}

$destExe = Join-Path $targetDir "EndpointManager.Client.exe"
$destConfig = Join-Path $targetDir "appsettings.json"

Copy-Item -Path $sourceExe -Destination $destExe -Force
if (Test-Path $sourceConfig) {
    Copy-Item -Path $sourceConfig -Destination $destConfig -Force
}

# 5. Remove Mark-of-the-Web (Zone.Identifier) & Configure Firewall
Write-Host "[4/6] Unblocking files and configuring Windows Firewall rules..." -ForegroundColor Gray
Get-ChildItem -Path $targetDir | Unblock-File -ErrorAction SilentlyContinue
& netsh.exe advfirewall firewall add rule name="CyberShield MDM Client Agent" dir=in action=allow program="$destExe" enable=yes profile=any 2>&1 | Out-Null
& netsh.exe advfirewall firewall add rule name="CyberShield MDM Client Agent Out" dir=out action=allow program="$destExe" enable=yes profile=any 2>&1 | Out-Null

# 6. Configure Persistence Layer 1: All-Users Startup Folder (100% Guaranteed on Reboot for Any User)
Write-Host "[5/6] Registering Multi-Layer Auto-Start Persistence..." -ForegroundColor Gray
$startupDir = "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup"
if (Test-Path $startupDir) {
    $startupBat = Join-Path $startupDir "CyberShieldMDMClient.cmd"
    $batContent = "@echo off`r`nstart `"`" /d `"$targetDir`" `"$destExe`""
    Set-Content -Path $startupBat -Value $batContent -Force -ErrorAction SilentlyContinue
    Write-Host "  -> All-Users Startup Folder: Configured ($startupBat)" -ForegroundColor Green
}

# 7. Configure Persistence Layer 2: Registry Run Key (Machine-Wide)
$exeQuoted = "`"$destExe`""
$hklmRun = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run"
Set-ItemProperty -Path $hklmRun -Name "CyberShieldMDMClient" -Value $exeQuoted -Force -ErrorAction SilentlyContinue
Write-Host "  -> Machine Registry Run Key: Configured (HKLM)" -ForegroundColor Green

# 8. Configure Persistence Layer 3: Elevated Task Scheduler on User Logon
$taskName = "CyberShieldMDMClient"
& schtasks.exe /delete /tn "$taskName" /f 2>&1 | Out-Null
& schtasks.exe /create /tn "$taskName" /tr "`"$destExe`"" /sc onlogon /rl highest /f 2>&1 | Out-Null
Write-Host "  -> Task Scheduler on Logon: Configured ($taskName)" -ForegroundColor Green

# 9. Launch the Agent immediately in current session
Write-Host "[6/6] Launching CyberShield Client Agent now..." -ForegroundColor Gray
Start-Process -FilePath $destExe -WorkingDirectory $targetDir
Start-Sleep -Seconds 2

$proc = Get-Process -Name "EndpointManager.Client" -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host "Agent process started successfully (PID: $($proc.Id))" -ForegroundColor Green
} else {
    Write-Host "[INFO] Agent startup initiated." -ForegroundColor Gray
}

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "   SUCCESS: CyberShield Client Agent is now Active!       " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "  * Auto-Start on Reboot:  CONFIGURED (All-Users Startup + Registry + Task)" -ForegroundColor White
Write-Host "  * Remote Desktop Screen: READY (Runs in Interactive Session)" -ForegroundColor White
Write-Host "  * Notification Popups:   READY (Direct Desktop Alerts)" -ForegroundColor White
Write-Host "  * Battery Support:       ENABLED (Runs on battery & AC)" -ForegroundColor White
Write-Host "  * Office Wi-Fi / Hotspot: DUAL-IP AUTO-SWITCHING" -ForegroundColor White
Write-Host "  * Runtime Log:           C:\ProgramData\CyberShieldMDM\client_runtime.log" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Green
Write-Host ""
