@echo off
title CyberShield MDM - Central Management Server
echo ==========================================================
echo         CyberShield MDM - Central Management Server
echo ==========================================================
echo.
echo Checking and clearing any previous running server instances...
taskkill /f /im EndpointManager.Server.exe >nul 2>&1
timeout /t 1 /nobreak >nul

echo Starting Central Server & UDP Auto-Discovery Service...
echo.

:: Open Windows Firewall rules for HTTP (TCP 5000) and Auto-Discovery (UDP 5002)
netsh advfirewall firewall show rule name="CyberShield MDM Port 5000" >nul 2>&1
if %errorlevel% neq 0 (
    echo [Firewall] Allowing inbound TCP 5000 & UDP 5002...
    netsh advfirewall firewall add rule name="CyberShield MDM Port 5000" dir=in action=allow protocol=TCP localport=5000 >nul 2>&1
    netsh advfirewall firewall add rule name="CyberShield MDM Port 5002" dir=in action=allow protocol=UDP localport=5002 >nul 2>&1
)

:: Open web dashboard in browser after 2 seconds in background
start "" /b cmd /c "timeout /t 2 /nobreak >nul & start http://localhost:5000"

:: Start Server process
"%~dp0EndpointManager.Server.exe" --urls "http://0.0.0.0:5000"
pause
