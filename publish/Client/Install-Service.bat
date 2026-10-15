@echo off
title CyberShield MDM - Silent Service Installer
echo ==========================================================
echo         CyberShield MDM - Windows Service Installer
echo ==========================================================
echo.

:: Elevate to Administrator if not already running as Admin
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting Administrator privileges...
    powershell -Command "Start-Process cmd -ArgumentList '/c \"\"%~dpnx0\"\"' -Verb RunAs"
    exit /b
)

echo [1/3] Preparing Destination Directory (C:\Program Files\CyberShieldMDM)...
if not exist "C:\Program Files\CyberShieldMDM" mkdir "C:\Program Files\CyberShieldMDM"

echo [2/3] Copying Client Executable and Configuration...
copy /Y "%~dp0EndpointManager.Client.exe" "C:\Program Files\CyberShieldMDM\" >nul
copy /Y "%~dp0appsettings.json" "C:\Program Files\CyberShieldMDM\" >nul

echo [3/3] Registering Windows Background Service (Auto-Start)...
sc.exe stop "EndpointManagerClient" >nul 2>&1
sc.exe delete "EndpointManagerClient" >nul 2>&1

sc.exe create "EndpointManagerClient" binPath= "C:\Program Files\CyberShieldMDM\EndpointManager.Client.exe" start= auto DisplayName= "CyberShield Endpoint Agent" >nul
sc.exe description "EndpointManagerClient" "Centralized Corporate Endpoint Management & Security Policy Agent." >nul
sc.exe start "EndpointManagerClient" >nul

echo.
echo ==========================================================
echo   SUCCESS: CyberShield Client Agent installed successfully!
echo   It is now running silently as an auto-starting Windows Service.
echo   You can close this window.
echo ==========================================================
echo.
pause
