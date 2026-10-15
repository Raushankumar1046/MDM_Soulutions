namespace EndpointManager.Shared.Models;

using EndpointManager.Shared.Enums;

public class PolicyCommand
{
    public Guid CommandId { get; set; } = Guid.NewGuid();
    public PolicyType Policy { get; set; }
    public bool EnableRestriction { get; set; } // true = Block/Restrict, false = Allow
    public string? Payload { get; set; } // Custom text, e.g. broadcast notification message
    public DateTime IssuedAtUtc { get; set; } = DateTime.UtcNow;
}
