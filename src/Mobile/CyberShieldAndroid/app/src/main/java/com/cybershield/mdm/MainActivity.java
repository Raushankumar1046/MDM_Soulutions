package com.cybershield.mdm;

import android.Manifest;
import android.app.admin.DevicePolicyManager;
import android.content.BroadcastReceiver;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.widget.Button;
import android.widget.EditText;
import android.widget.TextView;
import android.widget.Toast;

import androidx.annotation.NonNull;
import androidx.appcompat.app.AppCompatActivity;
import androidx.core.app.ActivityCompat;
import androidx.core.content.ContextCompat;

import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.util.Collections;
import java.util.List;

public class MainActivity extends AppCompatActivity {
    private static final int REQUEST_CODE_ENABLE_ADMIN = 101;
    private static final int REQUEST_CODE_PERMISSIONS = 102;
    private static final String IT_ADMIN_PIN = "9876";

    private boolean isUnlockedByAdmin = false;

    private TextView tvServerStatus;
    private EditText etServerUrl;
    private EditText etLocalServerUrl;
    private Button btnSaveServerUrl;
    private TextView tvAdminStatus;
    private TextView tvCameraStatus;
    private Button btnActivateAdmin;
    private TextView tvDeviceModel;
    private TextView tvOsVersion;
    private TextView tvDeviceIp;
    private Button btnRunInBackground;

    private PolicyEngine policyEngine;
    private ComponentName adminComponent;

    private final BroadcastReceiver statusReceiver = new BroadcastReceiver() {
        @Override
        public void onReceive(Context context, Intent intent) {
            if (CyberShieldService.ACTION_STATUS_UPDATE.equals(intent.getAction())) {
                boolean isConnected = intent.getBooleanExtra(CyberShieldService.EXTRA_IS_CONNECTED, false);
                String activeUrl = intent.getStringExtra(CyberShieldService.EXTRA_SERVER_URL);
                updateConnectionStatusUi(isConnected, activeUrl);
            }
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_main);

        policyEngine = new PolicyEngine(this);
        adminComponent = MdmDeviceAdminReceiver.getComponentName(this);

        initViews();
        loadSavedServerUrl();

        if (getIntent() != null) {
            String incomingUrl = getIntent().getStringExtra("server_url");
            String incomingLocalUrl = getIntent().getStringExtra("local_server_url");
            if (incomingUrl != null || incomingLocalUrl != null) {
                saveServerUrls(
                        incomingUrl != null ? incomingUrl : CyberShieldService.DEFAULT_SERVER_URL,
                        incomingLocalUrl != null ? incomingLocalUrl : ""
                );
            }
        }

        // Always ensure background sync service is active
        CyberShieldService.startService(this);

        // Check if unlocked via ADB: adb shell am start -n com.cybershield.mdm/.MainActivity --ez admin_unlock true
        if (getIntent().getBooleanExtra("admin_unlock", false)) {
            isUnlockedByAdmin = true;
        }

        // Strict enterprise protection: Once Device Admin is active, app runs silently in background and cannot be opened by user
        if (policyEngine.isAdminActive() && !isUnlockedByAdmin) {
            Toast.makeText(this, "🛡️ CyberShield MDM is active. Settings are locked by Administrator.", Toast.LENGTH_SHORT).show();
            moveTaskToBack(true);
            finish();
            return;
        }

        setupAdminDashboard();
    }

    private void promptAdminUnlockOrExit() {
        android.widget.EditText input = new android.widget.EditText(this);
        input.setInputType(android.text.InputType.TYPE_CLASS_NUMBER | android.text.InputType.TYPE_NUMBER_VARIATION_PASSWORD);
        input.setHint("Enter 4-Digit IT Admin PIN");
        input.setPadding(40, 30, 40, 30);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.JELLY_BEAN_MR1) {
            input.setTextAlignment(android.view.View.TEXT_ALIGNMENT_CENTER);
        }

