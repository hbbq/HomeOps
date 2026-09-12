namespace HomeOps.Api.Acquisition;

public sealed class AcquisitionOptions
{
    public const int DefaultIntervalSeconds = 30;
    public int IntervalSeconds { get; set; } = DefaultIntervalSeconds;
    public int MaxConcurrentSourceReads { get; set; } = 4;
}
