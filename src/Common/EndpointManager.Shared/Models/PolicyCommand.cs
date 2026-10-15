namespace EndpointManager.Shared.Models;

using EndpointManager.Shared.Enums;

public class PolicyCommand
{
    public Guid CommandId { get; set; } = Guid.NewGuid();
    public PolicyType Policy { get; set; }
    public bool EnableRestriction { get; set; } // true = Block/Restrict, false = Allow
    public DateTime IssuedAtUtc { get; set; } = DateTime.UtcNow;
}
