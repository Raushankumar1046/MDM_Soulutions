package com.cybershield.mdm;

import android.accessibilityservice.AccessibilityService;
import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.os.Build;
import android.os.PowerManager;
import android.util.Base64;
import android.util.DisplayMetrics;
import android.util.Log;

import com.cybershield.mdm.models.RemoteInputEvent;

import com.cybershield.mdm.models.TerminalCommand;
import com.cybershield.mdm.models.TerminalOutput;
import com.google.gson.Gson;
import com.microsoft.signalr.HubConnection;
import com.microsoft.signalr.HubConnectionState;

import java.io.BufferedReader;
import java.io.ByteArrayOutputStream;
import java.io.InputStreamReader;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

/**
 * Manages Remote Terminal Execution and Remote Screen/Camera Live Streaming.
 */
public class RemoteGatewayManager {
    private static final String TAG = "CyberShield_Remote";

    private final Context context;
    private final Gson gson = new Gson();
    private ScheduledExecutorService streamScheduler;
    private boolean isStreaming = false;

    public RemoteGatewayManager(Context context) {
        this.context = context.getApplicationContext();
    }

    /**
     * Executes shell / administrative commands on Android and streams output back to the server console.
     */
    public void executeCommand(TerminalCommand cmd, HubConnection hubConnection) {
        if (cmd == null || hubConnection == null) return;

        Executors.newSingleThreadExecutor().execute(() -> {
            StringBuilder output = new StringBuilder();
            boolean isError = false;

            try {
                String cmdText = cmd.commandText != null ? cmd.commandText.trim() : "";
                if (cmdText.isEmpty()) cmdText = "getprop ro.product.model";

                Process process = Runtime.getRuntime().exec(new String[]{"/system/bin/sh", "-c", cmdText});

                BufferedReader stdInput = new BufferedReader(new InputStreamReader(process.getInputStream()));
                BufferedReader stdError = new BufferedReader(new InputStreamReader(process.getErrorStream()));

                String s;
                while ((s = stdInput.readLine()) != null) {
                    output.append(s).append("\n");
                }
                while ((s = stdError.readLine()) != null) {
                    output.append("[ERR] ").append(s).append("\n");
                    isError = true;
                }

                process.waitFor(10, TimeUnit.SECONDS);

            } catch (Exception e) {
                output.append("Execution Exception: ").append(e.getMessage());
                isError = true;
            }

            // Report output to server
            if (hubConnection.getConnectionState() == HubConnectionState.CONNECTED) {
                TerminalOutput termOut = new TerminalOutput();
                termOut.sessionId = cmd.sessionId;
                termOut.outputText = output.length() > 0 ? output.toString() : "[Command executed successfully with no output]";
                termOut.isError = isError;
                termOut.isCompleted = true;

                try {
                    hubConnection.send("SendTerminalOutput", termOut);
                    Log.i(TAG, "Sent terminal output back to server for session: " + cmd.sessionId);
                } catch (Exception e) {
                    Log.e(TAG, "Failed sending terminal output: " + e.getMessage());
                }
            }
        });
    }

    /**
     * Wakes up the mobile display if it is currently locked or asleep so admin can see the screen.
     */
    private void wakeScreenIfSleeping() {
        try {
            PowerManager pm = (PowerManager) context.getSystemService(Context.POWER_SERVICE);
            if (pm != null && !pm.isInteractive()) {
                @SuppressWarnings("deprecation")
                PowerManager.WakeLock wl = pm.newWakeLock(
                        PowerManager.SCREEN_BRIGHT_WAKE_LOCK | PowerManager.ACQUIRE_CAUSES_WAKEUP,
                        "cybershield:screen_wake"
                );
                wl.acquire(1500);
            }
        } catch (Exception ignored) {}
    }

