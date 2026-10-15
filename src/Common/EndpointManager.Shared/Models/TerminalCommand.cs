namespace EndpointManager.Shared.Models;

public class TerminalCommand
{
    public string SessionId { get; set; } = string.Empty;
    public string CommandText { get; set; } = string.Empty;
}
