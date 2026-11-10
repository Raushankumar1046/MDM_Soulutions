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

// Register TOTP 2FA Service
builder.Services.AddSingleton<TotpService>();

// Register SMTP Email Notification Service
builder.Services.AddSingleton<EmailService>();

// Register Authentication & Session Service
builder.Services.AddSingleton<AuthService>();

// Register OTA Client Auto-Update Service
builder.Services.AddSingleton<ClientUpdateService>();

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

var staticOptions = new StaticFileOptions();
var fileProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
fileProvider.Mappings[".apk"] = "application/vnd.android.package-archive";
staticOptions.ContentTypeProvider = fileProvider;
staticOptions.OnPrepareResponse = ctx =>
{
    if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        ctx.Context.Response.Headers.Pragma = "no-cache";
        ctx.Context.Response.Headers.Expires = "0";
    }
};
app.UseStaticFiles(staticOptions);

app.UseCors();

// Direct APK download route with appropriate MIME headers
app.MapGet("/downloads/{fileName}", (string fileName) =>
{
    var safeName = Path.GetFileName(fileName);
    var filePath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "downloads", safeName);
    if (!File.Exists(filePath))
    {
        return Results.NotFound(new { Message = $"File {safeName} not found." });
    }
    return Results.File(filePath, "application/vnd.android.package-archive", safeName);
});

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

// === Authentication & 2FA Endpoints ===
app.MapPost("/api/auth/login", (LoginRequest req, AuthService auth) =>
{
    var (success, requires2Fa, token, tempToken, username, message) = auth.Login(req.Username, req.Password, req.TotpCode);
    if (requires2Fa)
    {
        return Results.Ok(new
        {
            Success = false,
            Requires2FA = true,
            TempToken = tempToken,
            Username = username,
            Message = message
        });
    }
    if (!success)
    {
        return Results.Json(new { Success = false, Message = message }, statusCode: 401);
    }
    auth.ValidateToken(token, out _, out var role, out var permissions);
    return Results.Ok(new
    {
        Success = true,
        Requires2FA = false,
        Token = token,
        Username = username,
        Role = role,
        Permissions = permissions,
        ExpiresInHours = 24
    });
});

app.MapPost("/api/auth/verify-2fa", (Verify2FaRequest req, AuthService auth) =>
{
    var (success, token, username, role, message) = auth.Verify2Fa(req.TempToken, req.TotpCode);
    if (!success)
    {
        return Results.Json(new { Success = false, Message = message }, statusCode: 401);
    }
    auth.ValidateToken(token, out _, out _, out var permissions);
    return Results.Ok(new
    {
        Success = true,
        Token = token,
        Username = username,
        Role = role,
        Permissions = permissions,
        ExpiresInHours = 24
    });
});

app.MapGet("/api/auth/verify", (HttpRequest req, AuthService auth) =>
{
    var token = AuthService.ExtractBearerToken(req);
    if (auth.ValidateToken(token, out var username, out var role, out var permissions))
    {
        return Results.Ok(new { Valid = true, Username = username, Role = role, Permissions = permissions });
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

// === Account Activation & 2FA Setup Flow (Public link accessed via Email/WhatsApp) ===
app.MapGet("/api/auth/activate/verify", (string token, AuthService auth) =>
{
    var (success, message, userInfo) = auth.VerifyInviteToken(token);
    if (!success || userInfo == null)
    {
        return Results.BadRequest(new { Success = false, Valid = false, Error = message, Message = message });
    }
    dynamic u = userInfo;
    return Results.Ok(new
    {
        Success = true,
        Valid = true,
        Username = (string)u.Username,
        FullName = (string)u.FullName,
        Email = (string)u.Email,
        Role = (string)u.Role,
        Secret = (string)u.TotpSecret,
        TotpSecret = (string)u.TotpSecret,
        OtpAuthUri = (string)u.QrUri,
        QrUri = (string)u.QrUri,
        User = userInfo
    });
});

app.MapPost("/api/auth/activate/complete", (CompleteActivationRequest req, AuthService auth) =>
{
    var (success, message) = auth.CompleteActivation(req.Token, req.NewPassword, req.TotpCode);
    if (!success)
    {
        return Results.BadRequest(new { Success = false, Error = message, Message = message });
    }
    return Results.Ok(new { Success = true, Message = message });
});

// === User Management Endpoints (Requires Admin Bearer Token) ===
app.MapGet("/api/users", (HttpRequest httpReq, AuthService auth) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out _)) return Results.Unauthorized();

    return Results.Ok(auth.GetAllUsers());
});

