namespace EndpointManager.Client.Services;

using System.Net;
using System.Net.Sockets;
using System.Text;
using EndpointManager.Shared.Discovery;

public class UdpDiscoveryClient
{
    private readonly ILogger<UdpDiscoveryClient> _logger;

    public UdpDiscoveryClient(ILogger<UdpDiscoveryClient> logger)
    {
        _logger = logger;
    }

    public async Task<string> DiscoverServerUrlAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Broadcasting UDP discovery requests on local network (Port {Port})...", DiscoveryConstants.UdpPort);

        using var udpClient = new UdpClient();
        udpClient.EnableBroadcast = true;
        // Bind to any ephemeral port for receiving response
        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var discoverBytes = Encoding.UTF8.GetBytes(DiscoveryConstants.DiscoverMessage);
        var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryConstants.UdpPort);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Send broadcast ping
                await udpClient.SendAsync(discoverBytes, discoverBytes.Length, broadcastEndpoint);
                _logger.LogDebug("Sent UDP discovery ping to 255.255.255.255:{Port}", DiscoveryConstants.UdpPort);

                // Wait up to 2.5 seconds for response
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(2.5));

                try
                {
                    while (!timeoutCts.IsCancellationRequested)
                    {
                        var receiveTask = udpClient.ReceiveAsync(timeoutCts.Token).AsTask();
                        var result = await receiveTask;
                        var message = Encoding.UTF8.GetString(result.Buffer);

                        if (message.StartsWith(DiscoveryConstants.AnnouncePrefix))
                        {
                            var serverUrl = message.Substring(DiscoveryConstants.AnnouncePrefix.Length).Trim();
                            _logger.LogInformation("SUCCESS: Discovered Central Management Server at {Url} from {Endpoint}!", 
                                serverUrl, result.RemoteEndPoint);
                            return serverUrl;
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Timeout for this ping interval, loop and ping again
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("UDP discovery probe failed: {Msg}. Retrying...", ex.Message);
                await Task.Delay(2000, cancellationToken);
            }
        }

        throw new OperationCanceledException(cancellationToken);
    }

    public async Task<string?> TryDiscoverServerUrlAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var udpClient = new UdpClient();
            udpClient.EnableBroadcast = true;
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            var discoverBytes = Encoding.UTF8.GetBytes(DiscoveryConstants.DiscoverMessage);
            var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryConstants.UdpPort);

            await udpClient.SendAsync(discoverBytes, discoverBytes.Length, broadcastEndpoint);
            
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var result = await udpClient.ReceiveAsync(timeoutCts.Token);
            var message = Encoding.UTF8.GetString(result.Buffer);
            if (message.StartsWith(DiscoveryConstants.AnnouncePrefix))
            {
                var serverUrl = message.Substring(DiscoveryConstants.AnnouncePrefix.Length).Trim();
                _logger.LogInformation("UDP Discovery found local server: {Url}", serverUrl);
                return serverUrl;
            }
        }
        catch
        {
            // UDP broadcast didn't receive response within timeout
        }
        return null;
    }
}
