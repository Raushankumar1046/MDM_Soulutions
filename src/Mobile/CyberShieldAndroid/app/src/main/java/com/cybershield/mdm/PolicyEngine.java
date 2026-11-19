package com.cybershield.mdm;

import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.admin.DevicePolicyManager;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import androidx.core.app.NotificationCompat;
import android.media.AudioAttributes;
import android.media.AudioManager;
import android.media.MediaPlayer;
import android.media.RingtoneManager;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.UserManager;
import android.os.VibrationEffect;
import android.os.Vibrator;
import android.util.Base64;
import android.util.Log;
import java.security.SecureRandom;
import java.util.ArrayList;
import java.util.List;

/**
 * Enterprise Policy Engine for executing hardware, security, installation,
 * anti-tamper, and lock policies on Android endpoints.
 */
public class PolicyEngine {
    private static final String TAG = "CyberShield_Policy";

    private final Context context;
    private final DevicePolicyManager dpm;
    private final ComponentName adminComponent;

    public PolicyEngine(Context context) {
        this.context = context.getApplicationContext();
        this.dpm = (DevicePolicyManager) context.getSystemService(Context.DEVICE_POLICY_SERVICE);
        this.adminComponent = MdmDeviceAdminReceiver.getComponentName(context);

        // Apply baseline anti-tamper on initialization
        applyDefaultProtectionPolicies();
    }

    public boolean isAdminActive() {
        return dpm != null && dpm.isAdminActive(adminComponent);
    }

    public boolean isDeviceOwner() {
        return dpm != null && dpm.isDeviceOwnerApp(context.getPackageName());
    }

    /**
     * Applies baseline corporate protections:
     * - Prevents user from Factory Resetting / Formatting mobile from Settings
     * - Prevents uninstalling the CyberShield MDM agent
     * - Pre-initializes screen lock token for zero-touch remote reset (Samsung Care bypass)
     * - Restricts personal Samsung Account sign-in
     */
    public void applyDefaultProtectionPolicies() {
        if (!isAdminActive()) return;

        // 1. Block Factory Reset (Format protection)
        try { setFactoryResetBlocked(true); } catch (Exception e) { Log.w(TAG, "Factory reset block: " + e.getMessage()); }

        // 2. Block uninstall of MDM agent
        try { setUninstallProtection(true); } catch (Exception e) { Log.w(TAG, "Uninstall protection: " + e.getMessage()); }

        // 3. Pre-initialize password reset token for remote screen lock clearing
        try { initPasswordResetToken(); } catch (Exception e) { Log.w(TAG, "Password token init: " + e.getMessage()); }

        // 4. Block personal Samsung account to prevent cloud/FRP locks
        try { setSamsungAccountBlocked(true); } catch (Exception e) { Log.w(TAG, "Samsung account block: " + e.getMessage()); }

        // 5. Force GPS Location Always ON (Option 6)
        try { setLocationAlwaysOn(true); } catch (Exception e) { Log.w(TAG, "Force GPS location: " + e.getMessage()); }

        Log.i(TAG, "Baseline enterprise anti-tamper, GPS force-on & lock protection policies evaluated.");
    }

