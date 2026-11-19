namespace EndpointManager.Shared.Enums;

public enum PolicyType
{
    UsbStorageBlock = 1,
    DisableSoftwareInstallation = 2,
    RestrictExternalFileTransfer = 3,
    WebFilteringBlock = 4,
    SelectivePrintRestriction = 5,
    LockWorkstation = 10,
    RestartLaptop = 11,
    SendNotification = 12,
    ClearScreenLock = 15,
    BlockSamsungAccount = 16,
    PlayLostAlarm = 18,
    ForceGpsAlwaysOn = 19,
    HideSyncNotification = 21,
    RemoteWipe = 99
}
