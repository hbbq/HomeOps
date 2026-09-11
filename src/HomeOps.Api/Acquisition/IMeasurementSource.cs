namespace HomeOps.Api.Acquisition;

public interface IMeasurementSource
{
    Task<IReadOnlyCollection<MeasurementSample>> ReadAsync(CancellationToken cancellationToken);
}
