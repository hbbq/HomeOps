using HomeOps.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeOps.Api.Endpoints;

public static class HomeOpsEndpoints
{
    public static IEndpointRouteBuilder MapHomeOpsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        api.MapGet("/devices", GetDevicesAsync);
        api.MapGet("/measurements/latest", GetLatestMeasurementsAsync);
        api.MapGet("/measurement-points/{pointId:int}/history", GetHistoryAsync);

        return endpoints;
    }

    private static async Task<IResult> GetDevicesAsync(
        IDbContextFactory<HomeOpsDbContext> dbContextFactory,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var devices = await db.Devices
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new
            {
                x.Id,
                x.Source,
                sourceDeviceId = x.SourceDeviceId,
                x.Name,
                measurementPoints = x.MeasurementPoints.OrderBy(p => p.Name).Select(p => new
                {
                    p.Id,
                    p.Key,
                    p.Name,
                    p.Kind,
                    p.Unit
                })
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(devices);
    }

    private static async Task<IResult> GetLatestMeasurementsAsync(
        IDbContextFactory<HomeOpsDbContext> dbContextFactory,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var latest = await db.MeasurementPoints
            .AsNoTracking()
            .Where(point => point.Measurements.Any())
            .OrderBy(point => point.Device.Name)
            .ThenBy(point => point.Name)
            .Select(point => new
            {
                deviceId = point.Device.Id,
                deviceName = point.Device.Name,
                pointId = point.Id,
                pointKey = point.Key,
                pointName = point.Name,
                point.Kind,
                point.Unit,
                value = point.Measurements
                    .OrderByDescending(value => value.Timestamp)
                    .ThenByDescending(value => value.Id)
                    .Select(value => value.Value)
                    .First(),
                timestamp = point.Measurements
                    .OrderByDescending(value => value.Timestamp)
                    .ThenByDescending(value => value.Id)
                    .Select(value => value.Timestamp)
                    .First()
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(latest);
    }

    private static async Task<IResult> GetHistoryAsync(
        int pointId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? limit,
        IDbContextFactory<HomeOpsDbContext> dbContextFactory,
        CancellationToken cancellationToken)
    {
        if (from > to)
        {
            return Results.BadRequest(new { error = "'from' must be earlier than or equal to 'to'." });
        }

        var resultLimit = Math.Clamp(limit ?? 500, 1, 5000);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var point = await db.MeasurementPoints
            .AsNoTracking()
            .Where(x => x.Id == pointId)
            .Select(x => new
            {
                x.Id,
                x.Key,
                x.Name,
                x.Kind,
                x.Unit,
                deviceId = x.Device.Id,
                deviceName = x.Device.Name
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (point is null)
        {
            return Results.NotFound();
        }

        var query = db.Measurements.AsNoTracking().Where(x => x.MeasurementPointId == pointId);
        if (from.HasValue)
        {
            query = query.Where(x => x.Timestamp >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(x => x.Timestamp <= to.Value);
        }

        var measurements = await query
            .OrderByDescending(x => x.Timestamp)
            .ThenByDescending(x => x.Id)
            .Take(resultLimit)
            .Select(x => new { x.Value, x.Timestamp })
            .ToListAsync(cancellationToken);

        return Results.Ok(new { measurementPoint = point, measurements });
    }
}
