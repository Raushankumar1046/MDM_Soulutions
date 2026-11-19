namespace EndpointManager.Server.Hubs;

using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using EndpointManager.Server.Services;
using EndpointManager.Shared.Contracts;
using EndpointManager.Shared.Models;

public class EndpointHub : Hub<IEndpointClientContract>
{
    private readonly ILogger<EndpointHub> _logger;
    private readonly LiteDbService _dbService;
    private readonly AuthService _authService;

    // Fast in-memory map of active connections: ConnectionId -> ClientHeartbeat
    public static readonly ConcurrentDictionary<string, ClientHeartbeat> ConnectedClients = new();

    public EndpointHub(ILogger<EndpointHub> logger, LiteDbService dbService, AuthService authService)
    {
        _logger = logger;
        _dbService = dbService;
        _authService = authService;
    }

    private bool IsAuthorizedAdmin(out string username, out UserPermissions permissions)
    {
        username = "Admin";
        permissions = UserPermissions.CreateSuperAdminDefaults();
        var httpCtx = Context.GetHttpContext();
        if (httpCtx == null) return false;
        var token = AuthService.ExtractBearerToken(httpCtx.Request);
        return _authService.ValidateToken(token, out username, out _, out permissions);
    }

    private bool IsAuthorizedAdmin(out string username) => IsAuthorizedAdmin(out username, out _);

