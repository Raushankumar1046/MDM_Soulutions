namespace EndpointManager.Client.Services;

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

public class ClientUpdateManager
{
    public const string CurrentVersion = "1.2.0";
    private readonly ILogger<ClientUpdateManager> _logger;
    private static readonly string UpdatesDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "CyberShieldMDM",
        "updates");

    private bool _isUpdating = false;
    private readonly object _lock = new();

    public ClientUpdateManager(ILogger<ClientUpdateManager> logger)
    {
        _logger = logger;
    }

    public async Task<bool> ApplyUpdateAsync(string downloadUrl, string targetVersion, string? expectedSha256, string? currentServerUrl)
    {
        lock (_lock)
        {
            if (_isUpdating)
            {
                _logger.LogWarning("Update already in progress. Ignoring duplicate trigger.");
                return false;
            }
            _isUpdating = true;
        }

        try
        {
            _logger.LogInformation("Initiating Client Auto-Update to v{Target} from {Url}...", targetVersion, downloadUrl);
            Program.LogRuntime($"[UPDATE] Initiating Client Auto-Update to v{targetVersion} from {downloadUrl}");

            string fullUrl = downloadUrl.Trim();
            if (!fullUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !fullUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var baseServer = (currentServerUrl ?? "http://localhost:5000").Replace("/hubs/endpoints", "");
                fullUrl = $"{baseServer.TrimEnd('/')}/{fullUrl.TrimStart('/')}";
            }

            Directory.CreateDirectory(UpdatesDirectory);
            string tempNewExe = Path.Combine(UpdatesDirectory, "EndpointManager.Client.new.exe");
            if (File.Exists(tempNewExe))
            {
                try { File.Delete(tempNewExe); } catch { }
            }

            // Candidate URLs for download fallback
            var candidateUrls = new List<string>();
            if (!string.IsNullOrWhiteSpace(fullUrl) &&
                !fullUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) &&
                !fullUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                candidateUrls.Add(fullUrl);
            }
            if (!string.IsNullOrWhiteSpace(currentServerUrl))
            {
                var curBase = currentServerUrl.Replace("/hubs/endpoints", "").TrimEnd('/');
                if (!candidateUrls.Contains($"{curBase}/api/updates/client/download"))
                {
                    candidateUrls.Add($"{curBase}/api/updates/client/download");
                }
            }
            if (!candidateUrls.Contains("http://192.168.68.13:5000/api/updates/client/download"))
            {
                candidateUrls.Add("http://192.168.68.13:5000/api/updates/client/download");
            }
            if (!candidateUrls.Contains("http://14.194.19.6:5000/api/updates/client/download"))
            {
                candidateUrls.Add("http://14.194.19.6:5000/api/updates/client/download");
            }

            // 1. Download updated binary with automatic URL failover
            bool downloadSuccess = false;
            using (var httpClient = new HttpClient())
            {
                httpClient.Timeout = TimeSpan.FromMinutes(5);
                foreach (var url in candidateUrls)
                {
                    try
                    {
                        _logger.LogInformation("Downloading client payload from candidate URL: {Url}...", url);
                        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                        if (!response.IsSuccessStatusCode)
                        {
                            _logger.LogWarning("Download failed with status {Code} from {Url}", response.StatusCode, url);
                            continue;
                        }

                        using var srcStream = await response.Content.ReadAsStreamAsync();
                        using var dstStream = new FileStream(tempNewExe, FileMode.Create, FileAccess.Write, FileShare.None);
                        await srcStream.CopyToAsync(dstStream);
                        dstStream.Flush();

                        if (new FileInfo(tempNewExe).Length > 20000000)
                        {
                            downloadSuccess = true;
                            _logger.LogInformation("Download complete from {Url}. Size: {Size} bytes.", url, new FileInfo(tempNewExe).Length);
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Candidate download failed from {Url}: {Err}", url, ex.Message);
                    }
                }
            }

            if (!downloadSuccess || !File.Exists(tempNewExe))
            {
                _logger.LogError("Failed to download client update payload from all endpoints.");
                Program.LogRuntime("[UPDATE ERROR] All candidate download URLs failed.");
                lock (_lock) { _isUpdating = false; }
                return false;
            }

            // 2. Verify SHA-256 Checksum (if provided)
            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                using var sha256 = SHA256.Create();
                using var fs = File.OpenRead(tempNewExe);
                byte[] hashBytes = await sha256.ComputeHashAsync(fs);
                string computedHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

                if (!computedHash.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("Update failed: SHA-256 hash mismatch! Expected: {Exp}, Got: {Got}", expectedSha256, computedHash);
                    Program.LogRuntime($"[UPDATE ERROR] Hash mismatch! Expected: {expectedSha256}, Got: {computedHash}");
                    try { File.Delete(tempNewExe); } catch { }
                    _isUpdating = false;
                    return false;
                }
                _logger.LogInformation("SHA-256 checksum verified successfully: {Hash}", computedHash);
            }

            // 3. Resolve Current Process Binary Location
            string currentExePath;
            try
            {
                currentExePath = Process.GetCurrentProcess().MainModule?.FileName ?? @"C:\Program Files\CyberShieldMDM\EndpointManager.Client.exe";
            }
            catch
            {
                currentExePath = @"C:\Program Files\CyberShieldMDM\EndpointManager.Client.exe";
            }

            string batPath = Path.Combine(UpdatesDirectory, "apply_update.bat");
            string logPath = Path.Combine(UpdatesDirectory, "update_execution.log");

            // 4. Generate Detached Silent Updater Script
            string batContent = $@"@echo off
setlocal EnableDelayedExpansion
set ""TARGET_EXE={currentExePath}""
set ""NEW_EXE={tempNewExe}""
set ""BAK_EXE={currentExePath}.bak""
set ""LOG_FILE={logPath}""

echo ========================================================== >> ""!LOG_FILE!""
echo [%date% %time%] CyberShield Client OTA Silent Updater Starting >> ""!LOG_FILE!""
echo ========================================================== >> ""!LOG_FILE!""

:: Allow parent process up to 3 seconds to exit gracefully
ping 127.0.0.1 -n 4 >nul

:: Terminate running client instances to release file locks
taskkill /f /im EndpointManager.Client.exe >> ""!LOG_FILE!"" 2>&1
ping 127.0.0.1 -n 2 >nul

:: Backup current executable
if exist ""!TARGET_EXE!"" (
    copy /y ""!TARGET_EXE!"" ""!BAK_EXE!"" >> ""!LOG_FILE!"" 2>&1
)

:: Replace running executable with downloaded new version
copy /y ""!NEW_EXE!"" ""!TARGET_EXE!"" >> ""!LOG_FILE!"" 2>&1
if !errorlevel! neq 0 (
    echo [%date% %time%] ERROR: File replacement failed! Restoring backup... >> ""!LOG_FILE!""
    copy /y ""!BAK_EXE!"" ""!TARGET_EXE!"" >> ""!LOG_FILE!"" 2>&1
) else (
    echo [%date% %time%] SUCCESS: Binary replaced with v{targetVersion}. >> ""!LOG_FILE!""
    del /f /q ""!NEW_EXE!"" >nul 2>&1
)

:: Unblock downloaded binary
powershell -NoProfile -Command ""Unblock-File -Path '!TARGET_EXE!'"" >> ""!LOG_FILE!"" 2>&1

:: Launch updated client executable
echo [%date% %time%] Launching updated client agent: !TARGET_EXE! >> ""!LOG_FILE!""
start """" ""!TARGET_EXE!""

echo [%date% %time%] OTA Update sequence completed. >> ""!LOG_FILE!""
endlocal
";
            File.WriteAllText(batPath, batContent);

            _logger.LogInformation("Launching detached silent updater script: {Bat}", batPath);
            Program.LogRuntime($"[UPDATE] Dispatched detached updater. Terminating current process to finalize update to v{targetVersion}...");

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batPath}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);

            // Give the detached process a moment to initialize before exiting
            await Task.Delay(200);

            // Exit cleanly so cmd.exe can replace the file
            Environment.Exit(0);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply Client Auto-Update.");
            Program.LogRuntime($"[UPDATE ERROR] Exception: {ex.Message}");
            lock (_lock) { _isUpdating = false; }
            return false;
        }
    }

    public async Task CheckForAutomaticUpdateAsync(string? currentServerUrl, CancellationToken cancellationToken)
    {
        try
        {
            var baseServer = (currentServerUrl ?? "http://localhost:5000").Replace("/hubs/endpoints", "");
            string infoUrl = $"{baseServer.TrimEnd('/')}/api/updates/client/info";

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(5);
            var res = await httpClient.GetAsync(infoUrl, cancellationToken);
            if (res.IsSuccessStatusCode)
            {
                var json = await res.Content.ReadAsStringAsync(cancellationToken);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                bool autoUpdateFleet = root.TryGetProperty("autoUpdateFleet", out var autoProp) && autoProp.GetBoolean();
                if (autoUpdateFleet)
                {
                    string serverVer = root.TryGetProperty("version", out var vProp) ? vProp.GetString() ?? "" : "";
                    string dlUrl = root.TryGetProperty("downloadUrl", out var dlProp) ? dlProp.GetString() ?? "" : "";
                    string sha = root.TryGetProperty("sha256", out var shaProp) ? shaProp.GetString() ?? "" : "";

                    if (!string.IsNullOrEmpty(serverVer) && IsNewerVersion(serverVer, CurrentVersion))
                    {
                        _logger.LogInformation("Newer client version found on Central Server: v{Server} (Current: v{Local}). Starting automatic background update...", serverVer, CurrentVersion);
                        Program.LogRuntime($"[AUTO-UPDATE] Server version v{serverVer} > Local v{CurrentVersion}. Triggering automatic background update...");
                        _ = ApplyUpdateAsync(dlUrl, serverVer, sha, currentServerUrl);
                    }
                }
            }
        }
        catch { }
    }

    public static bool IsNewerVersion(string remoteVersion, string localVersion)
    {
        try
        {
            var rParts = remoteVersion.Trim().TrimStart('v', 'V').Split('.');
            var lParts = localVersion.Trim().TrimStart('v', 'V').Split('.');

            int length = Math.Max(rParts.Length, lParts.Length);
            for (int i = 0; i < length; i++)
            {
                int rVal = i < rParts.Length && int.TryParse(rParts[i], out int rv) ? rv : 0;
                int lVal = i < lParts.Length && int.TryParse(lParts[i], out int lv) ? lv : 0;

                if (rVal > lVal) return true;
                if (rVal < lVal) return false;
            }
        }
        catch { }
        return false;
    }
}

