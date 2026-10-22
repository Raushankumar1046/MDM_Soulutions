@echo off
title CyberShield MDM - Client Uninstaller
echo ==========================================================
echo        CyberShield MDM - Client Agent Uninstaller
echo ==========================================================
echo.

net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -Command "Start-Process cmd -ArgumentList '/c \"\"%~dpnx0\"\"' -Verb RunAs"
    exit /b
)

echo [1/3] Stopping running agent process...
taskkill /f /im EndpointManager.Client.exe >nul 2>&1

echo [2/3] Removing Scheduled Tasks and Registry entries...
schtasks /delete /tn "CyberShieldMDMClient" /f >nul 2>&1
schtasks /delete /tn "EndpointManagerClient" /f >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "EndpointManagerClient" /f >nul 2>&1
reg delete "HKLM\Software\Microsoft\Windows\CurrentVersion\Run" /v "EndpointManagerClient" /f >nul 2>&1

echo [3/3] Removing installed files...
rd /s /q "C:\Program Files\CyberShieldMDM" >nul 2>&1

echo.
echo [SUCCESS] CyberShield MDM Client Agent uninstalled successfully.
echo.
pause
