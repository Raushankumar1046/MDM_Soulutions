namespace EndpointManager.Client.Policies;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

[SupportedOSPlatform("windows")]
public class PrintPolicyManager : IDisposable
{
    private const string PolicyRegistryPath = @"SOFTWARE\CyberShieldMDM\Policies";
    private const string PolicyValueName = "SelectivePrintRestricted";
    private static readonly string FlagFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "CyberShieldMDM",
        "selective_print_policy.flag");

    private readonly ILogger<PrintPolicyManager> _logger;
    private CancellationTokenSource? _guardCts;
    private Task? _guardTask;
    private readonly object _lock = new();
    private volatile bool _isEnforced = false;

    private DateTime _lastNotificationTime = DateTime.MinValue;
    private string _lastBlockedApp = string.Empty;

    public event Action<string, string>? OnPrintBlocked;

    // Standalone PDF readers / desktop document viewers restricted from printing
    private static readonly HashSet<string> BlockedPdfViewers = new(StringComparer.OrdinalIgnoreCase)
    {
        "AcroRd32", "Acrobat", "AdobeCollabSync", "FoxitPDFReader", "FoxitReader",
        "FoxitPhantomPDF", "SumatraPDF", "NitroPDF", "Nitro_PDF", "PDFXEdit",
        "PDFXCview", "pdf24", "pdf24-Reader", "SodaPDF", "wpspdf", "glsrHost", "Microsoft.Reader"
    };

    // Authorized web browsers allowed to print normally
    private static readonly HashSet<string> WhitelistedBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "waterfox"
    };

    public PrintPolicyManager(ILogger<PrintPolicyManager> logger)
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

    public bool IsPrintRestrictionEnforced()
    {
        if (_isEnforced) return true;

        try
        {
            // 1. Check HKLM
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(PolicyRegistryPath, writable: false);
                if (key != null)
                {
                    var val = key.GetValue(PolicyValueName);
                    if (val is int intVal && intVal == 1)
                    {
                        _isEnforced = true;
                        return true;
                    }
                }
            }
            catch { }

            // 2. Check HKCU
            try
            {
                using var cuKey = Registry.CurrentUser.OpenSubKey(PolicyRegistryPath, writable: false);
                if (cuKey != null)
                {
                    var val = cuKey.GetValue(PolicyValueName);
                    if (val is int intVal && intVal == 1)
                    {
                        _isEnforced = true;
                        return true;
                    }
                }
            }
            catch { }

            // 3. Check Flag File fallback
            if (File.Exists(FlagFilePath))
            {
                var content = File.ReadAllText(FlagFilePath).Trim();
                if (content == "1")
                {
                    _isEnforced = true;
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read Selective Print policy state.");
            return _isEnforced;
        }
    }

    public bool SetPrintRestriction(bool restrict)
    {
        lock (_lock)
        {
            try
            {
                _isEnforced = restrict;

                // Write HKLM (machine-wide)
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using var key = baseKey.CreateSubKey(PolicyRegistryPath, writable: true);
                    key?.SetValue(PolicyValueName, restrict ? 1 : 0, RegistryValueKind.DWord);
                }
                catch { }

                // Write HKCU (user-level)
                try
                {
                    using var cuKey = Registry.CurrentUser.CreateSubKey(PolicyRegistryPath, writable: true);
                    cuKey?.SetValue(PolicyValueName, restrict ? 1 : 0, RegistryValueKind.DWord);
                }
                catch { }

                // Write Flag File
                try
                {
                    var dir = Path.GetDirectoryName(FlagFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.WriteAllText(FlagFilePath, restrict ? "1" : "0");
                }
                catch { }

                // Manage Guard Loop
                if (restrict)
                {
                    StartPrintGuard();
                }
                else
                {
                    StopPrintGuard();
                }

                _logger.LogInformation("Selective Print Guard policy successfully set to: {State}", restrict ? "ENFORCED" : "ALLOWED");
                Program.LogRuntime($"[POLICY] Selective Print Guard set to: {(restrict ? "ENFORCED (PDF Readers Blocked, Browsers Allowed)" : "ALLOWED")}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply Selective Print policy.");
                return false;
            }
        }
    }

    public void StartPrintGuard()
    {
        lock (_lock)
        {
            if (_guardTask != null && !_guardTask.IsCompleted) return;

            _guardCts = new CancellationTokenSource();
            var token = _guardCts.Token;

            _guardTask = Task.Run(async () =>
            {
                _logger.LogInformation("Selective Print Guard active: Standalone PDF readers blocked, Web browsers whitelisted.");
                Program.LogRuntime("[PRINT GUARD] Active: Standalone PDF readers blocked, Web browsers whitelisted.");

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // 1. Proactively suppress any open Print Dialogs in PDF Readers
                        SuppressPdfReaderPrintDialogs();

                        // 2. Intercept and purge any print jobs created by PDF Readers in Windows Spooler
                        InspectAndPurgeSpoolerJobs();
                    }
                    catch { }

                    try
                    {
                        await Task.Delay(350, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, token);
        }
    }

    public void StopPrintGuard()
    {
        lock (_lock)
        {
            try
            {
                _guardCts?.Cancel();
                _guardCts?.Dispose();
                _guardCts = null;
                _guardTask = null;
                _logger.LogInformation("Selective Print Guard stopped.");
                Program.LogRuntime("[PRINT GUARD] Guard stopped.");
            }
            catch { }
        }
    }

    private void SuppressPdfReaderPrintDialogs()
    {
        EnumWindows((hWnd, lParam) =>
        {
            try
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == 0) return true;

                string procName = string.Empty;
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    procName = p.ProcessName;
                }
                catch
                {
                    return true;
                }

                // If process is a blocked standalone PDF viewer
                if (BlockedPdfViewers.Contains(procName))
                {
                    var sbClass = new StringBuilder(256);
                    GetClassName(hWnd, sbClass, sbClass.Capacity);
                    string className = sbClass.ToString();

                    var sbTitle = new StringBuilder(512);
                    GetWindowText(hWnd, sbTitle, sbTitle.Capacity);
                    string windowTitle = sbTitle.ToString();

                    // Check if window is a standard Win32 dialog (#32770) or has Print in title
                    bool isPrintDialog = className.Equals("#32770", StringComparison.OrdinalIgnoreCase) ||
                                         windowTitle.IndexOf("Print", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         windowTitle.IndexOf("Printing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         windowTitle.IndexOf("Drucken", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         windowTitle.IndexOf("Imprimir", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (isPrintDialog)
                    {
                        // Instantly dismiss dialog
                        PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

                        TriggerBlockedNotification(procName, "Print Dialog Suppressed");
                    }
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);
    }

    private void InspectAndPurgeSpoolerJobs()
    {
        var printerNames = GetPrinterNames();
        foreach (var printerName in printerNames)
        {
            if (OpenPrinter(printerName, out IntPtr hPrinter, IntPtr.Zero))
            {
                try
                {
                    EnumJobs(hPrinter, 0, 20, 1, IntPtr.Zero, 0, out uint pcbNeeded, out _);
                    if (pcbNeeded > 0)
                    {
                        IntPtr pJobs = Marshal.AllocHGlobal((int)pcbNeeded);
                        try
                        {
                            if (EnumJobs(hPrinter, 0, 20, 1, pJobs, pcbNeeded, out _, out uint pcReturned))
                            {
                                int structSize = Marshal.SizeOf<JOB_INFO_1>();
                                for (int i = 0; i < pcReturned; i++)
                                {
                                    IntPtr current = IntPtr.Add(pJobs, i * structSize);
                                    var job = Marshal.PtrToStructure<JOB_INFO_1>(current);

                                    // Inspect source
                                    bool shouldBlock = ShouldBlockPrintJob(job);
                                    if (shouldBlock)
                                    {
                                        // Delete / Cancel print job immediately
                                        SetJob(hPrinter, job.JobId, 0, IntPtr.Zero, JOB_CONTROL_DELETE);

                                        string app = GetActiveOrRelatedPdfViewer() ?? "Standalone PDF Reader";
                                        TriggerBlockedNotification(app, $"Spooler Job Aborted: {job.pDocument}");
                                    }
                                }
                            }
                        }
                        catch { }
                        finally
                        {
                            Marshal.FreeHGlobal(pJobs);
                        }
                    }
                }
                catch { }
                finally
                {
                    ClosePrinter(hPrinter);
                }
            }
        }
    }

    private bool ShouldBlockPrintJob(JOB_INFO_1 job)
    {
        // 1. If foreground window is a whitelisted web browser -> ALLOW
        string fgProc = GetForegroundProcessName();
        if (WhitelistedBrowsers.Contains(fgProc))
        {
            return false;
        }

        // 2. If foreground window is a blocked PDF reader -> BLOCK
        if (BlockedPdfViewers.Contains(fgProc))
        {
            return true;
        }

        // 3. Inspect document title
        string doc = job.pDocument ?? string.Empty;
        foreach (var viewer in BlockedPdfViewers)
        {
            if (doc.IndexOf(viewer, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        // 4. If any blocked PDF reader process is currently running and active
        string? runningViewer = GetActiveOrRelatedPdfViewer();
        if (runningViewer != null && doc.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string GetForegroundProcessName()
    {
        try
        {
            IntPtr fgHwnd = GetForegroundWindow();
            if (fgHwnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(fgHwnd, out uint pid);
                if (pid > 0)
                {
                    using var p = Process.GetProcessById((int)pid);
                    return p.ProcessName;
                }
            }
        }
        catch { }
        return string.Empty;
    }

    private static string? GetActiveOrRelatedPdfViewer()
    {
        try
        {
            foreach (var viewerName in BlockedPdfViewers)
            {
                var procs = Process.GetProcessesByName(viewerName);
                if (procs.Length > 0)
                {
                    foreach (var p in procs) p.Dispose();
                    return viewerName;
                }
            }
        }
        catch { }
        return null;
    }

    private void TriggerBlockedNotification(string appName, string reason)
    {
        // Debounce alert within 3 seconds
        if ((DateTime.UtcNow - _lastNotificationTime).TotalSeconds < 3 && _lastBlockedApp == appName)
        {
            return;
        }

        _lastNotificationTime = DateTime.UtcNow;
        _lastBlockedApp = appName;

        _logger.LogWarning("Print attempt BLOCKED from PDF viewer '{App}'. Reason: {Reason}", appName, reason);
        Program.LogRuntime($"[PRINT BLOCKED] Application: {appName}. Reason: {reason}");

        OnPrintBlocked?.Invoke(appName, reason);

        // Notify the user on desktop
        NotifyUser(appName);
    }

    private void NotifyUser(string appName)
    {
        Task.Run(() =>
        {
            const string title = "CyberShield MDM - Data Security Alert";
            string message = $"⚠️ PRINT ACCESS DENIED\n\n" +
                             $"Printing directly from standalone PDF readers ({appName}) is strictly restricted by enterprise data security policy.\n\n" +
                             $"✅ To print this document, please open and view it inside Google Chrome or Microsoft Edge.";

            // 1. WTSSendMessage to active desktop session
            try
            {
                int session = WTSGetActiveConsoleSessionId();
                if (session >= 0)
                {
                    WTSSendMessage(
                        IntPtr.Zero,
                        session,
                        title,
                        title.Length * 2,
                        message,
                        message.Length * 2,
                        0x00000030 | 0x00040000 | 0x00010000, // MB_ICONWARNING | MB_TOPMOST | MB_SETFOREGROUND
                        0,
                        out _,
                        false);
                }
            }
            catch { }

            // 2. Fallback to MessageBoxW with MB_SERVICE_NOTIFICATION
            try
            {
                ShowMessageBox(IntPtr.Zero, message, title, 0x00000030 | 0x00040000 | 0x00010000 | 0x00200000);
            }
            catch { }
        });
    }

    private List<string> GetPrinterNames()
    {
        var printers = new List<string>();
        try
        {
            uint flags = PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS;
            EnumPrinters(flags, null, 4, IntPtr.Zero, 0, out uint pcbNeeded, out _);
            if (pcbNeeded == 0) return printers;

            IntPtr pPrinters = Marshal.AllocHGlobal((int)pcbNeeded);
            try
            {
                if (EnumPrinters(flags, null, 4, pPrinters, pcbNeeded, out _, out uint pcReturned))
                {
                    int structSize = Marshal.SizeOf<PRINTER_INFO_4>();
                    for (int i = 0; i < pcReturned; i++)
                    {
                        IntPtr current = IntPtr.Add(pPrinters, i * structSize);
                        var info = Marshal.PtrToStructure<PRINTER_INFO_4>(current);
                        if (!string.IsNullOrEmpty(info.pPrinterName))
                        {
                            printers.Add(info.pPrinterName);
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pPrinters);
            }
        }
        catch { }
        return printers;
    }

    public void Dispose()
    {
        StopPrintGuard();
    }

    #region Win32 P/Invoke APIs
    private const uint WM_CLOSE = 0x0010;
    private const uint PRINTER_ENUM_LOCAL = 0x00000002;
    private const uint PRINTER_ENUM_CONNECTIONS = 0x00000004;
    private const uint JOB_CONTROL_DELETE = 5;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int ShowMessageBox(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSSendMessage(
        IntPtr hServer,
        int SessionId,
        string pTitle,
        int TitleLength,
        string pMessage,
        int MessageLength,
        int Style,
        int Timeout,
        out int pResponse,
        bool bWait);

    [DllImport("kernel32.dll")]
    private static extern int WTSGetActiveConsoleSessionId();

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool EnumPrinters(
        uint flags,
        string? name,
        uint level,
        IntPtr pPrinterEnum,
        uint cbBuf,
        out uint pcbNeeded,
        out uint pcReturned);

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool EnumJobs(
        IntPtr hPrinter,
        uint firstJob,
        uint noJobs,
        uint level,
        IntPtr pJob,
        uint cbBuf,
        out uint pcbNeeded,
        out uint pcReturned);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool SetJob(
        IntPtr hPrinter,
        uint jobId,
        uint level,
        IntPtr pJob,
        uint command);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PRINTER_INFO_4
    {
        [MarshalAs(UnmanagedType.LPTStr)]
        public string pPrinterName;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string pServerName;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct JOB_INFO_1
    {
        public uint JobId;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string pPrinterName;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string pMachineName;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string pUserName;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string pDocument;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string pDatatype;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string pStatus;
        public uint Status;
        public uint Priority;
        public uint Position;
        public uint TotalPages;
        public uint PagesPrinted;
        public ushort wYear;
        public ushort wMonth;
        public ushort wDayOfWeek;
        public ushort wDay;
        public ushort wHour;
        public ushort wMinute;
        public ushort wSecond;
        public ushort wMilliseconds;
    }
    #endregion
}
