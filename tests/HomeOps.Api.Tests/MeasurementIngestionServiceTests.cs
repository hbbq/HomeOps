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
}
