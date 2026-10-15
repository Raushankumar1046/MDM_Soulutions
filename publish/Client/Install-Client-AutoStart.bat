@echo off
:: =========================================================================
:: CyberShield MDM - Client Auto-Start & Background Logon Installer
:: =========================================================================
:: This script installs the client to automatically launch on Windows Logon
:: with full User Desktop session privileges (required for live screen capture,
:: remote mouse/keyboard control, and high-accuracy native geolocation).
:: =========================================================================

echo [INFO] Registering EndpointManagerClient in Windows Task Scheduler (Auto-Start on Logon)...

set "EXE_PATH=%~dp0EndpointManager.Client.exe"

if not exist "%EXE_PATH%" (
    echo [ERROR] %EXE_PATH% not found!
    pause
    exit /b 1
)

:: 1. Remove any old scheduled task if present
schtasks /delete /tn "EndpointManagerClient" /f >nul 2>&1

:: 2. Create high-priority task running on user logon
schtasks /create /tn "EndpointManagerClient" /tr "\"%EXE_PATH%\"" /sc onlogon /rl highest /f

:: 3. Also add to Registry Run as fallback
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "EndpointManagerClient" /t REG_SZ /d "\"%EXE_PATH%\"" /f >nul 2>&1

echo.
echo =========================================================================
echo [SUCCESS] Auto-Start configured!
echo The client will now start automatically in the background whenever Windows boots or logs in.
echo You will NEVER have to run any .bat file manually again.
echo =========================================================================
echo.

:: Start it immediately now
echo [INFO] Starting EndpointManagerClient now...
start "" "%EXE_PATH%"
echo [DONE] Client is running!
timeout /t 3 >nul
