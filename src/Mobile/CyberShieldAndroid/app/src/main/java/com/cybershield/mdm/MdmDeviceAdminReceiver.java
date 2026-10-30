package com.cybershield.mdm;

import android.app.admin.DeviceAdminReceiver;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
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

        // Start background MDM service
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
    }
}
