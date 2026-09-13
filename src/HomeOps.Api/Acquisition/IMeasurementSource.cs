namespace HomeOps.Api.Acquisition;

public interface IMeasurementSource
{
    TimeSpan? PollingInterval => null;

    Task<IReadOnlyCollection<MeasurementSample>> ReadAsync(CancellationToken cancellationToken);
}
