using System.Net;
using System.Text;
using System.Text.Json;
using HomeOps.Api.Acquisition;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeOps.Api.Tests;

public sealed class SmartThingsMeasurementSourceTests
{
    [Fact]
    public void MapStatus_MapsOnlySupportedNumericAttributesAndPreservesIdentity()
    {
        using var status = JsonDocument.Parse("""
            {
              "components": {
                "main": {
                  "temperatureMeasurement": {
                    "temperature": { "value": 21.25, "unit": "C", "timestamp": "2026-09-11T12:34:56Z" }
                  },
                  "relativeHumidityMeasurement": {
                    "humidity": { "value": 48, "unit": "%", "timestamp": "not-a-timestamp" }
                  },
                  "battery": { "battery": { "value": 87, "unit": "%" } },
                  "switch": { "switch": { "value": "on" } }
                },
                "meter": {
                  "powerMeter": { "power": { "value": 14.5, "unit": "W" } },
                  "energyMeter": { "energy": { "value": 3.2, "unit": "kWh" } },
                  "temperatureMeasurement": { "temperature": { "value": "22.0", "unit": "C" } }
                }
              }
            }
            """);
        var collectionTime = new DateTimeOffset(2026, 9, 11, 13, 0, 0, TimeSpan.Zero);

        var samples = SmartThingsMeasurementSource.MapStatus("device-1", "Kitchen", status.RootElement, collectionTime);

        Assert.Equal(5, samples.Count);
        var temperature = Assert.Single(samples, x => x.PointKey == "main/temperatureMeasurement/temperature");
        Assert.Equal(21.25m, temperature.Value);
        Assert.Equal(DateTimeOffset.Parse("2026-09-11T12:34:56Z"), temperature.Timestamp);
        var humidity = Assert.Single(samples, x => x.PointKey == "main/relativeHumidityMeasurement/humidity");
        Assert.Equal(collectionTime, humidity.Timestamp);
        var power = Assert.Single(samples, x => x.PointKey == "meter/powerMeter/power");
        Assert.Equal("Power (meter)", power.PointName);
    }

    [Fact]
    public async Task ReadAsync_UsesBearerTokenAllowlistAndContinuesAfterDeviceFailure()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/devices/bad/status", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            var json = request.RequestUri.AbsolutePath.EndsWith("/status", StringComparison.Ordinal)
                ? """{"components":{"main":{"battery":{"battery":{"value":90,"unit":"%"}}}}}"""
                : """{"label":"Kitchen sensor"}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.smartthings.com/v1/") };
        var options = Options.Create(new SmartThingsOptions
        {
            Token = "secret-token",
            DeviceIds = ["bad", "good", "good"]
        });
        var source = new SmartThingsMeasurementSource(
            new StubHttpClientFactory(client),
            options,
            NullLogger<SmartThingsMeasurementSource>.Instance);

        var samples = await source.ReadAsync(CancellationToken.None);

        var sample = Assert.Single(samples);
        Assert.Equal("good", sample.DeviceId);
        Assert.Equal("Kitchen sensor", sample.DeviceName);
        Assert.Equal(4, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("Bearer", request.AuthorizationScheme);
            Assert.Equal("secret-token", request.AuthorizationParameter);
        });
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<(string? AuthorizationScheme, string? AuthorizationParameter)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter));
            return Task.FromResult(responder(request));
        }
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
