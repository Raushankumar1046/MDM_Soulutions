@echo off
:: =========================================================================
:: CyberShield MDM - Server 24/7 Windows Service Installer
:: =========================================================================

echo [INFO] Installing CyberShield MDM Central Server as a 24/7 Windows Service...

set "EXE_PATH=%~dp0EndpointManager.Server.exe"

if not exist "%EXE_PATH%" (
    echo [ERROR] %EXE_PATH% not found!
    pause
    exit /b 1
)

:: 1. Stop and remove any previous service
net stop EndpointManagerServer >nul 2>&1
sc.exe delete EndpointManagerServer >nul 2>&1

:: 2. Create the Windows Service (Auto-Start on Boot, Runs 24/7 as LocalSystem)
sc.exe create EndpointManagerServer binPath= "\"%EXE_PATH%\"" start= auto DisplayName= "CyberShield MDM Management Server"

:: 3. Configure recovery on failure (restart service automatically)
sc.exe failure EndpointManagerServer reset= 86400 actions= restart/5000/restart/5000/restart/5000

:: 4. Add Firewall Rule to allow Port 5000 TCP and 5002 UDP
netsh advfirewall firewall add rule name="CyberShield MDM Server Port 5000" dir=in action=allow protocol=TCP localport=5000 >nul 2>&1
netsh advfirewall firewall add rule name="CyberShield MDM Discovery Port 5002" dir=in action=allow protocol=UDP localport=5002 >nul 2>&1

:: 5. Start the Service
net start EndpointManagerServer

echo.
echo =========================================================================
echo [SUCCESS] CyberShield MDM Server is installed and running 24/7!
echo Web Dashboard is live at: http://localhost:5000
echo =========================================================================
pause
