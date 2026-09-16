using System.Net;
using System.Net.Http.Headers;

namespace HomeOps.Api.SmartThingsAuth;

public sealed class SmartThingsAuthenticationHandler(SmartThingsTokenProvider tokenProvider) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var accessToken = await tokenProvider.GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized || request.Method != HttpMethod.Get)
        {
            return response;
        }

        response.Dispose();
        var replacementToken = await tokenProvider.RefreshAfterUnauthorizedAsync(accessToken, cancellationToken);
        using var retry = new HttpRequestMessage(request.Method, request.RequestUri);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", replacementToken);
        return await base.SendAsync(retry, cancellationToken);
    }
}
