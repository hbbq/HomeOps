namespace HomeOps.Api.Acquisition;

public sealed class SmartThingsOptions
{
    public bool Enabled { get; set; }
    public string AuthenticationMode { get; set; } = "OAuth";
    public string BaseUrl { get; set; } = "https://api.smartthings.com/v1/";
    public string Token { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public string AuthorizationUrl { get; set; } = "https://api.smartthings.com/oauth/authorize";
    public string TokenUrl { get; set; } = "https://api.smartthings.com/oauth/token";
    public string Scope { get; set; } = "r:devices:*";
    public string DataProtectionKeysPath { get; set; } = string.Empty;
    public int RefreshSkewMinutes { get; set; } = 5;
    public int AuthorizationStateLifetimeMinutes { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 15;
    public int MaxConcurrentDeviceReads { get; set; } = 4;
    public int MotionHoldSeconds { get; set; } = 120;
    public decimal TemperatureDeadbandCelsius { get; set; } = 0.3m;
}
