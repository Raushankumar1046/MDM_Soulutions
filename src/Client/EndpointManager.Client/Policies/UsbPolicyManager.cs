namespace EndpointManager.Client.Policies;

using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

[SupportedOSPlatform("windows")]
public class UsbPolicyManager
{
    private const string UsbStorRegistryPath = @"SYSTEM\CurrentControlSet\Services\USBSTOR";
    private const string ValueName = "Start";
    private readonly ILogger<UsbPolicyManager> _logger;

    public UsbPolicyManager(ILogger<UsbPolicyManager> logger)
    {
        _logger = logger;
    }

    public static bool HasAdministrativePrivilege()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public bool IsUsbStorageBlocked()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(UsbStorRegistryPath, writable: false);

            if (key == null) return false;

            var value = key.GetValue(ValueName);
            return value is int intVal && intVal == 4;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read USBSTOR policy state from registry.");
            return false;
        }
    }

    public bool SetUsbStorageBlockState(bool block)
    {
        if (!HasAdministrativePrivilege())
        {
            _logger.LogError("Access Denied: Administrative privileges are required to modify USB storage policies.");
            return false;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(UsbStorRegistryPath, writable: true);

            if (key == null)
            {
                _logger.LogError("Cannot apply policy: USBSTOR subkey not found at '{Path}'", UsbStorRegistryPath);
                return false;
            }

            // 4 = Disabled (Blocked), 3 = Demand Start (Allowed)
            int targetValue = block ? 4 : 3;
            key.SetValue(ValueName, targetValue, RegistryValueKind.DWord);

            _logger.LogInformation("Successfully updated USB policy. Blocked={BlockStatus} (Start={Value})", block, targetValue);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply USB storage policy state.");
            return false;
        }
    }
}
