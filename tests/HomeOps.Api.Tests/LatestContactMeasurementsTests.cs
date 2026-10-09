using System.Text.Json;
using HomeOps.Api.Acquisition;
using HomeOps.Api.Data;
using HomeOps.Api.Endpoints;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeOps.Api.Tests;

public sealed class LatestContactMeasurementsTests
{
    [Fact]
    public async Task Latest_ExposesContactMaximaAlongsideUnchangedValueAndLeavesOtherSensorsUnchanged()
    {
        var factory = new Factory();
        var service = new MeasurementPersistenceService(factory, Options.Create(new SmartThingsOptions()),
            new MotionHoldScheduleSignal(), TimeProvider.System);
        var time = DateTimeOffset.Parse("2026-10-08T17:21:11.463Z");
        var contact = new MeasurementSample("smartthings", "door", "Door", "main/contactSensor/contact",
            "Contact", "boolean", null, 0m, time);
        var temperature = new MeasurementSample("smartthings", "door", "Door", "main/temperatureMeasurement/temperature",
            "Temperature", "temperature", "C", 20m, time);
        await service.PersistAsync([contact, temperature], CancellationToken.None);
        var unknown = Assert.IsType<Ok<List<LatestMeasurementResponse>>>(
            await HomeOpsEndpoints.GetLatestMeasurementsAsync(factory, CancellationToken.None));
        Assert.All(unknown.Value!, value => { Assert.Null(value.LastOpened); Assert.Null(value.LastClosed); });

        await service.PersistAsync([contact with
        {
            ContactHistory = new ContactHistoryUpdate(time, time.AddMilliseconds(4859), new(time.AddMinutes(1)))
        }], CancellationToken.None);
        var result = Assert.IsType<Ok<List<LatestMeasurementResponse>>>(
            await HomeOpsEndpoints.GetLatestMeasurementsAsync(factory, CancellationToken.None));
        var door = Assert.Single(result.Value!, value => value.PointKey == contact.PointKey);
        Assert.Equal(0m, door.Value);
        Assert.Equal(time, door.Timestamp);
        Assert.Equal(time, door.LastOpened);
        Assert.Equal(time.AddMilliseconds(4859), door.LastClosed);
        var other = Assert.Single(result.Value!, value => value.PointKey == temperature.PointKey);
        var json = JsonSerializer.SerializeToElement(other, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.False(json.TryGetProperty("lastOpened", out _));
        Assert.False(json.TryGetProperty("lastClosed", out _));
        Assert.Equal(20m, other.Value);
    }

    [Fact]
    public void ContactMigrationMatchesTheCurrentSqlServerModelWithoutConnecting()
    {
        var options = new DbContextOptionsBuilder<HomeOpsDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options;
        using var db = new HomeOpsDbContext(options);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("20261009100000_AddContactHistory", db.Database.GetMigrations());
    }

    private sealed class Factory : IDbContextFactory<HomeOpsDbContext>
    {
        private readonly DbContextOptions<HomeOpsDbContext> _options = new DbContextOptionsBuilder<HomeOpsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public HomeOpsDbContext CreateDbContext() => new(_options);
    }
}
