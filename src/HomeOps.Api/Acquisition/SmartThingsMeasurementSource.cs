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

    public async Task<IReadOnlyCollection<MeasurementSample>> ReadAsync(CancellationToken cancellationToken)
    {
        var httpClient = httpClientFactory.CreateClient("SmartThings");
        var devices = await GetDevicesAsync(httpClient, cancellationToken);
        var maxConcurrency = Math.Clamp(_options.MaxConcurrentDeviceReads, 1, 16);
        using var concurrency = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var deviceReads = devices.Select(ReadDeviceAsync).ToArray();
        var samples = await Task.WhenAll(deviceReads);
        return samples.SelectMany(x => x).ToArray();

        async Task<IReadOnlyCollection<MeasurementSample>> ReadDeviceAsync(SmartThingsDevice device)
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                var escapedDeviceId = Uri.EscapeDataString(device.Id);
                using var status = await GetJsonAsync(httpClient, $"devices/{escapedDeviceId}/status", cancellationToken);
                return MapStatus(device.Id, device.Name, status.RootElement, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not read SmartThings device {DeviceId}; continuing with other devices", device.Id);
                return [];
            }
            finally
            {
                concurrency.Release();
            }
        }
    }

    private async Task<IReadOnlyCollection<SmartThingsDevice>> GetDevicesAsync(
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var devices = new Dictionary<string, SmartThingsDevice>(StringComparer.Ordinal);
        var visitedPages = new HashSet<Uri>();
        Uri? pageUri = new("devices", UriKind.Relative);

        while (pageUri is not null)
        {
            var absolutePageUri = new Uri(httpClient.BaseAddress!, pageUri);
            if (!visitedPages.Add(absolutePageUri))
            {
                throw new InvalidOperationException("SmartThings device pagination returned a repeated page URL.");
            }

            using var page = await GetJsonAsync(httpClient, pageUri, cancellationToken);
            var root = page.RootElement;
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var deviceId = GetNonEmptyString(item, "deviceId");
                    if (deviceId is null)
                    {
                        continue;
                    }

                    var name = GetNonEmptyString(item, "label") ?? GetNonEmptyString(item, "name") ?? deviceId;
                    devices.TryAdd(deviceId, new SmartThingsDevice(deviceId, name));
                }
            }

            pageUri = GetNextPageUri(httpClient.BaseAddress, root);
        }

        return devices.Values.ToArray();
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

    private async Task<JsonDocument> GetJsonAsync(
        HttpClient httpClient,
        Uri requestUri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private Task<JsonDocument> GetJsonAsync(
        HttpClient httpClient,
        string requestUri,
        CancellationToken cancellationToken) =>
        GetJsonAsync(httpClient, new Uri(requestUri, UriKind.Relative), cancellationToken);

    private static Uri? GetNextPageUri(Uri? baseAddress, JsonElement root)
    {
        if (baseAddress is null ||
            !root.TryGetProperty("_links", out var links) ||
            links.ValueKind != JsonValueKind.Object ||
            !links.TryGetProperty("next", out var next) ||
            next.ValueKind != JsonValueKind.Object ||
            GetNonEmptyString(next, "href") is not { } href)
        {
            return null;
        }

        var nextUri = new Uri(baseAddress, href);
        if (!string.Equals(nextUri.Scheme, baseAddress.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(nextUri.Host, baseAddress.Host, StringComparison.OrdinalIgnoreCase) ||
            nextUri.Port != baseAddress.Port)
        {
            throw new InvalidOperationException("SmartThings device pagination returned a URL outside the configured API origin.");
        }

        return nextUri;
    }

    private static string? GetNonEmptyString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;

    private sealed record MeasurementDefinition(string Name, string Kind);
    private sealed record SmartThingsDevice(string Id, string Name);
}
