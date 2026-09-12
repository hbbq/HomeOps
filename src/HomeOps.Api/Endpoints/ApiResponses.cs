namespace HomeOps.Api.Endpoints;

public sealed class DeviceResponse
{
    public int Id { get; init; }
    public required string Source { get; init; }
    public required string SourceDeviceId { get; init; }
    public required string Name { get; init; }
    public IEnumerable<MeasurementPointResponse> MeasurementPoints { get; init; } = [];
}

public sealed class MeasurementPointResponse
{
    public int Id { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public string? Unit { get; init; }
}

public sealed class LatestMeasurementResponse
{
    public int DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public int PointId { get; init; }
    public required string PointKey { get; init; }
    public required string PointName { get; init; }
    public required string Kind { get; init; }
    public string? Unit { get; init; }
    public decimal Value { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public sealed class MeasurementPointDetailsResponse
{
    public int Id { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public string? Unit { get; init; }
    public int DeviceId { get; init; }
    public required string DeviceName { get; init; }
}

public sealed class MeasurementResponse
{
    public decimal Value { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public sealed class MeasurementHistoryResponse
{
    public required MeasurementPointDetailsResponse MeasurementPoint { get; init; }
    public required IReadOnlyList<MeasurementResponse> Measurements { get; init; }
}

public sealed record ApiErrorResponse(string Error);
