package com.cybershield.mdm;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInstaller;
import android.util.Log;

/**
 * Handles completion of silent in-place package updates initiated via Device Owner PackageInstaller.
 */
public class SilentInstallReceiver extends BroadcastReceiver {
    private static final String TAG = "CyberShield_OTA";

    @Override
    public void onReceive(Context context, Intent intent) {
        if (intent == null) return;
        int status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE);
        String message = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE);

        if (status == PackageInstaller.STATUS_SUCCESS) {
            Log.i(TAG, "Silent Package Installation SUCCEEDED! Restarting CyberShield MDM service...");
            CyberShieldService.startService(context);
        } else {
            Log.w(TAG, "Silent Package Installation FAILED (Status " + status + "): " + message);
        }
    }
}

