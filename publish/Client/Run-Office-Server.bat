@echo off
title CyberShield MDM - Client Agent (14.194.19.6)
cd /d "%~dp0"
echo ==========================================================
echo        CyberShield MDM - Endpoint Client Agent
echo        Connecting to: http://14.194.19.6:5000
echo ==========================================================
echo.
echo Tip: For enforcing USB/Software blocking policies,
echo      ensure you right-click and 'Run as administrator'.
echo.
"%~dp0EndpointManager.Client.exe" --ServerUrl "http://14.194.19.6:5000/hubs/endpoints" --LocalServerUrl "auto"
pause
