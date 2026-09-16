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
    public async Task PersistAsync_OlderSmartThingsMotionActiveDoesNotReactivateOrCreateHold()
    {
        var fixture = new PersistenceFixture();
        var currentInactive = fixture.Sample(
            "smartthings",
            "main/motionSensor/motion",
            0m,
            fixture.Timestamp.AddMinutes(2));
        var olderActive = currentInactive with { Value = 1m, Timestamp = fixture.Timestamp.AddMinutes(1) };

        await fixture.Service.PersistAsync([currentInactive], CancellationToken.None);
        await fixture.Service.PersistAsync([olderActive], CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        Assert.Equal([0m, 1m], await db.Measurements.OrderBy(x => x.Id).Select(x => x.Value).ToListAsync());
        Assert.Equal(0m, Assert.Single(await db.ExposedMeasurements.ToListAsync()).Value);
        Assert.Empty(await db.MotionHolds.ToListAsync());
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
        await fixture.Service.PersistAsync(
            [active with { Timestamp = active.Timestamp.AddSeconds(30) }],
            CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        Assert.Single(await db.Measurements.ToListAsync());
        Assert.Single(await db.ExposedMeasurements.ToListAsync());
        var hold = Assert.Single(await db.MotionHolds.ToListAsync());
        Assert.Equal(fixture.TimeProvider.GetUtcNow().AddMinutes(2), hold.DueAt);
    }

    [Fact]
    public async Task PersistAsync_OlderSmartThingsMotionActiveDoesNotResetExistingHold()
    {
        var fixture = new PersistenceFixture();
        var currentActive = fixture.Sample(
            "smartthings",
            "main/motionSensor/motion",
            1m,
            fixture.Timestamp.AddMinutes(2));

        await fixture.Service.PersistAsync([currentActive], CancellationToken.None);
        var originalDueAt = fixture.TimeProvider.GetUtcNow().AddMinutes(2);
        fixture.TimeProvider.Advance(TimeSpan.FromSeconds(30));
        await fixture.Service.PersistAsync(
            [currentActive with { Timestamp = fixture.Timestamp.AddMinutes(1) }],
            CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(originalDueAt, Assert.Single(await db.MotionHolds.ToListAsync()).DueAt);
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
