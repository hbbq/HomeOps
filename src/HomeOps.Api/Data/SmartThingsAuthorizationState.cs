namespace HomeOps.Api.Data;

public sealed class SmartThingsAuthorizationState
{
    public int Id { get; set; }
    public string StateHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
}
