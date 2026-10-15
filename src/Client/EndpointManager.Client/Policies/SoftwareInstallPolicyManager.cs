namespace EndpointManager.Client.Policies;

using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

[SupportedOSPlatform("windows")]
public class SoftwareInstallPolicyManager
{
    private const string MsiServiceRegistryPath = @"SYSTEM\CurrentControlSet\Services\msiserver";
    private const string InstallerPolicyRegistryPath = @"SOFTWARE\Policies\Microsoft\Windows\Installer";
    private readonly ILogger<SoftwareInstallPolicyManager> _logger;

    public SoftwareInstallPolicyManager(ILogger<SoftwareInstallPolicyManager> logger)
    {
        _logger = logger;
    }

    public static bool HasAdministrativePrivilege()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public bool IsSoftwareInstallationDisabled()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            
            // Check Installer Policy
            using var policyKey = baseKey.OpenSubKey(InstallerPolicyRegistryPath, writable: false);
            if (policyKey != null)
            {
                var disableMsi = policyKey.GetValue("DisableMSI");
                if (disableMsi is int val && val >= 1) return true;
            }

            // Check msiserver service
            using var srvKey = baseKey.OpenSubKey(MsiServiceRegistryPath, writable: false);
            if (srvKey != null)
            {
                var startVal = srvKey.GetValue("Start");
                if (startVal is int startInt && startInt == 4) return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read software installation policy state.");
            return false;
        }
    }

    public bool SetSoftwareInstallationDisabled(bool disable)
    {
        if (!HasAdministrativePrivilege())
        {
            _logger.LogError("Access Denied: Administrative privileges required to configure software installation policy.");
            return false;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            // 1. Configure Windows Installer Group Policy (DisableMSI, DisableUserInstalls)
            using (var policyKey = baseKey.CreateSubKey(InstallerPolicyRegistryPath, writable: true))
            {
                if (policyKey != null)
                {
                    if (disable)
                    {
                        policyKey.SetValue("DisableMSI", 2, RegistryValueKind.DWord); // 2 = Completely Disabled
                        policyKey.SetValue("DisableUserInstalls", 1, RegistryValueKind.DWord); // 1 = Disallow User Installs
                    }
                    else
                    {
                        policyKey.SetValue("DisableMSI", 0, RegistryValueKind.DWord); // 0 = Enabled
                        policyKey.SetValue("DisableUserInstalls", 0, RegistryValueKind.DWord);
                    }
                }
            }

            // 2. Configure msiserver Service Start Type
            using (var srvKey = baseKey.OpenSubKey(MsiServiceRegistryPath, writable: true))
            {
                if (srvKey != null)
                {
                    // 4 = Disabled, 3 = Demand Start (Default Windows behavior)
                    int startType = disable ? 4 : 3;
                    srvKey.SetValue("Start", startType, RegistryValueKind.DWord);
                }
            }

            _logger.LogInformation("Successfully updated Software Installation policy. Disabled={Disabled}", disable);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply software installation policy state.");
            return false;
        }
    }
}
