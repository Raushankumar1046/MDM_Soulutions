namespace EndpointManager.Server.Services;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using EndpointManager.Server.Hubs;
using EndpointManager.Shared.Contracts;
using EndpointManager.Shared.Models;

public class ClientUpdateInfo
{
    public string Version { get; set; } = "1.2.0";
    public long FileSize { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = "/api/updates/client/download";
    public string ReleaseNotes { get; set; } = "Selective Print Guard, Granular RBAC, and OTA Silent Auto-Update Engine";
    public bool AutoUpdateFleet { get; set; }
    public DateTime? LastModifiedUtc { get; set; }
}

public class ClientUpdateService
{
    public const string ServerClientVersion = "1.2.0";
    private readonly ILogger<ClientUpdateService> _logger;
    private readonly LiteDbService _liteDb;

    private string _cachedSha256 = string.Empty;
    private long _cachedFileSize = 0;
    private DateTime _cachedFileTime = DateTime.MinValue;
    private readonly object _lock = new();

    public ClientUpdateService(ILogger<ClientUpdateService> logger, LiteDbService liteDb)
    {
        _logger = logger;
        _liteDb = liteDb;
    }

    public string? GetLatestClientFilePath()
    {
        string baseDir = AppContext.BaseDirectory;

        // Path candidate 1: publish/Client/EndpointManager.Client.exe (relative to publish/Server)
        string p1 = Path.GetFullPath(Path.Combine(baseDir, "..", "Client", "EndpointManager.Client.exe"));
        if (File.Exists(p1)) return p1;

        // Path candidate 2: wwwroot/downloads/EndpointManager.Client.exe
        string p2 = Path.Combine(baseDir, "wwwroot", "downloads", "EndpointManager.Client.exe");
        if (File.Exists(p2)) return p2;

        // Path candidate 3: in same server root folder
        string p3 = Path.Combine(baseDir, "EndpointManager.Client.exe");
        if (File.Exists(p3)) return p3;

        // Path candidate 4: Development workspace publish folder
        string p4 = @"C:\Users\hexah\OneDrive\Desktop\MDM\publish\Client\EndpointManager.Client.exe";
        if (File.Exists(p4)) return p4;

        return null;
    }

