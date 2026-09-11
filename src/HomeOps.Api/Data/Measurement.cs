namespace HomeOps.Api.Data;

public sealed class Measurement
{
    public long Id { get; set; }
    public int MeasurementPointId { get; set; }
    public decimal Value { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public MeasurementPoint MeasurementPoint { get; set; } = null!;
}
