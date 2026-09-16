using HomeOps.Api.Data;
using HomeOps.Api.Displays;
using Microsoft.EntityFrameworkCore;

namespace HomeOps.Api.Endpoints;

public static class HomeOpsEndpoints
{
    public static IEndpointRouteBuilder MapHomeOpsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api").WithTags("HomeOps");

        api.MapGet("/devices", GetDevicesAsync)
            .WithName("GetDevices")
            .WithSummary("List devices and their measurement points")
            .Produces<List<DeviceResponse>>();
        api.MapGet("/dashboard/devices", GetDashboardDevicesAsync)
            .WithName("GetDashboardDevices")
            .WithSummary("List all devices for dashboard management")
            .Produces<List<DashboardDeviceResponse>>();
        api.MapPut("/dashboard/devices/{deviceId:int}/enabled", SetDeviceEnabledAsync)
            .WithName("SetDeviceEnabled")
            .WithSummary("Enable or disable a device from the dashboard")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        api.MapGet("/measurements/latest", GetLatestMeasurementsAsync)
            .WithName("GetLatestMeasurements")
            .WithSummary("Get the latest measurement for every known point")
            .Produces<List<LatestMeasurementResponse>>();
        api.MapGet("/measurement-points/{pointId:int}/history", GetHistoryAsync)
            .WithName("GetMeasurementPointHistory")
            .WithSummary("Get newest-first history for a measurement point")
            .Produces<MeasurementHistoryResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        api.MapPost("/displays/{displayId}/messages", EnqueueDisplayMessage)
            .WithName("EnqueueDisplayMessage")
            .WithSummary("Queue a text message for a display")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status429TooManyRequests);
        api.MapGet("/displays/{displayId}/messages/next", GetNextDisplayMessage)
            .WithName("GetNextDisplayMessage")
            .WithSummary("Get and remove the oldest queued text message for a display")
            .Produces<DisplayMessageResponse>()
            .Produces(StatusCodes.Status204NoContent);

        return endpoints;
    }

    private static IResult EnqueueDisplayMessage(
        string displayId,
        DisplayMessageRequest request,
        DisplayMessageQueue messageQueue)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return Results.BadRequest(new ApiErrorResponse("'text' must not be empty."));
        }

        return messageQueue.TryEnqueue(displayId, request.Text) switch
        {
            DisplayMessageEnqueueResult.Enqueued => Results.NoContent(),
            DisplayMessageEnqueueResult.MessageTooLong => Results.BadRequest(new ApiErrorResponse(
                $"'text' must not exceed {DisplayMessageQueue.MaximumMessageBytes} UTF-8 bytes.")),
            DisplayMessageEnqueueResult.QueueFull => Results.Json(
                new ApiErrorResponse(
                    $"The display queue is full ({DisplayMessageQueue.MaximumQueueDepth} messages)."),
                statusCode: StatusCodes.Status429TooManyRequests),
            _ => throw new InvalidOperationException("Unknown display-message enqueue result.")
        };
    }

    private static IResult GetNextDisplayMessage(string displayId, DisplayMessageQueue messageQueue) =>
        messageQueue.TryDequeue(displayId, out var text)
            ? Results.Ok(new DisplayMessageResponse(text!))
            : Results.NoContent();

    private static async Task<IResult> GetDevicesAsync(
        IDbContextFactory<HomeOpsDbContext> dbContextFactory,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var devices = await db.Devices
            .AsNoTracking()
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.Name)
            .Select(x => new DeviceResponse
            {
                Id = x.Id,
                Source = x.Source,
                SourceDeviceId = x.SourceDeviceId,
                Name = x.Name,
                MeasurementPoints = x.MeasurementPoints.OrderBy(p => p.Name).Select(p => new MeasurementPointResponse
                {
                    Id = p.Id,
                    Key = p.Key,
                    Name = p.Name,
                    Kind = p.Kind,
                    Unit = p.Unit
                })
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(devices);
    }

    private static async Task<IResult> GetDashboardDevicesAsync(
        IDbContextFactory<HomeOpsDbContext> dbContextFactory,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var devices = await db.Devices
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new DashboardDeviceResponse
            {
                Id = x.Id,
                Source = x.Source,
                SourceDeviceId = x.SourceDeviceId,
                Name = x.Name,
                IsEnabled = x.IsEnabled
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(devices);
    }

    private static async Task<IResult> SetDeviceEnabledAsync(
        int deviceId,
        SetDeviceEnabledRequest request,
        IDbContextFactory<HomeOpsDbContext> dbContextFactory,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId, cancellationToken);
        if (device is null)
        {
            return Results.NotFound();
        }

        device.IsEnabled = request.Enabled;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> GetLatestMeasurementsAsync(
        IDbContextFactory<HomeOpsDbContext> dbContextFactory,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var latest = await db.MeasurementPoints
            .AsNoTracking()
            .Where(point => point.Device.IsEnabled && point.Measurements.Any())
            .OrderBy(point => point.Device.Name)
            .ThenBy(point => point.Name)
            .Select(point => new LatestMeasurementResponse
            {
                DeviceId = point.Device.Id,
                DeviceName = point.Device.Name,
                PointId = point.Id,
                PointKey = point.Key,
                PointName = point.Name,
                Kind = point.Kind,
                Unit = point.Unit,
                Value = point.Measurements
                    .OrderByDescending(value => value.Timestamp)
                    .ThenByDescending(value => value.Id)
                    .Select(value => value.Value)
                    .First(),
                Timestamp = point.Measurements
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
            return Results.BadRequest(new ApiErrorResponse("'from' must be earlier than or equal to 'to'."));
        }

        var resultLimit = Math.Clamp(limit ?? 500, 1, 5000);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var point = await db.MeasurementPoints
            .AsNoTracking()
            .Where(x => x.Id == pointId && x.Device.IsEnabled)
            .Select(x => new MeasurementPointDetailsResponse
            {
                Id = x.Id,
                Key = x.Key,
                Name = x.Name,
                Kind = x.Kind,
                Unit = x.Unit,
                DeviceId = x.Device.Id,
                DeviceName = x.Device.Name
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
            .Select(x => new MeasurementResponse {Value = x.Value, Timestamp = x.Timestamp })
            .ToListAsync(cancellationToken);

        return Results.Ok(new MeasurementHistoryResponse
        {
            MeasurementPoint = point,
            Measurements = measurements
        });
    }
}