app.MapPost("/api/users/invite", async (InviteUserRequest req, HttpRequest httpReq, AuthService auth, EmailService emailService) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out var adminUser, out var role, out var permissions)) return Results.Unauthorized();

    if (!permissions.CanManageUsers && !role.Equals("SuperAdmin", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { Success = false, Message = "Access Denied: You do not have permission to invite console users." });
    }

    var (success, message, inviteToken, totpSecret, qrUri) = auth.CreateUserInvite(
        req.FullName, req.Email, req.Username, req.Role, req.Permissions);

    if (!success)
    {
        return Results.BadRequest(new { Success = false, Message = message });
    }

    // Build the user activation URL based on the current request host (default to live server IP if on localhost)
    var scheme = httpReq.Scheme;
    var host = httpReq.Host.Value;
    if (host.Contains("localhost", StringComparison.OrdinalIgnoreCase) || host.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
    {
        host = "14.194.19.6:5000";
    }
    var inviteUrl = $"{scheme}://{host}/#/activate?token={inviteToken}";

    bool emailSent = false;
    string emailError = "";

    if (req.SendEmail)
    {
        var emailResult = await emailService.SendInviteEmailAsync(req.Email, req.FullName, inviteUrl);
        emailSent = emailResult.Success;
        if (!emailResult.Success)
        {
            emailError = emailResult.Message;
        }
    }

    return Results.Ok(new
    {
        Success = true,
        Message = emailSent 
            ? "Invitation sent via email successfully!" 
            : "User created and activation link generated.",
        InviteToken = inviteToken,
        InviteUrl = inviteUrl,
        QrUri = qrUri,
        TotpSecret = totpSecret,
        EmailSent = emailSent,
        EmailError = emailError
    });
});

app.MapPost("/api/users/reset-2fa", async (Reset2FaRequest req, HttpRequest httpReq, AuthService auth, EmailService emailService) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out var currentUsername)) return Results.Unauthorized();

    var (success, message, inviteToken, qrUri, secret) = auth.ResetUser2Fa(currentUsername, req.Username);
    if (!success)
    {
        return Results.BadRequest(new { Success = false, Message = message });
    }

    var scheme = httpReq.Scheme;
    var host = httpReq.Host.Value;
    if (host.Contains("localhost", StringComparison.OrdinalIgnoreCase) || host.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
    {
        host = "14.194.19.6:5000";
    }
    var inviteUrl = $"{scheme}://{host}/#/activate?token={inviteToken}";

    bool emailSent = false;
    string emailError = "";

    var allUsers = auth.GetAllUsers();
    var targetUser = allUsers.FirstOrDefault(u => (string)((dynamic)u).Username == req.Username.Trim().ToLowerInvariant());
    if (req.SendEmail && targetUser != null)
    {
        string? email = (string?)((dynamic)targetUser).Email;
        string? fullName = (string?)((dynamic)targetUser).FullName;
        if (!string.IsNullOrEmpty(email))
        {
            var emailResult = await emailService.SendInviteEmailAsync(email, fullName ?? req.Username, inviteUrl);
            emailSent = emailResult.Success;
            if (!emailResult.Success) emailError = emailResult.Message;
        }
    }

    return Results.Ok(new
    {
        Success = true,
        Message = "2FA reset successfully. A new activation link is ready.",
        InviteToken = inviteToken,
        InviteUrl = inviteUrl,
        QrUri = qrUri,
        TotpSecret = secret,
        EmailSent = emailSent,
        EmailError = emailError
    });
});

