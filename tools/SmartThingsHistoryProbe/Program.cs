using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HomeOps.Api.Acquisition;
using HomeOps.Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;

// Do not instantiate HomeOps' host or token provider: both can write to storage.
try
{
    VerifyTimestampConsistency();
    if (args is ["--self-test"])
    {
        Console.WriteLine("Timestamp self-check passed (6 cases); no configuration or credentials accessed.");
        return 0;
    }
    return await RunAsync();
}
catch (Exception exception)
{
    // Messages, stack traces and response bodies can reveal sensitive configuration.
    Console.WriteLine($"STOP: failed during {ProbeDiagnostics.Stage}.");
    for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
    {
        Console.WriteLine($"Exception type: {cause.GetType().Name}");
        if (cause is Microsoft.Data.SqlClient.SqlException sql)
            foreach (Microsoft.Data.SqlClient.SqlError error in sql.Errors)
                Console.WriteLine($"SQL error number: {error.Number}; state: {error.State}; class: {error.Class}");
        if (cause is ProbeFailure failure)
            Console.WriteLine("Reason: " + failure.SafeReason);
    }
    Console.WriteLine("Exception messages, configuration and credentials suppressed.");
    return 1;
}

static async Task<int> RunAsync()
{
    ProbeDiagnostics.Stage = "locating the HomeOps API project";
    var apiDirectory = FindApiDirectory();
    var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
    if (environment.IndexOfAny(['/', '\\']) >= 0)
        throw new ProbeFailure("The environment name contains a path separator.");
    ProbeDiagnostics.Stage = "loading HomeOps configuration and User Secrets";
    var config = new ConfigurationBuilder()
        .SetBasePath(apiDirectory)
        .AddJsonFile("appsettings.json")
        .AddJsonFile($"appsettings.{environment}.json", optional: true)
        .AddUserSecrets(typeof(HomeOpsDbContext).Assembly, optional: true)
        .AddEnvironmentVariables()
        .Build();
    ProbeDiagnostics.Stage = "binding SmartThings configuration";
    var settings = config.GetSection("SmartThings").Get<SmartThingsOptions>() ?? new();
    if (!settings.Enabled || !string.Equals(settings.AuthenticationMode, "OAuth",
            StringComparison.OrdinalIgnoreCase))
        return Stop("enabled HomeOps OAuth configuration is required.");
    if (string.IsNullOrWhiteSpace(settings.DataProtectionKeysPath))
        return Stop("existing data-protection keys are not configured.");
    ProbeDiagnostics.Stage = "checking the existing data-protection key directory";
    var keyPath = Path.GetFullPath(settings.DataProtectionKeysPath, apiDirectory);
    if (!Directory.Exists(keyPath))
        return Stop("existing data-protection key directory is unavailable.");
    var connectionString = config.GetConnectionString("HomeOps");
    if (string.IsNullOrWhiteSpace(connectionString))
        return Stop("HomeOps database configuration is unavailable.");

    // One SELECT only. No migrations, EnsureCreated, ingestion or SaveChanges.
    ProbeDiagnostics.Stage = "configuring the database read";
    var options = new DbContextOptionsBuilder<HomeOpsDbContext>()
        .UseSqlServer(connectionString)
        .AddInterceptors(new RejectDatabaseWrites())
        .Options;
    await using var db = new HomeOpsDbContext(options);
    ProbeDiagnostics.Stage = "reading existing OAuth authorization from SQL Server";
    var authorization = await db.SmartThingsAuthorizations.AsNoTracking()
        .Where(x => x.Id == 1)
        .Select(x => new { x.ProtectedAccessToken, x.AccessTokenExpiresAt,
            x.RequiresReauthorization })
        .SingleOrDefaultAsync();
    if (authorization is null || authorization.RequiresReauthorization)
        return Stop("existing OAuth authorization is unavailable; no authorization attempted.");
    if (authorization.AccessTokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(
            Math.Max(0, settings.RefreshSkewMinutes)))
        return Stop("authentication requires token refresh; no refresh attempted.");

    ProbeDiagnostics.Stage = "opening the existing data-protection key ring";
    var protection = DataProtectionProvider.Create(new DirectoryInfo(keyPath),
        builder => builder.SetApplicationName("HomeOps").DisableAutomaticKeyGeneration());
    // Same purpose as HomeOps. Decrypt only the existing access token internally.
    ProbeDiagnostics.Stage = "decrypting the existing OAuth access token";
    var token = protection.CreateProtector("SmartThings.AccessToken.v1")
        .Unprotect(authorization.ProtectedAccessToken);
    ProbeDiagnostics.Stage = "configuring the SmartThings HTTP client";
    var baseUri = new Uri(settings.BaseUrl.TrimEnd('/') + "/");
    if (baseUri.Scheme != "https" || !string.IsNullOrEmpty(baseUri.UserInfo) ||
        !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
        return Stop("a clean HTTPS SmartThings API base URL is required.");
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = baseUri,
        Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 1, 60))
    };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    Uri SafeUri(string address)
    {
        var uri = new Uri(baseUri, address);
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new ProbeFailure("A paging URL did not use HTTPS.");
        if (!string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != baseUri.Port)
            throw new ProbeFailure("A paging URL changed the configured API host or port.");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ProbeFailure("A paging URL contained user information or a fragment.");

        // History links may omit /v1/. Accept that exact endpoint, rather than
        // requiring all links to start with BaseUrl or allowing arbitrary paths.
        var devicePath = new Uri(baseUri, "devices").AbsolutePath;
        var historyPath = new Uri(baseUri, "history/devices").AbsolutePath;
        if (uri.AbsolutePath != devicePath && uri.AbsolutePath != historyPath &&
            uri.AbsolutePath != "/history/devices")
            throw new ProbeFailure("A paging URL was outside the permitted device/history endpoints.");
        return uri;
    }
    async Task<JsonDocument?> GetAsync(string address)
    {
        using var response = await http.GetAsync(SafeUri(address));
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            Stop("HTTP 401; token refresh intentionally disabled.");
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            Stop($"HTTP {(int)response.StatusCode}; response body suppressed.");
            return null;
        }
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    string? deviceId = null, locationId = null;
    string? page = "devices?capability=contactSensor";
    var visited = new HashSet<Uri>();
    // Bound discovery as well as history so a probe cannot run indefinitely.
    for (var number = 0; number < 10 && page is not null && deviceId is null; number++)
    {
        if (!visited.Add(SafeUri(page))) throw new ProbeFailure("The API returned a repeated paging URL.");
        ProbeDiagnostics.Stage = "requesting the contact-sensor device list";
        using var devices = await GetAsync(page);
        if (devices is null) return 1;
        ProbeDiagnostics.Stage = "parsing the contact-sensor device list";
        foreach (var device in devices.RootElement.GetProperty("items").EnumerateArray())
        {
            if (!Guid.TryParse(String(device, "deviceId"), out var id) ||
                !Guid.TryParse(String(device, "locationId"), out var location)) continue;
            deviceId = id.ToString();
            locationId = location.ToString();
            break;
        }
        page = Next(devices.RootElement);
    }
    if (deviceId is null)
        return Stop("no authorized contact sensor found within the discovery page limit.");

    page = $"history/devices?deviceId={deviceId}&locationId={locationId}&limit=20&oldestFirst=false";
    visited.Clear();
    var results = new Dictionary<string, ContactSummary>(StringComparer.Ordinal);
    var pagesFetched = 0;
    var contactEventCount = 0;
    var usableTimestamps = true;
    const int maxPages = 2;
    while (page is not null && pagesFetched < maxPages)
    {
        if (!visited.Add(SafeUri(page))) throw new ProbeFailure("The API returned a repeated paging URL.");
        ProbeDiagnostics.Stage = "requesting recent contact-sensor history";
        using var history = await GetAsync(page);
        if (history is null) return 1;
        ProbeDiagnostics.Stage = "parsing contact-sensor history";
        pagesFetched++;
        var items = history.RootElement.GetProperty("items");
        foreach (var item in items.EnumerateArray())
        {
            if (!Guid.TryParse(String(item, "deviceId"), out var id) ||
                id.ToString() != deviceId || String(item, "capability") != "contactSensor" ||
                String(item, "attribute") != "contact") continue;
            var component = String(item, "component");
            var value = String(item, "value");
            if (string.IsNullOrWhiteSpace(component) || value is not ("open" or "closed")) continue;
            contactEventCount++;
            var rawTime = String(item, "time");
            var time = ParseTime(rawTime);
            var epoch = Integer(item, "epoch");
            DateTimeOffset? epochTime = null;
            if (epoch is not null)
            {
                try { epochTime = DateTimeOffset.FromUnixTimeMilliseconds(epoch.Value); }
                catch (ArgumentOutOfRangeException) { }
            }
            // Compare parsed UTC values, independent of the provider's formatting.
            // A whole-second time (including .000) may omit epoch milliseconds.
            var agree = TimestampsAgree(time, epochTime);
            var timestamp = epochTime ?? time;
            usableTimestamps &= timestamp is not null && agree;
            Write(new { deviceId, component, capability = "contactSensor", attribute = "contact",
                value, timestamp, activityTime = time, epoch, hash = Integer(item, "hash"),
                timestampsAgree = agree });
            if (!results.TryGetValue(component, out var summary))
                results[component] = summary = new ContactSummary();
            if (value == "open")
            {
                summary.OpenPresent = true;
                if (agree && timestamp is not null &&
                    (summary.LastOpened is null || timestamp > summary.LastOpened))
                    summary.LastOpened = timestamp;
            }
            else
            {
                summary.ClosedPresent = true;
                if (agree && timestamp is not null &&
                    (summary.LastClosed is null || timestamp > summary.LastClosed))
                    summary.LastClosed = timestamp;
            }
        }
        page = Next(history.RootElement);
        // Never print arbitrary paging URLs; print only presence and numeric cursors.
        var cursors = new Dictionary<string, long>();
        if (page is not null)
        {
            foreach (var parameter in SafeUri(page).Query.TrimStart('?').Split('&'))
            {
                var pair = parameter.Split('=', 2);
                if (pair.Length == 2 && pair[0] is
                    ("pagingBeforeEpoch" or "pagingBeforeHash" or "pagingAfterEpoch" or "pagingAfterHash") &&
                    long.TryParse(Uri.UnescapeDataString(pair[1]), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var cursor)) cursors[pair[0]] = cursor;
            }
        }
        Write(new { deviceId, pagination = new { pageNumber = pagesFetched,
            itemCount = items.GetArrayLength(), hasNext = page is not null, cursors } });
    }
    Write(new { deviceId, summary = new { pagesFetched, maxPages,
        moreHistoryAvailable = page is not null, pagingFollowed = pagesFetched > 1,
        contactEventCount,
        usableTimestamps, components = results } });
    Console.WriteLine(results.Values.Any(x => x.LastOpened is not null && x.LastClosed is not null)
        && usableTimestamps
        ? "Both states have usable activity timestamps for at least one component in these pages."
        : "Both timestamped states were not established; absent history does not imply no transition.");
    Console.WriteLine("Retention, delayed visibility and rapid-transition completeness remain unverified.");
    return 0;
}

