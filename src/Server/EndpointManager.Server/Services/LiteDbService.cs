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

public class LiteDbService : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<EndpointDeviceRecord> _devices;
    private readonly ILiteCollection<PolicyAuditRecord> _auditLogs;
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
        
        _devices.EnsureIndex(x => x.MachineName, unique: true);
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

    public void Dispose()
    {
        _db.Dispose();
    }
}
