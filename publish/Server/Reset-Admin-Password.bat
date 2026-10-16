@echo off
title CyberShield MDM - Reset Admin Password
echo ==========================================================
echo       CyberShield MDM - Reset Admin Password Utility
echo ==========================================================
echo.
echo Resetting administrator password to default: admin123...
"%~dp0EndpointManager.Server.exe" --reset-admin
echo.
echo You can now log into the web dashboard with:
echo Username: admin
echo Password: admin123
echo.
pause
