using Microsoft.Extensions.Options;

namespace HomeOps.Api.Acquisition;

public sealed class MeasurementIngestionService(
    IEnumerable<IMeasurementSource> sources,
    MeasurementPersistenceService persistence,
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
                var storedCount = await persistence.PersistAsync(samples, stoppingToken);
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

}
