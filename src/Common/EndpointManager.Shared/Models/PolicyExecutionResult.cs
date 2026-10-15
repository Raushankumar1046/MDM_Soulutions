namespace EndpointManager.Shared.Models;

public class PolicyExecutionResult
{
    public Guid CommandId { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime ExecutedAtUtc { get; set; } = DateTime.UtcNow;
}
