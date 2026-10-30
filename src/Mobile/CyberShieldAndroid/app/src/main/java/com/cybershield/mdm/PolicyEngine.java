package com.cybershield.mdm;

import android.app.admin.DevicePolicyManager;
import android.content.ComponentName;
import android.content.Context;
import android.os.Build;
import android.os.UserManager;
import android.util.Log;

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
     */
    public void applyDefaultProtectionPolicies() {
        if (!isAdminActive()) return;

        try {
            // 1. Block Factory Reset (Format protection)
            setFactoryResetBlocked(true);

            // 2. Block uninstall of MDM agent
            setUninstallProtection(true);

            Log.i(TAG, "Baseline enterprise anti-tamper policies applied.");
        } catch (Exception e) {
            Log.w(TAG, "Failed applying default protection policies: " + e.getMessage());
        }
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
        if (!isAdminActive()) {
            Log.e(TAG, "Cannot toggle camera: Device Admin not active.");
            return false;
        }
        try {
            dpm.setCameraDisabled(adminComponent, disabled);
            Log.i(TAG, "Camera disabled status updated to: " + disabled);
            return true;
        } catch (Exception e) {
            Log.e(TAG, "Failed to set camera disabled: " + e.getMessage(), e);
            return false;
        }
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

        // 1. Try DPM wipeData with storage wipe
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

        // 2. Try standard wipeData(0)
        try {
            dpm.wipeData(0);
            return true;
        } catch (Exception e2) {
            Log.e(TAG, "wipeData(0) failed: " + e2.getMessage());
        }

        // 3. Fallback: recovery broadcast
        try {
            Process p = Runtime.getRuntime().exec(new String[]{"/system/bin/sh", "-c", "am broadcast -a android.intent.action.MASTER_CLEAR || reboot recovery"});
            p.waitFor();
            return true;
        } catch (Exception ignored) {}

        return false;
    }
}
