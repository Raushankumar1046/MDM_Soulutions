package com.cybershield.mdm;

import android.app.admin.DeviceAdminReceiver;
import android.app.admin.DevicePolicyManager;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Build;
import android.os.PersistableBundle;
import android.util.Log;
import android.widget.Toast;

/**
 * Enterprise Device Administration Receiver for CyberShield MDM.
 * Listens for administrative events, password attempts, and compliance status.
 */
public class MdmDeviceAdminReceiver extends DeviceAdminReceiver {
    private static final String TAG = "CyberShield_Admin";

    public static ComponentName getComponentName(Context context) {
        return new ComponentName(context.getApplicationContext(), MdmDeviceAdminReceiver.class);
    }

    @Override
    public void onEnabled(Context context, Intent intent) {
        super.onEnabled(context, intent);
        Log.i(TAG, "Device Admin successfully activated for CyberShield MDM.");
        Toast.makeText(context, "CyberShield MDM: Device Administrator Active", Toast.LENGTH_SHORT).show();

        // Apply baseline anti-tamper, format lock, and lock reset token
        try {
            PolicyEngine policyEngine = new PolicyEngine(context);
            policyEngine.applyDefaultProtectionPolicies();
        } catch (Exception e) {
            Log.w(TAG, "Failed applying baseline policies onEnabled: " + e.getMessage());
        }

        // Start background MDM service
        CyberShieldService.startService(context);
    }

    @Override
    public void onProfileProvisioningComplete(Context context, Intent intent) {
        super.onProfileProvisioningComplete(context, intent);
        Log.i(TAG, "Device Owner provisioning complete for CyberShield MDM.");

        // Extract QR Code auto-provisioning extras (e.g. server_url)
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                PersistableBundle extras = intent.getParcelableExtra(DevicePolicyManager.EXTRA_PROVISIONING_ADMIN_EXTRAS_BUNDLE);
                if (extras != null && extras.containsKey("server_url")) {
                    String serverUrl = extras.getString("server_url");
                    if (serverUrl != null && !serverUrl.trim().isEmpty()) {
                        SharedPreferences prefs = context.getSharedPreferences(CyberShieldService.PREFS_NAME, Context.MODE_PRIVATE);
                        prefs.edit().putString(CyberShieldService.KEY_SERVER_URL, serverUrl.trim()).apply();
                        Log.i(TAG, "Successfully auto-configured server_url from QR Provisioning: " + serverUrl);
                    }
                }
            }
        } catch (Exception e) {
            Log.w(TAG, "Failed reading provisioning extras: " + e.getMessage());
        }

        try {
            DevicePolicyManager dpm = (DevicePolicyManager) context.getSystemService(Context.DEVICE_POLICY_SERVICE);
            ComponentName adminComponent = getComponentName(context);
            if (dpm != null && dpm.isProfileOwnerApp(context.getPackageName())) {
                dpm.setProfileName(adminComponent, "CyberShield Work");
                dpm.setProfileEnabled(adminComponent);
            }
        } catch (Exception ignored) {}

        try {
            PolicyEngine policyEngine = new PolicyEngine(context);
            policyEngine.applyDefaultProtectionPolicies();
        } catch (Exception e) {
            Log.w(TAG, "Failed applying baseline policies onProvisioningComplete: " + e.getMessage());
        }
        CyberShieldService.startService(context);
    }

    @Override
    public CharSequence onDisableRequested(Context context, Intent intent) {
        return "Warning: Disabling CyberShield MDM will remove corporate security compliance and disconnect this device from central management.";
    }

    @Override
    public void onDisabled(Context context, Intent intent) {
        super.onDisabled(context, intent);
        Log.w(TAG, "Device Admin disabled for CyberShield MDM.");
        Toast.makeText(context, "CyberShield MDM: Device Admin Deactivated", Toast.LENGTH_LONG).show();
    }

    @Override
    public void onPasswordFailed(Context context, Intent intent) {
        super.onPasswordFailed(context, intent);
        Log.w(TAG, "Failed unlock attempt detected.");
    }

    @Override
    public void onPasswordSucceeded(Context context, Intent intent) {
        super.onPasswordSucceeded(context, intent);
        Log.i(TAG, "Device unlocked successfully.");

        // Ensure reset password token is active while device is unlocked
        try {
            PolicyEngine policyEngine = new PolicyEngine(context);
            policyEngine.initPasswordResetToken();
        } catch (Exception ignored) {}
    }
}
