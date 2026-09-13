using HomeOps.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HomeOps.Api.Acquisition;

public sealed class MeasurementIngestionService(
    IEnumerable<IMeasurementSource> sources,
    IDbContextFactory<HomeOpsDbContext> dbContextFactory,
    IOptions<AcquisitionOptions> options,
    ILogger<MeasurementIngestionService> logger) : BackgroundService
{
    private readonly IReadOnlyCollection<IMeasurementSource> _sources = sources.ToArray();
    private readonly TimeSpan _defaultInterval = TimeSpan.FromSeconds(
        Math.Max(1, options.Value.IntervalSeconds));
    private readonly int _maxConcurrentSourceReads = Math.Clamp(
        options.Value.MaxConcurrentSourceReads,
        1,
        16);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextReads = _sources.ToDictionary(source => source, _ => DateTimeOffset.UtcNow);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var dueSources = nextReads
                .Where(x => x.Value <= now)
                .Select(x => x.Key)
                .ToArray();

            if (dueSources.Length == 0)
            {
                var delay = nextReads.Count == 0
                    ? _defaultInterval
                    : nextReads.Values.Min() - now;
                await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, stoppingToken);
                continue;
            }

            try
            {
                var samples = await ReadSourcesAsync(
                    dueSources,
                    logger,
                    stoppingToken,
                    _maxConcurrentSourceReads);
                var storedCount = await PersistAsync(samples, stoppingToken);
                logger.LogInformation(
                    "Stored {MeasurementCount} new measurements from {SourceCount} sources",
                    storedCount,
                    dueSources.Length);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Measurement acquisition failed; the next attempt will run after the configured interval");
            }

            var completedAt = DateTimeOffset.UtcNow;
            foreach (var source in dueSources)
            {
                var interval = source.PollingInterval ?? _defaultInterval;
                nextReads[source] = completedAt + (interval > TimeSpan.Zero ? interval : _defaultInterval);
            }
        }
    }

    internal static async Task<IReadOnlyCollection<MeasurementSample>> ReadSourcesAsync(
        IEnumerable<IMeasurementSource> sources,
        ILogger logger,
        CancellationToken cancellationToken,
        int maxConcurrency = 4)
    {
        maxConcurrency = Math.Clamp(maxConcurrency, 1, 16);
        using var concurrency = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var sourceReads = sources.Select(ReadSourceAsync).ToArray();
        var samples = await Task.WhenAll(sourceReads);
        return samples.SelectMany(x => x).ToArray();

        async Task<IReadOnlyCollection<MeasurementSample>> ReadSourceAsync(IMeasurementSource source)
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                return await source.ReadAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Measurement acquisition from {MeasurementSource} failed",
                    source.GetType().Name);
                return [];
            }
            finally
            {
                concurrency.Release();
            }
        }
    }

    private async Task<int> PersistAsync(
        IReadOnlyCollection<MeasurementSample> samples,
        CancellationToken cancellationToken)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var storedCount = 0;

        foreach (var deviceSamples in samples.GroupBy(x => new { x.Source, x.DeviceId }))
        {
            var first = deviceSamples.First();
            var device = await db.Devices
                .Include(x => x.MeasurementPoints)
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

            var latestValues = new Dictionary<string, LatestMeasurement?>(StringComparer.Ordinal);
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

                if (!latestValues.TryGetValue(sample.PointKey, out var previousMeasurement))
                {
                    previousMeasurement = point.Id == 0
                        ? null
                        : await db.Measurements
                            .Where(x => x.MeasurementPointId == point.Id)
                            .OrderByDescending(x => x.Id)
                            .Select(x => new LatestMeasurement(x.Value, x.Timestamp))
                            .FirstOrDefaultAsync(cancellationToken);
                    latestValues.Add(sample.PointKey, previousMeasurement);
                }

                var shouldStore = sample.StoreForEachTimestamp
                    ? await HasNewTimestampAsync(point, sample, timestampsInBatch, db, cancellationToken)
                    : previousMeasurement?.Value != sample.Value;

                if (shouldStore)
                {
                    point.Measurements.Add(new Measurement
                    {
                        Value = sample.Value,
                        Timestamp = sample.Timestamp
                    });
                    latestValues[sample.PointKey] = new LatestMeasurement(sample.Value, sample.Timestamp);
                    storedCount++;
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return storedCount;
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
