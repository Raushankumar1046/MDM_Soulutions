namespace EndpointManager.Shared.Contracts;

using EndpointManager.Shared.Models;

public interface IEndpointClientContract
{
    Task ExecutePolicy(PolicyCommand command);
    Task ExecuteTerminalCommand(TerminalCommand command);
    Task StartScreenStream(string targetAdminConnectionId);
    Task StopScreenStream();
    Task ProcessRemoteInput(RemoteInputEvent input);

    // Callbacks for Web Admin Browser
    Task ReceiveTerminalOutput(TerminalOutput output);
    Task ReceiveScreenFrame(string base64Frame);
}

