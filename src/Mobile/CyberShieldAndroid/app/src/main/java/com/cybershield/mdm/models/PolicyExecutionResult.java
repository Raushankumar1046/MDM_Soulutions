package com.cybershield.mdm.models;

import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;
import java.util.TimeZone;

public class PolicyExecutionResult {
    public String commandId;
    public boolean success;
    public String message;
    public String executedAtUtc;

    public PolicyExecutionResult(String commandId, boolean success, String message) {
        this.commandId = commandId;
        this.success = success;
        this.message = message;
        SimpleDateFormat sdf = new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'", Locale.US);
        sdf.setTimeZone(TimeZone.getTimeZone("UTC"));
        this.executedAtUtc = sdf.format(new Date());
    }
}