app.MapPost("/api/users/delete", (DeleteUserRequest req, HttpRequest httpReq, AuthService auth) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out var currentUsername)) return Results.Unauthorized();

    var (success, message) = auth.DeleteUser(currentUsername, req.Username);
    return success ? Results.Ok(new { Success = true, Message = message }) : Results.BadRequest(new { Success = false, Message = message });
});

app.MapGet("/api/users/profile/{username}", (string username, HttpRequest httpReq, AuthService auth) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out _)) return Results.Unauthorized();

    var (success, message, user) = auth.GetUser(username);
    if (!success || user == null) return Results.NotFound(new { Success = false, Message = message });

    var perms = (user.Username.Equals("admin", StringComparison.OrdinalIgnoreCase) || user.Role.Equals("SuperAdmin", StringComparison.OrdinalIgnoreCase))
        ? UserPermissions.CreateSuperAdminDefaults()
        : (user.Permissions ?? (user.Role.Equals("Admin", StringComparison.OrdinalIgnoreCase) ? UserPermissions.CreateAdminDefaults() : UserPermissions.CreateViewerDefaults()));

    return Results.Ok(new
    {
        user.Username,
        user.FullName,
        user.Email,
        user.Role,
        user.IsActive,
        user.Is2FaEnabled,
        Permissions = perms,
        HasPendingInvite = !string.IsNullOrEmpty(user.InviteToken),
        user.CreatedAtUtc,
        user.LastLoginUtc
    });
});

app.MapPost("/api/users/update", (UpdateUserRequest req, HttpRequest httpReq, AuthService auth) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out var currentUsername)) return Results.Unauthorized();

    var (success, message) = auth.UpdateUser(
        currentUsername,
        req.Username,
        req.FullName,
        req.Email,
        req.Role,
        req.IsActive,
        req.Is2FaEnabled,
        req.NewPassword,
        req.Permissions
    );

    return success ? Results.Ok(new { Success = true, Message = message }) : Results.BadRequest(new { Success = false, Message = message });
});

app.MapPost("/api/users/toggle-status", (ToggleUserStatusRequest req, HttpRequest httpReq, AuthService auth) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out var currentUsername)) return Results.Unauthorized();

    var (success, message, newStatus) = auth.ToggleUserStatus(currentUsername, req.Username);
    return success 
        ? Results.Ok(new { Success = true, Message = message, IsActive = newStatus }) 
        : Results.BadRequest(new { Success = false, Message = message });
});

// === SMTP Email Configuration APIs (Requires Admin Bearer Token) ===
app.MapGet("/api/settings/smtp", (HttpRequest httpReq, AuthService auth, EmailService emailService) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out _)) return Results.Unauthorized();

    var s = emailService.GetSettings();
    return Results.Ok(new
    {
        s.Host,
        s.Port,
        s.EnableSsl,
        s.FromEmail,
        SenderEmail = s.FromEmail,
        s.SenderName,
        s.Username,
        HasPassword = !string.IsNullOrEmpty(s.Password),
        s.UpdatedAtUtc
    });
});

app.MapPost("/api/settings/smtp", (SmtpSettingsDto req, HttpRequest httpReq, AuthService auth, EmailService emailService) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out _)) return Results.Unauthorized();

    var existing = emailService.GetSettings();
    var fromEmail = !string.IsNullOrWhiteSpace(req.FromEmail) ? req.FromEmail.Trim() : req.SenderEmail?.Trim() ?? "";

    var toSave = new SmtpSettingsRecord
    {
        Id = "default",
        Host = req.Host?.Trim() ?? "",
        Port = req.Port > 0 ? req.Port : 587,
        EnableSsl = req.EnableSsl,
        FromEmail = fromEmail,
        SenderName = string.IsNullOrWhiteSpace(req.SenderName) ? "CyberShield MDM" : req.SenderName.Trim(),
        Username = req.Username?.Trim() ?? "",
        Password = !string.IsNullOrEmpty(req.Password) ? req.Password : existing.Password, // Preserve existing password if blank
        UpdatedAtUtc = DateTime.UtcNow
    };

    emailService.SaveSettings(toSave);
    return Results.Ok(new { Success = true, Message = "SMTP settings saved successfully." });
});

