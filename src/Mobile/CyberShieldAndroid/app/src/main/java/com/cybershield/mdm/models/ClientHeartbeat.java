package com.cybershield.mdm.models;

import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;
import java.util.TimeZone;

public class ClientHeartbeat {
    public String connectionId = "";
    public String machineName = "";
    public String userName = "Android User";
    public String ipAddress = "";
    public String osVersion = "";
    public boolean usbStorageBlocked = false;
    public boolean softwareInstallDisabled = false;
    public boolean externalFileTransferRestricted = false;
    public boolean webFilteringBlocked = false;
    public String city;
    public String region;
    public String country;
    public Double latitude;
    public Double longitude;
    public String isp;
    public String networkLocation = "In Office";
    public String timestampUtc;

    public ClientHeartbeat() {
        SimpleDateFormat sdf = new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'", Locale.US);
        sdf.setTimeZone(TimeZone.getTimeZone("UTC"));
        this.timestampUtc = sdf.format(new Date());
    }
}
