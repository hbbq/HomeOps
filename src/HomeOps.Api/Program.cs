using HomeOps.Api.Acquisition;
using HomeOps.Api.Data;
using HomeOps.Api.Endpoints;
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

if (builder.Configuration.GetValue("Simulator:Enabled", true))
{
    builder.Services.AddSingleton<IMeasurementSource, SimulatedMeasurementSource>();
}

if (builder.Configuration.GetValue("SmartThings:Enabled", false))
{
    var smartThingsToken = builder.Configuration["SmartThings:Token"];

    if (string.IsNullOrWhiteSpace(smartThingsToken))
    {
        throw new InvalidOperationException(
            "SmartThings:Token is required when SmartThings is enabled. Supply it through user secrets or an environment variable.");
    }

    builder.Services.AddHttpClient("SmartThings", (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SmartThingsOptions>>().Value;
        var baseUrl = options.BaseUrl.EndsWith('/') ? options.BaseUrl : $"{options.BaseUrl}/";
        client.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));
    });
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

await app.RunAsync();

public partial class Program;

public sealed record ServiceInfoResponse(string Service, string Version);
