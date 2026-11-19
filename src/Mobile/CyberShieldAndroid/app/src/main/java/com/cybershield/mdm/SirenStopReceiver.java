package com.cybershield.mdm;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.widget.Toast;

public class SirenStopReceiver extends BroadcastReceiver {
    public static final String ACTION_STOP_SIREN = "com.cybershield.mdm.ACTION_STOP_SIREN";

    @Override
    public void onReceive(Context context, Intent intent) {
        PolicyEngine policyEngine = new PolicyEngine(context);
        policyEngine.stopLostPhoneAlarm();
        Toast.makeText(context, "🛑 Emergency Siren Silenced.", Toast.LENGTH_SHORT).show();
    }
}

