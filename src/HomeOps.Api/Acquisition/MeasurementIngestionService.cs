using HomeOps.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HomeOps.Api.Acquisition;

public sealed class MeasurementIngestionService(
    IMeasurementSource source,
    IDbContextFactory<HomeOpsDbContext> dbContextFactory,
    IOptions<SimulatorOptions> options,
    ILogger<MeasurementIngestionService> logger) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(
        Math.Max(1, options.Value.IntervalSeconds));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var samples = await source.ReadAsync(stoppingToken);
                await PersistAsync(samples, stoppingToken);
                logger.LogInformation("Stored {MeasurementCount} simulated measurements", samples.Count);
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

    private async Task PersistAsync(
        IReadOnlyCollection<MeasurementSample> samples,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

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

                point.Measurements.Add(new Measurement
                {
                    Value = sample.Value,
                    Timestamp = sample.Timestamp
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
