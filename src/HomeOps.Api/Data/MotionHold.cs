namespace HomeOps.Api.Data;

public sealed class MotionHold
{
    public int MeasurementPointId { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public MeasurementPoint MeasurementPoint { get; set; } = null!;
}
