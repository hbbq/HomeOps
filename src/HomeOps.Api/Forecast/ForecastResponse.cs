using System.Text.Json;

namespace HomeOps.Api.Forecast;

public sealed record ForecastResponse(
    string Source,
    double Latitude,
    double Longitude,
    double GridLatitude,
    double GridLongitude,
    DateTimeOffset? CreatedTime,
    DateTimeOffset? ReferenceTime,
    DateTimeOffset RetrievedAt,
    bool Stale,
    IReadOnlyList<ForecastPeriod> Periods);

public sealed record ForecastPeriod(
    DateTimeOffset ValidTime,
    DateTimeOffset? IntervalParametersStartTime,
    double? TemperatureCelsius,
    double? PrecipitationAmountMillimeters,
    int? PrecipitationType,
    double? WindSpeedMetersPerSecond,
    double? VisibilityKilometers,
    int? WeatherSymbol,
    IReadOnlyDictionary<string, JsonElement> RawValues);
