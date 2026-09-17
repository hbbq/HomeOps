using System.Text.Json.Serialization;

namespace HomeOps.Api.SmartThingsAuth;

internal sealed class SmartThingsTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("installed_app_id")]
    public string? InstalledAppId { get; set; }
}
