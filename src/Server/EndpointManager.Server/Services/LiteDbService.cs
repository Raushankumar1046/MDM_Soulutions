namespace EndpointManager.Server.Services;

using LiteDB;
using EndpointManager.Shared.Models;

public class EndpointDeviceRecord
{
    [BsonId]
    public string MachineName { get; set; } = string.Empty;
    public string? HardwareId { get; set; } // BIOS Serial Number (PC) or IMEI / Hardware Serial (Android)
    public string LastConnectionId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public bool UsbStorageBlocked { get; set; }
    public bool SoftwareInstallDisabled { get; set; }
    public bool ExternalFileTransferRestricted { get; set; }
    public bool WebFilteringBlocked { get; set; }
    public bool SelectivePrintRestricted { get; set; }
    public bool SyncNotificationHidden { get; set; }
    public string ClientVersion { get; set; } = "1.0.0";
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

    // === IT Asset & Inventory Metadata (Level 2 Enterprise Asset Registry) ===
    public string? AssetTag { get; set; } // e.g. HEXA-LP-012
    public string? AssetCategory { get; set; } // e.g. Laptop, Mobile, Printer, CCTV, Network Switch, Desktop
    public string? Department { get; set; } // e.g. Tech, Sales, HR, Finance, Operations
    public string? EmployeeId { get; set; } // e.g. EMP-1042
    public string? AssetStatus { get; set; } = "In-Use"; // In-Use, Spare / In Stock, Under Repair, Decommissioned
    public string? PurchaseDate { get; set; } // YYYY-MM-DD
    public string? WarrantyExpiry { get; set; } // YYYY-MM-DD
    public string? AssetNotes { get; set; } // Hardware specs, AMC, notes
}

public class PolicyAuditRecord
{
    [BsonId]
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MachineName { get; set; } = string.Empty;
    public string PolicyName { get; set; } = string.Empty;
    public string ActionBy { get; set; } = "System";
    public string Category { get; set; } = "Policy"; // Policy, Auth, WebFilter, UserManagement, Device, RemoteControl
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

public class RegisterAssetItemDto
{
    public string MachineName { get; set; } = string.Empty;
    public string? HardwareId { get; set; }
    public string? AssetTag { get; set; }
    public string? AssetCategory { get; set; }
    public string? UserName { get; set; }
    public string? Department { get; set; }
    public string? EmployeeId { get; set; }
    public string? AssetStatus { get; set; }
    public string? PurchaseDate { get; set; }
    public string? WarrantyExpiry { get; set; }
    public string? AssetNotes { get; set; }
    public string? OsType { get; set; }
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
        _devices.EnsureIndex(x => x.HardwareId);
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
            EndpointDeviceRecord? existing = null;

            // 1. If HardwareId (BIOS Serial Number / IMEI) is provided, lookup by permanent HardwareId first
            if (!string.IsNullOrWhiteSpace(heartbeat.HardwareId))
            {
                existing = _devices.FindOne(x => x.HardwareId == heartbeat.HardwareId);

                // If found by HardwareId, but the hostname was renamed on the client!
                if (existing != null && !existing.MachineName.Equals(heartbeat.MachineName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Device with HardwareId '{HwId}' was renamed from '{OldName}' to '{NewName}'. Updating record in-place.",
                        heartbeat.HardwareId, existing.MachineName, heartbeat.MachineName);

                    // Delete the old record key from LiteDB (since MachineName is [BsonId])
                    _devices.Delete(existing.MachineName);

                    // Re-insert under new MachineName, retaining all policies and allowed exceptions
                    existing.MachineName = heartbeat.MachineName;
                    existing.HardwareId = heartbeat.HardwareId;
                    existing.LastConnectionId = connectionId;
                    existing.UserName = heartbeat.UserName;
                    existing.IpAddress = heartbeat.IpAddress;
                    existing.OsVersion = heartbeat.OsVersion;
                    existing.UsbStorageBlocked = heartbeat.UsbStorageBlocked;
                    existing.SoftwareInstallDisabled = heartbeat.SoftwareInstallDisabled;
                    existing.ExternalFileTransferRestricted = heartbeat.ExternalFileTransferRestricted;
                    existing.WebFilteringBlocked = heartbeat.WebFilteringBlocked;
                    existing.SelectivePrintRestricted = heartbeat.SelectivePrintRestricted;
                    existing.SyncNotificationHidden = heartbeat.SyncNotificationHidden;
                    existing.ClientVersion = heartbeat.ClientVersion ?? existing.ClientVersion;
                    existing.City = heartbeat.City ?? existing.City;
                    existing.Region = heartbeat.Region ?? existing.Region;
                    existing.Country = heartbeat.Country ?? existing.Country;
                    existing.Latitude = heartbeat.Latitude ?? existing.Latitude;
                    existing.Longitude = heartbeat.Longitude ?? existing.Longitude;
                    existing.Isp = heartbeat.Isp ?? existing.Isp;
                    existing.NetworkLocation = heartbeat.NetworkLocation ?? existing.NetworkLocation;
                    existing.IsOnline = true;
                    existing.LastSeenUtc = DateTime.UtcNow;

                    _devices.Insert(existing);
                    return;
                }
            }

