@echo off
title CyberShield MDM - Service Uninstaller
echo ==========================================================
echo         CyberShield MDM - Service Uninstaller
echo ==========================================================
echo.

:: Elevate to Administrator if not already running as Admin
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting Administrator privileges...
    powershell -Command "Start-Process cmd -ArgumentList '/c \"\"%~dpnx0\"\"' -Verb RunAs"
    exit /b
)

echo Stopping service...
sc.exe stop "EndpointManagerClient" >nul 2>&1

echo Deleting service...
sc.exe delete "EndpointManagerClient" >nul 2>&1

echo Cleaning files...
rmdir /S /Q "C:\Program Files\CyberShieldMDM" >nul 2>&1

echo.
echo ==========================================================
echo   SUCCESS: CyberShield Client Agent uninstalled cleanly.
echo ==========================================================
echo.
pause
