using System.Runtime.InteropServices;
using EndpointManager.Client;
using EndpointManager.Client.Policies;
using EndpointManager.Client.Services;
using Microsoft.Extensions.Hosting.WindowsServices;

// Ensure unhandled exceptions are logged to disk for debugging
AppDomain.CurrentDomain.UnhandledException += (s, e) =>
{
    try
    {
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CyberShieldMDM");
        Directory.CreateDirectory(logDir);
        File.AppendAllText(Path.Combine(logDir, "client_crash.log"), $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] FATAL: {e.ExceptionObject}\n");
    }
    catch { }
};

// Single-Instance Mutex (prevents duplicate instances in active desktop session)
const string MutexName = @"Local\CyberShieldMDM_Client_Singleton";
bool isFirstInstance = false;
Mutex? singletonMutex = null;
try
{
    singletonMutex = new Mutex(true, MutexName, out isFirstInstance);
}
catch (AbandonedMutexException)
{
    // Previous instance crashed or terminated unexpectedly; this instance now owns the lock.
    isFirstInstance = true;
}
catch
{
    isFirstInstance = true;
}

if (!isFirstInstance)
{
    // Another instance is already actively managing this endpoint session. Exit cleanly.
    return;
}

// Safe console logger (silently ignores if no console handle is attached)
try { Console.Title = "CyberShield MDM - Endpoint Client Agent"; } catch { }
SafeLog("==========================================================");
SafeLog("       CyberShield MDM - Endpoint Client Agent");
SafeLog("==========================================================");

bool isAdmin = UsbPolicyManager.HasAdministrativePrivilege();
if (!isAdmin)
{
    SafeLog("[INFO] Running in Standard User Mode. For system policy modifications, run as Administrator.", ConsoleColor.Yellow);
}
else
{
    SafeLog("[OK] Running with Full Administrative Privileges.", ConsoleColor.Green);
}

// Log startup to persistent file
LogRuntime($"[STARTUP] CyberShield Client Agent started. OS: {Environment.OSVersion}, Machine: {Environment.MachineName}, User: {Environment.UserName}, Admin: {isAdmin}, PID: {Environment.ProcessId}");

// Ensure process is fully DPI-aware so screen captures and cursor clicks operate on true physical screen pixels
try
{
    SetProcessDPIAware();
}
catch { }

// Always set working directory and content root to the executable's directory
var baseDir = AppContext.BaseDirectory;
try
{
    Directory.SetCurrentDirectory(baseDir);
}
catch { }

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = baseDir
});

// Configures the service lifetime to Windows Service Control Manager (SCM)
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "EndpointManagerClient";
});

// Register Policy Managers
builder.Services.AddSingleton<UsbPolicyManager>();
builder.Services.AddSingleton<SoftwareInstallPolicyManager>();
builder.Services.AddSingleton<ExternalFileTransferPolicyManager>();
builder.Services.AddSingleton<WebFilteringPolicyManager>();
builder.Services.AddSingleton<PrintPolicyManager>();

// Register UDP Auto-Discovery Client
builder.Services.AddSingleton<UdpDiscoveryClient>();

// Register Geo-Location Service
builder.Services.AddSingleton<LocationService>();

// Register Remote Assist Services (Terminal & Screen Stream)
builder.Services.AddSingleton<RemoteTerminalService>();
builder.Services.AddSingleton<RemoteScreenService>();

// Register OTA Client Auto-Update Manager
builder.Services.AddSingleton<ClientUpdateManager>();

// Register Main SignalR / Endpoint Worker
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

[DllImport("user32.dll")]
static extern bool SetProcessDPIAware();

static void SafeLog(string message, ConsoleColor? color = null)
{
    try
    {
        if (color.HasValue) Console.ForegroundColor = color.Value;
        Console.WriteLine(message);
        if (color.HasValue) Console.ResetColor();
    }
    catch { }
}

public partial class Program
{
    public static void LogRuntime(string message)
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CyberShieldMDM");
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, "client_runtime.log");
            File.AppendAllText(logFile, $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
