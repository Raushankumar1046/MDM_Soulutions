namespace EndpointManager.Shared.Models;

public class ClientHeartbeat
{
    public string ConnectionId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public bool UsbStorageBlocked { get; set; }
    public bool SoftwareInstallDisabled { get; set; }
    public bool ExternalFileTransferRestricted { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}
