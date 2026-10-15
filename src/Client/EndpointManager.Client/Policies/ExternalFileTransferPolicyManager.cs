namespace EndpointManager.Client.Policies;

using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

[SupportedOSPlatform("windows")]
public class ExternalFileTransferPolicyManager
{
    private const string BaseRemovablePolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices";
    // GUID for Portable Devices / WPD (Smartphones, Cameras, MTP devices)
    private const string WpdClassGuid = "{e880d051-2490-4496-88a2-2b2b1a134768}";
    // GUID for Removable Disks (Flash drives, External SSDs)
    private const string RemovableDisksGuid = "{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}";

    private readonly ILogger<ExternalFileTransferPolicyManager> _logger;

    public ExternalFileTransferPolicyManager(ILogger<ExternalFileTransferPolicyManager> logger)
    {
        _logger = logger;
    }

    public static bool HasAdministrativePrivilege()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public bool IsExternalFileTransferRestricted()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var rootPolicyKey = baseKey.OpenSubKey(BaseRemovablePolicyPath, writable: false);
            if (rootPolicyKey == null) return false;

            // Check if Deny_Write is enabled on all removable devices
            var denyWrite = rootPolicyKey.GetValue("Deny_Write");
            if (denyWrite is int dw && dw == 1) return true;

            // Check WPD class
            using var wpdKey = rootPolicyKey.OpenSubKey(WpdClassGuid, writable: false);
            if (wpdKey != null)
            {
                var wpdDeny = wpdKey.GetValue("Deny_Write");
                if (wpdDeny is int wdw && wdw == 1) return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read external file transfer policy state.");
            return false;
        }
    }

    public bool SetExternalFileTransferRestricted(bool restrict)
    {
        if (!HasAdministrativePrivilege())
        {
            _logger.LogError("Access Denied: Administrative privileges required to configure file transfer policy.");
            return false;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            int flag = restrict ? 1 : 0;

            // 1. Root Removable Storage Devices policy
            using (var rootKey = baseKey.CreateSubKey(BaseRemovablePolicyPath, writable: true))
            {
                if (rootKey != null)
                {
                    rootKey.SetValue("Deny_Write", flag, RegistryValueKind.DWord);
                }
            }

            // 2. WPD (Phones / Media Transfer Protocol) write protection
            using (var wpdKey = baseKey.CreateSubKey($@"{BaseRemovablePolicyPath}\{WpdClassGuid}", writable: true))
            {
                if (wpdKey != null)
                {
                    wpdKey.SetValue("Deny_Write", flag, RegistryValueKind.DWord);
                }
            }

            // 3. Removable Disks write protection
            using (var diskKey = baseKey.CreateSubKey($@"{BaseRemovablePolicyPath}\{RemovableDisksGuid}", writable: true))
            {
                if (diskKey != null)
                {
                    diskKey.SetValue("Deny_Write", flag, RegistryValueKind.DWord);
                }
            }

            _logger.LogInformation("Successfully updated External File Transfer policy. Restricted={Restrict}", restrict);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply external file transfer policy.");
            return false;
        }
    }
}
