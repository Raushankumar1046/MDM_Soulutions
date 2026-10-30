package com.cybershield.mdm.models;

import com.google.gson.annotations.SerializedName;

public class PolicyCommand {
    @SerializedName(value = "commandId", alternate = {"CommandId", "id", "Id"})
    public String commandId;

    @SerializedName(value = "policy", alternate = {"Policy", "policyType", "PolicyType"})
    public int policy; // 1 = UsbStorageBlock, 2 = DisableSoftware, 3 = RestrictTransfer, 4 = WebFilter, 10 = LockWorkstation, 11 = RestartLaptop, 12 = SendNotification, 99 = RemoteWipe

    @SerializedName(value = "enableRestriction", alternate = {"EnableRestriction", "enable", "Enable"})
    public boolean enableRestriction;

    @SerializedName(value = "payload", alternate = {"Payload", "data", "Data"})
    public String payload;

    @SerializedName(value = "issuedAtUtc", alternate = {"IssuedAtUtc"})
    public String issuedAtUtc;
}
