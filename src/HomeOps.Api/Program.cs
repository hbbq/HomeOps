using HomeOps.Api.Acquisition;
using HomeOps.Api.Data;
using HomeOps.Api.Displays;
using HomeOps.Api.Endpoints;
using HomeOps.Api.SmartThingsAuth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("HomeOps");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "The SQL Server connection string is required. Configure ConnectionStrings:HomeOps " +
        "or the ConnectionStrings__HomeOps environment variable.");
}

builder.Services.AddDbContextFactory<HomeOpsDbContext>(options =>
    options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
builder.Services.Configure<AcquisitionOptions>(builder.Configuration.GetSection("Acquisition"));
builder.Services.Configure<SimulatorOptions>(builder.Configuration.GetSection("Simulator"));
builder.Services.AddOptions<SmartThingsOptions>()
    .Bind(builder.Configuration.GetSection("SmartThings"))
    .Validate(x => x.MotionHoldSeconds > 0, "SmartThings:MotionHoldSeconds must be greater than zero.")
    .Validate(x => x.TemperatureDeadbandCelsius > 0, "SmartThings:TemperatureDeadbandCelsius must be greater than zero.")
    .ValidateOnStart();
builder.Services.Configure<SmhiWeatherOptions>(builder.Configuration.GetSection("SmhiWeather"));
builder.Services.AddSingleton<DisplayMessageQueue>();

if (builder.Configuration.GetValue("Simulator:Enabled", true))
{
    builder.Services.AddSingleton<IMeasurementSource, SimulatedMeasurementSource>();
}

if (builder.Configuration.GetValue("SmartThings:Enabled", false))
{
    var smartThings = builder.Configuration.GetSection("SmartThings").Get<SmartThingsOptions>() ?? new();
    var patMode = string.Equals(smartThings.AuthenticationMode, "Pat", StringComparison.OrdinalIgnoreCase);
    var oauthMode = string.Equals(smartThings.AuthenticationMode, "OAuth", StringComparison.OrdinalIgnoreCase);
    if (!patMode && !oauthMode)
    {
        throw new InvalidOperationException("SmartThings:AuthenticationMode must be OAuth or Pat.");
    }

    if (patMode && string.IsNullOrWhiteSpace(smartThings.Token))
    {
        throw new InvalidOperationException("SmartThings:Token is required in deprecated Pat mode.");
    }

    if (oauthMode &&
        (string.IsNullOrWhiteSpace(smartThings.ClientId) ||
         string.IsNullOrWhiteSpace(smartThings.ClientSecret) ||
         !Uri.TryCreate(smartThings.RedirectUri, UriKind.Absolute, out var redirectUri) || redirectUri.Scheme != Uri.UriSchemeHttps ||
         redirectUri.AbsolutePath != "/smartthings/oauth/callback" ||
         !Uri.TryCreate(smartThings.AuthorizationUrl, UriKind.Absolute, out var authorizationUri) || authorizationUri.Scheme != Uri.UriSchemeHttps ||
         !Uri.TryCreate(smartThings.TokenUrl, UriKind.Absolute, out var tokenUri) || tokenUri.Scheme != Uri.UriSchemeHttps ||
         smartThings.RefreshSkewMinutes < 0 ||
         smartThings.AuthorizationStateLifetimeMinutes <= 0 ||
         string.IsNullOrWhiteSpace(smartThings.DataProtectionKeysPath)))
    {
        throw new InvalidOperationException(
            "SmartThings OAuth requires ClientId, ClientSecret, HTTPS endpoint URLs, an HTTPS RedirectUri ending in " +
            "/smartthings/oauth/callback, valid lifetime settings, and DataProtectionKeysPath.");
    }

    var dataProtection = builder.Services.AddDataProtection().SetApplicationName("HomeOps");
    if (oauthMode)
    {
        dataProtection.PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(smartThings.DataProtectionKeysPath)));
    }

    builder.Services.AddSingleton<SmartThingsTokenProvider>();
    builder.Services.AddTransient<SmartThingsAuthenticationHandler>();
    builder.Services.AddSingleton<SmartThingsOAuthService>();
    builder.Services.AddHttpClient("SmartThingsOAuth", client =>
        client.Timeout = TimeSpan.FromSeconds(Math.Max(1, smartThings.TimeoutSeconds)));
    builder.Services.AddHttpClient("SmartThings", (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SmartThingsOptions>>().Value;
        var baseUrl = options.BaseUrl.EndsWith('/') ? options.BaseUrl : $"{options.BaseUrl}/";
        client.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));
    }).AddHttpMessageHandler<SmartThingsAuthenticationHandler>();
    builder.Services.AddSingleton<IMeasurementSource, SmartThingsMeasurementSource>();
}

if (builder.Configuration.GetValue("SmhiWeather:Enabled", false))
{
    var stationId = builder.Configuration["SmhiWeather:StationId"];
    var configuredBaseUrl = builder.Configuration["SmhiWeather:BaseUrl"];
    var pollingIntervalMinutes = builder.Configuration.GetValue("SmhiWeather:PollingIntervalMinutes", 15);
    var timeoutSeconds = builder.Configuration.GetValue("SmhiWeather:TimeoutSeconds", 15);
    if (string.IsNullOrWhiteSpace(stationId))
    {
        throw new InvalidOperationException("SmhiWeather:StationId is required when SMHI weather is enabled.");
    }

    if (pollingIntervalMinutes <= 0 || timeoutSeconds <= 0)
    {
        throw new InvalidOperationException(
            "SmhiWeather:PollingIntervalMinutes and SmhiWeather:TimeoutSeconds must be greater than zero.");
    }

    if (!Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var smhiBaseUri) ||
        (smhiBaseUri.Scheme != Uri.UriSchemeHttps && smhiBaseUri.Scheme != Uri.UriSchemeHttp))
    {
        throw new InvalidOperationException("SmhiWeather:BaseUrl must be an absolute HTTP or HTTPS URL.");
    }

    builder.Services.AddHttpClient("SmhiWeather", (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SmhiWeatherOptions>>().Value;
        var baseUrl = options.BaseUrl.EndsWith('/') ? options.BaseUrl : $"{options.BaseUrl}/";
        client.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    });
    builder.Services.AddSingleton<IMeasurementSource, SmhiWeatherMeasurementSource>();
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MotionHoldScheduleSignal>();
builder.Services.AddSingleton<MeasurementPersistenceService>();
builder.Services.AddHostedService<MeasurementIngestionService>();
builder.Services.AddHostedService<MotionHoldExpirationService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "HomeOps API",
        Version = "v1",
        Description = "Access to HomeOps devices, measurements, and dashboard device management."
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<HomeOpsDbContext>>();
    await using var db = await dbContextFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/dashboard", () => Results.Redirect("/dashboard/"));
app.MapGet("/", () => Results.Ok(new ServiceInfoResponse("HomeOps", "v1")))
    .WithName("GetServiceInfo")
    .WithSummary("Get service information")
    .WithTags("Service")
    .Produces<ServiceInfoResponse>();
app.MapHomeOpsEndpoints();
if (builder.Configuration.GetValue("SmartThings:Enabled", false) &&
    string.Equals(builder.Configuration["SmartThings:AuthenticationMode"] ?? "OAuth", "OAuth", StringComparison.OrdinalIgnoreCase))
{
    app.MapSmartThingsOAuthEndpoints();
}

await app.RunAsync();

public partial class Program;

public sealed record ServiceInfoResponse(string Service, string Version);
