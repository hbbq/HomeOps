namespace HomeOps.Api.Acquisition;

internal static class SmartThingsStabilizationPolicy
{
    private const string Source = "smartthings";
    private const string MotionKeySuffix = "/motionSensor/motion";
    private const string TemperatureKeySuffix = "/temperatureMeasurement/temperature";

    public static bool IsMotion(MeasurementSample sample) =>
        sample.Source == Source && sample.PointKey.EndsWith(MotionKeySuffix, StringComparison.Ordinal);

    public static bool IsTemperature(MeasurementSample sample) =>
        sample.Source == Source && sample.PointKey.EndsWith(TemperatureKeySuffix, StringComparison.Ordinal);

    public static bool ShouldExposeMotionImmediately(decimal? exposedValue, decimal observedValue) =>
        exposedValue is null || (observedValue == 1m && exposedValue != 1m);

    public static DateTimeOffset GetMotionHoldDueAt(DateTimeOffset observedAt, TimeSpan hold) =>
        observedAt + hold;

    public static bool CrossesTemperatureDeadband(
        decimal value,
        decimal exposedBaseline,
        string? unit,
        decimal deadbandCelsius)
    {
        var deadband = string.Equals(unit, "F", StringComparison.OrdinalIgnoreCase) || unit == "°F"
            ? deadbandCelsius * 1.8m
            : deadbandCelsius;
        return Math.Abs(value - exposedBaseline) >= deadband;
    }
}
