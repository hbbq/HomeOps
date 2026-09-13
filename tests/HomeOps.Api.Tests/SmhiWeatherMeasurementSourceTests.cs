using System.Collections.Concurrent;
using System.Net;
using System.Text;
using HomeOps.Api.Acquisition;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeOps.Api.Tests;

public sealed class SmhiWeatherMeasurementSourceTests
{
    [Fact]
    public async Task ReadAsync_MapsLatestAvailableObservationsWithStationIdentityAndCanonicalUnits()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                _ when path.Contains("/parameter/1/", StringComparison.Ordinal) => JsonResponse("""{"value":[{"date":1789120800000,"value":"12.3"},{"date":1789124400000,"value":"12.8"}]}"""),
                _ when path.Contains("/parameter/6/", StringComparison.Ordinal) => JsonResponse("""{"value":[{"date":1789124400000,"value":"74"}]}"""),
                _ when path.Contains("/parameter/9/", StringComparison.Ordinal) => JsonResponse("""{"value":[{"date":1789124400000,"value":"1012.6"}]}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var source = CreateSource(handler, new SmhiWeatherOptions
        {
            StationId = "98210",
            StationName = "Visby weather station",
            PollingIntervalMinutes = 15
        });

        var samples = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(3, samples.Count);
        Assert.All(samples, sample =>
        {
            Assert.Equal("smhi", sample.Source);
            Assert.Equal("98210", sample.DeviceId);
            Assert.Equal("Visby weather station", sample.DeviceName);
            Assert.True(sample.StoreForEachTimestamp);
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789124400000), sample.Timestamp);
        });
        var temperature = Assert.Single(samples, x => x.PointKey == "temperature");
        Assert.Equal(12.8m, temperature.Value);
        Assert.Equal("\u00B0C", temperature.Unit);
        Assert.Equal("hPa", Assert.Single(samples, x => x.PointKey == "air-pressure-sea-level").Unit);
        Assert.DoesNotContain(samples, x => x.PointKey == "wind-speed");
        Assert.Equal(TimeSpan.FromMinutes(15), source.PollingInterval);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task ReadAsync_OmitsInvalidAndFailedParametersWhileRetainingHealthyOnes()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/parameter/1/", StringComparison.Ordinal))
            {
                return JsonResponse("""{"value":[{"date":1789124400000,"value":"not-a-number"}]}""");
            }

            if (path.Contains("/parameter/4/", StringComparison.Ordinal))
            {
                return JsonResponse("""{"value":[{"date":1789124400000,"value":"5.4"}]}""");
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var source = CreateSource(handler, new SmhiWeatherOptions { StationId = "98210" });

        var samples = await source.ReadAsync(CancellationToken.None);

        var wind = Assert.Single(samples);
        Assert.Equal("wind-speed", wind.PointKey);
        Assert.Equal(5.4m, wind.Value);
        Assert.Equal("m/s", wind.Unit);
    }

    private static SmhiWeatherMeasurementSource CreateSource(
        StubHandler handler,
        SmhiWeatherOptions options)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://opendata-download-metobs.smhi.se/api/version/1.0/")
        };
        return new SmhiWeatherMeasurementSource(
            new StubHttpClientFactory(client),
            Options.Create(options),
            NullLogger<SmhiWeatherMeasurementSource>.Instance);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public ConcurrentBag<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(responder(request));
        }
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
