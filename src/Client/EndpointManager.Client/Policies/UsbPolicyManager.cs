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
        int targetValue = block ? 4 : 3;

        // Try direct .NET Registry write
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(UsbStorRegistryPath, writable: true);

            if (key != null)
            {
                key.SetValue(ValueName, targetValue, RegistryValueKind.DWord);
                _logger.LogInformation("Successfully updated USB policy via Registry API. Blocked={BlockStatus} (Start={Value})", block, targetValue);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Direct registry write failed ({Msg}), attempting native reg.exe fallback...", ex.Message);
        }

        // Native reg.exe fallback
        bool regOk = RunRegCommand($"add \"HKLM\\{UsbStorRegistryPath}\" /v {ValueName} /t REG_DWORD /d {targetValue} /f");
        if (regOk)
        {
            _logger.LogInformation("Successfully updated USB policy via reg.exe. Blocked={BlockStatus} (Start={Value})", block, targetValue);
            return true;
        }

        if (!HasAdministrativePrivilege())
        {
            _logger.LogError("Access Denied: Administrative privileges are required to modify USB storage policies. Please run client as Administrator.");
        }
        else
        {
            _logger.LogError("Failed to update USB policy via both Registry API and reg.exe.");
        }
        return false;
    }

    private static bool RunRegCommand(string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(3000);
            return proc != null && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
