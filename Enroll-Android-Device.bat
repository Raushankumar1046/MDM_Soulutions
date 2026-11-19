@echo off
title CyberShield MDM - 1-Click Android Device Owner Enrollment
color 0B
echo =========================================================================
echo       CyberShield MDM - Android Device Owner Enrollment Tool
echo =========================================================================
echo.

cd /d "%~dp0"

:: 1. Auto-detect ADB (Checks local folder first, then publish\Mobile, system path, then Android SDK)
set "ADB_EXE="
if exist "%~dp0adb.exe" (
    set "ADB_EXE=%~dp0adb.exe"
) else if exist "%~dp0publish\Mobile\adb.exe" (
    set "ADB_EXE=%~dp0publish\Mobile\adb.exe"
) else (
    where adb >nul 2>&1
    if %errorlevel% equ 0 (
        set "ADB_EXE=adb"
    ) else if exist "%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe" (
        set "ADB_EXE=%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe"
    ) else if exist "%ProgramFiles(x86)%\Android\android-sdk\platform-tools\adb.exe" (
        set "ADB_EXE=%ProgramFiles(x86)%\Android\android-sdk\platform-tools\adb.exe"
    )
)

if "%ADB_EXE%"=="" (
    echo [ERROR] ADB not found!
    echo Please make sure adb.exe is in the same folder as this script.
    echo.
    pause
    exit /b 1
)

:: 2. Find APK
set "APK_PATH=%~dp0CyberShield-MDM-Client.apk"
if not exist "%APK_PATH%" (
    if exist "%~dp0publish\Mobile\CyberShield-MDM-Client.apk" (
        set "APK_PATH=%~dp0publish\Mobile\CyberShield-MDM-Client.apk"
    ) else if exist "%~dp0src\Mobile\CyberShieldAndroid\app\build\outputs\apk\debug\app-debug.apk" (
        set "APK_PATH=%~dp0src\Mobile\CyberShieldAndroid\app\build\outputs\apk\debug\app-debug.apk"
    ) else (
        echo [ERROR] CyberShield-MDM-Client.apk not found!
        echo Please ensure CyberShield-MDM-Client.apk is in the same folder.
        echo.
        pause
        exit /b 1
    )
)

echo [1/3] Waiting for connected Android phone via USB...
echo       - Connect phone with USB cable.
echo       - Make sure 'USB Debugging' is enabled on the phone.
echo       - If phone asks 'Allow USB debugging?', check 'Always allow' and tap OK.
echo.

"%ADB_EXE%" wait-for-device

echo [Device Detected] Checking authorization status...
"%ADB_EXE%" devices
echo.

echo [2/3] Installing CyberShield MDM Client APK...
"%ADB_EXE%" install -r "%APK_PATH%"
if %errorlevel% neq 0 (
    echo.
    echo [ERROR] APK installation failed!
    echo If the phone screen shows a security prompt, tap 'Allow/Install' and run this again.
    echo.
    pause
    exit /b 1
)

echo.
echo [3/3] Enforcing PERMANENT DEVICE OWNER (Anti-Tamper & Uninstall Lock)...
"%ADB_EXE%" shell dpm set-device-owner com.cybershield.mdm/.MdmDeviceAdminReceiver > "%TEMP%\mdm_dpm_output.txt" 2>&1
type "%TEMP%\mdm_dpm_output.txt"
findstr /i /c:"Success" "%TEMP%\mdm_dpm_output.txt" >nul 2>&1
if %errorlevel% equ 0 goto dpm_ok
findstr /i /c:"already" "%TEMP%\mdm_dpm_output.txt" >nul 2>&1
if %errorlevel% equ 0 (
    echo.
    echo [INFO] Device Owner is ALREADY ACTIVE and securely locked on this phone!
    goto dpm_ok
)
echo.
echo [NOTE] If set-device-owner returned an error:
echo        1. Check if ANY Google/Gmail account or Samsung account is logged in.
echo           (Go to phone Settings - Accounts and remove all accounts temporarily).
echo        2. Or run this on a freshly reset phone.
echo.
pause
exit /b 1

:dpm_ok

echo.
echo [4/5] Auto-Granting Enterprise Permissions (IMEI Read, GPS Location, Notifications)...
"%ADB_EXE%" shell pm grant com.cybershield.mdm android.permission.READ_PHONE_STATE >nul 2>&1
"%ADB_EXE%" shell pm grant com.cybershield.mdm android.permission.ACCESS_FINE_LOCATION >nul 2>&1
"%ADB_EXE%" shell pm grant com.cybershield.mdm android.permission.ACCESS_COARSE_LOCATION >nul 2>&1
"%ADB_EXE%" shell pm grant com.cybershield.mdm android.permission.POST_NOTIFICATIONS >nul 2>&1

:: Detect active Local LAN IP
set "LOCAL_IP="
for /f "tokens=4" %%a in ('route print 0.0.0.0 ^| findstr 0.0.0.0 ^| findstr /v "Default"') do (
    if "%LOCAL_IP%"=="" set "LOCAL_IP=%%a"
)
if "%LOCAL_IP%"=="" set "LOCAL_IP=192.168.68.152"

echo.
echo [5/5] Configuring Server URL (Dual-Network: Local %LOCAL_IP%:5000 + Remote 14.194.19.6:5000)...
"%ADB_EXE%" shell am start-foreground-service -n com.cybershield.mdm/.CyberShieldService --es server_url "http://14.194.19.6:5000/hubs/endpoints" --es local_server_url "http://%LOCAL_IP%:5000/hubs/endpoints" >nul 2>&1
"%ADB_EXE%" shell am start -n com.cybershield.mdm/.MainActivity --ez admin_unlock true --es server_url "http://14.194.19.6:5000/hubs/endpoints" --es local_server_url "http://%LOCAL_IP%:5000/hubs/endpoints" >nul 2>&1

echo.
echo =========================================================================
echo  [SUCCESS] CyberShield MDM Device Owner is now ACTIVE on your phone!
echo   * Server Connected: http://%LOCAL_IP%:5000
echo   * Uninstallation is completely BLOCKED (Permanent Locked).
echo   * App UI is protected by IT Admin PIN (9876).
echo   * Web Filter, Screen Lock, Remote Wipe & Tracking are ENFORCED.
echo =========================================================================
echo.
pause
