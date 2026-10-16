namespace EndpointManager.Server.Hubs;

using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
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

    private bool IsAuthorizedAdmin()
    {
        var httpCtx = Context.GetHttpContext();
        if (httpCtx == null) return false;
        var token = AuthService.ExtractBearerToken(httpCtx.Request);
        return _authService.ValidateToken(token, out _);
    }

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

        // Purge any stale/dead connection entries for this same host
        foreach (var kvp in ConnectedClients.ToArray())
        {
            if (kvp.Key != Context.ConnectionId && kvp.Value.MachineName.Equals(heartbeat.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                ConnectedClients.TryRemove(kvp.Key, out _);
            }
        }

        ConnectedClients[Context.ConnectionId] = heartbeat;

        // Persist to LiteDB
        _dbService.UpsertHeartbeat(heartbeat, Context.ConnectionId);

        _logger.LogInformation("Heartbeat received: {Host} [{IP}] ({Id}) | USB Blocked: {Usb} | Software Disabled: {Soft} | Transfer Restricted: {Trans}", 
            heartbeat.MachineName, heartbeat.IpAddress, Context.ConnectionId, heartbeat.UsbStorageBlocked, heartbeat.SoftwareInstallDisabled, heartbeat.ExternalFileTransferRestricted);

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

        _dbService.LogAudit(hostName, "PolicyExecution", false, result.Success, $"[{result.CommandId}] {result.Message}");

        return Task.CompletedTask;
    }

    // Helper to resolve active connectionId even if machine name or reconnected ID was passed
    private string ResolveTargetConnectionId(string targetId)
    {
        // If targetId matches machineName, find freshest connection
        var byHost = ConnectedClients
            .Where(c => c.Value.MachineName.Equals(targetId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Value.TimestampUtc)
            .FirstOrDefault();

        if (!string.IsNullOrEmpty(byHost.Key))
        {
            return byHost.Key;
        }

        if (ConnectedClients.ContainsKey(targetId))
        {
            return targetId;
        }

        return targetId;
    }

    // === Remote Terminal Relay ===
    public async Task SendTerminalCommand(string targetConnectionId, TerminalCommand command)
    {
        if (!IsAuthorizedAdmin())
        {
            _logger.LogWarning("Unauthorized attempt to dispatch Terminal Command.");
            throw new HubException("Unauthorized: Valid authentication token required.");
        }

        string resolvedId = ResolveTargetConnectionId(targetConnectionId);
        string hostName = ConnectedClients.TryGetValue(resolvedId, out var client) ? client.MachineName : resolvedId;
        _logger.LogInformation("Admin dispatching Terminal Command to [{Host}] ({Id}): {Cmd}", hostName, resolvedId, command.CommandText);
        _dbService.LogAudit(hostName, "RemoteTerminal", false, true, $"PowerShell executed: {command.CommandText}");

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
        if (!IsAuthorizedAdmin())
        {
            _logger.LogWarning("Unauthorized attempt to start Remote Screen Session.");
            throw new HubException("Unauthorized: Valid authentication token required.");
        }

        string resolvedId = ResolveTargetConnectionId(targetConnectionId);
        string hostName = ConnectedClients.TryGetValue(resolvedId, out var client) ? client.MachineName : resolvedId;
        _logger.LogInformation("Starting Remote Screen Session -> [{Host}] ({Id}) for Admin [{AdminId}]", hostName, resolvedId, Context.ConnectionId);
        _dbService.LogAudit(hostName, "RemoteScreen", true, true, $"Admin initiated live screen stream");

        await Clients.Client(resolvedId).StartScreenStream(Context.ConnectionId);
    }

    public async Task StopRemoteScreen(string targetConnectionId)
    {
        if (!IsAuthorizedAdmin())
        {
            _logger.LogWarning("Unauthorized attempt to stop Remote Screen Session.");
            throw new HubException("Unauthorized: Valid authentication token required.");
        }

        string resolvedId = ResolveTargetConnectionId(targetConnectionId);
        string hostName = ConnectedClients.TryGetValue(resolvedId, out var client) ? client.MachineName : resolvedId;
        _logger.LogInformation("Stopping Remote Screen Session -> [{Host}] ({Id})", hostName, resolvedId);
        _dbService.LogAudit(hostName, "RemoteScreen", false, true, $"Admin terminated live screen stream");

        await Clients.Client(resolvedId).StopScreenStream();
    }

    public async Task SendScreenFrame(string targetAdminConnectionId, string base64Frame)
    {
        await Clients.Client(targetAdminConnectionId).ReceiveScreenFrame(base64Frame);
    }

    public async Task SendRemoteInput(string targetConnectionId, RemoteInputEvent input)
    {
        if (!IsAuthorizedAdmin())
        {
            _logger.LogWarning("Unauthorized attempt to send Remote Input.");
            throw new HubException("Unauthorized: Valid authentication token required.");
        }

        string resolvedId = ResolveTargetConnectionId(targetConnectionId);
        await Clients.Client(resolvedId).ProcessRemoteInput(input);
    }
}
