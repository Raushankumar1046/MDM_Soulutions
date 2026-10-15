using EndpointManager.Server.Hubs;
using EndpointManager.Server.Services;
using EndpointManager.Shared.Contracts;
using EndpointManager.Shared.Enums;
using EndpointManager.Shared.Models;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});

// Register Embedded LiteDB Service
builder.Services.AddSingleton<LiteDbService>();

// Register SignalR
builder.Services.AddSignalR();

// Register UDP Auto-Discovery Service
builder.Services.AddHostedService<UdpDiscoveryServerService>();

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(p => p.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin());
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();

// Map SignalR Hub Endpoint
app.MapHub<EndpointHub>("/hubs/endpoints");

// API: List all devices (Online & Offline)
app.MapGet("/api/devices", (LiteDbService db) =>
{
    var devices = db.GetAllDevices();
    // Reconcile online state with current memory connections
    foreach (var d in devices)
    {
        d.IsOnline = !string.IsNullOrEmpty(d.LastConnectionId) && 
                     EndpointHub.ConnectedClients.ContainsKey(d.LastConnectionId);
    }
    return Results.Ok(devices);
});

// API: Recent Audit Trail
app.MapGet("/api/audit", (LiteDbService db) =>
{
    return Results.Ok(db.GetRecentAuditLogs(30));
});

// API: Dispatch Remote Policy Command
app.MapPost("/api/policies/dispatch", async (
    PolicyDispatchRequest request, 
    LiteDbService db,
    IHubContext<EndpointHub, IEndpointClientContract> hubContext) =>
{
    var cmd = new PolicyCommand
    {
        Policy = (PolicyType)request.Policy,
        EnableRestriction = request.EnableRestriction,
        Payload = request.Payload
    };

    if (string.IsNullOrEmpty(request.ConnectionId) && string.IsNullOrEmpty(request.MachineName))
    {
        // Fleetwide Broadcast to ALL connected laptops
        await hubContext.Clients.All.ExecutePolicy(cmd);
        db.LogAudit("FLEETWIDE", cmd.Policy.ToString(), cmd.EnableRestriction, true, "Broadcasted policy command to all connected endpoints.");
        return Results.Ok(new { Success = true, Message = "Broadcasted to all endpoints." });
    }
    else
    {
        string? targetConnectionId = request.ConnectionId;
        
        // If connectionId not supplied directly, lookup active connection by machine name
        if (string.IsNullOrEmpty(targetConnectionId) && !string.IsNullOrEmpty(request.MachineName))
        {
            var match = EndpointHub.ConnectedClients.FirstOrDefault(c => c.Value.MachineName.Equals(request.MachineName, StringComparison.OrdinalIgnoreCase));
            targetConnectionId = match.Key;
        }

        if (string.IsNullOrEmpty(targetConnectionId) || !EndpointHub.ConnectedClients.ContainsKey(targetConnectionId))
        {
            return Results.BadRequest(new { Success = false, Message = $"Target machine is offline or disconnected." });
        }

        await hubContext.Clients.Client(targetConnectionId).ExecutePolicy(cmd);
        var clientName = EndpointHub.ConnectedClients.TryGetValue(targetConnectionId, out var client) ? client.MachineName : targetConnectionId;
        db.LogAudit(clientName, cmd.Policy.ToString(), cmd.EnableRestriction, true, $"Dispatched policy to {clientName}");

        return Results.Ok(new { Success = true, Message = $"Dispatched to {clientName}." });
    }
});

app.Run();

public class PolicyDispatchRequest
{
    public string? ConnectionId { get; set; }
    public string? MachineName { get; set; }
    public int Policy { get; set; }
    public bool EnableRestriction { get; set; }
    public string? Payload { get; set; }
}
