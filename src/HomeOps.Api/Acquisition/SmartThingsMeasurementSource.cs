using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HomeOps.Api.Acquisition;

public sealed class SmartThingsMeasurementSource(
    IHttpClientFactory httpClientFactory,
    IOptions<SmartThingsOptions> options,
    ILogger<SmartThingsMeasurementSource> logger) : IMeasurementSource
{
    private const string SourceName = "smartthings";

    private static readonly IReadOnlyDictionary<(string Capability, string Attribute), MeasurementDefinition>
        SupportedMeasurements = new Dictionary<(string, string), MeasurementDefinition>
        {
            [("temperatureMeasurement", "temperature")] = new("Temperature", "temperature"),
            [("relativeHumidityMeasurement", "humidity")] = new("Relative humidity", "humidity"),
            [("battery", "battery")] = new("Battery", "battery"),
            [("powerMeter", "power")] = new("Power", "power"),
            [("energyMeter", "energy")] = new("Energy", "energy")
        };

    private readonly SmartThingsOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, string> _deviceNames = new(StringComparer.Ordinal);

    public async Task<IReadOnlyCollection<MeasurementSample>> ReadAsync(CancellationToken cancellationToken)
    {
        var samples = new List<MeasurementSample>();
        var httpClient = httpClientFactory.CreateClient("SmartThings");

        foreach (var deviceId in _options.DeviceIds
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Select(x => x.Trim())
                     .Distinct(StringComparer.Ordinal))
        {
            try
            {
                var deviceName = await GetDeviceNameAsync(httpClient, deviceId, cancellationToken);
                using var status = await GetJsonAsync(httpClient, $"devices/{Uri.EscapeDataString(deviceId)}/status", cancellationToken);
                samples.AddRange(MapStatus(deviceId, deviceName, status.RootElement, DateTimeOffset.UtcNow));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not read SmartThings device {DeviceId}; continuing with other devices", deviceId);
            }
        }

        return samples;
    }

    internal static IReadOnlyCollection<MeasurementSample> MapStatus(
        string deviceId,
        string deviceName,
        JsonElement status,
        DateTimeOffset collectionTime)
    {
        var samples = new List<MeasurementSample>();
        if (!status.TryGetProperty("components", out var components) || components.ValueKind != JsonValueKind.Object)
        {
            return samples;
        }

        foreach (var component in components.EnumerateObject())
        {
            if (component.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var capability in component.Value.EnumerateObject())
            {
                if (capability.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var attribute in capability.Value.EnumerateObject())
                {
                    if (!SupportedMeasurements.TryGetValue((capability.Name, attribute.Name), out var definition) ||
                        attribute.Value.ValueKind != JsonValueKind.Object ||
                        !attribute.Value.TryGetProperty("value", out var valueElement) ||
                        valueElement.ValueKind != JsonValueKind.Number ||
                        !valueElement.TryGetDecimal(out var value))
                    {
                        continue;
                    }

                    string? unit = null;
                    if (attribute.Value.TryGetProperty("unit", out var unitElement) && unitElement.ValueKind == JsonValueKind.String)
                    {
                        unit = unitElement.GetString();
                    }

                    var timestamp = collectionTime;
                    if (attribute.Value.TryGetProperty("timestamp", out var timestampElement) &&
                        timestampElement.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(
                            timestampElement.GetString(),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var sourceTimestamp))
                    {
                        timestamp = sourceTimestamp;
                    }

                    var pointName = component.Name == "main"
                        ? definition.Name
                        : $"{definition.Name} ({component.Name})";

                    samples.Add(new MeasurementSample(
                        SourceName,
                        deviceId,
                        deviceName,
                        $"{component.Name}/{capability.Name}/{attribute.Name}",
                        pointName,
                        definition.Kind,
                        unit,
                        value,
                        timestamp));
                }
            }
        }

        return samples;
    }

    private async Task<string> GetDeviceNameAsync(
        HttpClient httpClient,
        string deviceId,
        CancellationToken cancellationToken)
    {
        if (_deviceNames.TryGetValue(deviceId, out var cachedName))
        {
            return cachedName;
        }

        try
        {
            using var device = await GetJsonAsync(httpClient, $"devices/{Uri.EscapeDataString(deviceId)}", cancellationToken);
            var root = device.RootElement;
            var name = GetNonEmptyString(root, "label") ?? GetNonEmptyString(root, "name") ?? deviceId;
            _deviceNames.TryAdd(deviceId, name);
            return name;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read the name of SmartThings device {DeviceId}; using its ID", deviceId);
            return deviceId;
        }
    }

    private async Task<JsonDocument> GetJsonAsync(
        HttpClient httpClient,
        string requestUri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string? GetNonEmptyString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;

    private sealed record MeasurementDefinition(string Name, string Kind);
}