    /**
     * Prevents user from hard resetting or formatting mobile from Android Settings.
     */
    public boolean setFactoryResetBlocked(boolean blocked) {
        if (!isAdminActive()) return false;
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                if (blocked) {
                    dpm.addUserRestriction(adminComponent, UserManager.DISALLOW_FACTORY_RESET);
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                        dpm.addUserRestriction(adminComponent, UserManager.DISALLOW_SAFE_BOOT);
                    }
                    Log.i(TAG, "Factory Reset (Format Mobile) is now BLOCKED.");
                } else {
                    dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_FACTORY_RESET);
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                        dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_SAFE_BOOT);
                    }
                    Log.i(TAG, "Factory Reset (Format Mobile) has been unblocked by server.");
                }
                return true;
            }
        } catch (Exception e) {
            Log.e(TAG, "Error setting factory reset restriction: " + e.getMessage());
        }
        return false;
    }

    /**
     * Blocks uninstalling the CyberShield MDM app and other apps.
     */
    public boolean setUninstallProtection(boolean blocked) {
        if (!isAdminActive()) return false;
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                // Block uninstalling this specific package
                dpm.setUninstallBlocked(adminComponent, context.getPackageName(), blocked);

                if (blocked) {
                    dpm.addUserRestriction(adminComponent, UserManager.DISALLOW_UNINSTALL_APPS);
                } else {
                    dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_UNINSTALL_APPS);
                }
                Log.i(TAG, "App Uninstall Protection set to: " + blocked);
                return true;
            }
        } catch (Exception e) {
            Log.e(TAG, "Error configuring uninstall protection: " + e.getMessage());
        }
        return false;
    }

    /**
     * Blocks app and APK installation across the mobile.
     */
    public boolean setSoftwareInstallBlocked(boolean blocked) {
        if (!isAdminActive()) return false;
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                if (blocked) {
                    dpm.addUserRestriction(adminComponent, UserManager.DISALLOW_INSTALL_UNKNOWN_SOURCES);
                    dpm.addUserRestriction(adminComponent, UserManager.DISALLOW_INSTALL_APPS);
                    Log.i(TAG, "App / Software installation has been BLOCKED.");
                } else {
                    dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_INSTALL_UNKNOWN_SOURCES);
                    dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_INSTALL_APPS);
                    Log.i(TAG, "App / Software installation has been ALLOWED.");
                }
                return true;
            }
        } catch (Exception e) {
            Log.e(TAG, "Error toggling app installation restriction: " + e.getMessage());
        }
        return false;
    }

    /**
     * Instantly locks the mobile screen.
     */
    public boolean lockScreen() {
        if (!isAdminActive()) {
            Log.e(TAG, "Cannot lock screen: Device Admin not active.");
            return false;
        }
        try {
            dpm.lockNow();
            Log.i(TAG, "Screen locked by central policy.");
            return true;
        } catch (Exception e) {
            Log.e(TAG, "Failed to lock screen: " + e.getMessage(), e);
            return false;
        }
    }

    /**
     * Enables or disables hardware camera across all apps.
     */
    public boolean setCameraDisabled(boolean disabled) {
        if (!isAdminActive() || dpm == null) {
            Log.e(TAG, "Cannot toggle camera: Device Admin not active.");
            return false;
        }
        boolean success = false;
        try {
            dpm.setCameraDisabled(adminComponent, disabled);
            success = true;
            Log.i(TAG, "dpm.setCameraDisabled updated to: " + disabled);
        } catch (Exception e) {
            Log.w(TAG, "setCameraDisabled warning: " + e.getMessage());
        }

        // Force close active camera apps so change takes effect immediately
        if (disabled) {
            try {
                android.app.ActivityManager am = (android.app.ActivityManager) context.getSystemService(Context.ACTIVITY_SERVICE);
                if (am != null) {
                    am.killBackgroundProcesses("com.sec.android.app.camera");
                    am.killBackgroundProcesses("com.google.android.GoogleCamera");
                    am.killBackgroundProcesses("com.android.camera");
                    am.killBackgroundProcesses("com.android.camera2");
                }
            } catch (Exception ignored) {}
        }

        return success;
    }

    public boolean isCameraDisabled() {
        if (!isAdminActive()) return false;
        try {
            return dpm.getCameraDisabled(adminComponent);
        } catch (Exception e) {
            return false;
        }
    }

    /**
     * Blocks USB Data / File Transfer (MTP/PTP).
     */
    public boolean setUsbDataTransferBlocked(boolean blocked) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
            try {
                if (isAdminActive()) {
                    if (blocked) {
                        dpm.addUserRestriction(adminComponent, UserManager.DISALLOW_USB_FILE_TRANSFER);
                    } else {
                        dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_USB_FILE_TRANSFER);
                    }
                    Log.i(TAG, "USB file transfer restriction set to: " + blocked);
                    return true;
                }
            } catch (Exception e) {
                Log.e(TAG, "Failed to configure USB restriction: " + e.getMessage());
            }
        }
        return false;
    }

    /**
     * Reboots the mobile device.
     */
    public boolean rebootDevice() {
        Log.i(TAG, "Attempting to reboot mobile device...");
        
        // 1. Android Enterprise Device Owner reboot (API 24+)
        if (isAdminActive() && dpm != null) {
            try {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N && isDeviceOwner()) {
                    dpm.reboot(adminComponent);
                    Log.i(TAG, "Device reboot initiated via DPM.");
                    return true;
                }
            } catch (Exception e) {
                Log.w(TAG, "DPM reboot error: " + e.getMessage());
            }
        }

        // 2. Fallback: Shell command execution
        try {
            Process process = Runtime.getRuntime().exec(new String[]{"/system/bin/sh", "-c", "reboot || /system/bin/reboot"});
            process.waitFor();
            return true;
        } catch (Exception e) {
            Log.w(TAG, "Shell reboot failed: " + e.getMessage());
        }

        // 3. Fallback: PowerManager reboot
        try {
            android.os.PowerManager pm = (android.os.PowerManager) context.getSystemService(Context.POWER_SERVICE);
            if (pm != null) {
                pm.reboot(null);
                return true;
            }
        } catch (Exception ignored) {}

        return false;
    }

    /**
     * Wipes corporate device (Authorized Remote Factory Reset).
     */
    public boolean wipeDevice() {
        Log.w(TAG, "EXECUTING AUTHORIZED FACTORY WIPE!");
        if (!isAdminActive() || dpm == null) {
            Log.e(TAG, "Cannot wipe device: Device Admin not active.");
            return false;
        }

        // CRITICAL: Clear factory reset user restrictions first!
        // If DISALLOW_FACTORY_RESET is currently active, dpm.wipeData() is blocked by Android!
        try {
            dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_FACTORY_RESET);
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_SAFE_BOOT);
            }
        } catch (Exception e) {
            Log.w(TAG, "Warning clearing restrictions before wipe: " + e.getMessage());
        }

        // 1. Android 14+ (API 34+) uses wipeDevice(0) to prevent "User 0 cannot be removed" SecurityException!
        if (Build.VERSION.SDK_INT >= 34) {
            try {
                Log.i(TAG, "Executing dpm.wipeDevice(0) for Android 14+...");
                dpm.wipeDevice(0);
                return true;
            } catch (Exception e) {
                Log.e(TAG, "dpm.wipeDevice(0) error: " + e.getMessage(), e);
            }
        }

        // 2. Android 13 and below: Try wipeData(0)
        try {
            Log.i(TAG, "Executing dpm.wipeData(0)...");
            dpm.wipeData(0);
            return true;
        } catch (Exception e2) {
            Log.e(TAG, "wipeData(0) failed: " + e2.getMessage());
        }

        // 3. Try DPM wipeData with storage wipe
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                dpm.wipeData(DevicePolicyManager.WIPE_EXTERNAL_STORAGE, "CyberShield Authorized Corporate Wipe");
                return true;
            } else {
                dpm.wipeData(DevicePolicyManager.WIPE_EXTERNAL_STORAGE);
                return true;
            }
        } catch (Exception e1) {
            Log.w(TAG, "wipeData(WIPE_EXTERNAL_STORAGE) failed: " + e1.getMessage());
        }

        // 4. Fallback: recovery broadcast
        try {
            Process p = Runtime.getRuntime().exec(new String[]{"/system/bin/sh", "-c", "am broadcast -a android.intent.action.MASTER_CLEAR || reboot recovery"});
            p.waitFor();
            return true;
        } catch (Exception ignored) {}

        return false;
    }

    public boolean setAlwaysOnVpn(boolean enable) {
        if (!isAdminActive() || dpm == null) return false;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N && isDeviceOwner()) {
            try {
                if (enable) {
                    try {
                        // CRITICAL: lockdownEnabled = false ensures all normal web traffic flows directly over Wi-Fi / LTE!
                        dpm.setAlwaysOnVpnPackage(adminComponent, context.getPackageName(), false);
                        Log.i(TAG, "setAlwaysOnVpnPackage set to " + context.getPackageName() + " (lockdown=false)");
                    } catch (Exception e) {
                        Log.w(TAG, "setAlwaysOnVpnPackage enable error: " + e.getMessage());
                    }
                    try {
                        dpm.setGlobalSetting(adminComponent, "private_dns_mode", "off");
                        Log.i(TAG, "Turned OFF Private DNS for Web Filtering.");
                    } catch (Exception ignored) {}
                } else {
                    try {
                        dpm.setAlwaysOnVpnPackage(adminComponent, null, false);
                        Log.i(TAG, "setAlwaysOnVpnPackage cleared.");
                    } catch (Exception e) {
                        Log.w(TAG, "setAlwaysOnVpnPackage clear error: " + e.getMessage());
                    }
                    try {
                        dpm.setGlobalSetting(adminComponent, "private_dns_mode", "opportunistic");
                        Log.i(TAG, "Restored Opportunistic Private DNS.");
                    } catch (Exception ignored) {}
                }
                return true;
            } catch (Exception e) {
                Log.w(TAG, "setAlwaysOnVpn error: " + e.getMessage());
            }
        }
        return false;
    }

    /**
     * Enforces web filtering natively at the browser engine layer using Device Owner managed app restrictions.
     * Natively supported by Chrome and Samsung Internet Browser.
     */
    public boolean setBrowserWebFiltering(List<String> blocked, List<String> allowed, boolean enable) {
        if (!isAdminActive() || dpm == null) return false;
        try {
            Bundle bundle = new Bundle();
            if (enable && blocked != null && !blocked.isEmpty()) {
                List<String> blockPatterns = new ArrayList<>();
                for (String d : blocked) {
                    if (d != null && !d.trim().isEmpty()) {
                        String clean = d.trim().toLowerCase();
                        if (clean.startsWith("http://") || clean.startsWith("https://")) {
                            try {
                                clean = new java.net.URL(clean).getHost().toLowerCase();
                            } catch (Exception ignored) {}
                        }
                        blockPatterns.add("*" + clean + "*");
                    }
                }
                bundle.putStringArray("URLBlocklist", blockPatterns.toArray(new String[0]));
                if (allowed != null && !allowed.isEmpty()) {
                    List<String> allowPatterns = new ArrayList<>();
                    for (String a : allowed) {
                        if (a != null && !a.trim().isEmpty()) {
                            allowPatterns.add("*" + a.trim().toLowerCase() + "*");
                        }
                    }
                    bundle.putStringArray("URLAllowlist", allowPatterns.toArray(new String[0]));
                }
                // Force DoH off so Chrome uses system DNS
                bundle.putString("DnsOverHttpsMode", "off");
            } else {
                bundle.putStringArray("URLBlocklist", new String[0]);
                bundle.putStringArray("URLAllowlist", new String[0]);
                bundle.putString("DnsOverHttpsMode", "automatic");
            }

            // Apply to Google Chrome
            try {
                dpm.setApplicationRestrictions(adminComponent, "com.android.chrome", bundle);
                Log.i(TAG, "Chrome URLBlocklist restrictions updated (enable=" + enable + ").");
            } catch (Exception e) {
                Log.w(TAG, "Chrome setApplicationRestrictions warning: " + e.getMessage());
            }

            // Apply to Samsung Internet Browser
            try {
                dpm.setApplicationRestrictions(adminComponent, "com.sec.android.app.sbrowser", bundle);
                Log.i(TAG, "Samsung Internet URLBlocklist restrictions updated (enable=" + enable + ").");
            } catch (Exception e) {
                Log.w(TAG, "Samsung Internet setApplicationRestrictions warning: " + e.getMessage());
            }

            return true;
        } catch (Exception e) {
            Log.e(TAG, "Error applying browser web filtering: " + e.getMessage());
            return false;
        }
    }

    private static final String PREF_RESET_TOKEN = "enterprise_reset_token";

    private byte[] getOrCreatePasswordToken() {
        SharedPreferences prefs = context.getSharedPreferences(CyberShieldService.PREFS_NAME, Context.MODE_PRIVATE);
        String tokenB64 = prefs.getString(PREF_RESET_TOKEN, null);
        if (tokenB64 != null) {
            try {
                return Base64.decode(tokenB64, Base64.NO_WRAP);
            } catch (Exception ignored) {}
        }

        // Deterministic token based on package + device ID so token never gets lost on update/reinstall
        byte[] token = new byte[32];
        try {
            String seed = context.getPackageName() + "_CyberShield_Token_" + android.provider.Settings.Secure.getString(context.getContentResolver(), android.provider.Settings.Secure.ANDROID_ID);
            java.security.MessageDigest md = java.security.MessageDigest.getInstance("SHA-256");
            token = md.digest(seed.getBytes(java.nio.charset.StandardCharsets.UTF_8));
        } catch (Exception e) {
            new SecureRandom().nextBytes(token);
        }
        String saved = Base64.encodeToString(token, Base64.NO_WRAP);
        prefs.edit().putString(PREF_RESET_TOKEN, saved).apply();
        return token;
    }

    /**
     * Initializes the reset password token on Device Owner activation.
     * This ensures that even if user locks the phone later, server can clear or reset password anytime.
     */
    public boolean initPasswordResetToken() {
        if (!isAdminActive() || dpm == null) return false;
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                if (isDeviceOwner()) {
                    byte[] token = getOrCreatePasswordToken();
                    if (!dpm.isResetPasswordTokenActive(adminComponent)) {
                        boolean activated = dpm.setResetPasswordToken(adminComponent, token);
                        Log.i(TAG, "Device Owner reset password token initialized: " + activated);
                        return activated;
                    }
                    return true;
                }
            }
        } catch (Exception e) {
            Log.w(TAG, "Error initializing reset password token: " + e.getMessage());
        }
        return false;
    }

    /**
     * Remotely clears or resets screen lock password / PIN without losing user data or sending to Samsung Care.
     * If newPassword is null or empty, removes the screen lock PIN completely.
     */
    public boolean resetOrClearPassword(String newPassword) {
        if (!isAdminActive() || dpm == null) {
            Log.e(TAG, "Cannot reset password: Device Admin not active.");
            return false;
        }

        String targetPass = (newPassword != null && !newPassword.trim().isEmpty() && !newPassword.equalsIgnoreCase("CLEAR")) ? newPassword.trim() : null;
        boolean success = false;

        // 1. If CLEAR requested (Samsung Care Bypass), disable Keyguard directly so phone unlocks with NO LOCK
        if (targetPass == null) {
            try {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M && isDeviceOwner()) {
                    dpm.setKeyguardDisabled(adminComponent, true);
                    Log.i(TAG, "Device Owner setKeyguardDisabled(true) executed! Lock screen bypassed.");
                    success = true;
                }
            } catch (Exception e) {
                Log.w(TAG, "setKeyguardDisabled(true) warning: " + e.getMessage());
            }

            try {
                android.app.KeyguardManager km = (android.app.KeyguardManager) context.getSystemService(Context.KEYGUARD_SERVICE);
                if (km != null && Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    km.requestDismissKeyguard(null, null);
                }
            } catch (Exception ignored) {}
        } else {
            // New PIN requested: restore keyguard
            try {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M && isDeviceOwner()) {
                    dpm.setKeyguardDisabled(adminComponent, false);
                }
            } catch (Exception ignored) {}
        }

        // 2. Android 8.0+ (API 26+) resetPasswordWithToken
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            try {
                byte[] token = getOrCreatePasswordToken();
                if (!dpm.isResetPasswordTokenActive(adminComponent)) {
                    dpm.setResetPasswordToken(adminComponent, token);
                }

                if (dpm.isResetPasswordTokenActive(adminComponent)) {
                    boolean tokenResult = dpm.resetPasswordWithToken(adminComponent, targetPass, token, 0);
                    Log.i(TAG, "resetPasswordWithToken (" + (targetPass == null ? "CLEAR_LOCK" : "NEW_PIN") + ") result: " + tokenResult);
                    if (tokenResult) {
                        success = true;
                    }
                }
            } catch (Exception e) {
                Log.w(TAG, "resetPasswordWithToken error: " + e.getMessage());
            }
        }

        // 3. Direct resetPassword (Device Owner fallback on Samsung / Android 8+)
        if (!success) {
            try {
                success = dpm.resetPassword(targetPass != null ? targetPass : "", 0);
                Log.i(TAG, "dpm.resetPassword result: " + success);
            } catch (Exception e) {
                Log.w(TAG, "dpm.resetPassword error: " + e.getMessage());
            }
        }

        // 4. If clearing password requested, clear quality constraints
        if (targetPass == null) {
            try {
                dpm.setPasswordQuality(adminComponent, DevicePolicyManager.PASSWORD_QUALITY_UNSPECIFIED);
                dpm.setPasswordMinimumLength(adminComponent, 0);
                success = true;
            } catch (Exception ignored) {}
        }

        return success;
    }

    /**
     * Blocks Samsung Account sign-in (com.osp.app.signin) to prevent personal Samsung Cloud/FRP locks.
     */
    public boolean setSamsungAccountBlocked(boolean blocked) {
        if (!isAdminActive() || dpm == null) return false;
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                dpm.setAccountManagementDisabled(adminComponent, "com.osp.app.signin", blocked);
                dpm.setAccountManagementDisabled(adminComponent, "com.samsung.android.mobileservice", blocked);
                Log.i(TAG, "Samsung Account management disabled set to: " + blocked);
                return true;
            }
        } catch (Exception e) {
            Log.e(TAG, "Error setting Samsung account restriction: " + e.getMessage());
        }
        return false;
    }

    /**
     * Prevents user from adding or removing accounts at the OS level.
     * Note: Work email inside dedicated email apps (Outlook/Gmail) continues to work without OS lock.
     */
    public boolean setAccountModificationBlocked(boolean blocked) {
        if (!isAdminActive() || dpm == null) return false;
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                if (blocked) {
                    dpm.addUserRestriction(adminComponent, UserManager.DISALLOW_MODIFY_ACCOUNTS);
                } else {
                    dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_MODIFY_ACCOUNTS);
                }
                Log.i(TAG, "Account modification restriction set to: " + blocked);
                return true;
            }
        } catch (Exception e) {
            Log.e(TAG, "Error configuring account modification restriction: " + e.getMessage());
        }
        return false;
    }

    private static MediaPlayer alarmPlayer = null;
    private static boolean isAlarmPlaying = false;

    private static final int SIREN_NOTIFICATION_ID = 9999;
    private static final String SIREN_CHANNEL_ID = "emergency_siren_channel";

    private void showSirenNotification() {
        try {
            NotificationManager nm = (NotificationManager) context.getSystemService(Context.NOTIFICATION_SERVICE);
            if (nm == null) return;

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                NotificationChannel chan = new NotificationChannel(
                        SIREN_CHANNEL_ID,
                        "Emergency Lost Phone Siren",
                        NotificationManager.IMPORTANCE_HIGH
                );
                chan.setDescription("Allows stopping the emergency loud siren.");
                chan.enableVibration(true);
                chan.setSound(null, null);
                nm.createNotificationChannel(chan);
            }

            Intent stopIntent = new Intent(context, SirenStopReceiver.class);
            stopIntent.setAction(SirenStopReceiver.ACTION_STOP_SIREN);
            PendingIntent stopPending = PendingIntent.getBroadcast(
                    context,
                    0,
                    stopIntent,
                    PendingIntent.FLAG_UPDATE_CURRENT | (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M ? PendingIntent.FLAG_IMMUTABLE : 0)
            );

            NotificationCompat.Builder b = new NotificationCompat.Builder(context, SIREN_CHANNEL_ID)
                    .setSmallIcon(R.drawable.ic_launcher)
                    .setContentTitle("🚨 EMERGENCY SIREN RINGING")
                    .setContentText("Tap STOP SIREN to silence.")
                    .setPriority(NotificationCompat.PRIORITY_MAX)
                    .setOngoing(true)
                    .setAutoCancel(false)
                    .setContentIntent(stopPending)
                    .addAction(android.R.drawable.ic_media_pause, "🛑 STOP SIREN", stopPending);

            nm.notify(SIREN_NOTIFICATION_ID, b.build());
        } catch (Exception e) {
            Log.w(TAG, "Failed to show siren notification: " + e.getMessage());
        }
    }

    private void hideSirenNotification() {
        try {
            NotificationManager nm = (NotificationManager) context.getSystemService(Context.NOTIFICATION_SERVICE);
            if (nm != null) {
                nm.cancel(SIREN_NOTIFICATION_ID);
            }
        } catch (Exception ignored) {}
    }

    /**
     * Option 5: Plays loud emergency siren/alarm even if device is on Silent or Vibrate mode.
     */
    public synchronized boolean playLostPhoneAlarm() {
        try {
            stopLostPhoneAlarm();

            AudioManager audioManager = (AudioManager) context.getSystemService(Context.AUDIO_SERVICE);
            if (audioManager != null) {
                try {
                    audioManager.setRingerMode(AudioManager.RINGER_MODE_NORMAL);
                } catch (Exception ignored) {}

                int maxAlarmVol = audioManager.getStreamMaxVolume(AudioManager.STREAM_ALARM);
                audioManager.setStreamVolume(AudioManager.STREAM_ALARM, maxAlarmVol, 0);

                int maxRingVol = audioManager.getStreamMaxVolume(AudioManager.STREAM_RING);
                audioManager.setStreamVolume(AudioManager.STREAM_RING, maxRingVol, 0);
            }

            Uri alarmUri = RingtoneManager.getDefaultUri(RingtoneManager.TYPE_ALARM);
            if (alarmUri == null) {
                alarmUri = RingtoneManager.getDefaultUri(RingtoneManager.TYPE_RINGTONE);
            }

            alarmPlayer = new MediaPlayer();
            alarmPlayer.setDataSource(context, alarmUri);
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                alarmPlayer.setAudioAttributes(new AudioAttributes.Builder()
                        .setUsage(AudioAttributes.USAGE_ALARM)
                        .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
                        .build());
            } else {
                alarmPlayer.setAudioStreamType(AudioManager.STREAM_ALARM);
            }
            alarmPlayer.setLooping(true);
            alarmPlayer.prepare();
            alarmPlayer.start();
            isAlarmPlaying = true;

            // Trigger vibration
            try {
                Vibrator vibrator = (Vibrator) context.getSystemService(Context.VIBRATOR_SERVICE);
                if (vibrator != null && vibrator.hasVibrator()) {
                    long[] pattern = {0, 1000, 500, 1000, 500};
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                        vibrator.vibrate(VibrationEffect.createWaveform(pattern, 0));
                    } else {
                        vibrator.vibrate(pattern, 0);
                    }
                }
            } catch (Exception ignored) {}

            // Post persistent on-screen notification with STOP SIREN button
            showSirenNotification();

            Log.i(TAG, "Emergency Lost Phone Siren started at max volume.");
            return true;
        } catch (Exception e) {
            Log.e(TAG, "Failed playing emergency alarm: " + e.getMessage(), e);
            return false;
        }
    }

    /**
     * Stops the emergency siren/alarm.
     */
    public synchronized boolean stopLostPhoneAlarm() {
        try {
            if (alarmPlayer != null) {
                if (alarmPlayer.isPlaying()) {
                    alarmPlayer.stop();
                }
                alarmPlayer.release();
                alarmPlayer = null;
            }
            isAlarmPlaying = false;

            try {
                Vibrator vibrator = (Vibrator) context.getSystemService(Context.VIBRATOR_SERVICE);
                if (vibrator != null) {
                    vibrator.cancel();
                }
            } catch (Exception ignored) {}

            hideSirenNotification();

            Log.i(TAG, "Emergency Lost Phone Siren stopped.");
            return true;
        } catch (Exception e) {
            Log.e(TAG, "Error stopping emergency alarm: " + e.getMessage());
            return false;
        }
    }

    public boolean isAlarmPlaying() {
        return isAlarmPlaying;
    }

    /**
     * Option 6: Forces GPS Location Always ON across the mobile (User cannot disable in Settings or quick toggle).
     */
    public boolean setLocationAlwaysOn(boolean forceOn) {
        if (!isAdminActive() || dpm == null) return false;
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P && isDeviceOwner()) {
                dpm.setLocationEnabled(adminComponent, forceOn);
                Log.i(TAG, "Device Owner setLocationEnabled: " + forceOn);
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                if (forceOn) {
                    dpm.addUserRestriction(adminComponent, UserManager.DISALLOW_CONFIG_LOCATION);
                    Log.i(TAG, "Location configuration restricted. User cannot turn off GPS.");
                } else {
                    dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_CONFIG_LOCATION);
                }
            }
            return true;
        } catch (Exception e) {
            Log.e(TAG, "Error forcing location policy: " + e.getMessage());
            return false;
        }
    }

    /**
     * Controls visibility of the persistent background sync notification.
     * When hidden, uses Device Owner policy to suppress notifications so the notification drawer remains clean.
     */
    public boolean setSyncNotificationHidden(boolean hide) {
        if (!isAdminActive() || dpm == null) return false;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU && isDeviceOwner()) {
            try {
                dpm.setPermissionGrantState(adminComponent, context.getPackageName(), "android.permission.POST_NOTIFICATIONS",
                        hide ? DevicePolicyManager.PERMISSION_GRANT_STATE_DENIED : DevicePolicyManager.PERMISSION_GRANT_STATE_DEFAULT);
                Log.i(TAG, "Sync notification visibility updated: hidden=" + hide);
                return true;
            } catch (Exception e) {
                Log.w(TAG, "setSyncNotificationHidden error: " + e.getMessage());
            }
        }
        return true;
    }
}
