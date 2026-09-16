using HomeOps.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HomeOps.Api.Acquisition;

public sealed class MeasurementPersistenceService(
    IDbContextFactory<HomeOpsDbContext> dbContextFactory,
    IOptions<SmartThingsOptions> options,
    MotionHoldScheduleSignal scheduleSignal,
    TimeProvider timeProvider)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _motionHold = TimeSpan.FromSeconds(options.Value.MotionHoldSeconds);
    private readonly decimal _temperatureDeadbandCelsius = options.Value.TemperatureDeadbandCelsius;

    public async Task<int> PersistAsync(
        IReadOnlyCollection<MeasurementSample> samples,
        CancellationToken cancellationToken)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        await _gate.WaitAsync(cancellationToken);
        var scheduleChanged = false;
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var storedCount = 0;
            var observedAt = timeProvider.GetUtcNow();

            foreach (var deviceSamples in samples.GroupBy(x => new { x.Source, x.DeviceId }))
            {
                var first = deviceSamples.First();
                var device = await db.Devices
                    .Include(x => x.MeasurementPoints)
                    .ThenInclude(x => x.MotionHold)
                    .SingleOrDefaultAsync(
                        x => x.Source == first.Source && x.SourceDeviceId == first.DeviceId,
                        cancellationToken);

                if (device is null)
                {
                    device = new Device
                    {
                        Source = first.Source,
                        SourceDeviceId = first.DeviceId,
                        Name = first.DeviceName
                    };
                    db.Devices.Add(device);
                }
                else
                {
                    device.Name = first.DeviceName;
                }

                var latestRaw = new Dictionary<string, LatestMeasurement?>(StringComparer.Ordinal);
                var latestExposed = new Dictionary<string, LatestMeasurement?>(StringComparer.Ordinal);
                var timestampsInBatch = new Dictionary<string, HashSet<DateTimeOffset>>(StringComparer.Ordinal);

                foreach (var sample in deviceSamples)
                {
                    var point = device.MeasurementPoints.SingleOrDefault(x => x.Key == sample.PointKey);
                    if (point is null)
                    {
                        point = new MeasurementPoint
                        {
                            Key = sample.PointKey,
                            Name = sample.PointName,
                            Kind = sample.Kind,
                            Unit = sample.Unit
                        };
                        device.MeasurementPoints.Add(point);
                    }
                    else
                    {
                        point.Name = sample.PointName;
                        point.Kind = sample.Kind;
                        point.Unit = sample.Unit;
                    }

                    var previousRaw = await GetLatestRawAsync(
                        sample.PointKey,
                        point,
                        latestRaw,
                        db,
                        cancellationToken);
                    var shouldStoreRaw = sample.StoreForEachTimestamp
                        ? await HasNewTimestampAsync(point, sample, timestampsInBatch, db, cancellationToken)
                        : previousRaw?.Value != sample.Value;

                    if (shouldStoreRaw)
                    {
                        point.Measurements.Add(new Measurement { Value = sample.Value, Timestamp = sample.Timestamp });
                        latestRaw[sample.PointKey] = new LatestMeasurement(sample.Value, sample.Timestamp);
                        storedCount++;
                    }

                    var previousExposed = await GetLatestExposedAsync(
                        sample.PointKey,
                        point,
                        latestExposed,
                        db,
                        cancellationToken);
                    var isSmartThingsMotion = SmartThingsStabilizationPolicy.IsMotion(sample);

                    // Motion freshness is the local observation time used for its hold. Its provider
                    // timestamp can remain older than a synthetic inactive publication timestamp.
                    if (!isSmartThingsMotion &&
                        previousExposed is not null &&
                        sample.Timestamp < previousExposed.Timestamp)
                    {
                        continue;
                    }

                    if (isSmartThingsMotion)
                    {
                        scheduleChanged |= ApplyMotion(point, sample, previousExposed, observedAt, latestExposed);
                    }
                    else if (sample.StoreForEachTimestamp
                        ? shouldStoreRaw && (previousExposed is null || sample.Timestamp >= previousExposed.Timestamp)
                        : ShouldExpose(sample, previousExposed))
                    {
                        AddExposed(point, sample.Value, sample.Timestamp, latestExposed);
                    }
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            return storedCount;
        }
        finally
        {
            _gate.Release();
            if (scheduleChanged)
            {
                scheduleSignal.Notify();
            }
        }
    }

    public async Task<DateTimeOffset?> ExpireMotionHoldsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var now = timeProvider.GetUtcNow();
            var motionPointsMissingHolds = await db.MeasurementPoints
                .Where(point =>
                    point.Device.Source == "smartthings" &&
                    point.Key.EndsWith("/motionSensor/motion") &&
                    point.MotionHold == null &&
                    point.ExposedMeasurements
                        .OrderByDescending(x => x.Id)
                        .Select(x => x.Value)
                        .FirstOrDefault() == 1m)
                .Select(point => point.Id)
                .ToListAsync(cancellationToken);

            foreach (var pointId in motionPointsMissingHolds)
            {
                db.MotionHolds.Add(new MotionHold
                {
                    MeasurementPointId = pointId,
                    DueAt = now + _motionHold
                });
            }

            if (motionPointsMissingHolds.Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            var dueHolds = await db.MotionHolds
                .Include(x => x.MeasurementPoint)
                .Where(x => x.DueAt <= now)
                .ToListAsync(cancellationToken);

            foreach (var hold in dueHolds)
            {
                var latest = await db.ExposedMeasurements
                    .Where(x => x.MeasurementPointId == hold.MeasurementPointId)
                    .OrderByDescending(x => x.Id)
                    .Select(x => (decimal?)x.Value)
                    .FirstOrDefaultAsync(cancellationToken);
                if (latest == 1m)
                {
                    db.ExposedMeasurements.Add(new ExposedMeasurement
                    {
                        MeasurementPointId = hold.MeasurementPointId,
                        Value = 0m,
                        Timestamp = now
                    });
                }

                db.MotionHolds.Remove(hold);
            }

            if (dueHolds.Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            return await db.MotionHolds
                .OrderBy(x => x.DueAt)
                .Select(x => (DateTimeOffset?)x.DueAt)
                .FirstOrDefaultAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool ApplyMotion(
        MeasurementPoint point,
        MeasurementSample sample,
        LatestMeasurement? previous,
        DateTimeOffset observedAt,
        IDictionary<string, LatestMeasurement?> latestExposed)
    {
        if (sample.Value == 1m)
        {
            if (SmartThingsStabilizationPolicy.ShouldExposeMotionImmediately(previous?.Value, sample.Value))
            {
                AddExposed(point, 1m, sample.Timestamp, latestExposed);
            }

            if (point.MotionHold is null)
            {
                point.MotionHold = new MotionHold();
            }

            point.MotionHold.DueAt = SmartThingsStabilizationPolicy.GetMotionHoldDueAt(observedAt, _motionHold);
            return true;
        }

        if (previous?.Value is null or 0m)
        {
            if (SmartThingsStabilizationPolicy.ShouldExposeMotionImmediately(previous?.Value, sample.Value))
            {
                AddExposed(point, 0m, sample.Timestamp, latestExposed);
            }

            if (point.MotionHold is not null)
            {
                point.MotionHold = null;
            }
        }

        return false;
    }

    private bool ShouldExpose(MeasurementSample sample, LatestMeasurement? previous)
    {
        if (previous is null)
        {
            return true;
        }

        if (SmartThingsStabilizationPolicy.IsTemperature(sample))
        {
            return SmartThingsStabilizationPolicy.CrossesTemperatureDeadband(
                sample.Value,
                previous.Value,
                sample.Unit,
                _temperatureDeadbandCelsius);
        }

        return sample.Value != previous.Value;
    }

    private static void AddExposed(
        MeasurementPoint point,
        decimal value,
        DateTimeOffset timestamp,
        IDictionary<string, LatestMeasurement?> latestExposed)
    {
        point.ExposedMeasurements.Add(new ExposedMeasurement { Value = value, Timestamp = timestamp });
        latestExposed[point.Key] = new LatestMeasurement(value, timestamp);
    }

    private static async Task<LatestMeasurement?> GetLatestRawAsync(
        string pointKey,
        MeasurementPoint point,
        IDictionary<string, LatestMeasurement?> cache,
        HomeOpsDbContext db,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(pointKey, out var cached))
        {
            return cached;
        }

        LatestMeasurement? latest = null;
        if (point.Id != 0)
        {
            latest = await db.Measurements
                .Where(x => x.MeasurementPointId == point.Id)
                .OrderByDescending(x => x.Id)
                .Select(x => new LatestMeasurement(x.Value, x.Timestamp))
                .FirstOrDefaultAsync(cancellationToken);
        }

        cache.Add(pointKey, latest);
        return latest;
    }

    private static async Task<LatestMeasurement?> GetLatestExposedAsync(
        string pointKey,
        MeasurementPoint point,
        IDictionary<string, LatestMeasurement?> cache,
        HomeOpsDbContext db,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(pointKey, out var cached))
        {
            return cached;
        }

        var latest = point.Id == 0
            ? null
            : await db.ExposedMeasurements
                .Where(x => x.MeasurementPointId == point.Id)
                .OrderByDescending(x => x.Id)
                .Select(x => new LatestMeasurement(x.Value, x.Timestamp))
                .FirstOrDefaultAsync(cancellationToken);

        cache.Add(pointKey, latest);
        return latest;
    }

    private static async Task<bool> HasNewTimestampAsync(
        MeasurementPoint point,
        MeasurementSample sample,
        IDictionary<string, HashSet<DateTimeOffset>> timestampsInBatch,
        HomeOpsDbContext db,
        CancellationToken cancellationToken)
    {
        if (!timestampsInBatch.TryGetValue(sample.PointKey, out var timestamps))
        {
            timestamps = [];
            timestampsInBatch.Add(sample.PointKey, timestamps);
        }

        if (!timestamps.Add(sample.Timestamp))
        {
            return false;
        }

        return point.Id == 0 || !await db.Measurements.AnyAsync(
            x => x.MeasurementPointId == point.Id && x.Timestamp == sample.Timestamp,
            cancellationToken);
    }

    private sealed record LatestMeasurement(decimal Value, DateTimeOffset Timestamp);
}