            // 2. If not found by HardwareId, fallback to MachineName lookup
            if (existing == null)
            {
                existing = _devices.FindById(heartbeat.MachineName);
            }

            if (existing == null)
            {
                var record = new EndpointDeviceRecord
                {
                    MachineName = heartbeat.MachineName,
                    HardwareId = heartbeat.HardwareId,
                    LastConnectionId = connectionId,
                    UserName = heartbeat.UserName,
                    IpAddress = heartbeat.IpAddress,
                    OsVersion = heartbeat.OsVersion,
                    UsbStorageBlocked = heartbeat.UsbStorageBlocked,
                    SoftwareInstallDisabled = heartbeat.SoftwareInstallDisabled,
                    ExternalFileTransferRestricted = heartbeat.ExternalFileTransferRestricted,
                    WebFilteringBlocked = heartbeat.WebFilteringBlocked,
                    SelectivePrintRestricted = heartbeat.SelectivePrintRestricted,
                    SyncNotificationHidden = heartbeat.SyncNotificationHidden,
                    ClientVersion = heartbeat.ClientVersion ?? "1.0.0",
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
                if (!string.IsNullOrWhiteSpace(heartbeat.HardwareId))
                {
                    existing.HardwareId = heartbeat.HardwareId;
                }
                existing.LastConnectionId = connectionId;
                existing.UserName = heartbeat.UserName;
                existing.IpAddress = heartbeat.IpAddress;
                existing.OsVersion = heartbeat.OsVersion;
                existing.UsbStorageBlocked = heartbeat.UsbStorageBlocked;
                existing.SoftwareInstallDisabled = heartbeat.SoftwareInstallDisabled;
                existing.ExternalFileTransferRestricted = heartbeat.ExternalFileTransferRestricted;
                existing.WebFilteringBlocked = heartbeat.WebFilteringBlocked;
                existing.SelectivePrintRestricted = heartbeat.SelectivePrintRestricted;
                existing.SyncNotificationHidden = heartbeat.SyncNotificationHidden;
                existing.ClientVersion = heartbeat.ClientVersion ?? existing.ClientVersion;
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

    public bool UpdateAssetInfo(
        string machineName,
        string? assetTag,
        string? department,
        string? employeeId,
        string? assetStatus,
        string? purchaseDate,
        string? warrantyExpiry,
        string? assetNotes,
        string? assetCategory = null)
    {
        try
        {
            var device = _devices.FindById(machineName);
            if (device == null) return false;

            device.AssetTag = string.IsNullOrWhiteSpace(assetTag) ? null : assetTag.Trim();
            device.Department = string.IsNullOrWhiteSpace(department) ? null : department.Trim();
            device.EmployeeId = string.IsNullOrWhiteSpace(employeeId) ? null : employeeId.Trim();
            device.AssetStatus = string.IsNullOrWhiteSpace(assetStatus) ? "In-Use" : assetStatus.Trim();
            device.PurchaseDate = string.IsNullOrWhiteSpace(purchaseDate) ? null : purchaseDate.Trim();
            device.WarrantyExpiry = string.IsNullOrWhiteSpace(warrantyExpiry) ? null : warrantyExpiry.Trim();
            device.AssetNotes = string.IsNullOrWhiteSpace(assetNotes) ? null : assetNotes.Trim();
            if (!string.IsNullOrWhiteSpace(assetCategory)) device.AssetCategory = assetCategory.Trim();

            return _devices.Update(device);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update asset metadata for device {Host}", machineName);
            return false;
        }
    }

    public record RegisterAssetResult(bool Success, bool IsNew, string Message, EndpointDeviceRecord? Record);

    public class BulkRegisterResult
    {
        public int TotalProcessed { get; set; }
        public int CreatedCount { get; set; }
        public int UpdatedCount { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    public RegisterAssetResult RegisterOrUpdateAsset(
        string machineName,
        string? hardwareId,
        string? assetTag,
        string? userName,
        string? department,
        string? employeeId,
        string? assetStatus,
        string? purchaseDate,
        string? warrantyExpiry,
        string? assetNotes,
        string? osType = null,
        string? assetCategory = null)
    {
        try
        {
            string host = (machineName ?? "").Trim();
            string? hwId = string.IsNullOrWhiteSpace(hardwareId) ? null : hardwareId.Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                if (!string.IsNullOrWhiteSpace(hwId))
                    host = "ASSET-" + hwId;
                else
                    return new RegisterAssetResult(false, false, "Device Hostname or BIOS Serial/IMEI is required.", null);
            }

            EndpointDeviceRecord? existing = null;
            if (!string.IsNullOrWhiteSpace(hwId))
            {
                existing = _devices.FindOne(x => x.HardwareId == hwId);
            }
            if (existing == null)
            {
                existing = _devices.FindById(host);
            }

            string? finalCat = !string.IsNullOrWhiteSpace(assetCategory) ? assetCategory.Trim() : (!string.IsNullOrWhiteSpace(osType) ? osType.Trim() : null);

            if (existing != null)
            {
                // Update existing record
                if (!string.IsNullOrWhiteSpace(assetTag)) existing.AssetTag = assetTag.Trim();
                if (!string.IsNullOrWhiteSpace(department)) existing.Department = department.Trim();
                if (!string.IsNullOrWhiteSpace(employeeId)) existing.EmployeeId = employeeId.Trim();
                if (!string.IsNullOrWhiteSpace(userName) && (string.IsNullOrWhiteSpace(existing.UserName) || existing.UserName == "Unassigned" || existing.UserName == "SYSTEM"))
                    existing.UserName = userName.Trim();
                if (!string.IsNullOrWhiteSpace(assetStatus)) existing.AssetStatus = assetStatus.Trim();
                if (!string.IsNullOrWhiteSpace(purchaseDate)) existing.PurchaseDate = purchaseDate.Trim();
                if (!string.IsNullOrWhiteSpace(warrantyExpiry)) existing.WarrantyExpiry = warrantyExpiry.Trim();
                if (!string.IsNullOrWhiteSpace(assetNotes)) existing.AssetNotes = assetNotes.Trim();
                if (!string.IsNullOrWhiteSpace(finalCat)) existing.AssetCategory = finalCat;
                if (string.IsNullOrWhiteSpace(existing.HardwareId) && !string.IsNullOrWhiteSpace(hwId))
                    existing.HardwareId = hwId;

                _devices.Update(existing);
                return new RegisterAssetResult(true, false, $"Updated existing asset '{existing.MachineName}'.", existing);
            }
            else
            {
                // Create brand new pre-provisioned asset
                string osVer = "Windows (Pre-Provisioned)";
                string catUpper = (finalCat ?? "").ToUpperInvariant();
                if (catUpper.Contains("PRINTER") || catUpper.Contains("SCANNER"))
                    osVer = "Printer / Scanner";
                else if (catUpper.Contains("CCTV") || catUpper.Contains("CAMERA") || catUpper.Contains("NVR") || catUpper.Contains("DVR"))
                    osVer = "CCTV Surveillance";
                else if (catUpper.Contains("SWITCH") || catUpper.Contains("ROUTER") || catUpper.Contains("NETWORK") || catUpper.Contains("FIREWALL"))
                    osVer = "Network Hardware";
                else if (catUpper.Contains("SERVER") || catUpper.Contains("NAS"))
                    osVer = "Server Hardware";
                else if (catUpper.Contains("TV") || catUpper.Contains("DISPLAY"))
                    osVer = "Smart Display / TV";
                else if (catUpper.Contains("ANDROID") || catUpper.Contains("MOBILE") || catUpper.Contains("TABLET") || host.StartsWith("SM-", StringComparison.OrdinalIgnoreCase) || host.StartsWith("SAMSUNG", StringComparison.OrdinalIgnoreCase) || (hwId != null && hwId.Length == 15 && long.TryParse(hwId, out _)))
                    osVer = "Android (Pre-Provisioned)";
                else if (!string.IsNullOrWhiteSpace(finalCat) && !finalCat.Contains("Windows") && !finalCat.Contains("Laptop"))
                    osVer = $"{finalCat} (Hardware)";

                var newRecord = new EndpointDeviceRecord
                {
                    MachineName = host,
                    HardwareId = hwId,
                    AssetTag = string.IsNullOrWhiteSpace(assetTag) ? null : assetTag.Trim(),
                    AssetCategory = finalCat,
                    UserName = string.IsNullOrWhiteSpace(userName) ? "Unassigned" : userName.Trim(),
                    EmployeeId = string.IsNullOrWhiteSpace(employeeId) ? null : employeeId.Trim(),
                    Department = string.IsNullOrWhiteSpace(department) ? null : department.Trim(),
                    AssetStatus = string.IsNullOrWhiteSpace(assetStatus) ? "Spare / In Stock" : assetStatus.Trim(),
                    PurchaseDate = string.IsNullOrWhiteSpace(purchaseDate) ? null : purchaseDate.Trim(),
                    WarrantyExpiry = string.IsNullOrWhiteSpace(warrantyExpiry) ? null : warrantyExpiry.Trim(),
                    AssetNotes = string.IsNullOrWhiteSpace(assetNotes) ? null : assetNotes.Trim(),
                    OsVersion = osVer,
                    IpAddress = "Awaiting Enrollment",
                    NetworkLocation = "Store / Pre-Provisioned",
                    IsOnline = false,
                    LastSeenUtc = DateTime.UtcNow
                };

                _devices.Insert(newRecord);
                return new RegisterAssetResult(true, true, $"Successfully registered new asset '{host}'.", newRecord);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register or update asset '{Host}'", machineName);
            return new RegisterAssetResult(false, false, $"Error: {ex.Message}", null);
        }
    }

    public BulkRegisterResult BulkRegisterAssets(List<RegisterAssetItemDto> items, string actionBy = "Admin")
    {
        var result = new BulkRegisterResult();
        if (items == null || items.Count == 0) return result;

        result.TotalProcessed = items.Count;
        foreach (var item in items)
        {
            try
            {
                var res = RegisterOrUpdateAsset(
                    item.MachineName,
                    item.HardwareId,
                    item.AssetTag,
                    item.UserName,
                    item.Department,
                    item.EmployeeId,
                    item.AssetStatus,
                    item.PurchaseDate,
                    item.WarrantyExpiry,
                    item.AssetNotes,
                    item.OsType,
                    item.AssetCategory);

                if (res.Success)
                {
                    if (res.IsNew) result.CreatedCount++;
                    else result.UpdatedCount++;
                }
                else
                {
                    result.Errors.Add($"[{item.MachineName}]: {res.Message}");
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"[{item.MachineName}]: {ex.Message}");
            }
        }

        LogAudit("INVENTORY", "BulkImport", true, true, 
            $"Bulk imported {result.CreatedCount} new assets and updated {result.UpdatedCount} existing assets via CSV/Ledger import.", 
            actionBy, "Asset");

        return result;
    }

    public void LogAudit(
        string machineName, 
        string policy, 
        bool enforced, 
        bool success, 
        string details,
        string actionBy = "System",
        string category = "Policy")
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
                ActionBy = string.IsNullOrWhiteSpace(actionBy) ? "System" : actionBy.Trim(),
                Category = string.IsNullOrWhiteSpace(category) ? "Policy" : category.Trim(),
                TimestampUtc = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to insert audit log in LiteDB.");
        }
    }

    public List<PolicyAuditRecord> GetRecentAuditLogs(int count = 100)
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

    public List<PolicyAuditRecord> GetAllAuditLogs(int maxRecords = 2000)
    {
        try
        {
            return _auditLogs.FindAll().OrderByDescending(a => a.TimestampUtc).Take(maxRecords).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get all audit logs from LiteDB.");
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

    public bool GetAutoUpdateFleet()
    {
        try
        {
            var col = _db.GetCollection<BsonDocument>("system_settings");
            var doc = col.FindById("auto_update_fleet");
            return doc != null && doc.ContainsKey("value") && doc["value"].AsBoolean;
        }
        catch
        {
            return false;
        }
    }

    public void SetAutoUpdateFleet(bool enabled)
    {
        try
        {
            var col = _db.GetCollection<BsonDocument>("system_settings");
            var doc = new BsonDocument
            {
                ["_id"] = "auto_update_fleet",
                ["value"] = enabled,
                ["updatedAt"] = DateTime.UtcNow
            };
            col.Upsert(doc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set auto_update_fleet setting in LiteDB.");
        }
    }

    public void Dispose()
    {
        _db.Dispose();
    }
}