    public ClientUpdateInfo GetUpdateInfo()
    {
        string? filePath = GetLatestClientFilePath();
        long fileSize = 0;
        string sha256 = string.Empty;
        DateTime? lastModified = null;

        if (filePath != null && File.Exists(filePath))
        {
            try
            {
                var fi = new FileInfo(filePath);
                fileSize = fi.Length;
                lastModified = fi.LastWriteTimeUtc;

                lock (_lock)
                {
                    if (_cachedFileTime == fi.LastWriteTimeUtc && !string.IsNullOrEmpty(_cachedSha256))
                    {
                        sha256 = _cachedSha256;
                    }
                    else
                    {
                        using var sha = SHA256.Create();
                        using var fs = File.OpenRead(filePath);
                        byte[] hash = sha.ComputeHash(fs);
                        sha256 = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();

                        _cachedSha256 = sha256;
                        _cachedFileSize = fileSize;
                        _cachedFileTime = fi.LastWriteTimeUtc;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to hash client binary: {Msg}", ex.Message);
            }
        }

        return new ClientUpdateInfo
        {
            Version = ServerClientVersion,
            FileSize = fileSize,
            Sha256 = sha256,
            DownloadUrl = "/api/updates/client/download",
            ReleaseNotes = "Selective Print Guard, Granular RBAC, and OTA Silent Auto-Update Engine",
            AutoUpdateFleet = _liteDb.GetAutoUpdateFleet(),
            LastModifiedUtc = lastModified
        };
    }

    public static string BuildUniversalUpdateCommand(string downloadUrl, string targetVersion, string sha256)
    {
        // 1. Build the robust, self-contained PowerShell script that runs detached in the background
        var ps1 = new System.Text.StringBuilder();
        ps1.AppendLine("$ProgressPreference = 'SilentlyContinue';");
        ps1.AppendLine("$ErrorActionPreference = 'Continue';");
        ps1.AppendLine("$logDir = 'C:\\ProgramData\\CyberShieldMDM';");
        ps1.AppendLine("if (-not (Test-Path $logDir)) { try { New-Item -ItemType Directory -Path $logDir -Force | Out-Null; } catch {} }");
        ps1.AppendLine("$logFile = if (Test-Path $logDir) { Join-Path $logDir 'ota_update.log' } else { Join-Path $env:TEMP 'ota_update.log' };");
        ps1.AppendLine("function Log-Ota($msg) {");
        ps1.AppendLine("    $line = \"[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $msg\";");
        ps1.AppendLine("    Write-Output $line;");
        ps1.AppendLine("    try { Add-Content -Path $logFile -Value $line -ErrorAction SilentlyContinue; } catch {}");
        ps1.AppendLine("}");
        ps1.AppendLine("Log-Ota '==========================================================';");
        ps1.AppendLine($"Log-Ota 'CyberShield MDM Silent OTA Updater Triggered (Target: v{targetVersion})';");
        ps1.AppendLine("Log-Ota \"User: $env:USERNAME | Computer: $env:COMPUTERNAME\";");

        // Target executable resolution
        ps1.AppendLine("$proc = Get-Process -Name 'EndpointManager.Client' -ErrorAction SilentlyContinue | Select-Object -First 1;");
        ps1.AppendLine("$targetExe = if ($proc -and $proc.Path) { $proc.Path } else { 'C:\\Program Files\\CyberShieldMDM\\EndpointManager.Client.exe' };");
        ps1.AppendLine("$targetDir = Split-Path -Parent $targetExe;");
        ps1.AppendLine("Log-Ota \"Target Executable: $targetExe\";");

        // Working directory for downloading payload (always use $env:TEMP so no permission issues occur)
        ps1.AppendLine("$workDir = $env:TEMP;");
        ps1.AppendLine("$newExe = Join-Path $workDir 'EndpointManager.Client.new.exe';");
        ps1.AppendLine("if (Test-Path $newExe) { try { Remove-Item $newExe -Force -ErrorAction SilentlyContinue; } catch {} }");

        // Dynamically detect server IP from active established connection to port 5000
        ps1.AppendLine("$activeConn = Get-NetTCPConnection -RemotePort 5000 -State Established -ErrorAction SilentlyContinue | Select-Object -First 1;");
        ps1.AppendLine("$detectedServerIp = if ($activeConn) { $activeConn.RemoteAddress } else { '192.168.68.152' };");
        ps1.AppendLine("Log-Ota \"Active server IP detected from TCP connection: $detectedServerIp\";");

        // Multi-endpoint candidate URLs with fallback (Active LAN IP -> 192.168.68.152 -> Primary -> 14.194.19.6)
        ps1.AppendLine($"$primaryUrl = '{downloadUrl}';");
        ps1.AppendLine("$candidateUrls = @(");
        ps1.AppendLine("    \"http://${detectedServerIp}:5000/api/updates/client/download\",");
        ps1.AppendLine("    'http://192.168.68.152:5000/api/updates/client/download',");
        ps1.AppendLine("    $primaryUrl,");
        ps1.AppendLine("    'http://192.168.68.13:5000/api/updates/client/download',");
        ps1.AppendLine("    'http://14.194.19.6:5000/api/updates/client/download'");
        ps1.AppendLine(") | Where-Object { $_ -and $_ -notmatch 'localhost' -and $_ -notmatch '127\\.0\\.0\\.1' } | Select-Object -Unique;");

        ps1.AppendLine("Log-Ota \"Attempting download from candidate endpoints: $($candidateUrls -join ', ')\";");

        // Attempt download from candidate endpoints in sequence
        ps1.AppendLine("$downloadSuccess = $false;");
        ps1.AppendLine("foreach ($u in $candidateUrls) {");
        ps1.AppendLine("    Log-Ota \"Trying download URL: $u ...\";");
        ps1.AppendLine("    try {");
        ps1.AppendLine("        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls11 -bor [Net.SecurityProtocolType]::Tls;");
        ps1.AppendLine("        [System.Net.ServicePointManager]::ServerCertificateValidationCallback = {$true};");
        ps1.AppendLine("        $wc = New-Object System.Net.WebClient;");
        ps1.AppendLine("        $wc.Headers.Add('User-Agent', 'CyberShield-OTA-Updater/1.2.0');");
        ps1.AppendLine("        $wc.DownloadFile($u, $newExe);");
        ps1.AppendLine("        if (Test-Path $newExe) {");
        ps1.AppendLine("            $sz = (Get-Item $newExe).Length;");
        ps1.AppendLine("            Log-Ota \"Downloaded file size: $sz bytes.\";");
        ps1.AppendLine("            if ($sz -gt 20000000) {");
        ps1.AppendLine("                $downloadSuccess = $true;");
        ps1.AppendLine("                Log-Ota \"Download successful from $u!\";");
        ps1.AppendLine("                break;");
        ps1.AppendLine("            } else {");
        ps1.AppendLine("                Log-Ota \"Downloaded file too small ($sz bytes). Retrying next candidate URL...\";");
        ps1.AppendLine("                Remove-Item $newExe -Force -ErrorAction SilentlyContinue;");
        ps1.AppendLine("            }");
        ps1.AppendLine("        }");
        ps1.AppendLine("    } catch {");
        ps1.AppendLine("        Log-Ota \"Download failed from ${u}: $($_.Exception.Message)\";");
        ps1.AppendLine("    }");
        ps1.AppendLine("}");

        ps1.AppendLine("if (-not $downloadSuccess -or -not (Test-Path $newExe)) {");
        ps1.AppendLine("    Log-Ota 'CRITICAL ERROR: Failed to download update payload from all endpoints. Aborting update.';");
        ps1.AppendLine("    exit 1;");
        ps1.AppendLine("}");

        // 3. Write detached swap-and-restart script
        ps1.AppendLine("$swapScript = Join-Path $env:TEMP 'swap_client.ps1';");
        ps1.AppendLine("$swapLines = @(");
        ps1.AppendLine("    'Start-Sleep -Seconds 2',");
        ps1.AppendLine("    'Stop-Process -Name ''EndpointManager.Client'' -Force -ErrorAction SilentlyContinue',");
        ps1.AppendLine("    '& taskkill.exe /f /im EndpointManager.Client.exe 2>&1 | Out-Null',");
        ps1.AppendLine("    'for ($i = 0; $i -lt 10; $i++) {',");
        ps1.AppendLine("    '    Start-Sleep -Milliseconds 500',");
        ps1.AppendLine("    '    $p = Get-Process -Name ''EndpointManager.Client'' -ErrorAction SilentlyContinue',");
        ps1.AppendLine("    '    if (-not $p) { break }',");
        ps1.AppendLine("    '}',");
        ps1.AppendLine("    '$target = ''C:\\Program Files\\CyberShieldMDM\\EndpointManager.Client.exe''',");
        ps1.AppendLine("    '$new = Join-Path $env:TEMP ''EndpointManager.Client.new.exe''',");
        ps1.AppendLine("    '$bak = ''C:\\Program Files\\CyberShieldMDM\\EndpointManager.Client.exe.bak''',");
        ps1.AppendLine("    'if (Test-Path $target) {',");
        ps1.AppendLine("    '    try { Remove-Item $bak -Force -ErrorAction SilentlyContinue } catch {}',");
        ps1.AppendLine("    '    try { Move-Item -Path $target -Destination $bak -Force -ErrorAction SilentlyContinue } catch {}',");
        ps1.AppendLine("    '}',");
        ps1.AppendLine("    'try {',");
        ps1.AppendLine("    '    Copy-Item -Path $new -Destination $target -Force',");
        ps1.AppendLine("    '    Remove-Item $new -Force -ErrorAction SilentlyContinue',");
        ps1.AppendLine("    '} catch {',");
        ps1.AppendLine("    '    Start-Process powershell.exe -ArgumentList (\"-NoProfile -Command `\"Copy-Item ''$new'' ''$target'' -Force`\"\") -Verb RunAs -Wait -WindowStyle Hidden',");
        ps1.AppendLine("    '}',");
        ps1.AppendLine("    'try { Unblock-File -Path $target -ErrorAction SilentlyContinue } catch {}',");
        ps1.AppendLine("    '& schtasks.exe /run /tn ''CyberShieldMDMClient'' 2>&1 | Out-Null',");
        ps1.AppendLine("    'Start-Sleep -Seconds 3',");
        ps1.AppendLine("    '$p = Get-Process -Name ''EndpointManager.Client'' -ErrorAction SilentlyContinue',");
        ps1.AppendLine("    'if (-not $p) {',");
        ps1.AppendLine("    '    Start-Process -FilePath $target -WorkingDirectory ''C:\\Program Files\\CyberShieldMDM''',");
        ps1.AppendLine("    '}'");
        ps1.AppendLine(");");
        ps1.AppendLine("[System.IO.File]::WriteAllLines($swapScript, $swapLines);");
        ps1.AppendLine("Log-Ota 'Launching detached swap script and exiting launcher...';");
        ps1.AppendLine("Start-Process powershell.exe -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"' + $swapScript + '\"');");
        ps1.AppendLine("Log-Ota 'OTA Updater Dispatched Successfully!';");

        return ps1.ToString();
    }

    public async Task<(bool Success, string Message, int OnlinePushed)> PushUpdateAsync(
        string targetMachine,
        string adminUsername,
        IHubContext<EndpointHub, IEndpointClientContract> hubContext,
        string? serverBaseUrl = null)
    {
        var info = GetUpdateInfo();
        if (info.FileSize == 0)
        {
            return (false, "Client executable payload was not found on server.", 0);
        }

        // Construct absolute download URL so remote clients know where to pull the binary.
        // Sanitize: If empty, localhost, or loopback, resolve to public static IP
        string fullDownloadUrl = info.DownloadUrl;
        if (!string.IsNullOrWhiteSpace(serverBaseUrl) &&
            !serverBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) &&
            !serverBaseUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            fullDownloadUrl = $"{serverBaseUrl.TrimEnd('/')}/{info.DownloadUrl.TrimStart('/')}";
        }
        else
        {
            fullDownloadUrl = $"http://14.194.19.6:5000/{info.DownloadUrl.TrimStart('/')}";
        }

        // Generate universal PowerShell command for legacy clients that don't have ExecuteClientUpdate
        string universalCmdText = BuildUniversalUpdateCommand(fullDownloadUrl, info.Version, info.Sha256);
        var terminalCmd = new TerminalCommand
        {
            SessionId = Guid.NewGuid().ToString("N"),
            CommandText = universalCmdText
        };

        bool isFleetWide = string.IsNullOrWhiteSpace(targetMachine) ||
                           targetMachine.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                           targetMachine.Equals("fleet", StringComparison.OrdinalIgnoreCase);

        if (isFleetWide)
        {
            // Enable auto-update for all reconnecting offline devices
            _liteDb.SetAutoUpdateFleet(true);

            // 1. Broadcast native signal (for v1.2.0+ clients)
            await hubContext.Clients.All.ExecuteClientUpdate(fullDownloadUrl, info.Version, info.Sha256);

            // 2. Direct push to EACH currently connected client connection ID
            int dispatchedCount = 0;
            var activeConnections = EndpointHub.ConnectedClients.ToArray();
            foreach (var kvp in activeConnections)
            {
                try
                {
                    // Avoid re-updating clients that already report the target version
                    if (string.Equals(kvp.Value.ClientVersion, info.Version, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var perClientCmd = new TerminalCommand
                    {
                        SessionId = Guid.NewGuid().ToString("N"),
                        CommandText = universalCmdText
                    };

                    await hubContext.Clients.Client(kvp.Key).ExecuteTerminalCommand(perClientCmd);
                    dispatchedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed dispatching update to {Host} ({Id}): {Msg}", kvp.Value.MachineName, kvp.Key, ex.Message);
                }
            }

            int onlineCount = activeConnections.Length;

            _liteDb.LogAudit(
                "FLEETWIDE",
                "ClientAutoUpdate",
                true,
                true,
                $"User '{adminUsername}' broadcasted Client Update v{info.Version} fleet-wide. Direct push dispatched to {dispatchedCount} of {onlineCount} online endpoints (URL: {fullDownloadUrl}).",
                actionBy: adminUsername,
                category: "ClientUpdate");

            _logger.LogInformation("Fleet-wide client update v{Ver} dispatched by '{Admin}'. Dispatched: {Dispatched}/{Online}", info.Version, adminUsername, dispatchedCount, onlineCount);
            return (true, $"Fleet-wide client update v{info.Version} successfully dispatched! {dispatchedCount} online endpoints are updating immediately.", dispatchedCount);
        }
        else
        {
            // Single target device for Canary / Staged testing
            string cleanTarget = targetMachine.Trim();
            string? targetConnectionId = null;
            var match = EndpointHub.ConnectedClients
                .Where(c => c.Value.MachineName.Trim().Equals(cleanTarget, StringComparison.OrdinalIgnoreCase) ||
                            c.Value.MachineName.Trim().Replace("-", "_").Equals(cleanTarget.Replace("-", "_"), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.Value.TimestampUtc)
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(match.Key))
            {
                targetConnectionId = match.Key;
            }

            if (string.IsNullOrEmpty(targetConnectionId))
            {
                return (false, $"Target machine '{targetMachine}' is currently offline. It will receive the update automatically when it reconnects if fleet auto-update is enabled.", 0);
            }

            // 1. Push native signal (for v1.2.0+ clients)
            await hubContext.Clients.Client(targetConnectionId).ExecuteClientUpdate(fullDownloadUrl, info.Version, info.Sha256);

            // 2. Push universal terminal command (for legacy v1.0.0 / v1.1.0 clients)
            await hubContext.Clients.Client(targetConnectionId).ExecuteTerminalCommand(terminalCmd);

            _liteDb.LogAudit(
                targetMachine,
                "ClientAutoUpdate",
                true,
                true,
                $"User '{adminUsername}' pushed Client Update v{info.Version} to '{targetMachine}' for testing (URL: {fullDownloadUrl}).",
                actionBy: adminUsername,
                category: "ClientUpdate");

            _logger.LogInformation("Canary update v{Ver} dispatched by '{Admin}' to [{Machine}] ({Id}) via Dual Signal & Terminal Command", info.Version, adminUsername, targetMachine, targetConnectionId);
            return (true, $"Client Update v{info.Version} successfully pushed to '{targetMachine}'. The laptop will silently update in 2-3 seconds.", 1);
        }
    }
}