static bool TimestampsAgree(DateTimeOffset? time, DateTimeOffset? epochTime)
{
    if (time is null || epochTime is null) return true;
    if (time.Value == epochTime.Value) return true;
    return time.Value.UtcTicks % TimeSpan.TicksPerSecond == 0 &&
        time.Value.UtcTicks / TimeSpan.TicksPerSecond ==
        epochTime.Value.UtcTicks / TimeSpan.TicksPerSecond;
}

static void VerifyTimestampConsistency()
{
    ProbeDiagnostics.Stage = "checking timestamp comparison against known examples";
    (string Time, long Epoch, bool Expected)[] cases =
    [
        ("2026-10-08T17:21:11+00:00", 1791480071463, true),
        ("2026-10-08T17:21:16.000+00:00", 1791480076322, true),
        ("2026-10-08T19:21:11.000+02:00", 1791480071463, true),
        ("2026-10-08T17:21:11.463Z", 1791480071463, true),
        ("2026-10-08T17:21:12Z", 1791480071463, false),
        ("2026-10-08T17:21:11.123Z", 1791480071463, false)
    ];
    foreach (var item in cases)
        if (TimestampsAgree(ParseTime(item.Time),
                DateTimeOffset.FromUnixTimeMilliseconds(item.Epoch)) != item.Expected)
            throw new ProbeFailure("Timestamp comparison self-check failed.");
}

