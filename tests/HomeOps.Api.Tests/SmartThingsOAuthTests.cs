using System.Net;
using System.Text;
using HomeOps.Api.Acquisition;
using HomeOps.Api.Data;
using HomeOps.Api.SmartThingsAuth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeOps.Api.Tests;

public sealed class SmartThingsOAuthTests
{
    [Fact]
    public async Task ExpiredCredential_RefreshesAndPersistsRotatedPairForNextProvider()
    {
        var dbOptions = new DbContextOptionsBuilder<HomeOpsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var factory = new TestDbContextFactory(dbOptions);
        var time = new TestTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var dataProtection = new EphemeralDataProtectionProvider();
        var handler = new TokenHandler();
        var httpFactory = new TestHttpClientFactory(new HttpClient(handler));
        var options = Options.Create(new SmartThingsOptions
        {
            AuthenticationMode = "OAuth",
            ClientId = "client",
            ClientSecret = "secret",
            TokenUrl = "https://api.smartthings.com/oauth/token",
            RefreshSkewMinutes = 5
        });
        var provider = CreateProvider(factory, httpFactory, dataProtection, options, time);
        await provider.StoreAsync(new SmartThingsTokenResponse
        {
            AccessToken = "old-access",
            RefreshToken = "old-refresh",
            ExpiresIn = 60
        }, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal("new-access", await provider.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("refresh_token=old-refresh", handler.RequestBody);

        await using (var db = new HomeOpsDbContext(dbOptions))
        {
            var stored = await db.SmartThingsAuthorizations.SingleAsync();
            Assert.DoesNotContain("new-access", stored.ProtectedAccessToken);
            Assert.DoesNotContain("new-refresh", stored.ProtectedRefreshToken);
        }

        var restartedProvider = CreateProvider(factory, httpFactory, dataProtection, options, time);
        Assert.Equal("new-access", await restartedProvider.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task CallerCancelledDuringRotatedPairPersistence_PersistsPairBeforeHonoringCancellation()
    {
        using var callerCancellation = new CancellationTokenSource();
        var cancellationInterceptor = new CancelCallerOnSaveInterceptor(callerCancellation);
        var dbOptions = new DbContextOptionsBuilder<HomeOpsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(cancellationInterceptor)
            .Options;
        var factory = new TestDbContextFactory(dbOptions);
        var time = new TestTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var dataProtection = new EphemeralDataProtectionProvider();
        var handler = new TokenHandler();
        var options = Options.Create(new SmartThingsOptions
        {
            AuthenticationMode = "OAuth",
            ClientId = "client",
            ClientSecret = "secret",
            TokenUrl = "https://api.smartthings.com/oauth/token",
            RefreshSkewMinutes = 5
        });
        var provider = CreateProvider(
            factory,
            new TestHttpClientFactory(new HttpClient(handler)),
            dataProtection,
            options,
            time);
        await provider.StoreAsync(new SmartThingsTokenResponse
        {
            AccessToken = "old-access",
            RefreshToken = "old-refresh",
            ExpiresIn = 60
        }, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(2));
        cancellationInterceptor.CancelOnNextSave = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetAccessTokenAsync(callerCancellation.Token));

        Assert.True(callerCancellation.IsCancellationRequested);
        Assert.Equal(1, handler.RequestCount);
        await using var db = new HomeOpsDbContext(dbOptions);
        var stored = await db.SmartThingsAuthorizations.SingleAsync();
        Assert.Equal(
            "new-access",
            dataProtection.CreateProtector("SmartThings.AccessToken.v1").Unprotect(stored.ProtectedAccessToken));
        Assert.Equal(
            "new-refresh",
            dataProtection.CreateProtector("SmartThings.RefreshToken.v1").Unprotect(stored.ProtectedRefreshToken));
    }

    [Fact]
    public async Task AuthorizationState_IsSingleUse()
    {
        var dbOptions = new DbContextOptionsBuilder<HomeOpsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var factory = new TestDbContextFactory(dbOptions);
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var options = Options.Create(new SmartThingsOptions
        {
            ClientId = "client",
            ClientSecret = "secret",
            RedirectUri = "https://homeops.example/smartthings/oauth/callback"
        });
        var handler = new TokenHandler();
        var httpFactory = new TestHttpClientFactory(new HttpClient(handler));
        var provider = CreateProvider(factory, httpFactory, new EphemeralDataProtectionProvider(), options, time);
        var oauth = new SmartThingsOAuthService(factory, provider, options, time);
        var authorizationUri = await oauth.CreateAuthorizationUriAsync(CancellationToken.None);
        var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorizationUri.Query)["state"].Single();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oauth.CompleteAuthorizationAsync(state, null, "access_denied", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oauth.CompleteAuthorizationAsync(state, "code", null, CancellationToken.None));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ApiUnauthorized_RefreshesAndRetriesOnlyOnce()
    {
        var dbOptions = new DbContextOptionsBuilder<HomeOpsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var factory = new TestDbContextFactory(dbOptions);
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var tokenHandler = new TokenHandler();
        var options = Options.Create(new SmartThingsOptions
        {
            AuthenticationMode = "OAuth",
            ClientId = "client",
            ClientSecret = "secret"
        });
        var provider = CreateProvider(
            factory,
            new TestHttpClientFactory(new HttpClient(tokenHandler)),
            new EphemeralDataProtectionProvider(),
            options,
            time);
        await provider.StoreAsync(new SmartThingsTokenResponse
        {
            AccessToken = "rejected-access",
            RefreshToken = "old-refresh",
            ExpiresIn = 3600
        }, CancellationToken.None);
        var apiHandler = new UnauthorizedThenOkHandler();
        using var authenticationHandler = new SmartThingsAuthenticationHandler(provider) { InnerHandler = apiHandler };
        using var invoker = new HttpMessageInvoker(authenticationHandler);

        using var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.smartthings.com/v1/devices"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["rejected-access", "new-access"], apiHandler.BearerTokens);
        Assert.Equal(1, tokenHandler.RequestCount);
    }

    private static SmartThingsTokenProvider CreateProvider(
        IDbContextFactory<HomeOpsDbContext> dbFactory,
        IHttpClientFactory httpFactory,
        IDataProtectionProvider dataProtection,
        IOptions<SmartThingsOptions> options,
        TimeProvider time) =>
        new(dbFactory, httpFactory, dataProtection, options, time, NullLogger<SmartThingsTokenProvider>.Instance);

    private sealed class TestDbContextFactory(DbContextOptions<HomeOpsDbContext> options) : IDbContextFactory<HomeOpsDbContext>
    {
        public HomeOpsDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TokenHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600,"scope":"r:devices:*"}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed class UnauthorizedThenOkHandler : HttpMessageHandler
    {
        public List<string?> BearerTokens { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            BearerTokens.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(
                BearerTokens.Count == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK));
        }
    }

    private sealed class CancelCallerOnSaveInterceptor(CancellationTokenSource callerCancellation) : SaveChangesInterceptor
    {
        public bool CancelOnNextSave { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (CancelOnNextSave)
            {
                CancelOnNextSave = false;
                callerCancellation.Cancel();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
