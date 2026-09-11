namespace HomeOps.Api.Acquisition;

public sealed class SmartThingsOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://api.smartthings.com/v1/";
    public string Token { get; set; } = string.Empty;
    public string[] DeviceIds { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 15;
}
