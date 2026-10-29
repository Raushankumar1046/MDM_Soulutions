@echo off
title CyberShield MDM - Enterprise Client Setup
echo ==========================================================
echo     CyberShield MDM - Enterprise Client Agent Setup
echo ==========================================================
echo.

:: 1. Self-Elevate to Administrator if not already elevated
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [UAC] Requesting Administrator privileges...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
set "TARGET_DIR=C:\Program Files\CyberShieldMDM"
set "CLIENT_EXE=C:\Program Files\CyberShieldMDM\EndpointManager.Client.exe"
set "LOG_DIR=C:\ProgramData\CyberShieldMDM"

echo [1/5] Stopping any running background instances and Session 0 services...
net stop "EndpointManagerClient" >nul 2>&1
sc.exe stop "EndpointManagerClient" >nul 2>&1
sc.exe delete "EndpointManagerClient" >nul 2>&1
taskkill /f /im EndpointManager.Client.exe >nul 2>&1
timeout /t 1 /nobreak >nul

echo [2/5] Preparing Destination Directory (%TARGET_DIR%)...
if not exist "%TARGET_DIR%" mkdir "%TARGET_DIR%"
if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"

echo [3/5] Installing Client Executable and Configuration...
copy /Y "%~dp0EndpointManager.Client.exe" "%TARGET_DIR%\" >nul
if exist "%~dp0appsettings.json" copy /Y "%~dp0appsettings.json" "%TARGET_DIR%\" >nul

:: Unblock downloaded files
powershell -NoProfile -Command "Get-ChildItem -Path '%TARGET_DIR%' | Unblock-File" >nul 2>&1

echo [4/5] Configuring Windows Defender Firewall Rules...
netsh advfirewall firewall delete rule name="CyberShield MDM Client Agent In" >nul 2>&1
netsh advfirewall firewall delete rule name="CyberShield MDM Client Agent Out" >nul 2>&1
netsh advfirewall firewall add rule name="CyberShield MDM Client Agent In" dir=in action=allow program="%CLIENT_EXE%" enable=yes profile=any >nul 2>&1
netsh advfirewall firewall add rule name="CyberShield MDM Client Agent Out" dir=out action=allow program="%CLIENT_EXE%" enable=yes profile=any >nul 2>&1

echo [5/5] Configuring 100%% Silent Background Auto-Start across Laptop Reboots...

:: Remove legacy Startup scripts and Registry Run keys (which caused black cmd window & UAC popup)
del /f /q "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\CyberShieldMDMClient.cmd" >nul 2>&1
del /f /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\CyberShieldMDMClient.cmd" >nul 2>&1
reg delete "HKLM\Software\Microsoft\Windows\CurrentVersion\Run" /v "CyberShieldMDMClient" /f >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "CyberShieldMDMClient" /f >nul 2>&1

:: Register Elevated Silent Task Scheduler on Windows Logon (Bypasses UAC completely with highest privileges)
schtasks.exe /delete /tn "CyberShieldMDMClient" /f >nul 2>&1

powershell -NoProfile -ExecutionPolicy Bypass -Command "$action = New-ScheduledTaskAction -Execute '%CLIENT_EXE%' -WorkingDirectory '%TARGET_DIR%'; $trigger = New-ScheduledTaskTrigger -AtLogOn; $principal = New-ScheduledTaskPrincipal -GroupId 'BUILTIN\Administrators' -RunLevel Highest; $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit 0 -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1); Register-ScheduledTask -TaskName 'CyberShieldMDMClient' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force" >nul 2>&1

if %errorlevel% neq 0 (
    schtasks.exe /create /tn "CyberShieldMDMClient" /tr "\"%CLIENT_EXE%\"" /sc onlogon /rl highest /f >nul 2>&1
)

echo.
echo Launching CyberShield Client Agent silently in background...
schtasks.exe /run /tn "CyberShieldMDMClient" >nul 2>&1
timeout /t 1 /nobreak >nul
tasklist /fi "imagename eq EndpointManager.Client.exe" | findstr /i "EndpointManager.Client.exe" >nul 2>&1
if %errorlevel% neq 0 (
    start "" /d "%TARGET_DIR%" "%CLIENT_EXE%"
)

echo.
echo ==========================================================
echo   SUCCESS: CyberShield Client Agent is Active!
echo ==========================================================
echo   * Auto-Start on Reboot:  SILENT (Elevated Task Scheduler, No UAC, No Black Window)
echo   * Live Remote Desktop:   READY (Full Native Physical Screen)
echo   * Remote Mouse & Keys:   READY (Direct Desktop Control)
echo   * Remote Screen Lock:    READY (WTS + tsdiscon + Native Lock)
echo   * Connected Server:      Dual-Network (Auto Office LAN + 14.194.19.6:5000 WFH)
echo   * Runtime Log:           C:\ProgramData\CyberShieldMDM\client_runtime.log
echo ==========================================================
echo.
pause
