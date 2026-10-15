namespace EndpointManager.Client.Services;

using System.Diagnostics;
using System.Text;
using EndpointManager.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

public class RemoteTerminalService
{
    private readonly ILogger<RemoteTerminalService> _logger;

    public RemoteTerminalService(ILogger<RemoteTerminalService> logger)
    {
        _logger = logger;
    }

    public async Task ExecuteCommandAsync(TerminalCommand command, HubConnection? hubConnection)
    {
        _logger.LogInformation("Executing Terminal Command [Session {Session}]: {Cmd}", 
            command.SessionId, command.CommandText);

        if (hubConnection == null || hubConnection.State != HubConnectionState.Connected)
        {
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command.CommandText.Replace("\"", "\\\"")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };

            process.OutputDataReceived += async (s, e) =>
            {
                if (e.Data != null)
                {
                    try
                    {
                        await hubConnection.InvokeAsync("SendTerminalOutput", new TerminalOutput
                        {
                            SessionId = command.SessionId,
                            OutputText = e.Data,
                            IsError = false,
                            IsCompleted = false
                        });
                    }
                    catch { }
                }
            };

            process.ErrorDataReceived += async (s, e) =>
            {
                if (e.Data != null)
                {
                    try
                    {
                        await hubConnection.InvokeAsync("SendTerminalOutput", new TerminalOutput
                        {
                            SessionId = command.SessionId,
                            OutputText = e.Data,
                            IsError = true,
                            IsCompleted = false
                        });
                    }
                    catch { }
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Wait with a max timeout of 60 seconds per command
            var completed = await Task.Run(() => process.WaitForExit(60000));
            if (!completed)
            {
                try { process.Kill(true); } catch { }
            }

            // Signal completion
            await hubConnection.InvokeAsync("SendTerminalOutput", new TerminalOutput
            {
                SessionId = command.SessionId,
                OutputText = string.Empty,
                IsError = false,
                IsCompleted = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute terminal command.");
            try
            {
                await hubConnection.InvokeAsync("SendTerminalOutput", new TerminalOutput
                {
                    SessionId = command.SessionId,
                    OutputText = $"Command execution error: {ex.Message}",
                    IsError = true,
                    IsCompleted = true
                });
            }
            catch { }
        }
    }
}
