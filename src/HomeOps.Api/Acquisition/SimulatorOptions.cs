namespace HomeOps.Api.Acquisition;

public sealed class SimulatorOptions
{
    public const int DefaultIntervalSeconds = 30;
    public int IntervalSeconds { get; set; } = DefaultIntervalSeconds;
}