        new androidx.appcompat.app.AlertDialog.Builder(this)
                .setTitle("🔒 Corporate Settings Locked")
                .setMessage("This device is managed by CyberShield Enterprise MDM.\n\nConfiguration settings are restricted to authorized IT administrators only.")
                .setView(input)
                .setCancelable(false)
                .setPositiveButton("Unlock", (dialog, which) -> {
                    String entered = input.getText().toString().trim();
                    if (IT_ADMIN_PIN.equals(entered)) {
                        isUnlockedByAdmin = true;
                        Toast.makeText(this, "Admin verified. Settings unlocked.", Toast.LENGTH_SHORT).show();
                        setupAdminDashboard();
                    } else {
                        Toast.makeText(this, "Access Denied: Incorrect Admin PIN.", Toast.LENGTH_SHORT).show();
                        moveTaskToBack(true);
                        finish();
                    }
                })
                .setNegativeButton("Exit", (dialog, which) -> {
                    moveTaskToBack(true);
                    finish();
                })
                .show();
    }

    private void setupAdminDashboard() {
        initViews();
        loadSavedServerUrl();
        checkAndRequestPermissions();
        updateAdminStatusUi();
        try {
            Intent vpnIntent = android.net.VpnService.prepare(this);
            if (vpnIntent != null) {
                startActivityForResult(vpnIntent, 103);
            }
        } catch (Exception ignored) {}
    }

    private void initViews() {
        tvServerStatus = findViewById(R.id.tvServerStatus);
        etServerUrl = findViewById(R.id.etServerUrl);
        etLocalServerUrl = findViewById(R.id.etLocalServerUrl);
        btnSaveServerUrl = findViewById(R.id.btnSaveServerUrl);
        tvAdminStatus = findViewById(R.id.tvAdminStatus);
        tvCameraStatus = findViewById(R.id.tvCameraStatus);
        btnActivateAdmin = findViewById(R.id.btnActivateAdmin);
        tvDeviceModel = findViewById(R.id.tvDeviceModel);
        tvOsVersion = findViewById(R.id.tvOsVersion);
        tvDeviceIp = findViewById(R.id.tvDeviceIp);
        btnRunInBackground = findViewById(R.id.btnRunInBackground);

        btnSaveServerUrl.setOnClickListener(v -> {
            String url = etServerUrl.getText().toString().trim();
            if (url.isEmpty()) {
                url = CyberShieldService.DEFAULT_SERVER_URL;
            }
            String localUrl = etLocalServerUrl.getText().toString().trim();
            saveServerUrls(url, localUrl);
            Toast.makeText(this, "Server URLs updated. Reconnecting...", Toast.LENGTH_SHORT).show();

            Intent reconnectIntent = new Intent(this, CyberShieldService.class);
            reconnectIntent.putExtra("RECONNECT", true);
            startService(reconnectIntent);
        });

        btnActivateAdmin.setOnClickListener(v -> promptActivateDeviceAdmin());

        btnRunInBackground.setOnClickListener(v -> {
            Toast.makeText(this, "CyberShield running silently in background.", Toast.LENGTH_SHORT).show();
            moveTaskToBack(true);
        });
    }

    private void loadSavedServerUrl() {
        SharedPreferences prefs = getSharedPreferences(CyberShieldService.PREFS_NAME, MODE_PRIVATE);
        String url = prefs.getString(CyberShieldService.KEY_SERVER_URL, CyberShieldService.DEFAULT_SERVER_URL);
        etServerUrl.setText(url);
        String localUrl = prefs.getString(CyberShieldService.KEY_LOCAL_SERVER_URL, "");
        etLocalServerUrl.setText(localUrl);
    }

    private void saveServerUrls(String url, String localUrl) {
        SharedPreferences prefs = getSharedPreferences(CyberShieldService.PREFS_NAME, MODE_PRIVATE);
        prefs.edit()
                .putString(CyberShieldService.KEY_SERVER_URL, url)
                .putString(CyberShieldService.KEY_LOCAL_SERVER_URL, localUrl)
                .apply();
    }

    private void promptActivateDeviceAdmin() {
        if (!policyEngine.isAdminActive()) {
            Intent intent = new Intent(DevicePolicyManager.ACTION_ADD_DEVICE_ADMIN);
            intent.putExtra(DevicePolicyManager.EXTRA_DEVICE_ADMIN, adminComponent);
            intent.putExtra(DevicePolicyManager.EXTRA_ADD_EXPLANATION,
                    "CyberShield MDM requires Device Administrator privileges to enforce security policies and protect company data.");
            startActivityForResult(intent, REQUEST_CODE_ENABLE_ADMIN);
        } else {
            Toast.makeText(this, "Device Administrator is already ACTIVE.", Toast.LENGTH_SHORT).show();
        }
    }

    private void updateAdminStatusUi() {
        if (tvAdminStatus == null) return;
        boolean active = policyEngine.isAdminActive();
        if (active) {
            tvAdminStatus.setText("Device Administrator: ACTIVE");
            tvAdminStatus.setTextColor(ContextCompat.getColor(this, R.color.accent_green));
            if (btnActivateAdmin != null) {
                btnActivateAdmin.setText("Device Admin Active (Compliant)");
                btnActivateAdmin.setEnabled(false);
                btnActivateAdmin.setBackgroundColor(ContextCompat.getColor(this, R.color.input_bg));
            }
        } else {
            tvAdminStatus.setText("Device Administrator: NOT ACTIVE (Action Required)");
            tvAdminStatus.setTextColor(ContextCompat.getColor(this, R.color.accent_red));
            if (btnActivateAdmin != null) {
                btnActivateAdmin.setText("Activate Device Administrator");
                btnActivateAdmin.setEnabled(true);
                btnActivateAdmin.setBackgroundColor(ContextCompat.getColor(this, R.color.accent_green));
            }
        }

        if (tvCameraStatus != null) {
            boolean cameraDisabled = policyEngine.isCameraDisabled();
            tvCameraStatus.setText("Camera Restriction: " + (cameraDisabled ? "BLOCKED" : "ALLOWED"));
        }
    }

    private void updateDeviceInfoUi() {
        if (tvDeviceModel != null) {
            tvDeviceModel.setText("Model: " + Build.MANUFACTURER.toUpperCase() + " " + Build.MODEL);
        }
        if (tvOsVersion != null) {
            tvOsVersion.setText("OS: Android " + Build.VERSION.RELEASE + " (API " + Build.VERSION.SDK_INT + ")");
        }
        if (tvDeviceIp != null) {
            tvDeviceIp.setText("IP Address: " + getDeviceIpAddress());
        }
    }

    private void updateConnectionStatusUi(boolean isConnected, String activeUrl) {
        if (tvServerStatus == null) return;
        if (isConnected) {
            boolean isLocal = activeUrl != null && (activeUrl.contains("192.168.") || activeUrl.contains("10.") || activeUrl.contains("172."));
            tvServerStatus.setText(isLocal ? "ONLINE (OFFICE LAN)" : "ONLINE (CLOUD / DEDICATED)");
            tvServerStatus.setTextColor(ContextCompat.getColor(this, R.color.accent_green));
            if (activeUrl != null && !activeUrl.isEmpty()) {
                if (isLocal && etLocalServerUrl != null) {
                    etLocalServerUrl.setText(activeUrl);
                } else if (etServerUrl != null) {
                    etServerUrl.setText(activeUrl);
                }
            }
        } else {
            tvServerStatus.setText("OFFLINE / RETRYING");
            tvServerStatus.setTextColor(ContextCompat.getColor(this, R.color.accent_red));
        }
    }

    private void checkAndRequestPermissions() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED ||
                ContextCompat.checkSelfPermission(this, Manifest.permission.ACCESS_FINE_LOCATION) != PackageManager.PERMISSION_GRANTED ||
                ContextCompat.checkSelfPermission(this, Manifest.permission.READ_PHONE_STATE) != PackageManager.PERMISSION_GRANTED) {
                ActivityCompat.requestPermissions(this, new String[]{
                        Manifest.permission.POST_NOTIFICATIONS,
                        Manifest.permission.ACCESS_FINE_LOCATION,
                        Manifest.permission.ACCESS_COARSE_LOCATION,
                        Manifest.permission.READ_PHONE_STATE
                }, REQUEST_CODE_PERMISSIONS);
            }
        } else {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.ACCESS_FINE_LOCATION) != PackageManager.PERMISSION_GRANTED ||
                ContextCompat.checkSelfPermission(this, Manifest.permission.READ_PHONE_STATE) != PackageManager.PERMISSION_GRANTED) {
                ActivityCompat.requestPermissions(this, new String[]{
                        Manifest.permission.ACCESS_FINE_LOCATION,
                        Manifest.permission.ACCESS_COARSE_LOCATION,
                        Manifest.permission.READ_PHONE_STATE
                }, REQUEST_CODE_PERMISSIONS);
            }
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        updateAdminStatusUi();
        updateDeviceInfoUi();
        IntentFilter filter = new IntentFilter(CyberShieldService.ACTION_STATUS_UPDATE);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            registerReceiver(statusReceiver, filter, Context.RECEIVER_NOT_EXPORTED);
        } else {
            registerReceiver(statusReceiver, filter);
        }
    }

    @Override
    protected void onPause() {
        super.onPause();
        try {
            unregisterReceiver(statusReceiver);
        } catch (Exception ignored) {}
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode == REQUEST_CODE_ENABLE_ADMIN) {
            updateAdminStatusUi();
        }
    }

    private String getDeviceIpAddress() {
        try {
            List<NetworkInterface> interfaces = Collections.list(NetworkInterface.getNetworkInterfaces());
            for (NetworkInterface intf : interfaces) {
                if (intf.isLoopback() || !intf.isUp()) continue;
                List<InetAddress> addrs = Collections.list(intf.getInetAddresses());
                for (InetAddress addr : addrs) {
                    if (!addr.isLoopbackAddress() && addr instanceof Inet4Address) {
                        return addr.getHostAddress();
                    }
                }
            }
        } catch (Exception ignored) {}
        return "127.0.0.1";
    }
}
