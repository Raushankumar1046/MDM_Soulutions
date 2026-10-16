@echo off
echo [INFO] Stopping and Removing CyberShield MDM Server Service...
net stop EndpointManagerServer >nul 2>&1
sc.exe delete EndpointManagerServer >nul 2>&1
echo [SUCCESS] Server service removed.
pause
