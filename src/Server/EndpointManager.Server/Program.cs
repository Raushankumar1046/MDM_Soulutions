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

// Enable Windows Service background execution
builder.Host.UseWindowsService();

// Bind to all network interfaces on port 5000 by default
builder.WebHost.UseUrls("http://0.0.0.0:5000");

// Register Embedded LiteDB Service
builder.Services.AddSingleton<LiteDbService>();

// Register Authentication & Session Service
builder.Services.AddSingleton<AuthService>();

// Register SignalR with high message size limit for Screen Capture frames (10 MB)
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
    options.EnableDetailedErrors = true;
});

// Register UDP Auto-Discovery Service
builder.Services.AddHostedService<UdpDiscoveryServerService>();

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(p => p.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin());
});

var app = builder.Build();

if (args.Contains("--reset-admin"))
{
    var auth = app.Services.GetRequiredService<AuthService>();
    auth.ResetAdminPassword("admin123");
    Console.WriteLine("==========================================================");
    Console.WriteLine("  SUCCESS: Admin password has been reset to: admin123");
    Console.WriteLine("==========================================================");
    return;
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();

// Map SignalR Hub Endpoint with large buffer (support both slash and dot notation)
app.MapHub<EndpointHub>("/hubs/endpoints", options =>
{
    options.ApplicationMaxBufferSize = 10 * 1024 * 1024;
    options.TransportMaxBufferSize = 10 * 1024 * 1024;
});
app.MapHub<EndpointHub>("/hubs.endpoints", options =>
{
    options.ApplicationMaxBufferSize = 10 * 1024 * 1024;
    options.TransportMaxBufferSize = 10 * 1024 * 1024;
});

// === Authentication Endpoints ===
app.MapPost("/api/auth/login", (LoginRequest req, AuthService auth) =>
{
    var (success, token, message) = auth.Login(req.Username, req.Password);
    if (!success)
    {
        return Results.Json(new { Success = false, Message = message }, statusCode: 401);
    }
    return Results.Ok(new { Success = true, Token = token, Username = req.Username.Trim().ToLowerInvariant(), ExpiresInHours = 24 });
});

app.MapGet("/api/auth/verify", (HttpRequest req, AuthService auth) =>
{
    var token = AuthService.ExtractBearerToken(req);
    if (auth.ValidateToken(token, out var username))
    {
        return Results.Ok(new { Valid = true, Username = username });
    }
    return Results.Unauthorized();
});

app.MapPost("/api/auth/logout", (HttpRequest req, AuthService auth) =>
{
    var token = AuthService.ExtractBearerToken(req);
    auth.Logout(token);
    return Results.Ok(new { Success = true });
});

app.MapPost("/api/auth/change-password", (ChangePasswordRequest req, HttpRequest httpReq, AuthService auth) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out var username))
    {
        return Results.Unauthorized();
    }
    var (success, message) = auth.ChangePassword(username, req.CurrentPassword, req.NewPassword);
    return success ? Results.Ok(new { Success = true, Message = message }) : Results.BadRequest(new { Success = false, Message = message });
});

// === Protected Management APIs (Requires Bearer Token) ===

// API: List all devices (Online & Offline)
app.MapGet("/api/devices", (HttpRequest httpReq, AuthService auth, LiteDbService db) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out _))
    {
        return Results.Unauthorized();
    }

    var devices = db.GetAllDevices();
    // Reconcile online state with current memory connections
    foreach (var d in devices)
    {
        var activeClient = EndpointHub.ConnectedClients.Values
            .FirstOrDefault(c => c.MachineName.Equals(d.MachineName, StringComparison.OrdinalIgnoreCase));
        d.IsOnline = activeClient != null;
        if (activeClient != null)
        {
            d.LastConnectionId = activeClient.ConnectionId;
            d.UsbStorageBlocked = activeClient.UsbStorageBlocked;
            d.SoftwareInstallDisabled = activeClient.SoftwareInstallDisabled;
            d.ExternalFileTransferRestricted = activeClient.ExternalFileTransferRestricted;
            d.WebFilteringBlocked = activeClient.WebFilteringBlocked;
        }
    }
    return Results.Ok(devices);
});

