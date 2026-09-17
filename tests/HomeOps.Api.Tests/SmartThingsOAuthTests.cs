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
    public async Task CallerCancelledDuringRefreshExchange_PersistsPairBeforeHonoringCancellation()
    {
        using var callerCancellation = new CancellationTokenSource();
        var dbOptions = new DbContextOptionsBuilder<HomeOpsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var factory = new TestDbContextFactory(dbOptions);
        var time = new TestTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var dataProtection = new EphemeralDataProtectionProvider();
        var handler = new CancelCallerDuringExchangeHandler(callerCancellation);
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetAccessTokenAsync(callerCancellation.Token));

        Assert.False(handler.ExchangeCancellationWasRequested);
        await AssertStoredTokenPairAsync(dbOptions, dataProtection);
    }

    [Fact]
    public async Task RefreshPersistence_GetsFreshTimeoutWhenExchangeCompletesNearDeadline()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var persistenceInterceptor = new AdvanceTimeOnCredentialSaveInterceptor(time);
        var dbOptions = new DbContextOptionsBuilder<HomeOpsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(persistenceInterceptor)
            .Options;
        var factory = new TestDbContextFactory(dbOptions);
        var dataProtection = new EphemeralDataProtectionProvider();
        var handler = new AdvanceTimeDuringExchangeHandler(time);
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
        persistenceInterceptor.AdvanceOnNextCredentialSave = true;

        Assert.Equal("new-access", await provider.GetAccessTokenAsync(CancellationToken.None));

        Assert.True(persistenceInterceptor.PersistenceCanBeCanceled);
        Assert.False(persistenceInterceptor.PersistenceCancellationWasRequested);
        await AssertStoredTokenPairAsync(dbOptions, dataProtection);
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
    public async Task CallerCancelledDuringAuthorizationPersistence_PersistsCredentialsBeforeHonoringCancellation()
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
            RedirectUri = "https://homeops.example/smartthings/oauth/callback"
        });
        var provider = CreateProvider(
            factory,
            new TestHttpClientFactory(new HttpClient(handler)),
            dataProtection,
            options,
            time);
        var oauth = new SmartThingsOAuthService(factory, provider, options, time);
        var authorizationUri = await oauth.CreateAuthorizationUriAsync(CancellationToken.None);
        var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorizationUri.Query)["state"].Single();
        cancellationInterceptor.CancelOnNextAuthorizationSave = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            oauth.CompleteAuthorizationAsync(state, "code", null, callerCancellation.Token));

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
    public async Task CallerCancelledDuringAuthorizationExchange_PersistsCredentialsBeforeHonoringCancellation()
    {
        using var callerCancellation = new CancellationTokenSource();
        var dbOptions = new DbContextOptionsBuilder<HomeOpsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var factory = new TestDbContextFactory(dbOptions);
        var time = new TestTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var dataProtection = new EphemeralDataProtectionProvider();
        var handler = new CancelCallerDuringExchangeHandler(callerCancellation);
        var options = Options.Create(new SmartThingsOptions
        {
            AuthenticationMode = "OAuth",
            ClientId = "client",
            ClientSecret = "secret",
            TokenUrl = "https://api.smartthings.com/oauth/token",
            RedirectUri = "https://homeops.example/smartthings/oauth/callback"
        });
        var provider = CreateProvider(
            factory,
            new TestHttpClientFactory(new HttpClient(handler)),
            dataProtection,
            options,
            time);
        var oauth = new SmartThingsOAuthService(factory, provider, options, time);
        var authorizationUri = await oauth.CreateAuthorizationUriAsync(CancellationToken.None);
        var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorizationUri.Query)["state"].Single();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            oauth.CompleteAuthorizationAsync(state, "code", null, callerCancellation.Token));

        Assert.False(handler.ExchangeCancellationWasRequested);
        await AssertStoredTokenPairAsync(dbOptions, dataProtection);
    }

    [Fact]
    public async Task AuthorizationPersistence_GetsFreshTimeoutWhenExchangeCompletesNearDeadline()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var persistenceInterceptor = new AdvanceTimeOnCredentialSaveInterceptor(time);
        var dbOptions = new DbContextOptionsBuilder<HomeOpsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(persistenceInterceptor)
            .Options;
        var factory = new TestDbContextFactory(dbOptions);
        var dataProtection = new EphemeralDataProtectionProvider();
        var handler = new AdvanceTimeDuringExchangeHandler(time);
        var options = Options.Create(new SmartThingsOptions
        {
            AuthenticationMode = "OAuth",
            ClientId = "client",
            ClientSecret = "secret",
            TokenUrl = "https://api.smartthings.com/oauth/token",
            RedirectUri = "https://homeops.example/smartthings/oauth/callback"
        });
        var provider = CreateProvider(
            factory,
            new TestHttpClientFactory(new HttpClient(handler)),
            dataProtection,
            options,
            time);
        var oauth = new SmartThingsOAuthService(factory, provider, options, time);
        var authorizationUri = await oauth.CreateAuthorizationUriAsync(CancellationToken.None);
        var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorizationUri.Query)["state"].Single();
        persistenceInterceptor.AdvanceOnNextCredentialSave = true;

        await oauth.CompleteAuthorizationAsync(state, "code", null, CancellationToken.None);

        Assert.True(persistenceInterceptor.PersistenceCanBeCanceled);
        Assert.False(persistenceInterceptor.PersistenceCancellationWasRequested);
        await AssertStoredTokenPairAsync(dbOptions, dataProtection);
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

    private static async Task AssertStoredTokenPairAsync(
        DbContextOptions<HomeOpsDbContext> dbOptions,
        IDataProtectionProvider dataProtection)
    {
        await using var db = new HomeOpsDbContext(dbOptions);
        var stored = await db.SmartThingsAuthorizations.SingleAsync();
        Assert.Equal(
            "new-access",
            dataProtection.CreateProtector("SmartThings.AccessToken.v1").Unprotect(stored.ProtectedAccessToken));
        Assert.Equal(
            "new-refresh",
            dataProtection.CreateProtector("SmartThings.RefreshToken.v1").Unprotect(stored.ProtectedRefreshToken));
    }

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

    private sealed class CancelCallerDuringExchangeHandler(CancellationTokenSource callerCancellation) : HttpMessageHandler
    {
        public bool ExchangeCancellationWasRequested { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            callerCancellation.Cancel();
            await Task.Yield();
            ExchangeCancellationWasRequested = cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600,"scope":"r:devices:*"}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed class AdvanceTimeDuringExchangeHandler(TestTimeProvider time) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            time.Advance(TimeSpan.FromSeconds(14.9));
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600,"scope":"r:devices:*"}""",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class AdvanceTimeOnCredentialSaveInterceptor(TestTimeProvider time) : SaveChangesInterceptor
    {
        public bool AdvanceOnNextCredentialSave { get; set; }
        public bool PersistenceCanBeCanceled { get; private set; }
        public bool PersistenceCancellationWasRequested { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (AdvanceOnNextCredentialSave &&
                eventData.Context!.ChangeTracker.Entries<SmartThingsAuthorization>().Any(
                    entry => entry.State is EntityState.Added or EntityState.Modified))
            {
                AdvanceOnNextCredentialSave = false;
                time.Advance(TimeSpan.FromSeconds(0.2));
                PersistenceCanBeCanceled = cancellationToken.CanBeCanceled;
                PersistenceCancellationWasRequested = cancellationToken.IsCancellationRequested;
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class CancelCallerOnSaveInterceptor(CancellationTokenSource callerCancellation) : SaveChangesInterceptor
    {
        public bool CancelOnNextSave { get; set; }
        public bool CancelOnNextAuthorizationSave { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var shouldCancel = CancelOnNextSave ||
                (CancelOnNextAuthorizationSave &&
                    eventData.Context!.ChangeTracker.Entries<SmartThingsAuthorization>().Any(
                        entry => entry.State is EntityState.Added or EntityState.Modified));
            if (shouldCancel)
            {
                CancelOnNextSave = false;
                CancelOnNextAuthorizationSave = false;
                callerCancellation.Cancel();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly List<TestTimer> _timers = [];

        public override DateTimeOffset GetUtcNow() => now;

        public override long GetTimestamp() => now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new TestTimer(this, callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            now += duration;
            foreach (var timer in _timers.ToArray())
            {
                timer.FireIfDue(now);
            }
        }

        private sealed class TestTimer : ITimer
        {
            private readonly TestTimeProvider _provider;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private TimeSpan _period;
            private DateTimeOffset? _dueAt;
            private bool _disposed;

            public TestTimer(
                TestTimeProvider provider,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                _provider = provider;
                _callback = callback;
                _state = state;
                _period = period;
                _dueAt = GetDueAt(dueTime);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed)
                {
                    return false;
                }

                _dueAt = GetDueAt(dueTime);
                _period = period;
                return true;
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void FireIfDue(DateTimeOffset currentTime)
            {
                if (_disposed || _dueAt is null || _dueAt > currentTime)
                {
                    return;
                }

                _dueAt = _period == Timeout.InfiniteTimeSpan ? null : currentTime + _period;
                _callback(_state);
            }

            private DateTimeOffset? GetDueAt(TimeSpan timeout) =>
                timeout == Timeout.InfiniteTimeSpan ? null : _provider.GetUtcNow() + timeout;
        }
    }
}
