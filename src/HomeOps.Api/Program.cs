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
builder.Services.Configure<SmartThingsOptions>(builder.Configuration.GetSection("SmartThings"));

if (builder.Configuration.GetValue("Simulator:Enabled", true))
{
    builder.Services.AddSingleton<IMeasurementSource, SimulatedMeasurementSource>();
}

if (builder.Configuration.GetValue("SmartThings:Enabled", false))
{
    var smartThingsToken = builder.Configuration["SmartThings:Token"];
    var smartThingsDeviceIds = builder.Configuration
        .GetSection("SmartThings:DeviceIds")
        .Get<string[]>() ?? [];

    if (string.IsNullOrWhiteSpace(smartThingsToken))
    {
        throw new InvalidOperationException(
            "SmartThings:Token is required when SmartThings is enabled. Supply it through user secrets or an environment variable.");
    }

    if (smartThingsDeviceIds.All(string.IsNullOrWhiteSpace))
    {
        throw new InvalidOperationException(
            "At least one SmartThings:DeviceIds entry is required when SmartThings is enabled.");
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

builder.Services.AddHostedService<MeasurementIngestionService>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<HomeOpsDbContext>>();
    await using var db = await dbContextFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

app.MapGet("/", () => Results.Ok(new { service = "HomeOps", version = "v1" }));
app.MapHomeOpsEndpoints();

await app.RunAsync();

public partial class Program;
