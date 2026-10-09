using HomeOps.Api.Acquisition;
using HomeOps.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeOps.Api.Tests;

public sealed class MeasurementPersistenceServiceTests
{
    [Fact]
    public async Task PersistAsync_OlderObservationPreservesRawHistoryWithoutRollingBackExposedState()
    {
        var fixture = new PersistenceFixture();
        var newer = fixture.Sample("default", "temperature", 22m, fixture.Timestamp.AddMinutes(2));
        var older = newer with { Value = 19m, Timestamp = fixture.Timestamp.AddMinutes(1) };

        await fixture.Service.PersistAsync([newer], CancellationToken.None);
        await fixture.Service.PersistAsync([older], CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var raw = await db.Measurements.OrderBy(x => x.Id).ToListAsync();
        var exposed = await db.ExposedMeasurements.OrderBy(x => x.Id).ToListAsync();

        Assert.Equal([22m, 19m], raw.Select(x => x.Value));
        var current = Assert.Single(exposed);
        Assert.Equal(22m, current.Value);
        Assert.Equal(newer.Timestamp, current.Timestamp);
    }

    [Fact]
    public async Task PersistAsync_ActiveMotionAfterExpiredHoldReactivatesDespiteUnchangedProviderTimestamp()
    {
        var fixture = new PersistenceFixture();
        var active = fixture.Sample(
            "smartthings",
            "main/motionSensor/motion",
            1m,
            fixture.Timestamp.AddHours(-1));

        await fixture.Service.PersistAsync([active], CancellationToken.None);
        fixture.TimeProvider.Advance(TimeSpan.FromMinutes(2));
        await fixture.Service.ExpireMotionHoldsAsync(CancellationToken.None);
        fixture.TimeProvider.Advance(TimeSpan.FromSeconds(30));
        await fixture.Service.PersistAsync([active], CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        Assert.Single(await db.Measurements.ToListAsync());
        var exposed = await db.ExposedMeasurements.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal([1m, 0m, 1m], exposed.Select(x => x.Value));
        Assert.Equal(active.Timestamp, exposed[^1].Timestamp);
        Assert.Equal(
            fixture.TimeProvider.GetUtcNow().AddMinutes(2),
            Assert.Single(await db.MotionHolds.ToListAsync()).DueAt);
    }

    [Fact]
    public async Task PersistAsync_RepeatedCurrentSmartThingsMotionActiveExtendsHoldWithoutNewRawRow()
    {
        var fixture = new PersistenceFixture();
        var active = fixture.Sample(
            "smartthings",
            "main/motionSensor/motion",
            1m,
            fixture.Timestamp);

        await fixture.Service.PersistAsync([active], CancellationToken.None);
        fixture.TimeProvider.Advance(TimeSpan.FromSeconds(30));
        await fixture.Service.PersistAsync([active], CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        Assert.Single(await db.Measurements.ToListAsync());
        Assert.Single(await db.ExposedMeasurements.ToListAsync());
        var hold = Assert.Single(await db.MotionHolds.ToListAsync());
        Assert.Equal(fixture.TimeProvider.GetUtcNow().AddMinutes(2), hold.DueAt);
    }

    [Fact]
    public async Task PersistAsync_SmartThingsTemperatureStillUsesDeadband()
    {
        var fixture = new PersistenceFixture();
        var initial = fixture.Sample(
            "smartthings",
            "main/temperatureMeasurement/temperature",
            20m,
            fixture.Timestamp) with { Unit = "C" };

        await fixture.Service.PersistAsync([initial], CancellationToken.None);
        await fixture.Service.PersistAsync(
            [initial with { Value = 20.2m, Timestamp = initial.Timestamp.AddMinutes(1) }],
            CancellationToken.None);
        await fixture.Service.PersistAsync(
            [initial with { Value = 20.3m, Timestamp = initial.Timestamp.AddMinutes(2) }],
            CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var exposed = await db.ExposedMeasurements.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal([20m, 20.3m], exposed.Select(x => x.Value));
    }

    [Fact]
    public async Task PersistAsync_InOrderObservationsUpdateExposedState()
    {
        var fixture = new PersistenceFixture();
        var first = fixture.Sample("default", "temperature", 19m, fixture.Timestamp);
        var second = first with { Value = 22m, Timestamp = fixture.Timestamp.AddMinutes(1) };

        await fixture.Service.PersistAsync([first], CancellationToken.None);
        await fixture.Service.PersistAsync([second], CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var exposed = await db.ExposedMeasurements.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal([19m, 22m], exposed.Select(x => x.Value));
        Assert.Equal(second.Timestamp, exposed[^1].Timestamp);
    }

    [Fact]
    public async Task PersistAsync_ContactCycleUpdatesMaximaWithoutChangingCurrentStateOrHistoryRows()
    {
        var fixture = new PersistenceFixture();
        var sample = fixture.Sample("smartthings", "main/contactSensor/contact", 0m, fixture.Timestamp);
        await fixture.Service.PersistAsync([sample], CancellationToken.None);
        var opened = fixture.Timestamp.AddMilliseconds(1000);
        var closed = opened.AddMilliseconds(4859);
        var progress = new ContactHistoryProgress(fixture.Timestamp.AddMinutes(1));
        await fixture.Service.PersistAsync([sample with
        {
            ContactHistory = new ContactHistoryUpdate(opened, closed, progress)
        }], CancellationToken.None);

        // No history, older history and unchanged repeats must not clear or regress either maximum.
        await fixture.Service.PersistAsync([sample], CancellationToken.None);
        await fixture.Service.PersistAsync([sample with
        {
            ContactHistory = new ContactHistoryUpdate(opened.AddSeconds(-1), null, progress)
        }], CancellationToken.None);
        await fixture.Service.PersistAsync([sample with
        {
            Timestamp = fixture.Timestamp.AddDays(-1),
            ContactHistory = new ContactHistoryUpdate(opened.AddMinutes(1), null, progress)
        }], CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var point = Assert.Single(await db.MeasurementPoints.ToListAsync());
        Assert.Equal(opened.AddMinutes(1), point.LastOpened);
        Assert.Equal(closed, point.LastClosed);
        Assert.Equal(0m, Assert.Single(await db.ExposedMeasurements.ToListAsync()).Value);
        Assert.Single(await db.Measurements.ToListAsync());
        var device = Assert.Single(await db.Devices.ToListAsync());
        Assert.Equal(progress.CheckedAt, device.ContactHistoryCheckedAt);
        Assert.Null(device.ContactHistoryNextPage);
    }

    [Fact]
    public async Task PersistAsync_EmptyHistoryDoesNotInferContactTimesAndPreservesContinuationOnFailure()
    {
        var fixture = new PersistenceFixture();
        var progress = new ContactHistoryProgress(null, "https://api.smartthings.com/history/devices?page=2", fixture.Timestamp);
        var sample = fixture.Sample("smartthings", "main/contactSensor/contact", 1m, fixture.Timestamp) with
        {
            ContactHistory = new ContactHistoryUpdate(null, null, progress)
        };
        await fixture.Service.PersistAsync([sample], CancellationToken.None);
        // A failed history retrieval carries only the polled state.
        await fixture.Service.PersistAsync([sample with { ContactHistory = null }], CancellationToken.None);
        await using var db = fixture.CreateDbContext();
        var point = Assert.Single(await db.MeasurementPoints.ToListAsync());
        Assert.Null(point.LastOpened);
        Assert.Null(point.LastClosed);
        var device = Assert.Single(await db.Devices.ToListAsync());
        Assert.Null(device.ContactHistoryCheckedAt);
        Assert.Equal(progress.NextPage, device.ContactHistoryNextPage);
        Assert.Equal(progress.ScanStartedAt, device.ContactHistoryScanStartedAt);
    }

    private sealed class PersistenceFixture
    {
        private readonly DbContextOptions<HomeOpsDbContext> _dbOptions =
            new DbContextOptionsBuilder<HomeOpsDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

        public PersistenceFixture()
        {
            TimeProvider = new TestTimeProvider(Timestamp);
            Service = new MeasurementPersistenceService(
                new TestDbContextFactory(_dbOptions),
                Options.Create(new SmartThingsOptions { MotionHoldSeconds = 120 }),
                new MotionHoldScheduleSignal(),
                TimeProvider);
        }

        public DateTimeOffset Timestamp { get; } = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        public TestTimeProvider TimeProvider { get; }
        public MeasurementPersistenceService Service { get; }

        public HomeOpsDbContext CreateDbContext() => new(_dbOptions);

        public MeasurementSample Sample(string source, string pointKey, decimal value, DateTimeOffset timestamp) =>
            new(source, "device", "Device", pointKey, "Point", "test", null, value, timestamp);
    }

    private sealed class TestDbContextFactory(DbContextOptions<HomeOpsDbContext> options)
        : IDbContextFactory<HomeOpsDbContext>
    {
        public HomeOpsDbContext CreateDbContext() => new(options);
    }

    public sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
