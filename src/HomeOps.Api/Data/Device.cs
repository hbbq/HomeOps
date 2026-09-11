namespace HomeOps.Api.Data;

public sealed class Device
{
    public int Id { get; set; }
    public required string Source { get; set; }
    public required string SourceDeviceId { get; set; }
    public required string Name { get; set; }
    public ICollection<MeasurementPoint> MeasurementPoints { get; set; } = [];
}
