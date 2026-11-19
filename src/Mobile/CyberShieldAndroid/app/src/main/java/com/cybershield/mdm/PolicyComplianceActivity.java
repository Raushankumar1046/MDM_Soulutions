package com.cybershield.mdm;

import android.Manifest;
import android.app.Activity;
import android.app.admin.DevicePolicyManager;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.os.PersistableBundle;
import android.util.Log;

import androidx.annotation.Nullable;

/**
 * Handles modern Android Enterprise (Android 10 - 15+)
 * Admin Policy Compliance Handshake (ACTION_ADMIN_POLICY_COMPLIANCE).
 * 
 * Invoked by the system Setup Wizard to verify that the DPC has finished
 * synchronous initialization before handing the phone over to the home screen.
 */
public class PolicyComplianceActivity extends Activity {
    private static final String TAG = "PolicyCompliance";

    @Override
    protected void onCreate(@Nullable Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        Intent intent = getIntent();
        Log.i(TAG, "ACTION_ADMIN_POLICY_COMPLIANCE received from Setup Wizard");

        try {
            // 1. Extract server_url if passed via QR code extra bundle
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP && intent != null) {
                PersistableBundle extras = intent.getParcelableExtra(DevicePolicyManager.EXTRA_PROVISIONING_ADMIN_EXTRAS_BUNDLE);
                if (extras != null && extras.containsKey("server_url")) {
                    String serverUrl = extras.getString("server_url");
                    if (serverUrl != null && !serverUrl.trim().isEmpty()) {
                        SharedPreferences prefs = getSharedPreferences(CyberShieldService.PREFS_NAME, Context.MODE_PRIVATE);
                        prefs.edit().putString(CyberShieldService.KEY_SERVER_URL, serverUrl.trim()).apply();
                        Log.i(TAG, "Configured server_url from QR extras: " + serverUrl);
                    }
                }
            }

            // 2. Enable work profile if provisioned as Profile Owner
            try {
                DevicePolicyManager dpm = (DevicePolicyManager) getSystemService(Context.DEVICE_POLICY_SERVICE);
                ComponentName adminComponent = MdmDeviceAdminReceiver.getComponentName(this);
                if (dpm != null && dpm.isProfileOwnerApp(getPackageName())) {
                    dpm.setProfileName(adminComponent, "CyberShield Work");
                    dpm.setProfileEnabled(adminComponent);
                }
            } catch (Exception ignored) {}

            // 3. Grant required runtime permissions automatically as Device Owner
            try {
                DevicePolicyManager dpm = (DevicePolicyManager) getSystemService(Context.DEVICE_POLICY_SERVICE);
                ComponentName adminComponent = MdmDeviceAdminReceiver.getComponentName(this);
                if (dpm != null && dpm.isDeviceOwnerApp(getPackageName())) {
                    String[] requiredPermissions = new String[]{
                            Manifest.permission.ACCESS_FINE_LOCATION,
                            Manifest.permission.ACCESS_COARSE_LOCATION,
                            "android.permission.READ_PHONE_STATE"
                    };
                    for (String perm : requiredPermissions) {
                        try {
                            if (checkSelfPermission(perm) != PackageManager.PERMISSION_GRANTED) {
                                dpm.setPermissionGrantState(adminComponent, getPackageName(), perm,
                                        DevicePolicyManager.PERMISSION_GRANT_STATE_GRANTED);
                            }
                        } catch (Exception ignored) {}
                    }
                    // For notifications, set to DEFAULT so the toggle switch remains accessible/unlocked in Android Settings
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                        try {
                            dpm.setPermissionGrantState(adminComponent, getPackageName(), "android.permission.POST_NOTIFICATIONS",
                                    DevicePolicyManager.PERMISSION_GRANT_STATE_DEFAULT);
                        } catch (Exception ignored) {}
                    }
                }
            } catch (Exception ignored) {}

            // 4. Return success to Setup Wizard
            setResult(Activity.RESULT_OK);
            Log.i(TAG, "Policy compliance successful. Releasing Setup Wizard to Home Launcher.");
        } catch (Exception e) {
            Log.e(TAG, "Compliance handling error: " + e.getMessage(), e);
            setResult(Activity.RESULT_OK); // Return OK anyway so Setup Wizard doesn't abort
        } finally {
            finish();
        }
    }
}