// API: Delete / Remove a device record from LiteDB
app.MapDelete("/api/devices/{machineName}", (string machineName, HttpRequest httpReq, AuthService auth, LiteDbService db) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out _))
    {
        return Results.Unauthorized();
    }

    bool deleted = db.DeleteDevice(machineName);
    if (deleted)
    {
        var activeKvp = EndpointHub.ConnectedClients.FirstOrDefault(c => c.Value.MachineName.Equals(machineName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(activeKvp.Key))
        {
            EndpointHub.ConnectedClients.TryRemove(activeKvp.Key, out _);
        }
        db.LogAudit(machineName, "DeviceRemoved", false, true, $"Admin deleted {machineName} from MDM console");
        return Results.Ok(new { Success = true, Message = $"Device '{machineName}' removed successfully." });
    }
    return Results.NotFound(new { Success = false, Message = $"Device '{machineName}' not found." });
});

// API: Recent Audit Trail
app.MapGet("/api/audit", (HttpRequest httpReq, AuthService auth, LiteDbService db) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out _))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(db.GetRecentAuditLogs(30));
});

// API: Dispatch Remote Policy Command
app.MapPost("/api/policies/dispatch", async (
    PolicyDispatchRequest request, 
    HttpRequest httpReq,
    AuthService auth,
    LiteDbService db,
    IHubContext<EndpointHub, IEndpointClientContract> hubContext) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out _))
    {
        return Results.Unauthorized();
    }

    string? payload = request.Payload;
    if (request.Policy == (int)PolicyType.WebFilteringBlock && request.EnableRestriction && string.IsNullOrEmpty(payload))
    {
        payload = string.Join(",", db.GetBlockedDomains());
    }

    var cmd = new PolicyCommand
    {
        Policy = (PolicyType)request.Policy,
        EnableRestriction = request.EnableRestriction,
        Payload = payload
    };

    if (string.IsNullOrEmpty(request.ConnectionId) && string.IsNullOrEmpty(request.MachineName))
    {
        // Fleetwide Broadcast to ALL connected laptops
        if (cmd.Policy == PolicyType.WebFilteringBlock && cmd.EnableRestriction)
        {
            var allBlocked = db.GetBlockedDomains();
            foreach (var clientKvp in EndpointHub.ConnectedClients)
            {
                var devAllowed = db.GetDeviceAllowedExceptions(clientKvp.Value.MachineName);
                var devPayload = System.Text.Json.JsonSerializer.Serialize(new { Blocked = allBlocked, Allowed = devAllowed });
                var devCmd = new PolicyCommand
                {
                    Policy = PolicyType.WebFilteringBlock,
                    EnableRestriction = true,
                    Payload = devPayload
                };
                await hubContext.Clients.Client(clientKvp.Key).ExecutePolicy(devCmd);
            }
        }
        else
        {
            await hubContext.Clients.All.ExecutePolicy(cmd);
        }

        db.LogAudit("FLEETWIDE", cmd.Policy.ToString(), cmd.EnableRestriction, true, "Broadcasted policy command to all connected endpoints.");
        return Results.Ok(new { Success = true, Message = "Broadcasted to all endpoints." });
    }
    else
    {
        string? targetConnectionId = null;

        // 1. Resolve active connection by MachineName first (guarantees freshest socket ID)
        if (!string.IsNullOrEmpty(request.MachineName))
        {
            var match = EndpointHub.ConnectedClients
                .Where(c => c.Value.MachineName.Equals(request.MachineName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.Value.TimestampUtc)
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(match.Key))
            {
                targetConnectionId = match.Key;
            }
        }

        // 2. Secondary fallback: check if request.ConnectionId is still valid in memory
        if (string.IsNullOrEmpty(targetConnectionId) && !string.IsNullOrEmpty(request.ConnectionId))
        {
            if (EndpointHub.ConnectedClients.ContainsKey(request.ConnectionId))
            {
                targetConnectionId = request.ConnectionId;
            }
        }

        if (string.IsNullOrEmpty(targetConnectionId) || !EndpointHub.ConnectedClients.ContainsKey(targetConnectionId))
        {
            return Results.BadRequest(new { Success = false, Message = $"Target machine '{request.MachineName ?? request.ConnectionId}' is offline or disconnected." });
        }

        var clientName = EndpointHub.ConnectedClients.TryGetValue(targetConnectionId, out var client) ? client.MachineName : (request.MachineName ?? targetConnectionId);

        if (cmd.Policy == PolicyType.WebFilteringBlock && cmd.EnableRestriction)
        {
            var allBlocked = db.GetBlockedDomains();
            var devAllowed = db.GetDeviceAllowedExceptions(clientName);
            cmd.Payload = System.Text.Json.JsonSerializer.Serialize(new { Blocked = allBlocked, Allowed = devAllowed });
        }

        await hubContext.Clients.Client(targetConnectionId).ExecutePolicy(cmd);
        db.LogAudit(clientName, cmd.Policy.ToString(), cmd.EnableRestriction, true, $"Dispatched policy to {clientName}");

        return Results.Ok(new { Success = true, Message = $"Dispatched to {clientName}." });
    }
});

