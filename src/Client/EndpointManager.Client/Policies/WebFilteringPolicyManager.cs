namespace EndpointManager.Client.Policies;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

[SupportedOSPlatform("windows")]
public class WebFilteringPolicyManager
{
    private readonly ILogger<WebFilteringPolicyManager> _logger;
    private const string BlockMarkerStart = "# --- CYBERSHIELD MDM WEB BLOCK START ---";
    private const string BlockMarkerEnd = "# --- CYBERSHIELD MDM WEB BLOCK END ---";

    private const string ChromePolicyPath = @"SOFTWARE\Policies\Google\Chrome";
    private const string EdgePolicyPath = @"SOFTWARE\Policies\Microsoft\Edge";

    // Standard default list of entertainment, streaming, and social media domains to block
    public static readonly string[] DefaultBlockedDomains = new[]
    {
        // Video & Streaming
        "youtube.com",
        "www.youtube.com",
        "m.youtube.com",
        "youtu.be",
        "googlevideo.com",
        "netflix.com",
        "www.netflix.com",
        "primevideo.com",
        "www.primevideo.com",
        "twitch.tv",
        "www.twitch.tv",
        "hotstar.com",
        "www.hotstar.com",

        // Social Media & Messaging
        "facebook.com",
        "www.facebook.com",
        "fb.com",
        "m.facebook.com",
        "instagram.com",
        "www.instagram.com",
        "threads.net",
        "www.threads.net",
        "twitter.com",
        "www.twitter.com",
        "x.com",
        "www.x.com",
        "tiktok.com",
        "www.tiktok.com",
        "reddit.com",
        "www.reddit.com",
        "snapchat.com",
        "www.snapchat.com",
        "pinterest.com",
        "www.pinterest.com"
    };

    public WebFilteringPolicyManager(ILogger<WebFilteringPolicyManager> logger)
    {
        _logger = logger;
    }

    private static string GetHostsFilePath()
    {
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return Path.Combine(windir, "System32", "drivers", "etc", "hosts");
    }

