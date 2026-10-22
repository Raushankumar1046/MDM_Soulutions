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
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Endpoint Agent Service started on host: {Host}", Environment.MachineName);

        // Resume active installer interception if policy is active
        if (_softwarePolicyManager.IsSoftwareInstallationDisabled())
        {
            _softwarePolicyManager.StartInstallerWatcher();
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

                // 2. Setup Hub Connection
                _hubConnection = new HubConnectionBuilder()
                    .WithUrl(_currentServerUrl!)
                    .WithAutomaticReconnect(new[] 
                    {
                        TimeSpan.FromSeconds(0),
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(10),
                        TimeSpan.FromSeconds(20)
                    })
                    .Build();

                _hubConnection.On<PolicyCommand>(nameof(IEndpointClientContract.ExecutePolicy), HandlePolicyCommandAsync);
                _hubConnection.On<TerminalCommand>(nameof(IEndpointClientContract.ExecuteTerminalCommand), cmd => _terminalService.ExecuteCommandAsync(cmd, _hubConnection));
                _hubConnection.On<string>(nameof(IEndpointClientContract.StartScreenStream), adminId => _screenService.StartScreenStream(adminId, _hubConnection));
                _hubConnection.On(nameof(IEndpointClientContract.StopScreenStream), () => _screenService.StopScreenStream());
                _hubConnection.On<RemoteInputEvent>(nameof(IEndpointClientContract.ProcessRemoteInput), input => _screenService.ProcessRemoteInput(input));

                _hubConnection.Reconnecting += error =>
                {
                    _logger.LogWarning("Connection interrupted: {Error}. Reconnecting automatically...", error?.Message);
                    return Task.CompletedTask;
                };

                _hubConnection.Reconnected += connectionId =>
                {
                    _logger.LogInformation("Connection re-established! ConnectionId: {Id}", connectionId);
                    return Task.CompletedTask;
                };

                // Connect with retry
                await _hubConnection.StartAsync(stoppingToken);
                _logger.LogInformation("Successfully connected to Central Management Server via {Location}!", _currentLocationType);

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
                case PolicyType.LockWorkstation:
                    try { LockWorkStation(); } catch { }
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "rundll32.exe",
                            Arguments = "user32.dll,LockWorkStation",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                    }
                    catch { }
                    result.Success = true;
                    result.Message = "Workstation screen locked successfully.";
                    break;

                case PolicyType.RestartLaptop:
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
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            // 0x00000040 (MB_ICONINFORMATION) | 0x00040000 (MB_TOPMOST) | 0x00010000 (MB_SETFOREGROUND)
                            ShowMessageBox(IntPtr.Zero, messageText, "CyberShield IT Management Alert", 0x00000040 | 0x00040000 | 0x00010000);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to display MessageBox on client.");
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

        var candidates = new List<ServerEndpointCandidate>();

        // 1. If LocalServerUrl is explicitly configured with an IP, add it first
        if (!string.IsNullOrEmpty(configuredLocalUrl) && !configuredLocalUrl.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedLocal = NormalizeHubUrl(configuredLocalUrl);
            if (!string.IsNullOrEmpty(normalizedLocal))
            {
                candidates.Add(new ServerEndpointCandidate(normalizedLocal, "In Office", true));
            }
        }

        // 2. Try fast UDP Auto-Discovery (Office Network broadcast)
        if (string.IsNullOrEmpty(configuredLocalUrl) || configuredLocalUrl.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var discovered = await _udpDiscoveryClient.TryDiscoverServerUrlAsync(TimeSpan.FromSeconds(1.5), stoppingToken);
                if (!string.IsNullOrEmpty(discovered))
                {
                    var normalizedDiscovered = NormalizeHubUrl(discovered);
                    if (!candidates.Any(c => c.Url.Equals(normalizedDiscovered, StringComparison.OrdinalIgnoreCase)))
                    {
                        candidates.Add(new ServerEndpointCandidate(normalizedDiscovered, "In Office", true));
                    }
                }
            }
            catch { }
        }

        // 3. Add Remote / Public Static IP or Cloudflare URL
        if (!string.IsNullOrEmpty(configuredRemoteUrl) && !configuredRemoteUrl.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedRemote = NormalizeHubUrl(configuredRemoteUrl);
            if (!string.IsNullOrEmpty(normalizedRemote) && !candidates.Any(c => c.Url.Equals(normalizedRemote, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(new ServerEndpointCandidate(normalizedRemote, "Work From Home", false));
            }
        }

        // 4. Guaranteed Hardcoded Fallbacks (Ensures standalone executable NEVER fails even without config files)
        const string DefaultRemoteUrl = "http://14.194.19.6:5000/hubs/endpoints";
        const string DefaultOfficeLanUrl = "http://192.168.68.13:5000/hubs/endpoints";

        if (!candidates.Any(c => c.Url.Equals(DefaultOfficeLanUrl, StringComparison.OrdinalIgnoreCase)))
        {
            candidates.Add(new ServerEndpointCandidate(DefaultOfficeLanUrl, "In Office", true));
        }
        if (!candidates.Any(c => c.Url.Equals(DefaultRemoteUrl, StringComparison.OrdinalIgnoreCase)))
        {
            candidates.Add(new ServerEndpointCandidate(DefaultRemoteUrl, "Work From Home", false));
        }

        // Fast TCP probe to find the reachable endpoint without blocking
        foreach (var candidate in candidates)
        {
            _logger.LogInformation("Testing reachability for [{Location}] server: {Url}...", candidate.LocationType, candidate.Url);
            if (await IsTcpReachableAsync(candidate.Url, TimeSpan.FromSeconds(1.5)))
            {
                _logger.LogInformation("Endpoint is reachable: {Url} ({Location})", candidate.Url, candidate.LocationType);
                return (candidate.Url, candidate.LocationType);
            }
            else
            {
                _logger.LogWarning("Endpoint not responding: {Url}. Checking next candidate...", candidate.Url);
            }
        }

        // If none responded to quick TCP probe, return the first remote candidate to let SignalR try full HTTP negotiation
        var defaultCandidate = candidates.FirstOrDefault(c => !c.IsLocal) ?? candidates[0];
        return (defaultCandidate.Url, defaultCandidate.LocationType);
    }

    private static async Task<bool> IsTcpReachableAsync(string url, TimeSpan timeout)
    {
        try
        {
            var uri = new Uri(url);
            using var tcp = new TcpClient();
            var connectTask = tcp.ConnectAsync(uri.Host, uri.Port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(timeout));
            if (completed == connectTask && tcp.Connected)
            {
                return true;
            }
        }
        catch
        {
            // TCP connect failed or timed out
        }
        return false;
    }

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
