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
    private readonly UdpDiscoveryClient _udpDiscoveryClient;
    private readonly IConfiguration _config;
    private HubConnection? _hubConnection;
    private string? _currentServerUrl;

    public Worker(
        ILogger<Worker> logger, 
        UsbPolicyManager usbPolicyManager,
        SoftwareInstallPolicyManager softwarePolicyManager,
        ExternalFileTransferPolicyManager fileTransferPolicyManager,
        UdpDiscoveryClient udpDiscoveryClient,
        IConfiguration config)
    {
        _logger = logger;
        _usbPolicyManager = usbPolicyManager;
        _softwarePolicyManager = softwarePolicyManager;
        _fileTransferPolicyManager = fileTransferPolicyManager;
        _udpDiscoveryClient = udpDiscoveryClient;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Endpoint Agent Service started on host: {Host}", Environment.MachineName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. Resolve Server URL (Auto-Discovery or Config)
                var configuredUrl = _config.GetValue<string>("ServerUrl")?.Trim();
                bool isAutoMode = string.IsNullOrEmpty(configuredUrl) || 
                                  configuredUrl.Equals("auto", StringComparison.OrdinalIgnoreCase);

                if (isAutoMode)
                {
                    _logger.LogInformation("Server URL set to 'auto'. Initiating UDP broadcast discovery on office network...");
                    _currentServerUrl = await _udpDiscoveryClient.DiscoverServerUrlAsync(stoppingToken);
                }
                else
                {
                    _currentServerUrl = configuredUrl;
                }

                _logger.LogInformation("Connecting to Central Server at: {Url}", _currentServerUrl);

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
                _logger.LogInformation("Successfully connected to Central Management Server!");

                // 3. Heartbeat loop while connected
                while (!stoppingToken.IsCancellationRequested && _hubConnection.State != HubConnectionState.Disconnected)
                {
                    if (_hubConnection.State == HubConnectionState.Connected)
                    {
                        try
                        {
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_hubConnection != null)
        {
            await _hubConnection.DisposeAsync();
        }
        await base.StopAsync(cancellationToken);
    }
}
