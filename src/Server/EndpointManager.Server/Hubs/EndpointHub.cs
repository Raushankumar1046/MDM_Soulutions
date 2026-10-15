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

    // Fast in-memory map of active connections: ConnectionId -> ClientHeartbeat
    public static readonly ConcurrentDictionary<string, ClientHeartbeat> ConnectedClients = new();

    public EndpointHub(ILogger<EndpointHub> logger, LiteDbService dbService)
    {
        _logger = logger;
        _dbService = dbService;
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
        ConnectedClients[Context.ConnectionId] = heartbeat;

        // Persist to LiteDB
        _dbService.UpsertHeartbeat(heartbeat, Context.ConnectionId);

        _logger.LogInformation("Heartbeat received: {Host} [{IP}] | USB Blocked: {Usb} | Software Disabled: {Soft} | Transfer Restricted: {Trans}", 
            heartbeat.MachineName, heartbeat.IpAddress, heartbeat.UsbStorageBlocked, heartbeat.SoftwareInstallDisabled, heartbeat.ExternalFileTransferRestricted);

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

    // === Remote Terminal Relay ===
    public async Task SendTerminalCommand(string targetConnectionId, TerminalCommand command)
    {
        string hostName = ConnectedClients.TryGetValue(targetConnectionId, out var client) ? client.MachineName : targetConnectionId;
        _logger.LogInformation("Admin dispatching Terminal Command to [{Host}]: {Cmd}", hostName, command.CommandText);
        _dbService.LogAudit(hostName, "RemoteTerminal", false, true, $"PowerShell executed: {command.CommandText}");

        await Clients.Client(targetConnectionId).ExecuteTerminalCommand(command);
    }

    public async Task SendTerminalOutput(TerminalOutput output)
    {
        // Broadcast output to admin browser sessions
        await Clients.All.ReceiveTerminalOutput(output);
    }

    // === Remote Screen Stream & Input Relay ===
    public async Task StartRemoteScreen(string targetConnectionId)
    {
        string hostName = ConnectedClients.TryGetValue(targetConnectionId, out var client) ? client.MachineName : targetConnectionId;
        _logger.LogInformation("Starting Remote Screen Session -> [{Host}] for Admin [{AdminId}]", hostName, Context.ConnectionId);
        _dbService.LogAudit(hostName, "RemoteScreen", true, true, $"Admin initiated live screen stream");

        await Clients.Client(targetConnectionId).StartScreenStream(Context.ConnectionId);
    }

    public async Task StopRemoteScreen(string targetConnectionId)
    {
        string hostName = ConnectedClients.TryGetValue(targetConnectionId, out var client) ? client.MachineName : targetConnectionId;
        _logger.LogInformation("Stopping Remote Screen Session -> [{Host}]", hostName);
        _dbService.LogAudit(hostName, "RemoteScreen", false, true, $"Admin terminated live screen stream");

        await Clients.Client(targetConnectionId).StopScreenStream();
    }

    public async Task SendScreenFrame(string targetAdminConnectionId, string base64Frame)
    {
        await Clients.Client(targetAdminConnectionId).ReceiveScreenFrame(base64Frame);
    }

    public async Task SendRemoteInput(string targetConnectionId, RemoteInputEvent input)
    {
        await Clients.Client(targetConnectionId).ProcessRemoteInput(input);
    }
}
