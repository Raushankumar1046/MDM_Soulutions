namespace EndpointManager.Client.Services;

using System.Text.Json;
using System.Text.Json.Serialization;

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
}

public class LocationService
{
    private readonly ILogger<LocationService> _logger;
    private readonly HttpClient _httpClient;
    private LocationData? _cachedLocation;
    private DateTime _lastFetchUtc = DateTime.MinValue;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(15);

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
        // Return cached location if still fresh
        if (_cachedLocation != null && (DateTime.UtcNow - _lastFetchUtc) < _cacheDuration)
        {
            return _cachedLocation;
        }

        try
        {
            // Lightweight public Geo-IP API with zero-auth
            var response = await _httpClient.GetStringAsync("http://ip-api.com/json");
            var data = JsonSerializer.Deserialize<LocationData>(response);

            if (data != null && !string.IsNullOrEmpty(data.City))
            {
                _cachedLocation = data;
                _lastFetchUtc = DateTime.UtcNow;
                _logger.LogInformation("Location resolved: {City}, {Region}, {Country} [{Lat}, {Lon}]",
                    data.City, data.Region, data.Country, data.Latitude, data.Longitude);
                return _cachedLocation;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Location lookup bypassed (offline or intranet): {Msg}", ex.Message);
        }

        return _cachedLocation;
    }
}
