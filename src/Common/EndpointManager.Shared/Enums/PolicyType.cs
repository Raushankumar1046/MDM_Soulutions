namespace EndpointManager.Shared.Enums;

public enum PolicyType
{
    UsbStorageBlock = 1,
    DisableSoftwareInstallation = 2,
    RestrictExternalFileTransfer = 3,
    LockWorkstation = 10,
    RestartLaptop = 11,
    SendNotification = 12
}
