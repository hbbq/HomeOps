using System.Security.Cryptography;
using System.Text;
using HomeOps.Api.Acquisition;
using HomeOps.Api.Data;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HomeOps.Api.SmartThingsAuth;

public sealed class SmartThingsOAuthService(
    IDbContextFactory<HomeOpsDbContext> dbContextFactory,
    SmartThingsTokenProvider tokenProvider,
    IOptions<SmartThingsOptions> options,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan TokenExchangeTimeout = TimeSpan.FromSeconds(15);
    private readonly SmartThingsOptions _options = options.Value;

    public async Task<Uri> CreateAuthorizationUriAsync(CancellationToken cancellationToken)
    {
        var state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var stateHash = HashState(state);
        var now = timeProvider.GetUtcNow();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var expired = await db.SmartThingsAuthorizationStates.Where(x => x.ExpiresAt <= now).ToListAsync(cancellationToken);
        db.SmartThingsAuthorizationStates.RemoveRange(expired);
        db.SmartThingsAuthorizationStates.Add(new SmartThingsAuthorizationState
        {
            StateHash = stateHash,
            ExpiresAt = now.AddMinutes(_options.AuthorizationStateLifetimeMinutes)
        });
        await db.SaveChangesAsync(cancellationToken);

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = _options.RedirectUri,
            ["scope"] = _options.Scope,
            ["state"] = state
        };
        return new Uri(QueryHelpers.AddQueryString(_options.AuthorizationUrl, query), UriKind.Absolute);
    }

    public async Task CompleteAuthorizationAsync(
        string? state,
        string? code,
        string? error,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            throw new InvalidOperationException("The SmartThings authorization state is missing or invalid.");
        }

        var now = timeProvider.GetUtcNow();
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var stateHash = HashState(state);
            var savedState = await db.SmartThingsAuthorizationStates
                .SingleOrDefaultAsync(x => x.StateHash == stateHash, cancellationToken);
            if (savedState is null || savedState.ExpiresAt <= now)
            {
                if (savedState is not null)
                {
                    db.SmartThingsAuthorizationStates.Remove(savedState);
                    await db.SaveChangesAsync(cancellationToken);
                }

                throw new InvalidOperationException("The SmartThings authorization state is missing, expired, or already used.");
            }

            db.SmartThingsAuthorizationStates.Remove(savedState);
            await db.SaveChangesAsync(cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            throw new InvalidOperationException("SmartThings authorization was denied or failed.");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("SmartThings did not return an authorization code.");
        }

        using var tokenExchangeCancellation = new CancellationTokenSource(TokenExchangeTimeout);
        var response = await tokenProvider.RequestTokenAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = _options.RedirectUri
            },
            tokenExchangeCancellation.Token);
        await tokenProvider.StoreAsync(response, tokenExchangeCancellation.Token);

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string HashState(string state) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
}
