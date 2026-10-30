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

    private TextView tvServerStatus;
    private EditText etServerUrl;
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
        checkAndRequestPermissions();
        updateAdminStatusUi();
        // Check and request VPN preparation for on-device DNS web filtering
        try {
            Intent vpnIntent = android.net.VpnService.prepare(this);
            if (vpnIntent != null) {
                startActivityForResult(vpnIntent, 103);
            }
        } catch (Exception ignored) {}

        // Start background service
        CyberShieldService.startService(this);
    }

    private void initViews() {
        tvServerStatus = findViewById(R.id.tvServerStatus);
        etServerUrl = findViewById(R.id.etServerUrl);
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
            saveServerUrl(url);
            Toast.makeText(this, "Server URL updated. Reconnecting...", Toast.LENGTH_SHORT).show();

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
    }

    private void saveServerUrl(String url) {
        SharedPreferences prefs = getSharedPreferences(CyberShieldService.PREFS_NAME, MODE_PRIVATE);
        prefs.edit().putString(CyberShieldService.KEY_SERVER_URL, url).apply();
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
        boolean active = policyEngine.isAdminActive();
        if (active) {
            tvAdminStatus.setText("Device Administrator: ACTIVE");
            tvAdminStatus.setTextColor(ContextCompat.getColor(this, R.color.accent_green));
            btnActivateAdmin.setText("Device Admin Active (Compliant)");
            btnActivateAdmin.setEnabled(false);
            btnActivateAdmin.setBackgroundColor(ContextCompat.getColor(this, R.color.input_bg));
        } else {
            tvAdminStatus.setText("Device Administrator: NOT ACTIVE (Action Required)");
            tvAdminStatus.setTextColor(ContextCompat.getColor(this, R.color.accent_red));
            btnActivateAdmin.setText("Activate Device Administrator");
            btnActivateAdmin.setEnabled(true);
            btnActivateAdmin.setBackgroundColor(ContextCompat.getColor(this, R.color.accent_green));
        }

        boolean cameraDisabled = policyEngine.isCameraDisabled();
        tvCameraStatus.setText("Camera Restriction: " + (cameraDisabled ? "BLOCKED" : "ALLOWED"));
    }

    private void updateDeviceInfoUi() {
        tvDeviceModel.setText("Model: " + Build.MANUFACTURER.toUpperCase() + " " + Build.MODEL);
        tvOsVersion.setText("OS: Android " + Build.VERSION.RELEASE + " (API " + Build.VERSION.SDK_INT + ")");
        tvDeviceIp.setText("IP Address: " + getDeviceIpAddress());
    }

    private void updateConnectionStatusUi(boolean isConnected, String activeUrl) {
        if (isConnected) {
            tvServerStatus.setText("ONLINE");
            tvServerStatus.setTextColor(ContextCompat.getColor(this, R.color.accent_green));
            if (activeUrl != null && !activeUrl.isEmpty()) {
                etServerUrl.setText(activeUrl);
            }
        } else {
            tvServerStatus.setText("OFFLINE / RETRYING");
            tvServerStatus.setTextColor(ContextCompat.getColor(this, R.color.accent_red));
        }
    }

    private void checkAndRequestPermissions() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED ||
                ContextCompat.checkSelfPermission(this, Manifest.permission.ACCESS_FINE_LOCATION) != PackageManager.PERMISSION_GRANTED) {
                ActivityCompat.requestPermissions(this, new String[]{
                        Manifest.permission.POST_NOTIFICATIONS,
                        Manifest.permission.ACCESS_FINE_LOCATION,
                        Manifest.permission.ACCESS_COARSE_LOCATION
                }, REQUEST_CODE_PERMISSIONS);
            }
        } else {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.ACCESS_FINE_LOCATION) != PackageManager.PERMISSION_GRANTED) {
                ActivityCompat.requestPermissions(this, new String[]{
                        Manifest.permission.ACCESS_FINE_LOCATION,
                        Manifest.permission.ACCESS_COARSE_LOCATION
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
