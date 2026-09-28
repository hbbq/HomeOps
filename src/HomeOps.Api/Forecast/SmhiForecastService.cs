using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HomeOps.Api.Forecast;

public sealed class SmhiForecastService(
    IHttpClientFactory httpClientFactory,
    IOptions<SmhiForecastOptions> options,
    TimeProvider timeProvider,
    ILogger<SmhiForecastService> logger)
{
    private readonly SmhiForecastOptions _options = options.Value;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private ForecastResponse? _cached;
    private DateTimeOffset _nextRefresh;

    public async Task<ForecastResponse?> GetAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (_cached is { } recent && timeProvider.GetUtcNow() < _nextRefresh)
            {
                return recent;
            }

            try
            {
                var forecast = await FetchAsync(cancellationToken);
                _cached = forecast;
                _nextRefresh = forecast.RetrievedAt.AddMinutes(_options.CacheMinutes);
                return forecast;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException)
            {
                logger.LogWarning(exception, "Could not refresh SMHI forecast");
                // Throttle retries after an outage while retaining the original retrieval timestamp.
                _nextRefresh = timeProvider.GetUtcNow().AddMinutes(1);
                _cached = _cached is null ? null : _cached with { Stale = true };
                return _cached;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<ForecastResponse> FetchAsync(CancellationToken cancellationToken)
    {
        var longitude = _options.Longitude!.Value.ToString("0.######", CultureInfo.InvariantCulture);
        var latitude = _options.Latitude!.Value.ToString("0.######", CultureInfo.InvariantCulture);
        var path = $"geotype/point/lon/{longitude}/lat/{latitude}/data.json";
        using var response = await httpClientFactory.CreateClient("SmhiForecast")
            .GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object ||
            !geometry.TryGetProperty("coordinates", out var coordinates) ||
            coordinates.ValueKind != JsonValueKind.Array || coordinates.GetArrayLength() < 2 ||
            coordinates[0].ValueKind != JsonValueKind.Number ||
            coordinates[1].ValueKind != JsonValueKind.Number ||
            !coordinates[0].TryGetDouble(out var gridLongitude) ||
            !coordinates[1].TryGetDouble(out var gridLatitude) ||
            !root.TryGetProperty("timeSeries", out var series) || series.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("SMHI forecast response is missing geometry or timeSeries.");
        }

        var periods = new List<ForecastPeriod>();
        foreach (var item in series.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("time", out var time) || time.ValueKind != JsonValueKind.String ||
                !time.TryGetDateTimeOffset(out var validTime) ||
                !item.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("SMHI forecast period is missing time or data.");
            }

            var raw = data.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone());
            periods.Add(new ForecastPeriod(validTime, Timestamp(item, "intervalParametersStartTime"),
                Number(raw, "air_temperature"),
                Number(raw, "precipitation_amount_mean_deterministic"),
                Integer(raw, "predominant_precipitation_type_at_surface"),
                Number(raw, "wind_speed"),
                Number(raw, "visibility_in_air"),
                Integer(raw, "symbol_code"), raw));
        }

        return new ForecastResponse("SMHI SNOW1gv1", _options.Latitude!.Value, _options.Longitude!.Value,
            gridLatitude, gridLongitude, Timestamp(root, "createdTime"), Timestamp(root, "referenceTime"),
            timeProvider.GetUtcNow(), false, periods);
    }

    private static DateTimeOffset? Timestamp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.TryGetDateTimeOffset(out var timestamp) ? timestamp : null;

    private static double? Number(IReadOnlyDictionary<string, JsonElement> values, string name) =>
        values.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number) && double.IsFinite(number) && number != 9999 ? number : null;

    private static int? Integer(IReadOnlyDictionary<string, JsonElement> values, string name) =>
        Number(values, name) is { } number && number == Math.Truncate(number) &&
        number >= int.MinValue && number <= int.MaxValue ? (int)number : null;
}