app.MapPost("/api/settings/smtp/test", async (TestEmailRequest req, HttpRequest httpReq, AuthService auth, EmailService emailService) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out _)) return Results.Unauthorized();

    var toEmail = !string.IsNullOrWhiteSpace(req.ToEmail) ? req.ToEmail.Trim() : req.RecipientEmail?.Trim();
    if (string.IsNullOrWhiteSpace(toEmail))
    {
        return Results.BadRequest(new { Success = false, Error = "Recipient test email address is required.", Message = "Recipient test email address is required." });
    }

    SmtpSettingsRecord? liveSettings = null;
    if (!string.IsNullOrWhiteSpace(req.Host))
    {
        var existing = emailService.GetSettings();
        var pass = !string.IsNullOrWhiteSpace(req.Password) && req.Password != "••••••••"
            ? req.Password
            : existing.Password;

        liveSettings = new SmtpSettingsRecord
        {
            Id = "default",
            Host = req.Host.Trim(),
            Port = req.Port.HasValue && req.Port.Value > 0 ? req.Port.Value : 587,
            EnableSsl = req.EnableSsl ?? true,
            FromEmail = (!string.IsNullOrWhiteSpace(req.FromEmail) ? req.FromEmail : req.SenderEmail)?.Trim() ?? existing.FromEmail,
            SenderName = !string.IsNullOrWhiteSpace(req.SenderName) ? req.SenderName.Trim() : "CyberShield MDM",
            Username = !string.IsNullOrWhiteSpace(req.Username) ? req.Username.Trim() : existing.Username,
            Password = pass
        };
    }

    var (success, message) = await emailService.SendTestEmailAsync(toEmail, liveSettings);
    return success 
        ? Results.Ok(new { Success = true, Message = message }) 
        : Results.BadRequest(new { Success = false, Error = message, Message = message });
});

// === Protected Management APIs (Requires Bearer Token) ===

// API: List all devices (Online & Offline)
app.MapGet("/api/devices", (HttpRequest httpReq, AuthService auth, LiteDbService db) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out var currentUsername))
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
            d.SelectivePrintRestricted = activeClient.SelectivePrintRestricted;
            if (!string.IsNullOrEmpty(activeClient.ClientVersion))
            {
                d.ClientVersion = activeClient.ClientVersion;
            }
        }
    }
    return Results.Ok(devices);
});

// API: Delete / Remove a device record from LiteDB
app.MapDelete("/api/devices/{machineName}", (string machineName, HttpRequest httpReq, AuthService auth, LiteDbService db) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out var currentUsername))
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
        db.LogAudit(machineName, "DeviceRemoved", false, true, $"User '{currentUsername}' deleted device {machineName} from MDM console", actionBy: currentUsername, category: "Device");
        return Results.Ok(new { Success = true, Message = $"Device '{machineName}' removed successfully." });
    }
    return Results.NotFound(new { Success = false, Message = $"Device '{machineName}' not found." });
});

// API: Recent Audit Trail (supports ?limit=N)
app.MapGet("/api/audit", (HttpRequest httpReq, AuthService auth, LiteDbService db, int? limit) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out var currentUsername))
    {
        return Results.Unauthorized();
    }

    int count = limit ?? 200;
    return Results.Ok(db.GetRecentAuditLogs(count));
});

