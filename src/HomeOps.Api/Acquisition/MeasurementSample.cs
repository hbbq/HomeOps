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
    bool StoreForEachTimestamp = false,
    ContactHistoryUpdate? ContactHistory = null);

public sealed record ContactHistoryProgress(
    DateTimeOffset? CheckedAt = null,
    string? NextPage = null,
    DateTimeOffset? ScanStartedAt = null);

public sealed record ContactHistoryUpdate(
    DateTimeOffset? LastOpened,
    DateTimeOffset? LastClosed,
    ContactHistoryProgress Progress);
