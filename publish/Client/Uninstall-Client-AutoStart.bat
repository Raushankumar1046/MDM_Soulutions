@echo off
echo [INFO] Removing EndpointManagerClient from Auto-Start...
schtasks /delete /tn "EndpointManagerClient" /f >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "EndpointManagerClient" /f >nul 2>&1
taskkill /f /im EndpointManager.Client.exe >nul 2>&1
echo [SUCCESS] Auto-Start removed and client stopped.
pause
