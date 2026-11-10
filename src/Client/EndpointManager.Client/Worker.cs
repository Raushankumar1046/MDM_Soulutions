namespace EndpointManager.Client;

using System.Net;
using System.Net.Sockets;
using EndpointManager.Client.Policies;
using EndpointManager.Client.Services;
using EndpointManager.Shared.Contracts;
using EndpointManager.Shared.Enums;
using EndpointManager.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly UsbPolicyManager _usbPolicyManager;
    private readonly SoftwareInstallPolicyManager _softwarePolicyManager;
    private readonly ExternalFileTransferPolicyManager _fileTransferPolicyManager;
    private readonly WebFilteringPolicyManager _webPolicyManager;
    private readonly PrintPolicyManager _printPolicyManager;
    private readonly ClientUpdateManager _updateManager;
    private readonly UdpDiscoveryClient _udpDiscoveryClient;
    private readonly LocationService _locationService;
    private readonly RemoteTerminalService _terminalService;
    private readonly RemoteScreenService _screenService;
    private readonly IConfiguration _config;
    private HubConnection? _hubConnection;
    private string? _currentServerUrl;
    private string _currentLocationType = "In Office";

    public Worker(
        ILogger<Worker> logger, 
        UsbPolicyManager usbPolicyManager,
        SoftwareInstallPolicyManager softwarePolicyManager,
        ExternalFileTransferPolicyManager fileTransferPolicyManager,
        WebFilteringPolicyManager webPolicyManager,
        PrintPolicyManager printPolicyManager,
        ClientUpdateManager updateManager,
        UdpDiscoveryClient udpDiscoveryClient,
        LocationService locationService,
        RemoteTerminalService terminalService,
        RemoteScreenService screenService,
        IConfiguration config)
    {
        _logger = logger;
        _usbPolicyManager = usbPolicyManager;
        _softwarePolicyManager = softwarePolicyManager;
        _fileTransferPolicyManager = fileTransferPolicyManager;
        _webPolicyManager = webPolicyManager;
        _printPolicyManager = printPolicyManager;
        _updateManager = updateManager;
        _udpDiscoveryClient = udpDiscoveryClient;
        _locationService = locationService;
        _terminalService = terminalService;
        _screenService = screenService;
        _config = config;

        _softwarePolicyManager.OnInstallerBlocked += (procName, reason) =>
        {
            _logger.LogWarning("Software installation blocked: {Proc} ({Reason})", procName, reason);
            if (_hubConnection?.State == HubConnectionState.Connected)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var auditResult = new PolicyExecutionResult
                        {
                            CommandId = Guid.NewGuid(),
                            Success = false,
                            Message = $"BLOCKED INSTALLER: {procName} ({reason})"
                        };
                        await _hubConnection.InvokeAsync("ReportExecutionResult", auditResult);
                    }
                    catch { }
                });
            }
        };

        _printPolicyManager.OnPrintBlocked += (appName, reason) =>
        {
            _logger.LogWarning("Print attempt blocked from PDF viewer: {App} ({Reason})", appName, reason);
            if (_hubConnection?.State == HubConnectionState.Connected)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var auditResult = new PolicyExecutionResult
                        {
                            CommandId = Guid.NewGuid(),
                            Success = false,
                            Message = $"BLOCKED PRINT: {appName} ({reason})"
                        };
                        await _hubConnection.InvokeAsync("ReportExecutionResult", auditResult);
                    }
                    catch { }
                });
            }
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Endpoint Agent Service started on host: {Host}", Environment.MachineName);

        // Resume active installer interception if policy is active
        if (_softwarePolicyManager.IsSoftwareInstallationDisabled())
        {
            _softwarePolicyManager.StartInstallerWatcher();
        }

        // Resume active print guard if policy is active
        if (_printPolicyManager.IsPrintRestrictionEnforced())
        {
            _printPolicyManager.StartPrintGuard();
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. Resolve Best Server URL with Auto Dual-IP Fallback (Office LAN vs Remote / WFH)
                var (resolvedUrl, locationType) = await ResolveActiveServerEndpointAsync(stoppingToken);
                _currentServerUrl = resolvedUrl;
                _currentLocationType = locationType;

                _logger.LogInformation("Connecting to Central Server at: {Url} (Location Mode: {Location})", _currentServerUrl, _currentLocationType);
                Program.LogRuntime($"[CONNECT] Connecting to Central Server at: {_currentServerUrl} ({_currentLocationType})");

                // 2. Setup Hub Connection with Infinite Auto-Reconnect (Never drops permanently)
                _hubConnection = new HubConnectionBuilder()
                    .WithUrl(_currentServerUrl!)
                    .WithAutomaticReconnect(new InfiniteRetryPolicy())
                    .Build();

                _hubConnection.On<PolicyCommand>(nameof(IEndpointClientContract.ExecutePolicy), HandlePolicyCommandAsync);
                _hubConnection.On<TerminalCommand>(nameof(IEndpointClientContract.ExecuteTerminalCommand), cmd => _terminalService.ExecuteCommandAsync(cmd, _hubConnection));
                _hubConnection.On<string>(nameof(IEndpointClientContract.StartScreenStream), adminId => _screenService.StartScreenStream(adminId, _hubConnection));
                _hubConnection.On(nameof(IEndpointClientContract.StopScreenStream), () => _screenService.StopScreenStream());
                _hubConnection.On<RemoteInputEvent>(nameof(IEndpointClientContract.ProcessRemoteInput), input => _screenService.ProcessRemoteInput(input));
                _hubConnection.On<string, string, string>(nameof(IEndpointClientContract.ExecuteClientUpdate), (url, ver, sha) => _ = _updateManager.ApplyUpdateAsync(url, ver, sha, _currentServerUrl));

                _hubConnection.Reconnecting += error =>
                {
                    _logger.LogWarning("Connection interrupted: {Error}. Reconnecting automatically...", error?.Message);
                    Program.LogRuntime($"[RECONNECTING] Connection interrupted: {error?.Message}. Reconnecting automatically...");
                    return Task.CompletedTask;
                };

                _hubConnection.Reconnected += connectionId =>
                {
                    _logger.LogInformation("Connection re-established! ConnectionId: {Id}", connectionId);
                    Program.LogRuntime($"[RECONNECTED] Connection re-established. ConnectionId: {connectionId}");
                    return Task.CompletedTask;
                };

                // Connect with retry
                await _hubConnection.StartAsync(stoppingToken);
                _logger.LogInformation("Successfully connected to Central Management Server via {Location}!", _currentLocationType);
                Program.LogRuntime($"[ONLINE] Successfully connected to Central Management Server at {_currentServerUrl} ({_currentLocationType})!");

                // Check for automatic fleet update in background
                _ = _updateManager.CheckForAutomaticUpdateAsync(_currentServerUrl, stoppingToken);

                // 3. Heartbeat loop while connected
                while (!stoppingToken.IsCancellationRequested && _hubConnection.State != HubConnectionState.Disconnected)
                {
                    if (_hubConnection.State == HubConnectionState.Connected)
                    {
                        try
                        {
                            var loc = await _locationService.GetCurrentLocationAsync();

                            var heartbeat = new ClientHeartbeat
                            {
                                ConnectionId = _hubConnection.ConnectionId ?? string.Empty,
                                MachineName = Environment.MachineName,
                                UserName = Environment.UserName,
                                IpAddress = GetLocalIpAddress(),
                                OsVersion = Environment.OSVersion.ToString(),
                                UsbStorageBlocked = _usbPolicyManager.IsUsbStorageBlocked(),
                                SoftwareInstallDisabled = _softwarePolicyManager.IsSoftwareInstallationDisabled(),
                                ExternalFileTransferRestricted = _fileTransferPolicyManager.IsExternalFileTransferRestricted(),
                                WebFilteringBlocked = _webPolicyManager.IsWebFilteringBlocked(),
                                SelectivePrintRestricted = _printPolicyManager.IsPrintRestrictionEnforced(),
                                ClientVersion = ClientUpdateManager.CurrentVersion,
                                City = loc?.City,
                                Region = loc?.Region,
                                Country = loc?.Country,
                                Latitude = loc?.Latitude,
                                Longitude = loc?.Longitude,
                                Isp = loc?.Isp,
                                NetworkLocation = _currentLocationType,
                                TimestampUtc = DateTime.UtcNow
                            };

                            await _hubConnection.InvokeAsync("SendHeartbeat", heartbeat, stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("Heartbeat transmission error: {Msg}", ex.Message);
                        }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Connection failed: {Message}. Re-attempting in 5 seconds...", ex.Message);
                Program.LogRuntime($"[RETRY] Connection failed: {ex.Message}. Re-attempting in 5 seconds...");
                await Task.Delay(5000, stoppingToken);
            }
            finally
            {
                if (_hubConnection != null)
                {
                    try { await _hubConnection.DisposeAsync(); } catch { }
                    _hubConnection = null;
                }
            }
        }
    }

    private async Task HandlePolicyCommandAsync(PolicyCommand command)
    {
        _logger.LogInformation("Command Received -> Policy: {Type}, Value: {Action}", 
            command.Policy, command.EnableRestriction ? "ENFORCE" : "RELAX");

        var result = new PolicyExecutionResult
        {
            CommandId = command.CommandId
        };

        try
        {
            switch (command.Policy)
            {
                case PolicyType.UsbStorageBlock:
                    bool usbSuccess = _usbPolicyManager.SetUsbStorageBlockState(command.EnableRestriction);
                    result.Success = usbSuccess;
                    result.Message = usbSuccess 
                        ? $"USB Storage successfully {(command.EnableRestriction ? "BLOCKED" : "UNBLOCKED")}." 
                        : "Failed to update USB policy registry.";
                    break;

                case PolicyType.DisableSoftwareInstallation:
                    bool softSuccess = _softwarePolicyManager.SetSoftwareInstallationDisabled(command.EnableRestriction);
                    result.Success = softSuccess;
                    result.Message = softSuccess
                        ? $"Software Installation successfully {(command.EnableRestriction ? "DISABLED" : "ENABLED")}."
                        : "Failed to update Software Installation registry policies.";
                    break;

                case PolicyType.RestrictExternalFileTransfer:
                    bool transferSuccess = _fileTransferPolicyManager.SetExternalFileTransferRestricted(command.EnableRestriction);
                    result.Success = transferSuccess;
                    result.Message = transferSuccess
                        ? $"External File Transfer successfully {(command.EnableRestriction ? "RESTRICTED (Write Denied)" : "ALLOWED")}."
                        : "Failed to update Removable Storage write protection policies.";
                    break;

                case PolicyType.WebFilteringBlock:
                    IEnumerable<string>? customDomains = null;
                    IEnumerable<string>? allowedExceptions = null;

                    if (!string.IsNullOrWhiteSpace(command.Payload))
                    {
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(command.Payload);
                            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                if (doc.RootElement.TryGetProperty("Blocked", out var bProp) && bProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    customDomains = bProp.EnumerateArray()
                                        .Select(x => x.GetString())
                                        .Where(x => !string.IsNullOrEmpty(x))
                                        .Select(x => x!)
                                        .ToList();
                                }
                                if (doc.RootElement.TryGetProperty("Allowed", out var aProp) && aProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    allowedExceptions = aProp.EnumerateArray()
                                        .Select(x => x.GetString())
                                        .Where(x => !string.IsNullOrEmpty(x))
                                        .Select(x => x!)
                                        .ToList();
                                }
                            }
                        }
                        catch
                        {
                            // Fallback to comma/delimiter separated list
                            customDomains = command.Payload.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        }
                    }

                    bool webSuccess = _webPolicyManager.SetWebFilteringBlocked(command.EnableRestriction, customDomains, allowedExceptions);
                    result.Success = webSuccess;
                    result.Message = webSuccess
                        ? $"Web & Entertainment Filtering successfully {(command.EnableRestriction ? "ENFORCED (Domains Blocked)" : "RELAXED (Allowed)")}."
                        : "Failed to update Web Filtering policies.";
                    break;

                case PolicyType.SelectivePrintRestriction:
                    bool printSuccess = _printPolicyManager.SetPrintRestriction(command.EnableRestriction);
                    result.Success = printSuccess;
                    result.Message = printSuccess
                        ? $"Selective Print Guard successfully {(command.EnableRestriction ? "ENFORCED (PDF Readers Blocked, Browsers Allowed)" : "RELAXED (All Printing Allowed)")}."
                        : "Failed to update Print Security policies.";
                    break;
                case PolicyType.LockWorkstation:
                    Program.LogRuntime("[ACTION] Remote lock workstation command received from Admin.");
                    bool locked = false;

                    // 1. Native user32 LockWorkStation (locks interactive desktop screen immediately)
                    try
                    {
                        if (LockWorkStation())
                        {
                            locked = true;
                            Program.LogRuntime("[ACTION] LockWorkStation() succeeded.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.LogRuntime($"[WARN] LockWorkStation error: {ex.Message}");
                    }

                    // 2. rundll32 user32.dll,LockWorkStation fallback
                    if (!locked)
                    {
                        try
                        {
                            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "rundll32.exe",
                                Arguments = "user32.dll,LockWorkStation",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            });
                            proc?.WaitForExit(1000);
                            locked = true;
                            Program.LogRuntime("[ACTION] rundll32 LockWorkStation invoked.");
                        }
                        catch { }
                    }

                    // 3. Native tsdiscon.exe console session lock/disconnect
                    if (!locked)
                    {
                        try
                        {
                            var tsProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "tsdiscon.exe",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            });
                            tsProc?.WaitForExit(1000);
                            locked = true;
                            Program.LogRuntime("[ACTION] tsdiscon invoked.");
                        }
                        catch { }
                    }

                    // 4. Session 0 Windows Service disconnect fallback
                    if (!locked)
                    {
                        try
                        {
                            int activeSession = WTSGetActiveConsoleSessionId();
                            if (activeSession >= 0 && activeSession != -1 && activeSession != 0xFFFFFFFF)
                            {
                                locked = WTSDisconnectSession(IntPtr.Zero, activeSession, false);
                                Program.LogRuntime($"[ACTION] WTSDisconnectSession on session {activeSession} result: {locked}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Program.LogRuntime($"[WARN] WTSDisconnectSession error: {ex.Message}");
                        }
                    }

                    result.Success = locked;
                    result.Message = locked ? "Workstation screen locked successfully." : "Failed to lock workstation.";
                    break;

                case PolicyType.RestartLaptop:
                    Program.LogRuntime("[ACTION] Remote restart command initiated by IT Administrator. Scheduling shutdown.exe /r /t 5 /f...");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "shutdown.exe",
                        Arguments = "/r /t 5 /f /c \"IT Administrator has initiated a remote system restart.\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    result.Success = true;
                    result.Message = "System restart scheduled with 5s countdown.";
                    break;

                case PolicyType.SendNotification:
                    var messageText = string.IsNullOrWhiteSpace(command.Payload) ? "IT Security Notification from Administrator." : command.Payload;
                    Program.LogRuntime($"[ALERT] Notification message received from Admin: {messageText}");
                    _ = Task.Run(() =>
                    {
                        bool shown = false;
                        // 1. WTSSendMessage directly to active user desktop session
                        try
                        {
                            int activeSession = WTSGetActiveConsoleSessionId();
                            if (activeSession >= 0)
                            {
                                const string title = "CyberShield IT Management Alert";
                                shown = WTSSendMessage(
                                    IntPtr.Zero,
                                    activeSession,
                                    title,
                                    title.Length * 2,
                                    messageText,
                                    messageText.Length * 2,
                                    0x00000040 | 0x00040000 | 0x00010000, // MB_ICONINFORMATION | MB_TOPMOST | MB_SETFOREGROUND
                                    0,
                                    out _,
                                    false);
                            }
                        }
                        catch { }

                        // 2. Fallback to MessageBoxW with MB_SERVICE_NOTIFICATION (0x00200000) so it routes to active desktop
                        try
                        {
                            ShowMessageBox(IntPtr.Zero, messageText, "CyberShield IT Management Alert", 0x00000040 | 0x00040000 | 0x00010000 | 0x00200000);
                            shown = true;
                        }
                        catch { }

                        // 3. Fallback to msg.exe *
                        if (!shown)
                        {
                            try
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = "msg.exe",
                                    Arguments = $"* /time:120 \"{messageText}\"",
                                    UseShellExecute = false,
                                    CreateNoWindow = true
                                });
                            }
                            catch { }
                        }
                    });
                    result.Success = true;
                    result.Message = "Notification alert displayed on user screen.";
                    break;

                default:
                    result.Success = false;
                    result.Message = $"Policy type {command.Policy} is unsupported.";
                    break;
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Exception during execution: {ex.Message}";
        }

        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            await _hubConnection.InvokeAsync("ReportExecutionResult", result);
        }
    }

    private record ServerEndpointCandidate(string Url, string LocationType, bool IsLocal);

    private async Task<(string Url, string LocationType)> ResolveActiveServerEndpointAsync(CancellationToken stoppingToken)
    {
        var configuredRemoteUrl = _config.GetValue<string>("ServerUrl")?.Trim();
        var configuredLocalUrl = _config.GetValue<string>("LocalServerUrl")?.Trim();

        // 1. Fast UDP Auto-Discovery (Office Network broadcast)
        // If LocalServerUrl is "auto" or empty, always probe local network first
        bool isLocalAuto = string.IsNullOrEmpty(configuredLocalUrl) || configuredLocalUrl.Equals("auto", StringComparison.OrdinalIgnoreCase);
        if (isLocalAuto)
        {
            try
            {
                var discovered = await _udpDiscoveryClient.TryDiscoverServerUrlAsync(TimeSpan.FromSeconds(1.2), stoppingToken);
                if (!string.IsNullOrEmpty(discovered))
                {
                    var normalizedDiscovered = NormalizeHubUrl(discovered);
                    if (!string.IsNullOrEmpty(normalizedDiscovered))
                    {
                        _logger.LogInformation("Testing reachability for discovered local office server: {Url}...", normalizedDiscovered);
                        if (await IsTcpReachableAsync(normalizedDiscovered, TimeSpan.FromSeconds(1.0)))
                        {
                            _logger.LogInformation("Discovered local office server is reachable: {Url} (In Office)", normalizedDiscovered);
                            Program.LogRuntime($"[RESOLVED] Local office server discovered & reachable: {normalizedDiscovered} (In Office)");
                            return (normalizedDiscovered, "In Office");
                        }
                    }
                }
            }
            catch { }
        }
        else
        {
            // If LocalServerUrl was explicitly configured with an IP, test it directly
            var normalizedLocal = NormalizeHubUrl(configuredLocalUrl);
            if (!string.IsNullOrEmpty(normalizedLocal) && await IsTcpReachableAsync(normalizedLocal, TimeSpan.FromSeconds(1.0)))
            {
                _logger.LogInformation("Explicit local office server is reachable: {Url} (In Office)", normalizedLocal);
                Program.LogRuntime($"[RESOLVED] Explicit local server is reachable: {normalizedLocal} (In Office)");
                return (normalizedLocal, "In Office");
            }
        }

        // 2. Resolve Remote / Public Static IP (for Mobile Hotspot / Work From Home)
        const string DefaultRemoteUrl = "http://14.194.19.6:5000/hubs/endpoints";
        string remoteUrlToUse = !string.IsNullOrEmpty(configuredRemoteUrl) && !configuredRemoteUrl.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? configuredRemoteUrl
            : DefaultRemoteUrl;
        var normalizedRemote = NormalizeHubUrl(remoteUrlToUse);

        // 3. Known Office LAN Fallback (if UDP broadcast was blocked by office router/switch)
        const string DefaultOfficeLanUrl = "http://192.168.68.13:5000/hubs/endpoints";
        const string DefaultLocalhostUrl = "http://127.0.0.1:5000/hubs/endpoints";

        // 4. Test Remote Endpoint first (Fast connection for Mobile Hotspot / WFH)
        _logger.LogInformation("Testing reachability for remote server: {Url}...", normalizedRemote);
        if (await IsTcpReachableAsync(normalizedRemote, TimeSpan.FromSeconds(2.0)))
        {
            _logger.LogInformation("Remote server is reachable: {Url} (Work From Home)", normalizedRemote);
            Program.LogRuntime($"[RESOLVED] Remote server is reachable: {normalizedRemote} (Work From Home)");
            return (normalizedRemote, "Work From Home");
        }

        // 5. Test Office LAN Fallback (for In Office when UDP was blocked)
        _logger.LogInformation("Testing reachability for Office LAN fallback: {Url}...", DefaultOfficeLanUrl);
        if (await IsTcpReachableAsync(DefaultOfficeLanUrl, TimeSpan.FromSeconds(1.0)))
        {
            _logger.LogInformation("Office LAN fallback is reachable: {Url} (In Office)", DefaultOfficeLanUrl);
            Program.LogRuntime($"[RESOLVED] Office LAN fallback is reachable: {DefaultOfficeLanUrl} (In Office)");
            return (DefaultOfficeLanUrl, "In Office");
        }

        // 6. Test Localhost fallback (if testing directly on server)
        if (await IsTcpReachableAsync(DefaultLocalhostUrl, TimeSpan.FromSeconds(0.5)))
        {
            return (DefaultLocalhostUrl, "Localhost");
        }

        // Fallback: Default to Remote URL so SignalR can attempt HTTP/WebSocket handshake
        _logger.LogWarning("TCP probe didn't resolve an immediate endpoint. Falling back to remote: {Url}", normalizedRemote);
        return (normalizedRemote, "Work From Home");
    }

    private static async Task<bool> IsTcpReachableAsync(string url, TimeSpan timeout)
    {
        try
        {
            var uri = new Uri(url);
            using var cts = new CancellationTokenSource(timeout);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(uri.Host, uri.Port, cts.Token);
            return tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSDisconnectSession(IntPtr hServer, int sessionId, bool bWait);

    [System.Runtime.InteropServices.DllImport("wtsapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
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

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int WTSGetActiveConsoleSessionId();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern int ShowMessageBox(IntPtr hWnd, string text, string caption, uint type);

    private static string GetLocalIpAddress()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                {
                    return ip.ToString();
                }
            }
        }
        catch { }
        return "127.0.0.1";
    }

    private static string NormalizeHubUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        var s = url.Trim();
        s = s.Replace("/hubs.endpoints", "/hubs/endpoints");
        if (s.EndsWith("/")) s = s.TrimEnd('/');
        if (!s.EndsWith("/hubs/endpoints", StringComparison.OrdinalIgnoreCase))
        {
            s += "/hubs/endpoints";
        }
        return s;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_hubConnection != null)
        {
            await _hubConnection.DisposeAsync();
        }
        await base.StopAsync(cancellationToken);
    }
}

public class InfiniteRetryPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        // Immediate first retry, then 2s, then 4s for momentary Wi-Fi blips
        if (retryContext.PreviousRetryCount == 0) return TimeSpan.FromSeconds(0);
        if (retryContext.PreviousRetryCount == 1) return TimeSpan.FromSeconds(2);
        if (retryContext.PreviousRetryCount == 2) return TimeSpan.FromSeconds(4);

        // After 3 failed reconnect attempts (~8-10 seconds total), return null so the connection
        // cleanly resets to Disconnected. This allows the Worker loop to re-run
        // ResolveActiveServerEndpointAsync, enabling automatic seamless switching
        // between Office Wi-Fi and Mobile Hotspot without requiring any manual restart!
        return null;
    }
}
