using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HomeOps.Api.Acquisition;

public sealed class SmhiWeatherMeasurementSource(
    IHttpClientFactory httpClientFactory,
    IOptions<SmhiWeatherOptions> options,
    ILogger<SmhiWeatherMeasurementSource> logger) : IMeasurementSource
{
    private const string SourceName = "smhi";

    private static readonly MeasurementDefinition[] Measurements =
    [
        new("1", "temperature", "Outdoor temperature", "temperature", "\u00B0C"),
        new("6", "relative-humidity", "Relative humidity", "humidity", "%"),
        new("9", "air-pressure-sea-level", "Air pressure at sea level", "pressure", "hPa"),
        new("4", "wind-speed", "Wind speed", "wind-speed", "m/s")
    ];

    private readonly SmhiWeatherOptions _options = options.Value;

    public TimeSpan? PollingInterval => TimeSpan.FromMinutes(_options.PollingIntervalMinutes);

    public async Task<IReadOnlyCollection<MeasurementSample>> ReadAsync(CancellationToken cancellationToken)
    {
        var httpClient = httpClientFactory.CreateClient("SmhiWeather");
        var reads = Measurements.Select(definition => ReadMeasurementAsync(httpClient, definition, cancellationToken));
        var samples = await Task.WhenAll(reads);
        return samples.OfType<MeasurementSample>().ToArray();
    }

    private async Task<MeasurementSample?> ReadMeasurementAsync(
        HttpClient httpClient,
        MeasurementDefinition definition,
        CancellationToken cancellationToken)
    {
        try
        {
            var stationId = Uri.EscapeDataString(_options.StationId.Trim());
            var requestUri = $"parameter/{definition.ParameterId}/station/{stationId}/period/latest-hour/data.json";
            using var response = await httpClient.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return MapLatestObservation(definition, document.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not read SMHI parameter {ParameterId} for station {StationId}; continuing with other weather measurements",
                definition.ParameterId,
                _options.StationId);
            return null;
        }
    }

    private MeasurementSample? MapLatestObservation(MeasurementDefinition definition, JsonElement response)
    {
        if (!response.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        (decimal Value, DateTimeOffset Timestamp)? latest = null;
        foreach (var observation in values.EnumerateArray())
        {
            if (!observation.TryGetProperty("date", out var date) || !date.TryGetInt64(out var milliseconds) ||
                !observation.TryGetProperty("value", out var valueElement) ||
                valueElement.ValueKind != JsonValueKind.String ||
                !decimal.TryParse(valueElement.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            if (latest is null || timestamp > latest.Value.Timestamp)
            {
                latest = (value, timestamp);
            }
        }

        if (latest is null)
        {
            return null;
        }

        var stationId = _options.StationId.Trim();
        var stationName = string.IsNullOrWhiteSpace(_options.StationName)
            ? $"SMHI station {stationId}"
            : _options.StationName.Trim();
        return new MeasurementSample(
            SourceName,
            stationId,
            stationName,
            definition.PointKey,
            definition.PointName,
            definition.Kind,
            definition.Unit,
            latest.Value.Value,
            latest.Value.Timestamp,
            StoreForEachTimestamp: true);
    }

    private sealed record MeasurementDefinition(
        string ParameterId,
        string PointKey,
        string PointName,
        string Kind,
        string Unit);
}
