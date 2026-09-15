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
                  "motionSensor": { "motion": { "value": "active", "timestamp": "2026-09-11T12:35:00Z" } },
                  "contactSensor": { "contact": { "value": "closed" } },
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

        Assert.Equal(7, samples.Count);
        var temperature = Assert.Single(samples, x => x.PointKey == "main/temperatureMeasurement/temperature");
        Assert.Equal(21.25m, temperature.Value);
        Assert.Equal(DateTimeOffset.Parse("2026-09-11T12:34:56Z"), temperature.Timestamp);
        var humidity = Assert.Single(samples, x => x.PointKey == "main/relativeHumidityMeasurement/humidity");
        Assert.Equal(collectionTime, humidity.Timestamp);
        var power = Assert.Single(samples, x => x.PointKey == "meter/powerMeter/power");
        Assert.Equal("Power (meter)", power.PointName);
        var motion = Assert.Single(samples, x => x.PointKey == "main/motionSensor/motion");
        Assert.Equal("Motion", motion.PointName);
        Assert.Equal("boolean", motion.Kind);
        Assert.Null(motion.Unit);
        Assert.Equal(1, motion.Value);
        Assert.Equal(DateTimeOffset.Parse("2026-09-11T12:35:00Z"), motion.Timestamp);
        var contact = Assert.Single(samples, x => x.PointKey == "main/contactSensor/contact");
        Assert.Equal("Contact", contact.PointName);
        Assert.Equal("boolean", contact.Kind);
        Assert.Equal(0, contact.Value);
    }

    [Fact]
    public void MapStatus_MapsInactiveMotionAndOpenContactAndIgnoresUnknownStates()
    {
        using var status = JsonDocument.Parse("""
            {
              "components": {
                "main": {
                  "motionSensor": { "motion": { "value": "inactive" } },
                  "contactSensor": { "contact": { "value": "open" } }
                },
                "secondary": {
                  "motionSensor": { "motion": { "value": "unknown" } },
                  "contactSensor": { "contact": { "value": null } }
                }
              }
            }
            """);

        var samples = SmartThingsMeasurementSource.MapStatus(
            "device-1",
            "Hallway",
            status.RootElement,
            DateTimeOffset.UtcNow);

        Assert.Collection(
            samples.OrderBy(x => x.PointKey),
            contact =>
            {
                Assert.Equal("main/contactSensor/contact", contact.PointKey);
                Assert.Equal(1, contact.Value);
            },
            motion =>
            {
                Assert.Equal("main/motionSensor/motion", motion.PointKey);
                Assert.Equal(0, motion.Value);
            });
    }

    [Fact]
    public async Task ReadAsync_DiscoversPaginatedDevicesAndContinuesAfterDeviceFailure()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/devices", StringComparison.Ordinal))
            {
                var json = request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal)
                    ? """{"items":[{"deviceId":"good","label":"Kitchen sensor"},{"deviceId":"good","label":"Duplicate"}],"_links":{}}"""
                    : """{"items":[{"deviceId":"bad","label":"Unavailable sensor"}],"_links":{"next":{"href":"https://api.smartthings.com/v1/devices?page=2"}}}""";
                return JsonResponse(json);
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/devices/bad/status", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return JsonResponse("""{"components":{"main":{"battery":{"battery":{"value":90,"unit":"%"}}}}}""");
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.smartthings.com/v1/") };
        var options = Options.Create(new SmartThingsOptions
        {
            Token = "secret-token"
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

    [Fact]
    public async Task ReadAsync_ReadsDevicesWithBoundedConcurrencyAndOverlapsDeviceRequests()
    {
        var handler = new GatedStatusHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.smartthings.com/v1/") };
        var options = Options.Create(new SmartThingsOptions
        {
            Token = "secret-token",
            MaxConcurrentDeviceReads = 2
        });
        var source = new SmartThingsMeasurementSource(
            new StubHttpClientFactory(client),
            options,
            NullLogger<SmartThingsMeasurementSource>.Instance);

        var readTask = source.ReadAsync(CancellationToken.None);

        Assert.Equal(2, handler.StatusRequestCount);

        handler.ReleaseStatuses();
        var samples = await readTask;

        Assert.Equal(3, samples.Count);
        Assert.Equal(3, handler.StatusRequestCount);
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

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class GatedStatusHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _releaseStatuses = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _statusRequestCount;

        public int StatusRequestCount => Volatile.Read(ref _statusRequestCount);

        public void ReleaseStatuses() => _releaseStatuses.SetResult();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/devices", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    """{"items":[{"deviceId":"one","label":"One"},{"deviceId":"two","label":"Two"},{"deviceId":"three","label":"Three"}],"_links":{}}"""));
            }

            Interlocked.Increment(ref _statusRequestCount);
            return WaitForStatusAsync(cancellationToken);
        }

        private async Task<HttpResponseMessage> WaitForStatusAsync(CancellationToken cancellationToken)
        {
            await _releaseStatuses.Task.WaitAsync(cancellationToken);
            return JsonResponse("""{"components":{"main":{"battery":{"battery":{"value":90,"unit":"%"}}}}}""");
        }
    }
}
