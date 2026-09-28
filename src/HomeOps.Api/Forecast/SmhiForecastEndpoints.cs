using HomeOps.Api.Endpoints;

namespace HomeOps.Api.Forecast;

public static class SmhiForecastEndpoints
{
    public static IEndpointRouteBuilder MapSmhiForecastEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/forecast", async (SmhiForecastService forecast, CancellationToken cancellationToken) =>
        {
            var result = await forecast.GetAsync(cancellationToken);
            return result is null
                ? Results.Json(new ApiErrorResponse("SMHI forecast is currently unavailable."), statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(result);
        })
        .WithName("GetSmhiForecast")
        .WithSummary("Get all SMHI forecast periods for the configured location")
        .WithTags("HomeOps")
        .Produces<ForecastResponse>()
        .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }
}