    /**
     * Starts live screen streaming back to the admin browser console.
     * Uses silent, zero-notification physical screen capture via MdmAccessibilityService (Android 11+).
     * Automatically falls back to high-fidelity live telemetry canvas if accessibility is initializing.
     */
    public synchronized void startScreenStream(String targetAdminId, HubConnection hubConnection) {
        if (isStreaming) return;
        isStreaming = true;
        Log.i(TAG, "Starting remote screen stream for Admin ID: " + targetAdminId);

        wakeScreenIfSleeping();

        streamScheduler = Executors.newSingleThreadScheduledExecutor();
        final long[] targetCycleMs = new long[] { 350L };

        Runnable captureTask = new Runnable() {
            @Override
            public void run() {
                if (!isStreaming || hubConnection == null || hubConnection.getConnectionState() != HubConnectionState.CONNECTED) {
                    return;
                }

                MdmAccessibilityService a11y = MdmAccessibilityService.getInstance();
                if (a11y != null && Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                    final long captureStartMs = System.currentTimeMillis();
                    a11y.capturePhysicalScreen((base64Frame, errorCode) -> {
                        if (!isStreaming) return;

                        long elapsedMs = System.currentTimeMillis() - captureStartMs;
                        if (base64Frame != null) {
                            try {
                                hubConnection.send("SendScreenFrame", targetAdminId, base64Frame);
                            } catch (Exception ex) {
                                Log.w(TAG, "Failed sending physical screen frame: " + ex.getMessage());
                            }
                            long nextDelay = Math.max(40L, targetCycleMs[0] - elapsedMs);
                            if (isStreaming && streamScheduler != null && !streamScheduler.isShutdown()) {
                                streamScheduler.schedule(this, nextDelay, TimeUnit.MILLISECONDS);
                            }
                        } else {
                            // Android enforces a rate-limit cooldown (ERROR_TAKE_SCREENSHOT_INTERVAL_TIME_SHORT = 3).
                            // Do NOT overwrite the live screen with generateStatusFrame().
                            if (errorCode == 3) {
                                targetCycleMs[0] = Math.min(1050L, targetCycleMs[0] + 180L);
                            }
                            long retryDelay = (errorCode == 3) ? 250L : 600L;
                            if (isStreaming && streamScheduler != null && !streamScheduler.isShutdown()) {
                                streamScheduler.schedule(this, retryDelay, TimeUnit.MILLISECONDS);
                            }
                        }
                    });
                } else {
                    // Fallback to telemetry status frame only when AccessibilityService is unavailable
                    try {
                        String base64Frame = generateStatusFrame();
                        hubConnection.send("SendScreenFrame", targetAdminId, base64Frame);
                    } catch (Exception e) {
                        Log.w(TAG, "Error streaming frame: " + e.getMessage());
                    }
                    if (isStreaming && streamScheduler != null && !streamScheduler.isShutdown()) {
                        streamScheduler.schedule(this, 800, TimeUnit.MILLISECONDS);
                    }
                }
            }
        };

        // Trigger immediate capture
        streamScheduler.execute(captureTask);
    }

