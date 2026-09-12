using HomeOps.Api.Acquisition;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HomeOps.Api.Tests;

public sealed class MeasurementIngestionServiceTests
{
    [Fact]
    public async Task ReadSourcesAsync_CombinesHealthySourcesWhenAnotherSourceFails()
    {
        var expected = new MeasurementSample(
            "healthy", "device", "Device", "point", "Point", "test", null, 1m, DateTimeOffset.UtcNow);
        IMeasurementSource[] sources = [new FailingSource(), new StaticSource(expected)];

        var samples = await MeasurementIngestionService.ReadSourcesAsync(
            sources,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(expected, Assert.Single(samples));
    }

    [Fact]
    public async Task ReadSourcesAsync_StartsHealthySourceWithoutWaitingForBlockedSource()
    {
        var releaseBlockedSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthySourceRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new MeasurementSample(
            "healthy", "device", "Device", "point", "Point", "test", null, 1m, DateTimeOffset.UtcNow);
        IMeasurementSource[] sources =
        [
            new BlockingSource(releaseBlockedSource.Task),
            new SignalingSource(expected, healthySourceRead)
        ];

        var readTask = MeasurementIngestionService.ReadSourcesAsync(
            sources,
            NullLogger.Instance,
            CancellationToken.None);

        try
        {
            await healthySourceRead.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            releaseBlockedSource.SetResult();
        }

        var samples = await readTask;
        Assert.Equal(expected, Assert.Single(samples));
    }

    private sealed class FailingSource : IMeasurementSource
    {
        public Task<IReadOnlyCollection<MeasurementSample>> ReadAsync(CancellationToken cancellationToken) =>
            throw new HttpRequestException("Unavailable");
    }

    private sealed class StaticSource(MeasurementSample sample) : IMeasurementSource
    {
        public Task<IReadOnlyCollection<MeasurementSample>> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<MeasurementSample>>([sample]);
    }

    private sealed class BlockingSource(Task release) : IMeasurementSource
    {
        public async Task<IReadOnlyCollection<MeasurementSample>> ReadAsync(CancellationToken cancellationToken)
        {
            await release.WaitAsync(cancellationToken);
            return [];
        }
    }

    private sealed class SignalingSource(
        MeasurementSample sample,
        TaskCompletionSource read) : IMeasurementSource
    {
        public Task<IReadOnlyCollection<MeasurementSample>> ReadAsync(CancellationToken cancellationToken)
        {
            read.SetResult();
            return Task.FromResult<IReadOnlyCollection<MeasurementSample>>([sample]);
        }
    }
}