// === Web Filtering Custom Domain APIs ===
app.MapGet("/api/webfilter/domains", (HttpRequest httpReq, AuthService auth, LiteDbService db) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out _)) return Results.Unauthorized();
    return Results.Ok(db.GetBlockedDomains());
});

app.MapPost("/api/webfilter/domains", async (
    AddDomainRequest req, 
    HttpRequest httpReq, 
    AuthService auth, 
    LiteDbService db,
    IHubContext<EndpointHub, IEndpointClientContract> hubContext) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out _)) return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(req.Domain))
    {
        return Results.BadRequest(new { Success = false, Message = "Domain name cannot be empty." });
    }

    bool added = db.AddBlockedDomain(req.Domain, req.Category ?? "Custom");
    if (added)
    {
        db.LogAudit("FLEETWIDE", "WebFilter", true, true, $"Admin added domain to blocklist: {req.Domain.Trim().ToLowerInvariant()}");
        
        // Dynamically sync updated blocklist to all connected clients that currently have Web Filtering ENFORCED!
        var updatedDomains = db.GetBlockedDomains();

        foreach (var clientKvp in EndpointHub.ConnectedClients)
        {
            if (clientKvp.Value.WebFilteringBlocked)
            {
                var devAllowed = db.GetDeviceAllowedExceptions(clientKvp.Value.MachineName);
                var devPayload = System.Text.Json.JsonSerializer.Serialize(new { Blocked = updatedDomains, Allowed = devAllowed });
                var syncCmd = new PolicyCommand
                {
                    Policy = PolicyType.WebFilteringBlock,
                    EnableRestriction = true,
                    Payload = devPayload
                };
                await hubContext.Clients.Client(clientKvp.Key).ExecutePolicy(syncCmd);
            }
        }

        return Results.Ok(new { Success = true, Domains = updatedDomains });
    }

    return Results.BadRequest(new { Success = false, Message = "Failed to add domain." });
});

app.MapDelete("/api/webfilter/domains/{domain}", async (
    string domain, 
    HttpRequest httpReq, 
    AuthService auth, 
    LiteDbService db,
    IHubContext<EndpointHub, IEndpointClientContract> hubContext) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out _)) return Results.Unauthorized();

    bool removed = db.RemoveBlockedDomain(domain);
    if (removed)
    {
        db.LogAudit("FLEETWIDE", "WebFilter", false, true, $"Admin removed domain from blocklist: {domain.Trim().ToLowerInvariant()}");
        
        // Dynamically sync updated blocklist to all connected clients that currently have Web Filtering ENFORCED!
        var updatedDomains = db.GetBlockedDomains();

        foreach (var clientKvp in EndpointHub.ConnectedClients)
        {
            if (clientKvp.Value.WebFilteringBlocked)
            {
                var devAllowed = db.GetDeviceAllowedExceptions(clientKvp.Value.MachineName);
                var devPayload = System.Text.Json.JsonSerializer.Serialize(new { Blocked = updatedDomains, Allowed = devAllowed });
                var syncCmd = new PolicyCommand
                {
                    Policy = PolicyType.WebFilteringBlock,
                    EnableRestriction = true,
                    Payload = devPayload
                };
                await hubContext.Clients.Client(clientKvp.Key).ExecutePolicy(syncCmd);
            }
        }

        return Results.Ok(new { Success = true, Domains = updatedDomains });
    }

    return Results.NotFound(new { Success = false, Message = $"Domain '{domain}' was not found in blocklist." });
});