// API: Export Full Audit Report (CSV)
app.MapGet("/api/audit/export", (HttpRequest httpReq, AuthService auth, LiteDbService db) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out var currentUsername))
    {
        return Results.Unauthorized();
    }

    var logs = db.GetAllAuditLogs(5000);
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("Timestamp (Local),Action By,Category,Target Machine,Policy / Action,Status,Details");
    foreach (var l in logs)
    {
        var localTime = l.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        var status = l.Success ? "Success" : "Failed";
        string Escape(string? val) => $"\"{(val ?? "").Replace("\"", "\"\"")}\"";
        sb.AppendLine($"{Escape(localTime)},{Escape(l.ActionBy)},{Escape(l.Category)},{Escape(l.MachineName)},{Escape(l.PolicyName)},{Escape(status)},{Escape(l.Details)}");
    }

    var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    return Results.File(bytes, "text/csv; charset=utf-8", $"CyberShield_Audit_Report_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
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
    if (!auth.ValidateToken(token, out var currentUsername, out var role, out var permissions))
    {
        return Results.Unauthorized();
    }

    var targetPolicy = (PolicyType)request.Policy;

    // Granular Policy Permissions Enforcement
    if (targetPolicy == PolicyType.UsbStorageBlock && !permissions.CanToggleUsbLock)
    {
        return Results.BadRequest(new { Success = false, Message = "Access Denied: You do not have permission to modify USB Mass Storage Lock." });
    }
    if (targetPolicy == PolicyType.DisableSoftwareInstallation && !permissions.CanToggleSoftwareBlock)
    {
        return Results.BadRequest(new { Success = false, Message = "Access Denied: You do not have permission to modify Software Installation Block." });
    }
    if (targetPolicy == PolicyType.RestrictExternalFileTransfer && !permissions.CanToggleFileTransfer)
    {
        return Results.BadRequest(new { Success = false, Message = "Access Denied: You do not have permission to modify File Transfer Restrictions." });
    }
    if (targetPolicy == PolicyType.WebFilteringBlock && !permissions.CanToggleWebFilter)
    {
        return Results.BadRequest(new { Success = false, Message = "Access Denied: You do not have permission to modify Web Filtering." });
    }
    if (targetPolicy == PolicyType.SelectivePrintRestriction && !permissions.CanTogglePrintControl)
    {
        return Results.BadRequest(new { Success = false, Message = "Access Denied: You do not have permission to manage Print Security policies." });
    }
    if ((targetPolicy == PolicyType.LockWorkstation || targetPolicy == PolicyType.RestartLaptop || request.Policy == 99) && !permissions.CanEmergencyAction)
    {
        return Results.BadRequest(new { Success = false, Message = "Access Denied: You do not have permission to execute emergency actions (Lock/Restart/Wipe)." });
    }

    string? payload = request.Payload;
    if (request.Policy == (int)PolicyType.WebFilteringBlock && request.EnableRestriction && string.IsNullOrEmpty(payload))
    {
        payload = string.Join(",", db.GetBlockedDomains());
    }

    var cmd = new PolicyCommand
    {
        Policy = targetPolicy,
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

        db.LogAudit("FLEETWIDE", cmd.Policy.ToString(), cmd.EnableRestriction, true, $"User '{currentUsername}' broadcasted policy {cmd.Policy} = {(cmd.EnableRestriction ? "ENFORCED" : "REMOVED")} to all endpoints.", actionBy: currentUsername, category: "Policy");
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
        db.LogAudit(clientName, cmd.Policy.ToString(), cmd.EnableRestriction, true, $"User '{currentUsername}' set policy {cmd.Policy} = {(cmd.EnableRestriction ? "ENFORCED" : "REMOVED")} on {clientName}", actionBy: currentUsername, category: "Policy");

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
    if (!auth.ValidateToken(token, out var currentUsername, out _, out var permissions)) return Results.Unauthorized();
    if (!permissions.CanToggleWebFilter) return Results.BadRequest(new { Success = false, Message = "Access Denied: You do not have permission to modify Web Filter domains." });

    if (string.IsNullOrWhiteSpace(req.Domain))
    {
        return Results.BadRequest(new { Success = false, Message = "Domain name cannot be empty." });
    }

    bool added = db.AddBlockedDomain(req.Domain, req.Category ?? "Custom");
    if (added)
    {
        db.LogAudit("FLEETWIDE", "WebFilter", true, true, $"User '{currentUsername}' added domain to blocklist: {req.Domain.Trim().ToLowerInvariant()}", actionBy: currentUsername, category: "WebFilter");
        
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
    if (!auth.ValidateToken(token, out var currentUsername, out _, out var permissions)) return Results.Unauthorized();
    if (!permissions.CanToggleWebFilter) return Results.BadRequest(new { Success = false, Message = "Access Denied: You do not have permission to modify Web Filter domains." });

    bool removed = db.RemoveBlockedDomain(domain);
    if (removed)
    {
        db.LogAudit("FLEETWIDE", "WebFilter", false, true, $"User '{currentUsername}' removed domain from blocklist: {domain.Trim().ToLowerInvariant()}", actionBy: currentUsername, category: "WebFilter");
        
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
    if (!auth.ValidateToken(token, out var currentUsername, out _, out var permissions)) return Results.Unauthorized();
    if (!permissions.CanToggleWebFilter) return Results.BadRequest(new { Success = false, Message = "Access Denied: You do not have permission to modify Web Filter exceptions." });

    if (string.IsNullOrWhiteSpace(req.Domain))
    {
        return Results.BadRequest(new { Success = false, Message = "Domain name cannot be empty." });
    }

    bool added = db.AddDeviceAllowedException(machineName, req.Domain);
    if (added)
    {
        db.LogAudit(machineName, "WebFilterException", true, true, $"User '{currentUsername}' allowed site '{req.Domain.Trim().ToLowerInvariant()}' as an exception for {machineName}", actionBy: currentUsername, category: "WebFilter");

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
    if (!auth.ValidateToken(token, out var currentUsername, out _, out var permissions)) return Results.Unauthorized();
    if (!permissions.CanToggleWebFilter) return Results.BadRequest(new { Success = false, Message = "Access Denied: You do not have permission to modify Web Filter exceptions." });

    bool removed = db.RemoveDeviceAllowedException(machineName, domain);
    if (removed)
    {
        db.LogAudit(machineName, "WebFilterException", false, true, $"User '{currentUsername}' revoked allowed site '{domain.Trim().ToLowerInvariant()}' for {machineName}", actionBy: currentUsername, category: "WebFilter");

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

// === OTA Client Auto-Update APIs ===
app.MapGet("/api/updates/client/info", (ClientUpdateService updateService) =>
{
    return Results.Ok(updateService.GetUpdateInfo());
});

app.MapGet("/api/updates/client/download", (ClientUpdateService updateService) =>
{
    string? path = updateService.GetLatestClientFilePath();
    if (path == null || !File.Exists(path))
    {
        return Results.NotFound(new { Success = false, Message = "Client executable not found on server." });
    }
    return Results.File(path, "application/octet-stream", "EndpointManager.Client.exe");
});

app.MapPost("/api/updates/client/push", async (
    PushUpdateRequest req,
    HttpRequest httpReq,
    AuthService auth,
    ClientUpdateService updateService,
    IHubContext<EndpointHub, IEndpointClientContract> hubContext) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out var currentUsername, out var role, out var permissions))
    {
        return Results.Unauthorized();
    }
    if (!permissions.CanManageUsers && role != "SuperAdmin" && role != "Admin")
    {
        return Results.BadRequest(new { Success = false, Message = "Access Denied: Administrative privilege required to push software updates." });
    }

    string serverBaseUrl = !string.IsNullOrWhiteSpace(req.ServerBaseUrl)
        ? req.ServerBaseUrl.Trim()
        : $"{httpReq.Scheme}://{httpReq.Host}";

    // Sanitize: If localhost or loopback, resolve to public static IP
    if (serverBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
        serverBaseUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
    {
        serverBaseUrl = "http://14.194.19.6:5000";
    }

    var result = await updateService.PushUpdateAsync(req.MachineName ?? "all", currentUsername, hubContext, serverBaseUrl);
    return Results.Ok(new
    {
        Success = result.Success,
        Message = result.Message,
        OnlinePushed = result.OnlinePushed
    });
});

app.MapPost("/api/updates/client/settings", (
    UpdateSettingsRequest req,
    HttpRequest httpReq,
    AuthService auth,
    LiteDbService db) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out var currentUsername, out var role, out _))
    {
        return Results.Unauthorized();
    }
    if (role != "SuperAdmin" && role != "Admin")
    {
        return Results.BadRequest(new { Success = false, Message = "Access Denied: Administrator required." });
    }

    db.SetAutoUpdateFleet(req.AutoUpdateFleet);
    return Results.Ok(new { Success = true, AutoUpdateFleet = req.AutoUpdateFleet });
});

app.MapPost("/api/fleet/power-action", async (
    FleetPowerRequest req,
    HttpRequest httpReq,
    AuthService auth,
    LiteDbService db,
    IHubContext<EndpointHub, IEndpointClientContract> hubContext) =>
{
    var token = AuthService.ExtractBearerToken(httpReq);
    if (!auth.ValidateToken(token, out var currentUsername, out var role, out var permissions))
    {
        return Results.Unauthorized();
    }
    if (!permissions.CanEmergencyAction && role != "SuperAdmin")
    {
        return Results.BadRequest(new { Success = false, Message = "Access Denied: You do not have permission to execute emergency power actions (Restart/Shutdown)." });
    }

    string action = (req.Action ?? "restart").Trim().ToLowerInvariant();
    int delay = Math.Clamp(req.DelaySeconds, 0, 86400); // 0 to 24 hours
    string customMsg = string.IsNullOrWhiteSpace(req.CustomMessage)
        ? (action == "shutdown" ? "IT Maintenance: This system is scheduled to shut down. Please save your work." : "IT Maintenance: This system is scheduled to restart for updates. Please save your work.")
        : req.CustomMessage.Trim();

    // Sanitize customMsg for command line (remove double quotes and backticks)
    customMsg = customMsg.Replace("\"", "'").Replace("`", "");

    // Build the PowerShell command
    string psCmd;
    if (action == "shutdown")
    {
        psCmd = $"shutdown.exe /s /t {delay} /f /c \"{customMsg}\"";
    }
    else if (action == "abort")
    {
        psCmd = "shutdown.exe /a";
    }
    else // default restart
    {
        psCmd = $"shutdown.exe /r /t {delay} /f /c \"{customMsg}\"";
    }

    string targetMachine = req.MachineName?.Trim() ?? "all";
    bool isFleetWide = string.IsNullOrWhiteSpace(targetMachine) ||
                       targetMachine.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                       targetMachine.Equals("fleet", StringComparison.OrdinalIgnoreCase);

    int dispatchedCount = 0;
    var activeConnections = EndpointHub.ConnectedClients.ToArray();

    if (isFleetWide)
    {
        foreach (var kvp in activeConnections)
        {
            try
            {
                var termCmd = new TerminalCommand
                {
                    SessionId = Guid.NewGuid().ToString("N"),
                    CommandText = psCmd
                };
                await hubContext.Clients.Client(kvp.Key).ExecuteTerminalCommand(termCmd);
                dispatchedCount++;
            }
            catch { }
        }

        db.LogAudit(
            "FLEETWIDE",
            "FleetPowerAction",
            true,
            true,
            $"User '{currentUsername}' dispatched {action.ToUpper()} (Countdown: {delay}s) fleet-wide to {dispatchedCount} of {activeConnections.Length} online endpoints. Message: '{customMsg}'",
            actionBy: currentUsername,
            category: "RemoteControl");

        return Results.Ok(new
        {
            Success = true,
            Message = $"Fleet-wide {action.ToUpper()} successfully dispatched to {dispatchedCount} online endpoints (Countdown: {delay}s).",
            DispatchedCount = dispatchedCount,
            TotalOnline = activeConnections.Length,
            Action = action
        });
    }
    else
    {
        // Single machine target (Canary / selective testing)
        var match = activeConnections
            .Where(c => c.Value.MachineName.Trim().Equals(targetMachine, StringComparison.OrdinalIgnoreCase) ||
                        c.Value.MachineName.Trim().Replace("-", "_").Equals(targetMachine.Replace("-", "_"), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Value.TimestampUtc)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(match.Key))
        {
            return Results.BadRequest(new { Success = false, Message = $"Target machine '{targetMachine}' is currently offline." });
        }

        var termCmd = new TerminalCommand
        {
            SessionId = Guid.NewGuid().ToString("N"),
            CommandText = psCmd
        };
        await hubContext.Clients.Client(match.Key).ExecuteTerminalCommand(termCmd);
        dispatchedCount = 1;

        db.LogAudit(
            match.Value.MachineName,
            "FleetPowerAction",
            true,
            true,
            $"User '{currentUsername}' dispatched {action.ToUpper()} (Countdown: {delay}s) to '{match.Value.MachineName}'. Message: '{customMsg}'",
            actionBy: currentUsername,
            category: "RemoteControl");

        return Results.Ok(new
        {
            Success = true,
            Message = $"{action.ToUpper()} successfully dispatched to '{match.Value.MachineName}' (Countdown: {delay}s).",
            DispatchedCount = 1,
            TotalOnline = activeConnections.Length,
            Action = action
        });
    }
});

app.Run();

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? TotpCode { get; set; }
}

