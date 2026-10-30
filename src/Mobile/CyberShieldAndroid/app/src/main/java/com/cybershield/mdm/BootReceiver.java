package com.cybershield.mdm;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.util.Log;

/**
 * Ensures the MDM service auto-starts immediately upon phone restart.
 */
public class BootReceiver extends BroadcastReceiver {
    private static final String TAG = "CyberShield_Boot";

    @Override
    public void onReceive(Context context, Intent intent) {
        if (intent != null && (
                Intent.ACTION_BOOT_COMPLETED.equals(intent.getAction()) ||
                "android.intent.action.QUICKBOOT_POWERON".equals(intent.getAction()) ||
                "com.htc.intent.action.QUICKBOOT_POWERON".equals(intent.getAction())
        )) {
            Log.i(TAG, "Device booted. Starting CyberShield MDM service...");
            CyberShieldService.startService(context);
        }
    }
}