// === Device Allowed Exceptions (Per-Device Web Filter Whitelist) APIs ===
app.MapGet("/api/devices/{machineName}/exceptions", (string machineName, HttpRequest httpReq, AuthService auth, LiteDbService db) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out _)) return Results.Unauthorized();
    return Results.Ok(db.GetDeviceAllowedExceptions(machineName));
});

app.MapPost("/api/devices/{machineName}/exceptions", async (
    string machineName,
    AddDomainRequest req,
    HttpRequest httpReq,
    AuthService auth,
    LiteDbService db,
    IHubContext<EndpointHub, IEndpointClientContract> hubContext) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out _)) return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(req.Domain))
    {
        return Results.BadRequest(new { Success = false, Message = "Domain name cannot be empty." });
    }

    bool added = db.AddDeviceAllowedException(machineName, req.Domain);
    if (added)
    {
        db.LogAudit(machineName, "WebFilterException", true, true, $"Admin allowed site '{req.Domain.Trim().ToLowerInvariant()}' as an exception for {machineName}");

        // If machine is currently connected and Web Filtering is ENFORCED, push live update immediately!
        var match = EndpointHub.ConnectedClients
            .Where(c => c.Value.MachineName.Equals(machineName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Value.TimestampUtc)
            .FirstOrDefault();

        if (!string.IsNullOrEmpty(match.Key) && match.Value.WebFilteringBlocked)
        {
            var blocked = db.GetBlockedDomains();
            var allowed = db.GetDeviceAllowedExceptions(machineName);
            var payload = System.Text.Json.JsonSerializer.Serialize(new { Blocked = blocked, Allowed = allowed });
            var syncCmd = new PolicyCommand
            {
                Policy = PolicyType.WebFilteringBlock,
                EnableRestriction = true,
                Payload = payload
            };
            await hubContext.Clients.Client(match.Key).ExecutePolicy(syncCmd);
        }

        return Results.Ok(new { Success = true, Exceptions = db.GetDeviceAllowedExceptions(machineName) });
    }

    return Results.BadRequest(new { Success = false, Message = "Failed to add allowed site exception." });
});

app.MapDelete("/api/devices/{machineName}/exceptions/{domain}", async (
    string machineName,
    string domain,
    HttpRequest httpReq,
    AuthService auth,
    LiteDbService db,
    IHubContext<EndpointHub, IEndpointClientContract> hubContext) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out _)) return Results.Unauthorized();

    bool removed = db.RemoveDeviceAllowedException(machineName, domain);
    if (removed)
    {
        db.LogAudit(machineName, "WebFilterException", false, true, $"Admin revoked allowed site '{domain.Trim().ToLowerInvariant()}' for {machineName}");

        // If machine is currently connected and Web Filtering is ENFORCED, push live update immediately!
        var match = EndpointHub.ConnectedClients
            .Where(c => c.Value.MachineName.Equals(machineName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Value.TimestampUtc)
            .FirstOrDefault();

        if (!string.IsNullOrEmpty(match.Key) && match.Value.WebFilteringBlocked)
        {
            var blocked = db.GetBlockedDomains();
            var allowed = db.GetDeviceAllowedExceptions(machineName);
            var payload = System.Text.Json.JsonSerializer.Serialize(new { Blocked = blocked, Allowed = allowed });
            var syncCmd = new PolicyCommand
            {
                Policy = PolicyType.WebFilteringBlock,
                EnableRestriction = true,
                Payload = payload
            };
            await hubContext.Clients.Client(match.Key).ExecutePolicy(syncCmd);
        }

        return Results.Ok(new { Success = true, Exceptions = db.GetDeviceAllowedExceptions(machineName) });
    }

    return Results.NotFound(new { Success = false, Message = $"Domain '{domain}' was not found in device exceptions." });
});

app.Run();

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public class PolicyDispatchRequest
{
    public string? ConnectionId { get; set; }
    public string? MachineName { get; set; }
    public int Policy { get; set; }
    public bool EnableRestriction { get; set; }
    public string? Payload { get; set; }
}

public class AddDomainRequest
{
    public string Domain { get; set; } = string.Empty;
    public string? Category { get; set; }
}