public class Verify2FaRequest
{
    public string TempToken { get; set; } = string.Empty;
    public string TotpCode { get; set; } = string.Empty;
    public string Code
    {
        get => TotpCode;
        set { if (!string.IsNullOrEmpty(value)) TotpCode = value; }
    }
}

public class InviteUserRequest
{
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = "Admin";
    public bool SendEmail { get; set; } = true;
    public UserPermissions? Permissions { get; set; }
}

public class CompleteActivationRequest
{
    public string Token { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
    public string TotpCode { get; set; } = string.Empty;
    public string Code
    {
        get => TotpCode;
        set { if (!string.IsNullOrEmpty(value)) TotpCode = value; }
    }
}

public class Reset2FaRequest
{
    public string Username { get; set; } = string.Empty;
    public bool SendEmail { get; set; } = true;
}

public class DeleteUserRequest
{
    public string Username { get; set; } = string.Empty;
}

public class UpdateUserRequest
{
    public string Username { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? Role { get; set; }
    public bool? IsActive { get; set; }
    public bool? Is2FaEnabled { get; set; }
    public string? NewPassword { get; set; }
    public UserPermissions? Permissions { get; set; }
}

public class ToggleUserStatusRequest
{
    public string Username { get; set; } = string.Empty;
}

public class SmtpSettingsDto
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string FromEmail { get; set; } = string.Empty;
    public string SenderEmail
    {
        get => FromEmail;
        set { if (!string.IsNullOrEmpty(value)) FromEmail = value; }
    }
    public string SenderName { get; set; } = "CyberShield MDM";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class TestEmailRequest
{
    public string ToEmail { get; set; } = string.Empty;
    public string RecipientEmail
    {
        get => ToEmail;
        set { if (!string.IsNullOrEmpty(value)) ToEmail = value; }
    }
    public string? Host { get; set; }
    public int? Port { get; set; }
    public bool? EnableSsl { get; set; }
    public string? FromEmail { get; set; }
    public string? SenderEmail
    {
        get => FromEmail;
        set { if (!string.IsNullOrEmpty(value)) FromEmail = value; }
    }
    public string? SenderName { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
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

public class PushUpdateRequest
{
    public string? MachineName { get; set; }
    public string? ServerBaseUrl { get; set; }
}

public class UpdateSettingsRequest
{
    public bool AutoUpdateFleet { get; set; }
}

public class FleetPowerRequest
{
    public string Action { get; set; } = "restart";
    public int DelaySeconds { get; set; } = 300;
    public string? CustomMessage { get; set; }
    public string? MachineName { get; set; }
}
