namespace EndpointManager.Shared.Contracts;

using EndpointManager.Shared.Models;

public interface IEndpointClientContract
{
    Task ExecutePolicy(PolicyCommand command);
}
