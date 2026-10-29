@echo off
title CyberShield MDM - Client Agent Uninstaller
echo ==========================================================
echo        CyberShield MDM - Complete Agent Uninstaller
echo ==========================================================
echo.

:: 1. Elevate to Administrator
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [UAC] Requesting Administrator privileges...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"

echo [1/4] Stopping background Windows Service and running processes...
net stop "EndpointManagerClient" >nul 2>&1
sc.exe stop "EndpointManagerClient" >nul 2>&1
taskkill /f /im EndpointManager.Client.exe >nul 2>&1
timeout /t 1 /nobreak >nul

echo [2/4] Deleting Windows Service...
sc.exe delete "EndpointManagerClient" >nul 2>&1

echo [3/4] Removing Scheduled Tasks, Registry entries, and Startup folder...
schtasks /delete /tn "CyberShieldMDMClient" /f >nul 2>&1
schtasks /delete /tn "EndpointManagerClient" /f >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "EndpointManagerClient" /f >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "CyberShieldMDMClient" /f >nul 2>&1
reg delete "HKLM\Software\Microsoft\Windows\CurrentVersion\Run" /v "EndpointManagerClient" /f >nul 2>&1
reg delete "HKLM\Software\Microsoft\Windows\CurrentVersion\Run" /v "CyberShieldMDMClient" /f >nul 2>&1
del /f /q "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\CyberShieldMDMClient.cmd" >nul 2>&1

echo [4/4] Removing Firewall Rules & program files...
netsh advfirewall firewall delete rule name="CyberShield MDM Client Agent In" >nul 2>&1
netsh advfirewall firewall delete rule name="CyberShield MDM Client Agent Out" >nul 2>&1
rd /s /q "C:\Program Files\CyberShieldMDM" >nul 2>&1

echo.
echo ==========================================================
echo   SUCCESS: CyberShield MDM Client Agent uninstalled!
echo ==========================================================
echo.
pause