static string FindApiDirectory()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
         directory = directory.Parent)
    {
        var candidate = Path.Combine(directory.FullName, "src", "HomeOps.Api");
        if (File.Exists(Path.Combine(candidate, "HomeOps.Api.csproj"))) return candidate;
    }
    throw new DirectoryNotFoundException();
}
static string? String(JsonElement element, string name) =>
    element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() : null;
static long? Integer(JsonElement element, string name) =>
    element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
    value.TryGetInt64(out var number) ? number : null;
static DateTimeOffset? ParseTime(string? value) =>
    DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
        out var timestamp) ? timestamp.ToUniversalTime() : null;
static string? Next(JsonElement root) =>
    root.TryGetProperty("_links", out var links) &&
    links.TryGetProperty("next", out var next) ? String(next, "href") : null;
static void Write<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value,
    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
static int Stop(string reason)
{
    Console.WriteLine("STOP: " + reason);
    return 1;
}
sealed class ContactSummary
{
    public bool OpenPresent { get; set; }
    public bool ClosedPresent { get; set; }
    public DateTimeOffset? LastOpened { get; set; }
    public DateTimeOffset? LastClosed { get; set; }
}
sealed class RejectDatabaseWrites : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData,
        InterceptionResult<int> result) => throw new InvalidOperationException("Database writes disabled.");
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Database writes disabled.");
}

static class ProbeDiagnostics
{
    public static string Stage { get; set; } = "initialization";
}
sealed class ProbeFailure(string safeReason) : Exception
{
    public string SafeReason { get; } = safeReason;
}