    public bool IsWebFilteringBlocked()
    {
        try
        {
            // 1. Check Chrome or Edge Policy in Registry
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var chromeBlockKey = baseKey.OpenSubKey($@"{ChromePolicyPath}\URLBlocklist", writable: false);
            if (chromeBlockKey != null && chromeBlockKey.ValueCount > 0)
            {
                return true;
            }

            // 2. Check System Hosts file
            var hostsPath = GetHostsFilePath();
            if (File.Exists(hostsPath))
            {
                var content = File.ReadAllText(hostsPath);
                if (content.Contains(BlockMarkerStart, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read Web Filtering policy status.");
            return false;
        }
    }

    public bool SetWebFilteringBlocked(bool block, IEnumerable<string>? customDomains = null, IEnumerable<string>? allowedExceptions = null)
    {
        var activeDomains = ResolveActiveDomainList(customDomains);
        var activeAllowed = ResolveAllowedList(allowedExceptions);
        _logger.LogInformation("Applying Web Filtering Policy -> Block: {Block}, Domains count: {Count}, Allowed Exceptions count: {AllowedCount}...", 
            block, activeDomains.Count, activeAllowed.Count);

        bool browserPolicySuccess = SetBrowserUrlBlocklist(block, activeDomains, activeAllowed);
        bool hostsPolicySuccess = SetHostsFileSinkhole(block, activeDomains, activeAllowed);

        // Flush Windows DNS resolver cache to apply changes immediately
        FlushDnsResolverCache();

        bool overallSuccess = browserPolicySuccess || hostsPolicySuccess;
        _logger.LogInformation("Web Filtering Policy applied. Result: {Success} (Browser: {B}, Hosts: {H})", 
            overallSuccess, browserPolicySuccess, hostsPolicySuccess);

        return overallSuccess;
    }

    private List<string> ResolveAllowedList(IEnumerable<string>? allowedExceptions)
    {
        var list = new List<string>();
        if (allowedExceptions != null)
        {
            foreach (var a in allowedExceptions)
            {
                var cleaned = CleanDomain(a);
                if (!string.IsNullOrEmpty(cleaned))
                {
                    list.Add(cleaned);
                }
            }
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsDomainAllowed(string domain, List<string> allowedDomains)
    {
        if (allowedDomains == null || allowedDomains.Count == 0) return false;
        var clean = CleanDomain(domain);
        foreach (var allowed in allowedDomains)
        {
            if (clean.Equals(allowed, StringComparison.OrdinalIgnoreCase)) return true;
            if (clean.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase)) return true;

            // Related domains for streaming / video platforms
            if (allowed.Equals("youtube.com", StringComparison.OrdinalIgnoreCase))
            {
                if (clean.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
                    clean.EndsWith(".youtu.be", StringComparison.OrdinalIgnoreCase) ||
                    clean.Equals("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
                    clean.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
                    clean.Equals("ytimg.com", StringComparison.OrdinalIgnoreCase) ||
                    clean.EndsWith(".ytimg.com", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private List<string> ResolveActiveDomainList(IEnumerable<string>? customDomains)
    {
        var list = new List<string>();

        if (customDomains != null && customDomains.Any())
        {
            foreach (var d in customDomains)
            {
                var cleaned = CleanDomain(d);
                if (!string.IsNullOrEmpty(cleaned))
                {
                    list.Add(cleaned);
                    if (!cleaned.StartsWith("www."))
                    {
                        list.Add("www." + cleaned);
                    }
                }
            }
        }

        if (list.Count == 0)
        {
            list.AddRange(DefaultBlockedDomains);
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string CleanDomain(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var s = input.Trim().ToLowerInvariant();
        if (s.StartsWith("https://")) s = s.Substring(8);
        if (s.StartsWith("http://")) s = s.Substring(7);
        int slashIdx = s.IndexOf('/');
        if (slashIdx >= 0) s = s.Substring(0, slashIdx);
        int colonIdx = s.IndexOf(':');
        if (colonIdx >= 0) s = s.Substring(0, colonIdx);
        return s.Trim();
    }

    // =========================================================================
    // LAYER 1: Chrome & Microsoft Edge Enterprise Policy (URLBlocklist & URLAllowlist)
    // =========================================================================
    private bool SetBrowserUrlBlocklist(bool block, List<string> domains, List<string> allowedDomains)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            string[] browserBases = { ChromePolicyPath, EdgePolicyPath };

            foreach (var browserPath in browserBases)
            {
                if (block)
                {
                    using var blockKey = baseKey.CreateSubKey($@"{browserPath}\URLBlocklist", writable: true);
                    if (blockKey != null)
                    {
                        foreach (var val in blockKey.GetValueNames())
                        {
                            try { blockKey.DeleteValue(val); } catch { }
                        }

                        int index = 1;
                        foreach (var domain in domains.Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            blockKey.SetValue(index.ToString(), $"*://*.{domain}/*", RegistryValueKind.String);
                            index++;
                        }
                    }

                    // Populate URLAllowlist: Company domains + Allowed Exceptions for this device
                    using var allowKey = baseKey.CreateSubKey($@"{browserPath}\URLAllowlist", writable: true);
                    if (allowKey != null)
                    {
                        foreach (var val in allowKey.GetValueNames())
                        {
                            try { allowKey.DeleteValue(val); } catch { }
                        }

                        int allowIdx = 1;
                        allowKey.SetValue(allowIdx.ToString(), "*://*.hexalhealth.com/*", RegistryValueKind.String);
                        allowIdx++;

                        foreach (var allowed in allowedDomains.Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            allowKey.SetValue(allowIdx.ToString(), $"*://*.{allowed}/*", RegistryValueKind.String);
                            allowIdx++;
                            allowKey.SetValue(allowIdx.ToString(), $"*://{allowed}/*", RegistryValueKind.String);
                            allowIdx++;

                            if (allowed.Equals("youtube.com", StringComparison.OrdinalIgnoreCase))
                            {
                                allowKey.SetValue(allowIdx.ToString(), "*://*.youtu.be/*", RegistryValueKind.String);
                                allowIdx++;
                                allowKey.SetValue(allowIdx.ToString(), "*://*.googlevideo.com/*", RegistryValueKind.String);
                                allowIdx++;
                                allowKey.SetValue(allowIdx.ToString(), "*://*.ytimg.com/*", RegistryValueKind.String);
                                allowIdx++;
                            }
                        }
                    }
                }
                else
                {
                    // Relax: remove URLBlocklist and URLAllowlist
                    try
                    {
                        baseKey.DeleteSubKeyTree($@"{browserPath}\URLBlocklist", throwOnMissingSubKey: false);
                    }
                    catch { }
                    try
                    {
                        baseKey.DeleteSubKeyTree($@"{browserPath}\URLAllowlist", throwOnMissingSubKey: false);
                    }
                    catch { }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Direct Registry write for browser URLBlocklist failed. Trying reg.exe fallback...");
            return ApplyBrowserPolicyViaRegExe(block, domains, allowedDomains);
        }
    }

    private bool ApplyBrowserPolicyViaRegExe(bool block, List<string> domains, List<string> allowedDomains)
    {
        try
        {
            string[] blockKeys = { $@"HKLM\{ChromePolicyPath}\URLBlocklist", $@"HKLM\{EdgePolicyPath}\URLBlocklist" };
            string[] allowKeys = { $@"HKLM\{ChromePolicyPath}\URLAllowlist", $@"HKLM\{EdgePolicyPath}\URLAllowlist" };

            if (!block)
            {
                foreach (var k in blockKeys.Concat(allowKeys))
                {
                    RunRegCommand($"reg delete \"{k}\" /f");
                }
                return true;
            }

            foreach (var k in blockKeys.Concat(allowKeys))
            {
                RunRegCommand($"reg delete \"{k}\" /f");
            }

            int index = 1;
            foreach (var domain in domains.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var k in blockKeys)
                {
                    RunRegCommand($"reg add \"{k}\" /v {index} /t REG_SZ /d \"*://*.{domain}/*\" /f");
                }
                index++;
            }

            int allowIdx = 1;
            foreach (var k in allowKeys)
            {
                RunRegCommand($"reg add \"{k}\" /v {allowIdx} /t REG_SZ /d \"*://*.hexalhealth.com/*\" /f");
            }
            allowIdx++;

            foreach (var allowed in allowedDomains.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var k in allowKeys)
                {
                    RunRegCommand($"reg add \"{k}\" /v {allowIdx} /t REG_SZ /d \"*://*.{allowed}/*\" /f");
                }
                allowIdx++;
                foreach (var k in allowKeys)
                {
                    RunRegCommand($"reg add \"{k}\" /v {allowIdx} /t REG_SZ /d \"*://{allowed}/*\" /f");
                }
                allowIdx++;

                if (allowed.Equals("youtube.com", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var k in allowKeys)
                    {
                        RunRegCommand($"reg add \"{k}\" /v {allowIdx} /t REG_SZ /d \"*://*.youtu.be/*\" /f");
                        RunRegCommand($"reg add \"{k}\" /v {allowIdx + 1} /t REG_SZ /d \"*://*.googlevideo.com/*\" /f");
                        RunRegCommand($"reg add \"{k}\" /v {allowIdx + 2} /t REG_SZ /d \"*://*.ytimg.com/*\" /f");
                    }
                    allowIdx += 3;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply browser URLBlocklist via reg.exe.");
            return false;
        }
    }

    // =========================================================================
    // LAYER 2: Windows System Hosts File DNS Sinkhole (0.0.0.0)
    // =========================================================================
    private bool SetHostsFileSinkhole(bool block, List<string> domains, List<string> allowedDomains)
    {
        var hostsPath = GetHostsFilePath();

        try
        {
            if (!File.Exists(hostsPath))
            {
                _logger.LogWarning("Hosts file not found at: {Path}", hostsPath);
                return false;
            }

            // Remove read-only attribute if set
            var attributes = File.GetAttributes(hostsPath);
            if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            {
                File.SetAttributes(hostsPath, attributes & ~FileAttributes.ReadOnly);
            }

            var lines = File.ReadAllLines(hostsPath, Encoding.UTF8).ToList();

            // Strip out any existing CYBERSHIELD MDM block section
            var cleanedLines = new List<string>();
            bool inBlockSection = false;

            foreach (var line in lines)
            {
                if (line.Trim().Equals(BlockMarkerStart, StringComparison.OrdinalIgnoreCase))
                {
                    inBlockSection = true;
                    continue;
                }
                if (line.Trim().Equals(BlockMarkerEnd, StringComparison.OrdinalIgnoreCase))
                {
                    inBlockSection = false;
                    continue;
                }

                if (!inBlockSection)
                {
                    cleanedLines.Add(line);
                }
            }

            if (block)
            {
                // Filter out any domains that are allowed exceptions on this machine
                var effectiveSinkholeDomains = domains
                    .Where(d => !IsDomainAllowed(d, allowedDomains))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                cleanedLines.Add(string.Empty);
                cleanedLines.Add(BlockMarkerStart);
                cleanedLines.Add("# CyberShield MDM Automated Security Enforcement Policy");
                cleanedLines.Add($"# Enforced: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");

                foreach (var domain in effectiveSinkholeDomains)
                {
                    cleanedLines.Add($"0.0.0.0 {domain}");
                }

                cleanedLines.Add(BlockMarkerEnd);
            }

            File.WriteAllLines(hostsPath, cleanedLines, Encoding.UTF8);
            _logger.LogInformation("Hosts file successfully updated -> Blocked: {Block} ({Count} domains sinkholed)", block, domains.Count);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogWarning("Direct file access to hosts denied. Attempting elevated PowerShell script execution...");
            return ApplyHostsFileViaPowerShell(block, hostsPath, domains, allowedDomains);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update hosts file.");
            return false;
        }
    }

    private bool ApplyHostsFileViaPowerShell(bool block, string hostsPath, List<string> domains, List<string> allowedDomains)
    {
        try
        {
            string script;
            if (block)
            {
                var effectiveSinkholeDomains = domains
                    .Where(d => !IsDomainAllowed(d, allowedDomains))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var sb = new StringBuilder();
                sb.AppendLine($"$markerStart = '{BlockMarkerStart}'");
                sb.AppendLine($"$markerEnd = '{BlockMarkerEnd}'");
                sb.AppendLine($"$hosts = Get-Content '{hostsPath}'");
                sb.AppendLine("$cleaned = @()");
                sb.AppendLine("$skip = $false");
                sb.AppendLine("foreach ($line in $hosts) {");
                sb.AppendLine("  if ($line.Trim() -eq $markerStart) { $skip = $true; continue }");
                sb.AppendLine("  if ($line.Trim() -eq $markerEnd) { $skip = $false; continue }");
                sb.AppendLine("  if (-not $skip) { $cleaned += $line }");
                sb.AppendLine("}");
                sb.AppendLine("$cleaned += ''");
                sb.AppendLine($"$cleaned += '{BlockMarkerStart}'");
                foreach (var d in effectiveSinkholeDomains)
                {
                    sb.AppendLine($"$cleaned += '0.0.0.0 {d}'");
                }
                sb.AppendLine($"$cleaned += '{BlockMarkerEnd}'");
                sb.AppendLine($"$cleaned | Set-Content -Path '{hostsPath}' -Encoding UTF8");
                script = sb.ToString();
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine($"$markerStart = '{BlockMarkerStart}'");
                sb.AppendLine($"$markerEnd = '{BlockMarkerEnd}'");
                sb.AppendLine($"$hosts = Get-Content '{hostsPath}'");
                sb.AppendLine("$cleaned = @()");
                sb.AppendLine("$skip = $false");
                sb.AppendLine("foreach ($line in $hosts) {");
                sb.AppendLine("  if ($line.Trim() -eq $markerStart) { $skip = $true; continue }");
                sb.AppendLine("  if ($line.Trim() -eq $markerEnd) { $skip = $false; continue }");
                sb.AppendLine("  if (-not $skip) { $cleaned += $line }");
                sb.AppendLine("}");
                sb.AppendLine($"$cleaned | Set-Content -Path '{hostsPath}' -Encoding UTF8");
                script = sb.ToString();
            }

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            return proc?.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerShell fallback for hosts file failed.");
            return false;
        }
    }

    private static void FlushDnsResolverCache()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ipconfig.exe",
                Arguments = "/flushdns",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(2000);
        }
        catch { }
    }

    private static bool RunRegCommand(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
