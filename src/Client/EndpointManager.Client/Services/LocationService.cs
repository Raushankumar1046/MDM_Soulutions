namespace EndpointManager.Client.Services;

using System.Text.Json;
using System.Text.Json.Serialization;
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
            Timeout = TimeSpan.FromSeconds(3)
        };
    }

    public async Task<LocationData?> GetCurrentLocationAsync()
    {
        if (_cachedLocation != null && (DateTime.UtcNow - _lastFetchUtc) < _cacheDuration)
        {
            return _cachedLocation;
        }

        // 1. First Priority: Try Windows High-Accuracy Wi-Fi Triangulation API
        try
        {
            var winLocation = await GetWindowsNativeLocationAsync();
            if (winLocation != null && winLocation.Latitude.HasValue && winLocation.Longitude.HasValue)
            {
                // We got high-accuracy physical coordinates!
                // Enrich with City/Country name from Geo-IP if available
                var ipData = await FetchIpLocationAsync();
                
                _cachedLocation = new LocationData
                {
                    Latitude = winLocation.Latitude,
                    Longitude = winLocation.Longitude,
                    AccuracyMeters = winLocation.AccuracyMeters,
                    City = ipData?.City ?? "Near Wi-Fi AP",
                    Region = ipData?.Region,
                    Country = ipData?.Country ?? "India",
                    Isp = ipData?.Isp != null ? $"{ipData.Isp} (Wi-Fi High-Accuracy ~{Math.Round(winLocation.AccuracyMeters ?? 50)}m)" : "Wi-Fi Triangulated"
                };

                _lastFetchUtc = DateTime.UtcNow;
                _logger.LogInformation("High-Accuracy Wi-Fi Location resolved: [{Lat}, {Lon}] ±{Acc}m",
                    winLocation.Latitude, winLocation.Longitude, winLocation.AccuracyMeters);
                return _cachedLocation;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Windows native Wi-Fi location bypassed.");
        }

        // 2. Second Priority: Fallback to Public IP Geolocation
        var fallbackIp = await FetchIpLocationAsync();
        if (fallbackIp != null)
        {
            _cachedLocation = fallbackIp;
            _lastFetchUtc = DateTime.UtcNow;
            return _cachedLocation;
        }

        return _cachedLocation;
    }

    private async Task<LocationData?> GetWindowsNativeLocationAsync()
    {
        try
        {
            var geolocator = new Geolocator
            {
                DesiredAccuracy = PositionAccuracy.High,
                DesiredAccuracyInMeters = 50
            };

            var pos = await geolocator.GetGeopositionAsync(
                maximumAge: TimeSpan.FromMinutes(2),
                timeout: TimeSpan.FromSeconds(4)
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
            _logger.LogDebug("Native geolocation unavailable: {Message}", ex.Message);
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
            _logger.LogDebug("IP location fallback failed: {Msg}", ex.Message);
        }

        return null;
    }
}
