namespace EndpointManager.Server.Services;

using System.Net;
using System.Net.Sockets;
using System.Text;
using EndpointManager.Shared.Discovery;

public class UdpDiscoveryServerService : BackgroundService
{
    private readonly ILogger<UdpDiscoveryServerService> _logger;
    private readonly int _serverHttpPort;

    public UdpDiscoveryServerService(ILogger<UdpDiscoveryServerService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _serverHttpPort = configuration.GetValue<int>("ServerPort", 5000);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting UDP Auto-Discovery Service on UDP Port {Port}...", DiscoveryConstants.UdpPort);

        UdpClient? udpServer = null;
        try
        {
            udpServer = new UdpClient();
            udpServer.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpServer.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryConstants.UdpPort));
            udpServer.EnableBroadcast = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to bind UDP Discovery socket on port {Port}.", DiscoveryConstants.UdpPort);
            return;
        }

        using (udpServer)
        {
            // Task 1: Periodic broadcast announcement loop (every 5 seconds)
            var broadcastTask = Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var lanIp = GetLocalLanIp();
                        if (!string.IsNullOrEmpty(lanIp))
                        {
                            var announcePayload = $"{DiscoveryConstants.AnnouncePrefix}http://{lanIp}:{_serverHttpPort}/hubs/endpoints";
                            var bytes = Encoding.UTF8.GetBytes(announcePayload);
                            var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryConstants.UdpPort);
                            await udpServer.SendAsync(bytes, bytes.Length, broadcastEndpoint);
                        }

                        await Task.Delay(5000, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch { }
                }
            }, stoppingToken);

            // Task 2: Listen for direct discovery pings from new clients
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var receiveResult = await udpServer.ReceiveAsync(stoppingToken);
                    var message = Encoding.UTF8.GetString(receiveResult.Buffer);

                    if (message == DiscoveryConstants.DiscoverMessage)
                    {
                        var lanIp = GetLocalLanIp();
                        var serverUrl = $"http://{lanIp}:{_serverHttpPort}/hubs/endpoints";
                        var responsePayload = $"{DiscoveryConstants.AnnouncePrefix}{serverUrl}";
                        var responseBytes = Encoding.UTF8.GetBytes(responsePayload);

                        await udpServer.SendAsync(responseBytes, responseBytes.Length, receiveResult.RemoteEndPoint);
                        _logger.LogInformation("Discovered client at {ClientEp}. Sent Server URL: {Url}", receiveResult.RemoteEndPoint, serverUrl);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error handling UDP discovery request.");
                }
            }

            try
            {
                await broadcastTask;
            }
            catch (OperationCanceledException) { }
        }
    }

    public static string GetLocalLanIp()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                {
                    return ip.ToString();
                }
            }
        }
        catch { }
        return "127.0.0.1";
    }
}
