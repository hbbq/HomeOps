namespace HomeOps.Api.Data;

public sealed class SmartThingsAuthorization
{
    public int Id { get; set; }
    public string ProtectedAccessToken { get; set; } = string.Empty;
    public string ProtectedRefreshToken { get; set; } = string.Empty;
    public DateTimeOffset AccessTokenExpiresAt { get; set; }
    public string? InstalledAppId { get; set; }
    public string? Scope { get; set; }
    public bool RequiresReauthorization { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
