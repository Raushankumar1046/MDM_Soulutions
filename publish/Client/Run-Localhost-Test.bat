@echo off
title CyberShield MDM - Client Agent (Localhost Test)
cd /d "%~dp0"
echo ==========================================================
echo        CyberShield MDM - Localhost Testing Mode
echo        Connecting to: http://localhost:5000
echo ==========================================================
echo.
echo Tip: For enforcing USB/Software blocking policies,
echo      ensure you right-click and 'Run as administrator'.
echo.
"%~dp0EndpointManager.Client.exe" --ServerUrl "http://localhost:5000/hubs/endpoints" --LocalServerUrl "http://localhost:5000/hubs/endpoints"
pause
