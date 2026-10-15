@echo off
title CyberShield MDM - Client Agent
echo ==========================================================
echo         CyberShield MDM - Endpoint Client Agent
echo ==========================================================
echo.
echo Searching for Central Server via UDP Auto-Discovery...
echo.

:: Check Admin Rights
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [WARNING] Not running as Administrator! 
    echo Requesting elevated Administrator privileges...
    powershell -Command "Start-Process cmd -ArgumentList '/k \"\"%~dpnx0\"\"' -Verb RunAs"
    exit /b
)

"%~dp0EndpointManager.Client.exe"
pause