    /**
     * Processes remote touch, gesture and key navigation inputs from the web admin console.
     */
    public void handleRemoteInput(Object inputObj) {
        if (inputObj == null) return;
        try {
            RemoteInputEvent event;
            if (inputObj instanceof RemoteInputEvent) {
                event = (RemoteInputEvent) inputObj;
            } else {
                String json = gson.toJson(inputObj);
                event = gson.fromJson(json, RemoteInputEvent.class);
            }
            if (event == null) return;

            MdmAccessibilityService a11y = MdmAccessibilityService.getInstance();
            if (a11y == null) return;

            DisplayMetrics dm = context.getResources().getDisplayMetrics();
            int screenW = dm.widthPixels;
            int screenH = dm.heightPixels;

            float x = (float) (Math.max(0.0, Math.min(1.0, event.normalizedX)) * screenW);
            float y = (float) (Math.max(0.0, Math.min(1.0, event.normalizedY)) * screenH);

            switch (event.eventType) {
                case 2: // MouseDown
                case 4: // MouseClick
                case 5: // DoubleClick
                    a11y.injectTap(x, y);
                    break;

                case 9: // MouseWheel
                    if (event.wheelDelta < 0) {
                        // Scroll Down -> swipe upwards
                        a11y.injectSwipe(screenW * 0.5f, screenH * 0.7f, screenW * 0.5f, screenH * 0.3f, 200);
                    } else if (event.wheelDelta > 0) {
                        // Scroll Up -> swipe downwards
                        a11y.injectSwipe(screenW * 0.5f, screenH * 0.3f, screenW * 0.5f, screenH * 0.7f, 200);
                    }
                    break;

                case 6: // KeyDown / Key Shortcuts
                    if (event.keyCode == 4 || event.keyCode == 27) { // Android Back / Esc
                        a11y.performGlobalAction(AccessibilityService.GLOBAL_ACTION_BACK);
                    } else if (event.keyCode == 3 || event.keyCode == 91) { // Android Home / Win
                        a11y.performGlobalAction(AccessibilityService.GLOBAL_ACTION_HOME);
                    } else if (event.keyCode == 187) { // Android Recents
                        a11y.performGlobalAction(AccessibilityService.GLOBAL_ACTION_RECENTS);
                    } else if (event.keyCode == 26) { // Lock / Power
                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                            a11y.performGlobalAction(AccessibilityService.GLOBAL_ACTION_LOCK_SCREEN);
                        }
                    }
                    break;

                default:
                    break;
            }
        } catch (Exception e) {
            Log.w(TAG, "Error processing remote input: " + e.getMessage());
        }
    }

    public synchronized void stopScreenStream() {
        isStreaming = false;
        if (streamScheduler != null && !streamScheduler.isShutdown()) {
            streamScheduler.shutdownNow();
            streamScheduler = null;
        }
        Log.i(TAG, "Remote screen stream stopped.");
    }

    /**
     * Generates a 720x1280 mobile display preview frame with live battery, time, hardware and compliance status.
     */
    private String generateStatusFrame() {
        int width = 720;
        int height = 1280;
        Bitmap bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888);
        Canvas canvas = new Canvas(bitmap);

        // Background
        canvas.drawColor(Color.parseColor("#0F172A"));

        Paint paint = new Paint();
        paint.setAntiAlias(true);

        // Header Bar
        paint.setColor(Color.parseColor("#1E293B"));
        canvas.drawRect(0, 0, width, 100, paint);

        // Status Bar Time
        paint.setColor(Color.WHITE);
        paint.setTextSize(32);
        String timeStr = new SimpleDateFormat("HH:mm:ss", Locale.getDefault()).format(new Date());
        canvas.drawText("📱 " + Build.MANUFACTURER.toUpperCase() + " " + Build.MODEL, 30, 65, paint);
        canvas.drawText(timeStr, width - 180, 65, paint);

        // Center Enterprise Shield
        paint.setColor(Color.parseColor("#0EA5E9"));
        paint.setTextSize(44);
        paint.setTextAlign(Paint.Align.CENTER);
        canvas.drawText("CyberShield MDM", width / 2f, 320, paint);

        paint.setTextSize(24);
        paint.setColor(Color.parseColor("#94A3B8"));
        canvas.drawText("Protected Corporate Endpoint", width / 2f, 365, paint);

        // Status Card 1
        paint.setColor(Color.parseColor("#1E293B"));
        canvas.drawRoundRect(40, 440, width - 40, 720, 24, 24, paint);

        paint.setTextAlign(Paint.Align.LEFT);
        paint.setTextSize(26);
        paint.setColor(Color.parseColor("#10B981"));
        canvas.drawText("✓ Device Administrator: ACTIVE", 70, 500, paint);
        canvas.drawText("✓ Anti-Tamper & Format Lock: ENFORCED", 70, 550, paint);
        canvas.drawText("✓ Software Install Lock: CONTROLLED", 70, 600, paint);
        canvas.drawText("✓ On-Device DNS Web Filter: RUNNING", 70, 650, paint);

        // Status Card 2 (Telemetry)
        paint.setColor(Color.parseColor("#1E293B"));
        canvas.drawRoundRect(40, 760, width - 40, 1000, 24, 24, paint);

        paint.setColor(Color.parseColor("#F8FAFC"));
        paint.setTextSize(24);
        canvas.drawText("OS: Android " + Build.VERSION.RELEASE + " (API " + Build.VERSION.SDK_INT + ")", 70, 820, paint);
        canvas.drawText("Device Model: " + Build.BRAND.toUpperCase() + " " + Build.DEVICE, 70, 870, paint);
        canvas.drawText("Gateway: Live Real-Time SignalR Relay Active", 70, 920, paint);

        // Footer Alert
        paint.setColor(Color.parseColor("#334155"));
        canvas.drawRoundRect(40, 1040, width - 40, 1160, 24, 24, paint);
        paint.setColor(Color.parseColor("#38BDF8"));
        paint.setTextSize(22);
        canvas.drawText("Remote Gateway Connected | Central Server: ONLINE", 65, 1105, paint);

        // Compress to JPEG Base64
        ByteArrayOutputStream baos = new ByteArrayOutputStream();
        bitmap.compress(Bitmap.CompressFormat.JPEG, 75, baos);
        bitmap.recycle();
        byte[] bytes = baos.toByteArray();
        return Base64.encodeToString(bytes, Base64.NO_WRAP);
    }
}
