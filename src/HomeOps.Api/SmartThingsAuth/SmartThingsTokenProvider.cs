using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using HomeOps.Api.Acquisition;
using HomeOps.Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HomeOps.Api.SmartThingsAuth;

public sealed class SmartThingsTokenProvider(
    IDbContextFactory<HomeOpsDbContext> dbContextFactory,
    IHttpClientFactory httpClientFactory,
    IDataProtectionProvider dataProtectionProvider,
    IOptions<SmartThingsOptions> options,
    TimeProvider timeProvider,
    ILogger<SmartThingsTokenProvider> logger)
{
    private const int AuthorizationId = 1;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SmartThingsOptions _options = options.Value;
    private readonly IDataProtector _accessTokenProtector = dataProtectionProvider.CreateProtector("SmartThings.AccessToken.v1");
    private readonly IDataProtector _refreshTokenProtector = dataProtectionProvider.CreateProtector("SmartThings.RefreshToken.v1");

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (IsPatMode())
        {
            return _options.Token;
        }

        return await GetOAuthTokenAsync(null, cancellationToken);
    }

    public Task<string> RefreshAfterUnauthorizedAsync(string rejectedAccessToken, CancellationToken cancellationToken) =>
        IsPatMode() ? Task.FromResult(_options.Token) : GetOAuthTokenAsync(rejectedAccessToken, cancellationToken);

    internal async Task StoreAsync(SmartThingsTokenResponse response, CancellationToken cancellationToken)
    {
        ValidateTokenResponse(response);
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var authorization = await db.SmartThingsAuthorizations.SingleOrDefaultAsync(x => x.Id == AuthorizationId, cancellationToken);
            if (authorization is null)
            {
                authorization = new SmartThingsAuthorization { Id = AuthorizationId };
                db.SmartThingsAuthorizations.Add(authorization);
            }

            authorization.ProtectedAccessToken = _accessTokenProtector.Protect(response.AccessToken);
            authorization.ProtectedRefreshToken = _refreshTokenProtector.Protect(response.RefreshToken);
            authorization.AccessTokenExpiresAt = timeProvider.GetUtcNow().AddSeconds(response.ExpiresIn);
            authorization.InstalledAppId = response.InstalledAppId;
            authorization.Scope = response.Scope;
            authorization.RequiresReauthorization = false;
            authorization.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<string> GetOAuthTokenAsync(string? rejectedAccessToken, CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var authorization = await db.SmartThingsAuthorizations.SingleOrDefaultAsync(x => x.Id == AuthorizationId, cancellationToken);
            if (authorization is null || authorization.RequiresReauthorization)
            {
                throw new SmartThingsAuthorizationRequiredException(
                    "SmartThings authorization is required. Visit /admin/smartthings/authorize from the owner network.");
            }

            string accessToken;
            string refreshToken;
            try
            {
                accessToken = _accessTokenProtector.Unprotect(authorization.ProtectedAccessToken);
                refreshToken = _refreshTokenProtector.Unprotect(authorization.ProtectedRefreshToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Stored SmartThings credentials cannot be decrypted; reauthorization is required");
                authorization.RequiresReauthorization = true;
                await db.SaveChangesAsync(cancellationToken);
                throw new SmartThingsAuthorizationRequiredException("Stored SmartThings credentials cannot be decrypted; reauthorization is required.");
            }

            var refreshSkew = TimeSpan.FromMinutes(Math.Max(0, _options.RefreshSkewMinutes));
            var tokenWasReplaced = rejectedAccessToken is not null &&
                !string.Equals(rejectedAccessToken, accessToken, StringComparison.Ordinal);
            if (tokenWasReplaced || (rejectedAccessToken is null && authorization.AccessTokenExpiresAt > timeProvider.GetUtcNow() + refreshSkew))
            {
                return accessToken;
            }

            try
            {
                var response = await RequestTokenAsync(
                    new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = refreshToken
                    },
                    cancellationToken);
                ValidateTokenResponse(response);

                // Persist the rotated pair together before allowing API use.
                authorization.ProtectedAccessToken = _accessTokenProtector.Protect(response.AccessToken);
                authorization.ProtectedRefreshToken = _refreshTokenProtector.Protect(response.RefreshToken);
                authorization.AccessTokenExpiresAt = timeProvider.GetUtcNow().AddSeconds(response.ExpiresIn);
                authorization.Scope = response.Scope ?? authorization.Scope;
                authorization.InstalledAppId = response.InstalledAppId ?? authorization.InstalledAppId;
                authorization.RequiresReauthorization = false;
                authorization.UpdatedAt = timeProvider.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
                return response.AccessToken;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (SmartThingsTokenEndpointException exception) when (exception.RequiresReauthorization)
            {
                logger.LogError(exception, "SmartThings token refresh failed; reauthorization is required");
                authorization.RequiresReauthorization = true;
                authorization.UpdatedAt = timeProvider.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
                throw new SmartThingsAuthorizationRequiredException("SmartThings token refresh failed; reauthorization is required.");
            }
            catch (InvalidOperationException exception)
            {
                logger.LogError(exception, "SmartThings returned an invalid token refresh response; reauthorization is required");
                authorization.RequiresReauthorization = true;
                authorization.UpdatedAt = timeProvider.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
                throw new SmartThingsAuthorizationRequiredException("SmartThings token refresh failed; reauthorization is required.");
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "SmartThings token refresh failed temporarily; it will be retried");
                throw;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    internal async Task<SmartThingsTokenResponse> RequestTokenAsync(
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenUrl);
        var basicCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicCredentials);
        request.Content = new FormUrlEncodedContent(fields);
        using var response = await httpClientFactory.CreateClient("SmartThingsOAuth").SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new SmartThingsTokenEndpointException(
                $"SmartThings token endpoint returned HTTP {(int)response.StatusCode}.",
                response.StatusCode is System.Net.HttpStatusCode.BadRequest or
                    System.Net.HttpStatusCode.Unauthorized or
                    System.Net.HttpStatusCode.Forbidden);
        }

        return await response.Content.ReadFromJsonAsync<SmartThingsTokenResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("SmartThings token endpoint returned an empty response.");
    }

    private bool IsPatMode() => string.Equals(_options.AuthenticationMode, "Pat", StringComparison.OrdinalIgnoreCase);

    private static void ValidateTokenResponse(SmartThingsTokenResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.AccessToken) ||
            string.IsNullOrWhiteSpace(response.RefreshToken) ||
            response.ExpiresIn <= 0)
        {
            throw new InvalidOperationException("SmartThings returned an incomplete renewable token pair.");
        }
    }
}

internal sealed class SmartThingsTokenEndpointException(string message, bool requiresReauthorization)
    : HttpRequestException(message)
{
    public bool RequiresReauthorization { get; } = requiresReauthorization;
}
