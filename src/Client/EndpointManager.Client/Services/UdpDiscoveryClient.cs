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

    public static List<IPEndPoint> GetBroadcastEndpoints()
    {
        var endpoints = new List<IPEndPoint>
        {
            new IPEndPoint(IPAddress.Broadcast, DiscoveryConstants.UdpPort)
        };

        try
        {
            var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                            n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback);

            foreach (var ni in interfaces)
            {
                var ipProps = ni.GetIPProperties();
                foreach (var u in ipProps.UnicastAddresses)
                {
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask != null)
                    {
                        var ipBytes = u.Address.GetAddressBytes();
                        var maskBytes = u.IPv4Mask.GetAddressBytes();
                        var broadcastBytes = new byte[4];
                        for (int i = 0; i < 4; i++)
                        {
                            broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                        }
                        var subnetBroadcast = new IPAddress(broadcastBytes);
                        var ep = new IPEndPoint(subnetBroadcast, DiscoveryConstants.UdpPort);
                        if (!endpoints.Any(e => e.Address.Equals(ep.Address)))
                        {
                            endpoints.Add(ep);
                        }
                    }
                }
                foreach (var gw in ipProps.GatewayAddresses)
                {
                    if (gw.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(gw.Address))
                    {
                        var gwEp = new IPEndPoint(gw.Address, DiscoveryConstants.UdpPort);
                        if (!endpoints.Any(e => e.Address.Equals(gwEp.Address)))
                        {
                            endpoints.Add(gwEp);
                        }
                    }
                }
            }
        }
        catch { }

        return endpoints;
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
        var broadcastTargets = GetBroadcastEndpoints();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Send broadcast ping to all network targets
                foreach (var target in broadcastTargets)
                {
                    try { await udpClient.SendAsync(discoverBytes, discoverBytes.Length, target); } catch { }
                }

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
            var broadcastTargets = GetBroadcastEndpoints();

            // Initial ping to all subnet targets
            foreach (var target in broadcastTargets)
            {
                try { await udpClient.SendAsync(discoverBytes, discoverBytes.Length, target); } catch { }
            }
            
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            // Redundant burst ping after 120ms to guarantee delivery over busy Wi-Fi networks
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(120, timeoutCts.Token);
                    foreach (var target in broadcastTargets)
                    {
                        try { await udpClient.SendAsync(discoverBytes, discoverBytes.Length, target); } catch { }
                    }
                }
                catch { }
            }, timeoutCts.Token);

            while (!timeoutCts.IsCancellationRequested)
            {
                var result = await udpClient.ReceiveAsync(timeoutCts.Token);
                var message = Encoding.UTF8.GetString(result.Buffer);
                if (message.StartsWith(DiscoveryConstants.AnnouncePrefix))
                {
                    var serverUrl = message.Substring(DiscoveryConstants.AnnouncePrefix.Length).Trim();
                    _logger.LogInformation("UDP Discovery found local server: {Url}", serverUrl);
                    return serverUrl;
                }
            }
        }
        catch
        {
            // UDP broadcast didn't receive response within timeout
        }
        return null;
    }
}
