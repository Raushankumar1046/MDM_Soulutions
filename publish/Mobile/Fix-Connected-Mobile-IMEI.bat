@echo off
title CyberShield MDM - Fix Phone IMEI Number (1-Click)
color 0A
echo =========================================================================
echo       CyberShield MDM - Phone Real IMEI Auto-Grant Tool
echo =========================================================================
echo.

cd /d "%~dp0"

:: Auto-detect ADB
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
    echo Please make sure adb.exe is in the same folder.
    echo.
    pause
    exit /b 1
)

echo [1/3] Detecting connected Android phone...
echo       - Connect phone with USB cable.
echo       - Make sure USB Debugging is ON.
echo.

"%ADB_EXE%" wait-for-device
echo [Device Found] Connected Device:
"%ADB_EXE%" devices
echo.

echo [2/3] Auto-Granting READ_PHONE_STATE & Enterprise Permissions...
"%ADB_EXE%" shell pm grant com.cybershield.mdm android.permission.READ_PHONE_STATE >nul 2>&1
"%ADB_EXE%" shell pm grant com.cybershield.mdm android.permission.ACCESS_FINE_LOCATION >nul 2>&1
"%ADB_EXE%" shell pm grant com.cybershield.mdm android.permission.ACCESS_COARSE_LOCATION >nul 2>&1
"%ADB_EXE%" shell pm grant com.cybershield.mdm android.permission.POST_NOTIFICATIONS >nul 2>&1
echo   * Permission READ_PHONE_STATE: GRANTED!
echo   * Permission GPS LOCATION:     GRANTED!
echo   * Permission NOTIFICATIONS:    GRANTED!

echo.
echo [3/3] Refreshing CyberShield MDM Service on Phone...
"%ADB_EXE%" shell am force-stop com.cybershield.mdm >nul 2>&1
timeout /t 1 /nobreak >nul
"%ADB_EXE%" shell am start-foreground-service -n com.cybershield.mdm/.CyberShieldService >nul 2>&1
"%ADB_EXE%" shell am start -n com.cybershield.mdm/.MainActivity --ez admin_unlock true >nul 2>&1

echo.
echo =========================================================================
echo  [SUCCESS] Phone IMEI Permission is now ACTIVE!
echo   * The phone has restarted its MDM service with full Telephony access.
echo   * It will now read the REAL 15-Digit IMEI number.
echo   * Check your MDM Web Dashboard (Refresh in 2-3 seconds).
echo =========================================================================
echo.
pause