    private bool IsAuthorizedAdmin() => IsAuthorizedAdmin(out _, out _);

    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("Agent connected: ConnectionId={Id}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (ConnectedClients.TryRemove(Context.ConnectionId, out var client))
        {
            _logger.LogWarning("Agent disconnected: {Host} ({Id})", client.MachineName, Context.ConnectionId);
        }
        _dbService.SetDeviceOfflineByConnectionId(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public Task SendHeartbeat(ClientHeartbeat heartbeat)
    {
        heartbeat.ConnectionId = Context.ConnectionId;
        heartbeat.TimestampUtc = DateTime.UtcNow;

        // Purge any stale/dead connection entries for this same host or hardware
        foreach (var kvp in ConnectedClients.ToArray())
        {
            bool sameMachine = kvp.Value.MachineName.Equals(heartbeat.MachineName, StringComparison.OrdinalIgnoreCase);
            bool sameHardware = !string.IsNullOrEmpty(heartbeat.HardwareId) && 
                                !string.IsNullOrEmpty(kvp.Value.HardwareId) && 
                                kvp.Value.HardwareId.Equals(heartbeat.HardwareId, StringComparison.OrdinalIgnoreCase);

            if (kvp.Key != Context.ConnectionId && (sameMachine || sameHardware))
            {
                ConnectedClients.TryRemove(kvp.Key, out _);
            }
        }

        // Ensure accurate NetworkLocation:
        // A device is ONLY "In Office" if its IP belongs to the official Office LAN subnet (192.168.68.0/22: 192.168.68.x - 192.168.71.x or localhost)
        // All personal mobile hotspots (10.x.x.x, 192.168.43.x, 172.20.10.x), home broadband, and 4G/5G cellular are "Work From Home"
        string devIp = (heartbeat.IpAddress ?? "").Trim();
        bool isOfficeSubnet = devIp.StartsWith("192.168.68.") || 
                              devIp.StartsWith("192.168.69.") || 
                              devIp.StartsWith("192.168.70.") || 
                              devIp.StartsWith("192.168.71.") || 
                              devIp == "127.0.0.1" || devIp == "::1";

        heartbeat.NetworkLocation = isOfficeSubnet ? "In Office" : "Work From Home";

        ConnectedClients[Context.ConnectionId] = heartbeat;

        // Persist to LiteDB
        _dbService.UpsertHeartbeat(heartbeat, Context.ConnectionId);

        _logger.LogInformation("Heartbeat received: {Host} [{IP}] ({Loc}) ({Id}) | USB Blocked: {Usb} | Software Disabled: {Soft} | Transfer Restricted: {Trans} | Web Filtered: {Web}", 
            heartbeat.MachineName, heartbeat.IpAddress, heartbeat.NetworkLocation, Context.ConnectionId, heartbeat.UsbStorageBlocked, heartbeat.SoftwareInstallDisabled, heartbeat.ExternalFileTransferRestricted, heartbeat.WebFilteringBlocked);

        // Check for fleet auto-update on reconnecting outdated clients (e.g. offline clients coming online)
        if (_dbService.GetAutoUpdateFleet() &&
            !string.Equals(heartbeat.ClientVersion, ClientUpdateService.ServerClientVersion, StringComparison.OrdinalIgnoreCase))
        {
            var connId = Context.ConnectionId;
            var host = heartbeat.MachineName;
            var httpCtx = Context.GetHttpContext();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2500);
                    var updateSvc = httpCtx?.RequestServices.GetService<ClientUpdateService>();
                    if (updateSvc != null)
                    {
                        var info = updateSvc.GetUpdateInfo();
                        if (info.FileSize > 0)
                        {
                            var cmd = new TerminalCommand
                            {
                                SessionId = Guid.NewGuid().ToString("N"),
                                CommandText = ClientUpdateService.BuildUniversalUpdateCommand(info.DownloadUrl, info.Version, info.Sha256)
                            };
                            await Clients.Client(connId).ExecuteTerminalCommand(cmd);
                            await Clients.Client(connId).ExecuteClientUpdate(info.DownloadUrl, info.Version, info.Sha256);
                            _logger.LogInformation("Auto-dispatched update v{Ver} to reconnecting client [{Host}]", info.Version, host);
                        }
                    }
                }
                catch { }
            });
        }

        return Task.CompletedTask;
    }

    public Task ReportExecutionResult(PolicyExecutionResult result)
    {
        _logger.LogInformation("Command {Id} Result: Success={Success}, Msg={Msg}",
            result.CommandId, result.Success, result.Message);

        string hostName = "Unknown";
        if (ConnectedClients.TryGetValue(Context.ConnectionId, out var client))
        {
            hostName = client.MachineName;
        }

        string category = "Policy";
        if (result.Message.StartsWith("BLOCKED PRINT:", StringComparison.OrdinalIgnoreCase))
        {
            category = "PrintControl";
        }
        else if (result.Message.StartsWith("BLOCKED INSTALLER:", StringComparison.OrdinalIgnoreCase))
        {
            category = "SoftwareBlock";
        }

        _dbService.LogAudit(hostName, "PolicyExecution", false, result.Success, $"[{result.CommandId}] {result.Message}", actionBy: "ClientAgent", category: category);

        return Task.CompletedTask;
    }

    // Helper to resolve active connectionId even if machine name or reconnected ID was passed
    private string ResolveTargetConnectionId(string targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId)) return targetId;

        // 1. Direct match in active memory
        if (ConnectedClients.ContainsKey(targetId))
        {
            return targetId;
        }

        // 2. Direct match by MachineName in active memory
        var byHost = ConnectedClients
            .Where(c => c.Value.MachineName.Equals(targetId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Value.TimestampUtc)
            .FirstOrDefault();

        if (!string.IsNullOrEmpty(byHost.Key))
        {
            return byHost.Key;
        }

        // 3. Fallback: Lookup in LiteDB to find the machine name from a stale connection ID, then find active socket
        try
        {
            var dbDevice = _dbService.GetAllDevices().FirstOrDefault(d => 
                d.LastConnectionId == targetId || 
                d.MachineName.Equals(targetId, StringComparison.OrdinalIgnoreCase));

            if (dbDevice != null)
            {
                var activeMatch = ConnectedClients
                    .Where(c => c.Value.MachineName.Equals(dbDevice.MachineName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(c => c.Value.TimestampUtc)
                    .FirstOrDefault();

                if (!string.IsNullOrEmpty(activeMatch.Key))
                {
                    return activeMatch.Key;
                }
            }
        }
        catch { }

        return targetId;
    }

    // === Remote Terminal Relay ===
    public async Task SendTerminalCommand(string targetConnectionId, TerminalCommand command)
    {
        if (!IsAuthorizedAdmin(out var username, out var permissions) || !permissions.CanRemoteAccess)
        {
            _logger.LogWarning("Unauthorized attempt by '{User}' to dispatch Terminal Command without CanRemoteAccess permission.", username);
            throw new HubException("Access Denied: You do not have permission to use Remote Terminal.");
        }

        string resolvedId = ResolveTargetConnectionId(targetConnectionId);
        string hostName = ConnectedClients.TryGetValue(resolvedId, out var client) ? client.MachineName : resolvedId;
        _logger.LogInformation("User [{User}] dispatching Terminal Command to [{Host}] ({Id}): {Cmd}", username, hostName, resolvedId, command.CommandText);
        _dbService.LogAudit(hostName, "RemoteTerminal", false, true, $"PowerShell executed: {command.CommandText}", actionBy: username, category: "RemoteControl");

        await Clients.Client(resolvedId).ExecuteTerminalCommand(command);
    }

    public async Task SendTerminalOutput(TerminalOutput output)
    {
        // Broadcast output to admin browser sessions
        await Clients.All.ReceiveTerminalOutput(output);
    }

    // === Remote Screen Stream & Input Relay ===
    public async Task StartRemoteScreen(string targetConnectionId)
    {
        if (!IsAuthorizedAdmin(out var username, out var permissions) || !permissions.CanRemoteAccess)
        {
            _logger.LogWarning("Unauthorized attempt by '{User}' to start Remote Screen Session without CanRemoteAccess permission.", username);
            throw new HubException("Access Denied: You do not have permission to use Remote Screen Control.");
        }

        string resolvedId = ResolveTargetConnectionId(targetConnectionId);
        string hostName = ConnectedClients.TryGetValue(resolvedId, out var client) ? client.MachineName : resolvedId;
        _logger.LogInformation("Starting Remote Screen Session -> [{Host}] ({Id}) for User [{User}]", hostName, resolvedId, username);
        _dbService.LogAudit(hostName, "RemoteScreen", true, true, $"User '{username}' initiated live screen stream", actionBy: username, category: "RemoteControl");

        await Clients.Client(resolvedId).StartScreenStream(Context.ConnectionId);
    }

    public async Task StopRemoteScreen(string targetConnectionId)
    {
        if (!IsAuthorizedAdmin(out var username, out var permissions) || !permissions.CanRemoteAccess)
        {
            _logger.LogWarning("Unauthorized attempt by '{User}' to stop Remote Screen Session without CanRemoteAccess permission.", username);
            throw new HubException("Access Denied: You do not have permission to use Remote Screen Control.");
        }

        string resolvedId = ResolveTargetConnectionId(targetConnectionId);
        string hostName = ConnectedClients.TryGetValue(resolvedId, out var client) ? client.MachineName : resolvedId;
        _logger.LogInformation("Stopping Remote Screen Session -> [{Host}] ({Id}) for User [{User}]", hostName, resolvedId, username);
        _dbService.LogAudit(hostName, "RemoteScreen", false, true, $"User '{username}' terminated live screen stream", actionBy: username, category: "RemoteControl");

        await Clients.Client(resolvedId).StopScreenStream();
    }

    public async Task SendScreenFrame(string targetAdminConnectionId, string base64Frame)
    {
        await Clients.Client(targetAdminConnectionId).ReceiveScreenFrame(base64Frame);
    }

    public async Task SendRemoteInput(string targetConnectionId, RemoteInputEvent input)
    {
        if (!IsAuthorizedAdmin(out var username, out var permissions) || !permissions.CanRemoteAccess)
        {
            _logger.LogWarning("Unauthorized attempt by '{User}' to send Remote Input without CanRemoteAccess permission.", username);
            throw new HubException("Access Denied: You do not have permission to send remote inputs.");
        }

        string resolvedId = ResolveTargetConnectionId(targetConnectionId);
        await Clients.Client(resolvedId).ProcessRemoteInput(input);
    }
}
