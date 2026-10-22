@echo off
title CyberShield MDM - Client Enterprise Silent Installer
echo ==========================================================
echo     CyberShield MDM - Enterprise Client Agent Installer
echo ==========================================================
echo.

:: 1. Elevate to Administrator if not already running as Admin
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [UAC] Requesting Administrator privileges...
    powershell -NoProfile -Command "Start-Process cmd -ArgumentList '/c \"\"%~dpnx0\"\"' -Verb RunAs"
    exit /b
)

:: Ensure current working directory is the script directory
cd /d "%~dp0"

echo [1/5] Stopping any previous running client instances...
taskkill /f /im EndpointManager.Client.exe >nul 2>&1
timeout /t 1 /nobreak >nul

echo [2/5] Preparing secure system destination (C:\Program Files\CyberShieldMDM)...
if not exist "C:\Program Files\CyberShieldMDM" mkdir "C:\Program Files\CyberShieldMDM"

echo [3/5] Copying Client executable and configuration...
if not exist "%~dp0EndpointManager.Client.exe" (
    echo [ERROR] EndpointManager.Client.exe was not found in %~dp0!
    echo Please make sure all files were extracted properly.
    pause
    exit /b 1
)

copy /Y "%~dp0EndpointManager.Client.exe" "C:\Program Files\CyberShieldMDM\" >nul
if exist "%~dp0appsettings.json" (
    copy /Y "%~dp0appsettings.json" "C:\Program Files\CyberShieldMDM\" >nul
)

echo [4/5] Removing Mark of the Web (Zone.Identifier) to prevent Security Warnings...
powershell -NoProfile -Command "Get-ChildItem 'C:\Program Files\CyberShieldMDM' | Unblock-File" >nul 2>&1

:: Remove old non-elevated registry run entries that caused the 'Open File' popup
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "EndpointManagerClient" /f >nul 2>&1
reg delete "HKLM\Software\Microsoft\Windows\CurrentVersion\Run" /v "EndpointManagerClient" /f >nul 2>&1
schtasks /delete /tn "EndpointManagerClient" /f >nul 2>&1
schtasks /delete /tn "CyberShieldMDMClient" /f >nul 2>&1

echo [5/5] Registering High-Priority Elevated Task in Windows Task Scheduler...
:: Runs automatically on any user logon with highest administrative privileges (Session 1 desktop)
schtasks /create /tn "CyberShieldMDMClient" /tr "\"C:\Program Files\CyberShieldMDM\EndpointManager.Client.exe\"" /sc onlogon /rl highest /f >nul

echo.
echo ==========================================================
echo   SUCCESS: CyberShield Client Agent Installed Successfully!
echo.
echo   * Background Mode: 100%% Silent (No CMD Window)
echo   * Security: Runs with Highest Admin Privileges
echo   * Tamper-Proof: Protected inside C:\Program Files\
echo   * No Popups: Windows SmartScreen/Security Warning removed
echo   * Auto-Starts automatically on Windows boot/logon
echo ==========================================================
echo.

:: Start the agent immediately in background with working directory set
echo Starting background agent now...
start "" /d "C:\Program Files\CyberShieldMDM" "C:\Program Files\CyberShieldMDM\EndpointManager.Client.exe"

echo [OK] Client is running in background.
echo Check the Server Web Dashboard to confirm this laptop is Online.
echo.
pause
