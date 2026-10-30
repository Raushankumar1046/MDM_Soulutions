@echo off
title CyberShield MDM - Client Service Installer
echo ==========================================================
echo     CyberShield MDM - Enterprise Windows Service Setup
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

echo [1/5] Stopping any running agent or legacy service...
net stop "EndpointManagerClient" >nul 2>&1
taskkill /f /im EndpointManager.Client.exe >nul 2>&1
timeout /t 1 /nobreak >nul

echo [2/5] Preparing Destination Directory (%TARGET_DIR%)...
if not exist "%TARGET_DIR%" mkdir "%TARGET_DIR%"
if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"

echo [3/5] Installing Client Executable and Configuration...
copy /Y "%~dp0EndpointManager.Client.exe" "%TARGET_DIR%\"
if exist "%~dp0appsettings.json" copy /Y "%~dp0appsettings.json" "%TARGET_DIR%\"

:: Unblock downloaded files
powershell -NoProfile -Command "Get-ChildItem -Path '%TARGET_DIR%' | Unblock-File" >nul 2>&1

echo [4/5] Configuring Windows Defender Firewall Rules...
netsh advfirewall firewall delete rule name="CyberShield MDM Client Agent In" >nul 2>&1
netsh advfirewall firewall delete rule name="CyberShield MDM Client Agent Out" >nul 2>&1
netsh advfirewall firewall add rule name="CyberShield MDM Client Agent In" dir=in action=allow program="%CLIENT_EXE%" enable=yes profile=any >nul 2>&1
netsh advfirewall firewall add rule name="CyberShield MDM Client Agent Out" dir=out action=allow program="%CLIENT_EXE%" enable=yes profile=any >nul 2>&1

echo [5/5] Registering 24/7 Windows Service (EndpointManagerClient)...
sc.exe stop "EndpointManagerClient" >nul 2>&1
sc.exe delete "EndpointManagerClient" >nul 2>&1
timeout /t 1 /nobreak >nul

:: Note: binPath must have escaped quotes so spaces in 'Program Files' are preserved by SCM
sc.exe create "EndpointManagerClient" binPath= "\"%CLIENT_EXE%\"" start= auto DisplayName= "CyberShield Endpoint Agent"
sc.exe description "EndpointManagerClient" "Centralized Corporate Endpoint Management & Security Policy Agent."
sc.exe failure "EndpointManagerClient" reset= 86400 actions= restart/5000/restart/5000/restart/5000

:: Configure All-Users Startup Folder for Interactive User Desktop
if exist "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup" (
    (
        echo @echo off
        echo start "" /d "%TARGET_DIR%" "%CLIENT_EXE%"
    ) > "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\CyberShieldMDMClient.cmd"
)

:: Registry Run key as secondary guarantee
reg add "HKLM\Software\Microsoft\Windows\CurrentVersion\Run" /v "CyberShieldMDMClient" /t REG_SZ /d "\"%CLIENT_EXE%\"" /f >nul 2>&1

echo.
echo Starting EndpointManagerClient Windows Service...
net start "EndpointManagerClient"

echo.
echo Checking Service Status:
sc.exe query "EndpointManagerClient" | findstr /i "STATE"

echo.
echo ==========================================================
echo   SUCCESS: CyberShield Client Agent is now Installed!
echo ==========================================================
echo   * Task Manager -> Services: EndpointManagerClient (Running)
echo   * Auto-Start on Reboot:     YES (24/7 Windows Service)
echo   * Target Server:            192.168.68.13:5000 (Office LAN) / 14.194.19.6:5000 (Remote)
echo   * Runtime Log:              C:\ProgramData\CyberShieldMDM\client_runtime.log
echo ==========================================================
echo.
pause
