namespace EndpointManager.Client.Policies;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

[SupportedOSPlatform("windows")]
public class SoftwareInstallPolicyManager : IDisposable
{
    private const string MsiServiceRegistryPath = @"SYSTEM\CurrentControlSet\Services\msiserver";
    private const string InstallerPolicyRegistryPath = @"SOFTWARE\Policies\Microsoft\Windows\Installer";
    private readonly ILogger<SoftwareInstallPolicyManager> _logger;

    private CancellationTokenSource? _watcherCts;
    private Task? _pollTask;
    private ManagementEventWatcher? _wmiWatcher;
    private readonly HashSet<int> _inspectedProcessIds = new();
    private readonly object _lock = new();

    public event Action<string, string>? OnInstallerBlocked;

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int ShowMessageBox(IntPtr hWnd, string text, string caption, uint type);

    // List of standard safe system processes to ignore immediately for zero overhead
    private static readonly HashSet<string> SystemWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "svchost", "csrss", "smss", "wininit", "winlogon", "services", "lsass",
        "taskmgr", "RuntimeBroker", "dwm", "ctfmon", "System", "Idle", "EndpointManager.Client",
        "EndpointManager.Server", "powershell", "cmd", "conhost", "devenv", "code", "SearchHost",
        "StartMenuExperienceHost", "ShellExperienceHost", "ApplicationFrameHost", "sihost",
        "fontdrvhost", "spoolsv", "audiodg", "wlanext", "dasHost", "smartscreen", "TrustedInstaller", "TiWorker",
        "updater", "GoogleUpdate", "MicrosoftEdgeUpdate", "OneDriveStandaloneUpdater", "DiscordUpdate"
    };

    // Regex pattern for installer process names (standalone new application setups/installers)
    private static readonly Regex InstallerNamePattern = new(
        @"(setup|install|installer|uninst|msiexec|patcher|vlc-.*win|chrome_installer|zoom_installer|7z\d+|winrar|teamviewer_setup|anydesk)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public SoftwareInstallPolicyManager(ILogger<SoftwareInstallPolicyManager> logger)
    {
        _logger = logger;
    }

    public static bool HasAdministrativePrivilege()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
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

            return _watcherCts != null && !_watcherCts.IsCancellationRequested;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read software installation policy state.");
            return false;
        }
    }

    public bool SetSoftwareInstallationDisabled(bool disable)
    {
        int msiVal = disable ? 2 : 0;
        int userInstVal = disable ? 1 : 0;
        int srvStart = disable ? 4 : 3;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            // 1. Configure Windows Installer Group Policy (DisableMSI, DisableUserInstalls, AlwaysInstallElevated)
            using (var policyKey = baseKey.CreateSubKey(InstallerPolicyRegistryPath, writable: true))
            {
                if (policyKey != null)
                {
                    policyKey.SetValue("DisableMSI", msiVal, RegistryValueKind.DWord);
                    policyKey.SetValue("DisableUserInstalls", userInstVal, RegistryValueKind.DWord);
                    policyKey.SetValue("AlwaysInstallElevated", 0, RegistryValueKind.DWord);
                }
            }

            // 2. Configure msiserver Service Start Type
            using (var srvKey = baseKey.OpenSubKey(MsiServiceRegistryPath, writable: true))
            {
                if (srvKey != null)
                {
                    srvKey.SetValue("Start", srvStart, RegistryValueKind.DWord);
                }
            }

            _logger.LogInformation("Successfully updated Software Installation registry policies. Disabled={Disabled}", disable);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Direct registry write failed ({Msg}), attempting native reg.exe fallback...", ex.Message);
            RunRegCommand($"add \"HKLM\\{InstallerPolicyRegistryPath}\" /v DisableMSI /t REG_DWORD /d {msiVal} /f");
            RunRegCommand($"add \"HKLM\\{InstallerPolicyRegistryPath}\" /v DisableUserInstalls /t REG_DWORD /d {userInstVal} /f");
            RunRegCommand($"add \"HKLM\\{InstallerPolicyRegistryPath}\" /v AlwaysInstallElevated /t REG_DWORD /d 0 /f");
            RunRegCommand($"add \"HKLM\\{MsiServiceRegistryPath}\" /v Start /t REG_DWORD /d {srvStart} /f");
        }

        // 3. Start or Stop Active Real-Time Process Watcher for EXE/NSIS/Inno/MSI installers
        if (disable)
        {
            StartInstallerWatcher();
        }
        else
        {
            StopInstallerWatcher();
        }

        return true;
    }

    public void StartInstallerWatcher()
    {
        lock (_lock)
        {
            if (_watcherCts != null && !_watcherCts.IsCancellationRequested)
            {
                return; // Already running
            }

            _watcherCts = new CancellationTokenSource();
            var token = _watcherCts.Token;

            _logger.LogInformation("Starting Real-Time Software Installation Interception Watcher...");

            // 1. Fast Background Polling Scanner (250ms loop)
            _pollTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        ScanAndInterceptRunningProcesses();
                    }
                    catch { }

                    try
                    {
                        await Task.Delay(250, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, token);

            // 2. WMI Process Start Trace for 0ms instantaneous intercept
            try
            {
                var query = new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace");
                _wmiWatcher = new ManagementEventWatcher(query);
                _wmiWatcher.EventArrived += OnProcessStartedWmi;
                _wmiWatcher.Start();
                _logger.LogInformation("WMI ProcessStartTrace active for instant installer blocking.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("WMI ProcessStartTrace could not be started ({Msg}). Relying on high-speed background process scanner.", ex.Message);
            }
        }
    }

    public void StopInstallerWatcher()
    {
        lock (_lock)
        {
            try
            {
                if (_wmiWatcher != null)
                {
                    _wmiWatcher.Stop();
                    _wmiWatcher.Dispose();
                    _wmiWatcher = null;
                }
            }
            catch { }

            try
            {
                if (_watcherCts != null)
                {
                    _watcherCts.Cancel();
                    _watcherCts.Dispose();
                    _watcherCts = null;
                }
            }
            catch { }

            _inspectedProcessIds.Clear();
            _logger.LogInformation("Real-Time Software Installation Interception Watcher STOPPED.");
        }
    }

    private void OnProcessStartedWmi(object sender, EventArrivedEventArgs e)
    {
        try
        {
            string? procName = e.NewEvent.Properties["ProcessName"].Value?.ToString();
            int procId = Convert.ToInt32(e.NewEvent.Properties["ProcessID"].Value);

            if (string.IsNullOrEmpty(procName)) return;
            string cleanName = procName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? procName[..^4] : procName;
            if (SystemWhitelist.Contains(cleanName) || SystemWhitelist.Contains(procName)) return;

            EvaluateAndKillIfInstaller(procId, procName);
        }
        catch { }
    }

    private void ScanAndInterceptRunningProcesses()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return;
        }

        foreach (var p in processes)
        {
            try
            {
                string name = p.ProcessName;
                if (SystemWhitelist.Contains(name)) continue;

                int id = p.Id;
                lock (_inspectedProcessIds)
                {
                    if (_inspectedProcessIds.Contains(id)) continue;
                }

                if (EvaluateAndKillIfInstaller(id, name, p))
                {
                    // Killed
                }
                else
                {
                    lock (_inspectedProcessIds)
                    {
                        if (_inspectedProcessIds.Count > 1000) _inspectedProcessIds.Clear();
                        _inspectedProcessIds.Add(id);
                    }
                }
            }
            catch { }
            finally
            {
                p.Dispose();
            }
        }
    }

    private bool EvaluateAndKillIfInstaller(int processId, string processName, Process? proc = null)
    {
        Process? targetProc = proc;
        bool shouldDispose = false;

        try
        {
            if (targetProc == null)
            {
                targetProc = Process.GetProcessById(processId);
                shouldDispose = true;
            }

            if (targetProc.HasExited) return false;
            if (processId == Environment.ProcessId) return false;
            if (processName.StartsWith("EndpointManager", StringComparison.OrdinalIgnoreCase)) return false;
            string baseName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
            if (SystemWhitelist.Contains(baseName) || SystemWhitelist.Contains(processName)) return false;

            bool isInstaller = false;
            string reason = string.Empty;

            // 1. Process Name Check
            string nameWithExe = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) 
                ? processName 
                : processName + ".exe";

            if (InstallerNamePattern.IsMatch(nameWithExe))
            {
                isInstaller = true;
                reason = $"Process name matched installer signature ({processName})";
            }

            // 2. Executable metadata inspection (FileVersionInfo)
            if (!isInstaller)
            {
                try
                {
                    string? path = targetProc.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        var info = FileVersionInfo.GetVersionInfo(path);
                        string desc = (info.FileDescription ?? string.Empty).ToLowerInvariant();
                        string prod = (info.ProductName ?? string.Empty).ToLowerInvariant();
                        string comments = (info.Comments ?? string.Empty).ToLowerInvariant();

                        if (desc.Contains("setup") || desc.Contains("installer") || desc.Contains("installation") ||
                            desc.Contains("nullsoft") || desc.Contains("inno setup") || desc.Contains("installshield") ||
                            prod.Contains("setup") || prod.Contains("installer") || comments.Contains("nullsoft") || comments.Contains("inno setup"))
                        {
                            isInstaller = true;
                            reason = $"Installer metadata detected: {info.FileDescription ?? info.ProductName} ({processName})";
                        }
                    }
                }
                catch { }
            }

            // 3. Window title check
            if (!isInstaller)
            {
                try
                {
                    string title = (targetProc.MainWindowTitle ?? string.Empty).ToLowerInvariant();
                    if (title.Contains(" setup") || title.StartsWith("setup ") || title.Contains("installer") ||
                        title.Contains("installation wizard") || title.Contains("installing "))
                    {
                        isInstaller = true;
                        reason = $"Installer window title detected: {targetProc.MainWindowTitle} ({processName})";
                    }
                }
                catch { }
            }

            if (isInstaller)
            {
                _logger.LogWarning("🚨 UNAUTHORIZED SOFTWARE INSTALLATION INTERCEPTED: {Reason}. Killing PID: {Pid}...", reason, processId);

                // Terminate installer process tree instantly
                try
                {
                    targetProc.Kill(entireProcessTree: true);
                }
                catch
                {
                    RunRegCommand($"taskkill /f /t /pid {processId}");
                }

                _logger.LogInformation("✅ Installer silently terminated: {ProcessName} (PID: {Pid})", processName, processId);

                // Notify server audit trail silently (No intrusive popup on client screen)
                OnInstallerBlocked?.Invoke(processName, reason);

                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (shouldDispose && targetProc != null)
            {
                targetProc.Dispose();
            }
        }
    }

    private static bool RunRegCommand(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {args}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
            return proc != null && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        StopInstallerWatcher();
    }
}
