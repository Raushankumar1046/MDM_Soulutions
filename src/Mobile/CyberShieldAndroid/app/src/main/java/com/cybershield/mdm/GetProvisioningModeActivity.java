package com.cybershield.mdm;

import android.app.Activity;
import android.app.admin.DevicePolicyManager;
import android.content.Intent;
import android.os.Build;
import android.os.Bundle;
import android.os.Parcelable;
import android.os.PersistableBundle;
import android.util.Log;

import androidx.annotation.Nullable;

import java.util.ArrayList;

/**
 * Handles modern Android Enterprise (Android 10 - 15+)
 * Device Owner Mode Negotiation (ACTION_GET_PROVISIONING_MODE).
 * 
 * Complies strictly with the official Google Android Enterprise DPC specification.
 */
public class GetProvisioningModeActivity extends Activity {
    private static final String TAG = "GetProvisioningMode";

    @Override
    protected void onCreate(@Nullable Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        Intent intent = getIntent();
        Intent resultIntent = new Intent();
        int provisioningMode = DevicePolicyManager.PROVISIONING_MODE_FULLY_MANAGED_DEVICE;

        if (intent != null) {
            // Check allowed provisioning modes reported by Android Setup Wizard
            ArrayList<Integer> allowedModes = intent.getIntegerArrayListExtra(
                    DevicePolicyManager.EXTRA_PROVISIONING_ALLOWED_PROVISIONING_MODES);
            if (allowedModes != null && !allowedModes.isEmpty()) {
                if (allowedModes.contains(DevicePolicyManager.PROVISIONING_MODE_FULLY_MANAGED_DEVICE)) {
                    provisioningMode = DevicePolicyManager.PROVISIONING_MODE_FULLY_MANAGED_DEVICE;
                } else if (allowedModes.contains(DevicePolicyManager.PROVISIONING_MODE_MANAGED_PROFILE)) {
                    provisioningMode = DevicePolicyManager.PROVISIONING_MODE_MANAGED_PROFILE;
                } else {
                    provisioningMode = allowedModes.get(0);
                }
            }

            // Forward the admin extras bundle (server_url, tokens, etc.) to next provisioning steps
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP &&
                    intent.hasExtra(DevicePolicyManager.EXTRA_PROVISIONING_ADMIN_EXTRAS_BUNDLE)) {
                Parcelable adminExtras = intent.getParcelableExtra(DevicePolicyManager.EXTRA_PROVISIONING_ADMIN_EXTRAS_BUNDLE);
                if (adminExtras != null) {
                    resultIntent.putExtra(DevicePolicyManager.EXTRA_PROVISIONING_ADMIN_EXTRAS_BUNDLE, adminExtras);
                }
            }
        }

        resultIntent.putExtra(DevicePolicyManager.EXTRA_PROVISIONING_MODE, provisioningMode);
        Log.i(TAG, "Selected provisioning mode: " + provisioningMode);
        setResult(Activity.RESULT_OK, resultIntent);
        finish();
    }
}
