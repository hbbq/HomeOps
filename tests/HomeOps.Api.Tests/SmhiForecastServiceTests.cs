using System.Net;
using System.Text;
using HomeOps.Api.Forecast;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeOps.Api.Tests;

public sealed class SmhiForecastServiceTests
{
    private const string ForecastJson = """
        {
          "createdTime": "2026-09-28T12:00:00Z",
          "referenceTime": "2026-09-28T11:00:00Z",
          "geometry": { "coordinates": [18.08, 59.34] },
          "timeSeries": [
            { "time": "2026-09-28T14:00:00Z", "intervalParametersStartTime": "2026-09-28T13:00:00Z", "data": {
              "air_temperature": 12.5, "precipitation_amount_mean_deterministic": 1.2,
              "predominant_precipitation_type_at_surface": 3, "wind_speed": 4.5,
              "visibility_in_air": 8, "symbol_code": 7, "extra_parameter": 42 } },
            { "time": "2026-09-28T15:00:00Z", "data": {
              "air_temperature": 9999, "wind_speed": 2 } }
          ]
        }
        """;

    [Fact]
    public async Task GetAsync_MapsAllPeriodsAndRetainsRawValuesAndFreshness()
    {
        var clock = new TestClock();
        var handler = new StubHandler(_ => Json(ForecastJson));
        var service = CreateService(handler, clock);

        var result = await service.GetAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("/api/category/snow1g/version/1/geotype/point/lon/18.07/lat/59.33/data.json", handler.LastRequest!.AbsolutePath);
        Assert.Equal(59.33, result.Latitude);
        Assert.Equal(18.08, result.GridLongitude);
        Assert.Equal(DateTimeOffset.Parse("2026-09-28T12:00:00Z"), result.CreatedTime);
        Assert.Equal(clock.GetUtcNow(), result.RetrievedAt);
        Assert.False(result.Stale);
        Assert.Equal(2, result.Periods.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-09-28T13:00:00Z"), result.Periods[0].IntervalParametersStartTime);
        Assert.Equal(1.2, result.Periods[0].PrecipitationAmountMillimeters);
        Assert.Equal(3, result.Periods[0].PrecipitationType);
        Assert.Equal(8, result.Periods[0].VisibilityKilometers);
        Assert.Equal(7, result.Periods[0].WeatherSymbol);
        Assert.Equal(42, result.Periods[0].RawValues["extra_parameter"].GetInt32());
        Assert.Null(result.Periods[1].TemperatureCelsius);
        Assert.Equal(9999, result.Periods[1].RawValues["air_temperature"].GetInt32());
    }

    [Fact]
    public async Task GetAsync_ReusesCacheAndReturnsStaleForecastWhenRefreshFails()
    {
        var clock = new TestClock();
        var fail = false;
        var handler = new StubHandler(_ => fail ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(ForecastJson));
        var service = CreateService(handler, clock);
        var first = await service.GetAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Same(first, await service.GetAsync(CancellationToken.None));
        Assert.Equal(1, handler.Count);

        fail = true;
        clock.Advance(TimeSpan.FromMinutes(2));
        var stale = await service.GetAsync(CancellationToken.None);
        Assert.True(stale!.Stale);
        Assert.Equal(first!.RetrievedAt, stale.RetrievedAt);
        Assert.Equal(2, handler.Count);
        Assert.Same(stale, await service.GetAsync(CancellationToken.None));
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task GetAsync_WithoutCachedForecast_ReturnsUnavailable()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Assert.Null(await CreateService(handler, new TestClock()).GetAsync(CancellationToken.None));
    }

    [Fact]
    public void Options_RejectMissingOrInvalidCoordinates()
    {
        Assert.False(new SmhiForecastOptions { Enabled = true }.IsValid());
        Assert.False(new SmhiForecastOptions { Enabled = true, Latitude = 91, Longitude = 18 }.IsValid());
        Assert.True(new SmhiForecastOptions { Enabled = true, Latitude = 59.33, Longitude = 18.07 }.IsValid());
    }

    private static SmhiForecastService CreateService(StubHandler handler, TimeProvider clock)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test/api/category/snow1g/version/1/")
        };
        return new SmhiForecastService(new StubFactory(client),
            Options.Create(new SmhiForecastOptions { Enabled = true, Latitude = 59.33, Longitude = 18.07 }),
            clock, NullLogger<SmhiForecastService>.Instance);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        public Uri? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            LastRequest = request.RequestUri;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-09-28T13:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
