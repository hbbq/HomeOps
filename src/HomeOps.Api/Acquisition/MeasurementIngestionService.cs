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
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(
        Math.Max(1, options.Value.IntervalSeconds));
    private readonly int _maxConcurrentSourceReads = Math.Clamp(
        options.Value.MaxConcurrentSourceReads,
        1,
        16);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var samples = await ReadSourcesAsync(
                    _sources,
                    logger,
                    stoppingToken,
                    _maxConcurrentSourceReads);
                var storedCount = await PersistAsync(samples, stoppingToken);
                logger.LogInformation(
                    "Stored {MeasurementCount} changed measurements from {SourceCount} sources",
                    storedCount,
                    _sources.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Measurement acquisition failed; the next attempt will run after the configured interval");
            }

            await Task.Delay(_interval, stoppingToken);
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

            var latestValues = new Dictionary<string, decimal?>(StringComparer.Ordinal);
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

                if (!latestValues.TryGetValue(sample.PointKey, out var previousValue))
                {
                    previousValue = point.Id == 0
                        ? null
                        : await db.Measurements
                            .Where(x => x.MeasurementPointId == point.Id)
                            .OrderByDescending(x => x.Id)
                            .Select(x => (decimal?)x.Value)
                            .FirstOrDefaultAsync(cancellationToken);
                    latestValues.Add(sample.PointKey, previousValue);
                }

                if (previousValue != sample.Value)
                {
                    point.Measurements.Add(new Measurement
                    {
                        Value = sample.Value,
                        Timestamp = sample.Timestamp
                    });
                    latestValues[sample.PointKey] = sample.Value;
                    storedCount++;
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return storedCount;
    }
}
