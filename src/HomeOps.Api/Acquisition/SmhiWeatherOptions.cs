namespace HomeOps.Api.Acquisition;

public sealed class SmhiWeatherOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://opendata-download-metobs.smhi.se/api/version/1.0/";
    public string StationId { get; set; } = string.Empty;
    public string? StationName { get; set; }
    public int PollingIntervalMinutes { get; set; } = 15;
    public int TimeoutSeconds { get; set; } = 15;
}
