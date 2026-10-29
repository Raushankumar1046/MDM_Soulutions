@echo off
title CyberShield MDM - Central Management Server
echo ==========================================================
echo         CyberShield MDM - Central Management Server
echo ==========================================================
echo.
echo Checking and clearing any previous running server instances...
taskkill /f /im EndpointManager.Server.exe >nul 2>&1
timeout /t 1 /nobreak >nul

:: Ensure .NET 8 Runtime is found (Auto-detect .NET 8 runtime paths)
if not exist "%ProgramFiles%\dotnet\shared\Microsoft.AspNetCore.App\8.*" (
    if exist "%USERPROFILE%\.dotnet\shared\Microsoft.AspNetCore.App\8.*" (
        set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
        set "PATH=%USERPROFILE%\.dotnet;%PATH%"
    ) else if exist "%ProgramFiles%\Microsoft Visual Studio\2022\Community\dotnet\net8.0\runtime" (
        set "DOTNET_ROOT=%ProgramFiles%\Microsoft Visual Studio\2022\Community\dotnet\net8.0\runtime"
        set "PATH=%ProgramFiles%\Microsoft Visual Studio\2022\Community\dotnet\net8.0\runtime;%PATH%"
    )
)

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
