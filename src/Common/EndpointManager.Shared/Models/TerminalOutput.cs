namespace EndpointManager.Shared.Models;

public class TerminalOutput
{
    public string SessionId { get; set; } = string.Empty;
    public string OutputText { get; set; } = string.Empty;
    public bool IsError { get; set; }
    public bool IsCompleted { get; set; }
}
