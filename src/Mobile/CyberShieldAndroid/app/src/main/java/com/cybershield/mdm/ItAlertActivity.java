package com.cybershield.mdm;

import android.app.Activity;
import android.app.AlertDialog;
import android.graphics.Color;
import android.graphics.drawable.ColorDrawable;
import android.os.Bundle;
import android.view.WindowManager;

/**
 * Translucent popup Activity to guarantee IT Admin Alert Messages appear
 * directly on the user's screen even when notifications are hidden/muted.
 */
public class ItAlertActivity extends Activity {
    public static final String EXTRA_MESSAGE = "it_alert_message";

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        try {
            getWindow().setBackgroundDrawable(new ColorDrawable(Color.TRANSPARENT));
            getWindow().addFlags(
                    WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED |
                    WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON |
                    WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON
            );
        } catch (Exception ignored) {}

        String msg = getIntent().getStringExtra(EXTRA_MESSAGE);
        if (msg == null || msg.trim().isEmpty()) {
            msg = "Corporate IT Compliance Notice";
        }

        new AlertDialog.Builder(this, android.R.style.Theme_DeviceDefault_Dialog_Alert)
                .setTitle("🛡️ CyberShield IT Admin Notice")
                .setMessage(msg)
                .setCancelable(false)
                .setPositiveButton("OK, Got It", (dialog, which) -> {
                    dialog.dismiss();
                    finish();
                })
                .setOnDismissListener(dialog -> finish())
                .show();
    }
}
