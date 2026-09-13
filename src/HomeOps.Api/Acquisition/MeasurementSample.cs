namespace HomeOps.Api.Acquisition;

public sealed record MeasurementSample(
    string Source,
    string DeviceId,
    string DeviceName,
    string PointKey,
    string PointName,
    string Kind,
    string? Unit,
    decimal Value,
    DateTimeOffset Timestamp,
    bool StoreForEachTimestamp = false);
