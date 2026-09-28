namespace HomeOps.Api.Forecast;

public sealed class SmhiForecastOptions
{
    public bool Enabled { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string BaseUrl { get; set; } = "https://opendata-download-metfcst.smhi.se/api/category/snow1g/version/1/";
    public int TimeoutSeconds { get; set; } = 15;
    public int CacheMinutes { get; set; } = 15;

    public bool IsValid() => !Enabled ||
        Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180 &&
        TimeoutSeconds > 0 && CacheMinutes > 0 &&
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
}
