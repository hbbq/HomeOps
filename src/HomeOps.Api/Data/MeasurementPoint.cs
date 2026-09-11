namespace HomeOps.Api.Data;

public sealed class MeasurementPoint
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public required string Key { get; set; }
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public string? Unit { get; set; }
    public Device Device { get; set; } = null!;
    public ICollection<Measurement> Measurements { get; set; } = [];
}
