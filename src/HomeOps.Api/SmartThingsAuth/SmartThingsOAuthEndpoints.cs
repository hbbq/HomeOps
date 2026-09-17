namespace HomeOps.Api.SmartThingsAuth;

public static class SmartThingsOAuthEndpoints
{
    public static IEndpointRouteBuilder MapSmartThingsOAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/admin/smartthings/authorize", async (
            SmartThingsOAuthService oauth,
            CancellationToken cancellationToken) =>
        {
            var uri = await oauth.CreateAuthorizationUriAsync(cancellationToken);
            return Results.Redirect(uri.ToString());
        }).ExcludeFromDescription();

        endpoints.MapGet("/smartthings/oauth/callback", async (
            HttpRequest request,
            SmartThingsOAuthService oauth,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await oauth.CompleteAuthorizationAsync(
                    request.Query["state"].FirstOrDefault(),
                    request.Query["code"].FirstOrDefault(),
                    request.Query["error"].FirstOrDefault(),
                    cancellationToken);
                return Results.Text("SmartThings authorization completed. You may close this window.", "text/plain");
            }
            catch (InvalidOperationException exception)
            {
                return Results.Text(exception.Message, "text/plain", statusCode: StatusCodes.Status400BadRequest);
            }
            catch (HttpRequestException)
            {
                return Results.Text(
                    "SmartThings authorization could not be completed. Start authorization again or check the service logs.",
                    "text/plain",
                    statusCode: StatusCodes.Status502BadGateway);
            }
        }).ExcludeFromDescription();

        return endpoints;
    }
}
