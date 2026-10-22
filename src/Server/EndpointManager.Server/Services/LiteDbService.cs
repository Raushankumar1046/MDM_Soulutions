namespace EndpointManager.Server.Services;

using LiteDB;
using EndpointManager.Shared.Models;

public class EndpointDeviceRecord
{
    [BsonId]
    public string MachineName { get; set; } = string.Empty;
    public string LastConnectionId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public bool UsbStorageBlocked { get; set; }
    public bool SoftwareInstallDisabled { get; set; }
    public bool ExternalFileTransferRestricted { get; set; }
    public bool WebFilteringBlocked { get; set; }
    public List<string> WebFilterAllowedExceptions { get; set; } = new();
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? Country { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Isp { get; set; }
    public string NetworkLocation { get; set; } = "In Office";
    public bool IsOnline { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

public class PolicyAuditRecord
{
    [BsonId]
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MachineName { get; set; } = string.Empty;
    public string PolicyName { get; set; } = string.Empty;
    public bool Enforced { get; set; }
    public bool Success { get; set; }
    public string Details { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}

public class BlockedDomainRecord
{
    [BsonId]
    public string Domain { get; set; } = string.Empty;
    public string Category { get; set; } = "Custom";
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
}

public class LiteDbService : IDisposable
{
    private readonly LiteDatabase _db;
    public LiteDatabase Database => _db;
    private readonly ILiteCollection<EndpointDeviceRecord> _devices;
    private readonly ILiteCollection<PolicyAuditRecord> _auditLogs;
    private readonly ILiteCollection<BlockedDomainRecord> _blockedDomains;
    private readonly ILogger<LiteDbService> _logger;

    public LiteDbService(ILogger<LiteDbService> logger)
    {
        _logger = logger;
        
        var dbPath = Path.Combine(AppContext.BaseDirectory, "endpoints_data.db");
        var connectionString = new ConnectionString
        {
            Filename = dbPath,
            Connection = ConnectionType.Shared
        };

        _db = new LiteDatabase(connectionString);
        _devices = _db.GetCollection<EndpointDeviceRecord>("devices");
        _auditLogs = _db.GetCollection<PolicyAuditRecord>("audit_logs");
        _blockedDomains = _db.GetCollection<BlockedDomainRecord>("blocked_domains");
        
        _devices.EnsureIndex(x => x.MachineName, unique: true);
        _blockedDomains.EnsureIndex(x => x.Domain, unique: true);

        // Seed default entertainment/social domains if collection is fresh
        if (_blockedDomains.Count() == 0)
        {
            string[] defaultSeed = { "youtube.com", "facebook.com", "instagram.com", "twitter.com", "x.com", "tiktok.com", "netflix.com", "primevideo.com", "reddit.com", "twitch.tv" };
            foreach (var d in defaultSeed)
            {
                _blockedDomains.Insert(new BlockedDomainRecord { Domain = d, Category = "Default" });
            }
        }

        _logger.LogInformation("LiteDB Initialized at {Path}", dbPath);
    }

    public void UpsertHeartbeat(ClientHeartbeat heartbeat, string connectionId)
    {
        try
        {
            var existing = _devices.FindById(heartbeat.MachineName);
            if (existing == null)
            {
                var record = new EndpointDeviceRecord
                {
                    MachineName = heartbeat.MachineName,
                    LastConnectionId = connectionId,
                    UserName = heartbeat.UserName,
                    IpAddress = heartbeat.IpAddress,
                    OsVersion = heartbeat.OsVersion,
                    UsbStorageBlocked = heartbeat.UsbStorageBlocked,
                    SoftwareInstallDisabled = heartbeat.SoftwareInstallDisabled,
                    ExternalFileTransferRestricted = heartbeat.ExternalFileTransferRestricted,
                    WebFilteringBlocked = heartbeat.WebFilteringBlocked,
                    City = heartbeat.City,
                    Region = heartbeat.Region,
                    Country = heartbeat.Country,
                    Latitude = heartbeat.Latitude,
                    Longitude = heartbeat.Longitude,
                    Isp = heartbeat.Isp,
                    NetworkLocation = heartbeat.NetworkLocation ?? "In Office",
                    IsOnline = true,
                    LastSeenUtc = DateTime.UtcNow
                };
                _devices.Insert(record);
            }
            else
            {
                existing.LastConnectionId = connectionId;
                existing.UserName = heartbeat.UserName;
                existing.IpAddress = heartbeat.IpAddress;
                existing.OsVersion = heartbeat.OsVersion;
                existing.UsbStorageBlocked = heartbeat.UsbStorageBlocked;
                existing.SoftwareInstallDisabled = heartbeat.SoftwareInstallDisabled;
                existing.ExternalFileTransferRestricted = heartbeat.ExternalFileTransferRestricted;
                existing.WebFilteringBlocked = heartbeat.WebFilteringBlocked;
                existing.City = heartbeat.City ?? existing.City;
                existing.Region = heartbeat.Region ?? existing.Region;
                existing.Country = heartbeat.Country ?? existing.Country;
                existing.Latitude = heartbeat.Latitude ?? existing.Latitude;
                existing.Longitude = heartbeat.Longitude ?? existing.Longitude;
                existing.Isp = heartbeat.Isp ?? existing.Isp;
                existing.NetworkLocation = heartbeat.NetworkLocation ?? existing.NetworkLocation;
                existing.IsOnline = true;
                existing.LastSeenUtc = DateTime.UtcNow;
                _devices.Update(existing);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upsert heartbeat in LiteDB.");
        }
    }

    public void SetDeviceOfflineByConnectionId(string connectionId)
    {
        try
        {
            var device = _devices.FindOne(x => x.LastConnectionId == connectionId);
            if (device != null)
            {
                device.IsOnline = false;
                _devices.Update(device);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set device offline in LiteDB.");
        }
    }

    public List<EndpointDeviceRecord> GetAllDevices()
    {
        try
        {
            return _devices.FindAll().OrderByDescending(d => d.LastSeenUtc).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get all devices from LiteDB.");
            return new List<EndpointDeviceRecord>();
        }
    }

    public EndpointDeviceRecord? GetDevice(string machineName)
    {
        try
        {
            return _devices.FindById(machineName);
        }
        catch
        {
            return null;
        }
    }

    public bool DeleteDevice(string machineName)
    {
        try
        {
            return _devices.Delete(machineName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete device {Host} from LiteDB.", machineName);
            return false;
        }
    }

    public void LogAudit(string machineName, string policy, bool enforced, bool success, string details)
    {
        try
        {
            _auditLogs.Insert(new PolicyAuditRecord
            {
                MachineName = machineName,
                PolicyName = policy,
                Enforced = enforced,
                Success = success,
                Details = details,
                TimestampUtc = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to insert audit log in LiteDB.");
        }
    }

    public List<PolicyAuditRecord> GetRecentAuditLogs(int count = 50)
    {
        try
        {
            return _auditLogs.FindAll().OrderByDescending(a => a.TimestampUtc).Take(count).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get audit logs from LiteDB.");
            return new List<PolicyAuditRecord>();
        }
    }

    public List<string> GetBlockedDomains()
    {
        try
        {
            return _blockedDomains.FindAll()
                .OrderBy(d => d.Domain)
                .Select(d => d.Domain)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get blocked domains from LiteDB.");
            return new List<string> { "youtube.com", "facebook.com", "instagram.com", "netflix.com" };
        }
    }

    public bool AddBlockedDomain(string domain, string category = "Custom")
    {
        try
        {
            domain = CleanDomain(domain);
            if (string.IsNullOrWhiteSpace(domain)) return false;

            var existing = _blockedDomains.FindById(domain);
            if (existing != null) return true;

            _blockedDomains.Insert(new BlockedDomainRecord
            {
                Domain = domain,
                Category = category,
                AddedAtUtc = DateTime.UtcNow
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add blocked domain '{Domain}' to LiteDB.", domain);
            return false;
        }
    }

    public bool RemoveBlockedDomain(string domain)
    {
        try
        {
            domain = CleanDomain(domain);
            return _blockedDomains.Delete(domain);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete blocked domain '{Domain}' from LiteDB.", domain);
            return false;
        }
    }

    private static string CleanDomain(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var s = input.Trim().ToLowerInvariant();
        if (s.StartsWith("https://")) s = s.Substring(8);
        if (s.StartsWith("http://")) s = s.Substring(7);
        if (s.StartsWith("www.")) s = s.Substring(4);
        int slashIdx = s.IndexOf('/');
        if (slashIdx >= 0) s = s.Substring(0, slashIdx);
        int colonIdx = s.IndexOf(':');
        if (colonIdx >= 0) s = s.Substring(0, colonIdx);
        return s.Trim();
    }

    public List<string> GetDeviceAllowedExceptions(string machineName)
    {
        try
        {
            var device = _devices.FindById(machineName);
            return device?.WebFilterAllowedExceptions ?? new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get allowed exceptions for {Machine}", machineName);
            return new List<string>();
        }
    }

    public bool AddDeviceAllowedException(string machineName, string domain)
    {
        try
        {
            domain = CleanDomain(domain);
            if (string.IsNullOrWhiteSpace(domain)) return false;

            var device = _devices.FindById(machineName);
            if (device == null) return false;

            device.WebFilterAllowedExceptions ??= new List<string>();
            if (!device.WebFilterAllowedExceptions.Any(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase)))
            {
                device.WebFilterAllowedExceptions.Add(domain);
                _devices.Update(device);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add allowed exception for {Machine}", machineName);
            return false;
        }
    }

    public bool RemoveDeviceAllowedException(string machineName, string domain)
    {
        try
        {
            domain = CleanDomain(domain);
            var device = _devices.FindById(machineName);
            if (device == null || device.WebFilterAllowedExceptions == null) return false;

            int removed = device.WebFilterAllowedExceptions.RemoveAll(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                _devices.Update(device);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove allowed exception for {Machine}", machineName);
            return false;
        }
    }

    public List<string> GetEffectiveBlockedDomainsForDevice(string machineName)
    {
        var allBlocked = GetBlockedDomains();
        var allowed = GetDeviceAllowedExceptions(machineName);
        var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);

        return allBlocked.Where(b => !allowedSet.Contains(b)).ToList();
    }

    public void Dispose()
    {
        _db.Dispose();
    }
}
