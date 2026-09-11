namespace HomeOps.Api.Acquisition;

public sealed class SimulatedMeasurementSource : IMeasurementSource
{
    private const string SourceName = "simulator";
    private const string DeviceId = "living-room-sensor";
    private const string DeviceName = "Living room sensor";

    public Task<IReadOnlyCollection<MeasurementSample>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timestamp = DateTimeOffset.UtcNow;
        var hourWave = Math.Sin(timestamp.TimeOfDay.TotalHours / 24 * Math.PI * 2);
        var temperature = 21.0m + (decimal)hourWave + RandomOffset(0.3m);
        var humidity = 42.0m - (decimal)(hourWave * 3) + RandomOffset(1.0m);
        var occupied = Random.Shared.Next(0, 2);

        IReadOnlyCollection<MeasurementSample> samples =
        [
            new(SourceName, DeviceId, DeviceName, "temperature", "Temperature", "temperature", "°C", decimal.Round(temperature, 2), timestamp),
            new(SourceName, DeviceId, DeviceName, "humidity", "Humidity", "humidity", "%", decimal.Round(humidity, 2), timestamp),
            new(SourceName, DeviceId, DeviceName, "occupied", "Occupied", "boolean", null, occupied, timestamp)
        ];

        return Task.FromResult(samples);
    }

    private static decimal RandomOffset(decimal maximum) =>
        ((decimal)Random.Shared.NextDouble() * 2 - 1) * maximum;
}
