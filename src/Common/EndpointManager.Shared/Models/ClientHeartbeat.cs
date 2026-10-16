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
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? Country { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Isp { get; set; }
    public string NetworkLocation { get; set; } = "In Office"; // "In Office" or "Remote / WFH"
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}
