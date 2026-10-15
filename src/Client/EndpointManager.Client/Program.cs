using EndpointManager.Client;
using EndpointManager.Client.Policies;
using EndpointManager.Client.Services;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = Host.CreateApplicationBuilder(args);

// Configures the service lifetime to Windows Service Control Manager (SCM)
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "EndpointManagerClient";
});

// Register Policy Managers
builder.Services.AddSingleton<UsbPolicyManager>();
builder.Services.AddSingleton<SoftwareInstallPolicyManager>();
builder.Services.AddSingleton<ExternalFileTransferPolicyManager>();

// Register UDP Auto-Discovery Client
builder.Services.AddSingleton<UdpDiscoveryClient>();

// Register Geo-Location Service
builder.Services.AddSingleton<LocationService>();

// Register Remote Assist Services (Terminal & Screen Stream)
builder.Services.AddSingleton<RemoteTerminalService>();
builder.Services.AddSingleton<RemoteScreenService>();

// Register Main SignalR / Endpoint Worker
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
