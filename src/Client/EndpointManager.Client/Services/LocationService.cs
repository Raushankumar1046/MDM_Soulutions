namespace EndpointManager.Client.Services;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Windows.Devices.Geolocation;

public class LocationData
{
    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("regionName")]
    public string? Region { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("lat")]
    public double? Latitude { get; set; }

    [JsonPropertyName("lon")]
    public double? Longitude { get; set; }

    [JsonPropertyName("isp")]
    public string? Isp { get; set; }

    public double? AccuracyMeters { get; set; }
}

public class LocationService
{
    private readonly ILogger<LocationService> _logger;
    private readonly HttpClient _httpClient;
    private LocationData? _cachedLocation;
    private DateTime _lastFetchUtc = DateTime.MinValue;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5);

    public LocationService(ILogger<LocationService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(4)
        };
    }

    public async Task<LocationData?> GetCurrentLocationAsync()
    {
        if (_cachedLocation != null && (DateTime.UtcNow - _lastFetchUtc) < _cacheDuration)
        {
            return _cachedLocation;
        }

        // 1. Programmatically enable and verify Windows Location Services
        EnsureWindowsLocationServiceEnabled();

        // 2. High-Accuracy Wi-Fi / Windows Location Triangulation
        try
        {
            var winLocation = await GetWindowsNativeLocationAsync();
            if (winLocation != null && winLocation.Latitude.HasValue && winLocation.Longitude.HasValue)
            {
                var ipData = await FetchIpLocationAsync();
                
                _cachedLocation = new LocationData
                {
                    Latitude = winLocation.Latitude,
                    Longitude = winLocation.Longitude,
                    AccuracyMeters = winLocation.AccuracyMeters,
                    City = ipData?.City ?? "Near Wi-Fi AP",
                    Region = ipData?.Region,
                    Country = ipData?.Country ?? "India",
                    Isp = ipData?.Isp != null 
                        ? $"{ipData.Isp} (Wi-Fi High-Accuracy ~{Math.Round(winLocation.AccuracyMeters ?? 30)}m)" 
                        : $"Wi-Fi Accurate (±{Math.Round(winLocation.AccuracyMeters ?? 30)}m)"
                };

                _lastFetchUtc = DateTime.UtcNow;
                _logger.LogInformation("High-Accuracy Wi-Fi Location resolved: [{Lat}, {Lon}] ±{Acc}m",
                    winLocation.Latitude, winLocation.Longitude, winLocation.AccuracyMeters);
                return _cachedLocation;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Windows Wi-Fi Triangulation failed: {Msg}", ex.Message);
        }

        // 3. Fallback to ISP Public IP Geolocation
        _logger.LogInformation("Falling back to ISP Public IP Geolocation...");
        var fallbackIp = await FetchIpLocationAsync();
        if (fallbackIp != null)
        {
            _cachedLocation = fallbackIp;
            _lastFetchUtc = DateTime.UtcNow;
            return _cachedLocation;
        }

        return _cachedLocation;
    }

    private void EnsureWindowsLocationServiceEnabled()
    {
        try
        {
            // Set Windows Location Consent to Allow for all apps/services
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", writable: true);
            if (key != null)
            {
                key.SetValue("Value", "Allow", RegistryValueKind.String);
            }

            // Ensure lfsvc (Geolocation Service) is set to Demand start
            using var srvKey = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\lfsvc", writable: true);
            if (srvKey != null)
            {
                var startVal = srvKey.GetValue("Start");
                if (startVal is int s && s == 4) // if disabled
                {
                    srvKey.SetValue("Start", 3, RegistryValueKind.DWord); // 3 = Demand
                }
            }
        }
        catch { }
    }

    private async Task<LocationData?> GetWindowsNativeLocationAsync()
    {
        try
        {
            // 1. Request access permissions
            var access = await Geolocator.RequestAccessAsync();
            _logger.LogInformation("Windows Geolocation Access Status: {Status}", access);

            if (access == GeolocationAccessStatus.Denied)
            {
                _logger.LogWarning("Windows Location is turned OFF in Windows Settings (Privacy -> Location).");
                return null;
            }

            // 2. Initialize high-accuracy Geolocator
            var geolocator = new Geolocator
            {
                DesiredAccuracy = PositionAccuracy.High,
                DesiredAccuracyInMeters = 30
            };

            // Allow 12 seconds for Wi-Fi adapter scanning & triangulation
            var pos = await geolocator.GetGeopositionAsync(
                maximumAge: TimeSpan.FromMinutes(2),
                timeout: TimeSpan.FromSeconds(12)
            );

            if (pos?.Coordinate?.Point?.Position != null)
            {
                return new LocationData
                {
                    Latitude = pos.Coordinate.Point.Position.Latitude,
                    Longitude = pos.Coordinate.Point.Position.Longitude,
                    AccuracyMeters = pos.Coordinate.Accuracy
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Native Geolocator error: {Message}", ex.Message);
        }

        return null;
    }

    private async Task<LocationData?> FetchIpLocationAsync()
    {
        try
        {
            var response = await _httpClient.GetStringAsync("http://ip-api.com/json");
            var data = JsonSerializer.Deserialize<LocationData>(response);

            if (data != null && !string.IsNullOrEmpty(data.City))
            {
                return data;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("IP location fallback error: {Msg}", ex.Message);
        }

        return null;
    }
}
