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
}
