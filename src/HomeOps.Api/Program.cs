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
builder.Services.Configure<SimulatorOptions>(builder.Configuration.GetSection("Simulator"));
builder.Services.AddSingleton<IMeasurementSource, SimulatedMeasurementSource>();
builder.Services.AddHostedService<MeasurementIngestionService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "HomeOps API",
        Version = "v1",
        Description = "Read-only access to HomeOps devices and measurements."
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

app.MapGet("/", () => Results.Ok(new ServiceInfoResponse("HomeOps", "v1")))
    .WithName("GetServiceInfo")
    .WithSummary("Get service information")
    .WithTags("Service")
    .Produces<ServiceInfoResponse>();
app.MapHomeOpsEndpoints();

await app.RunAsync();

public partial class Program;

public sealed record ServiceInfoResponse(string Service, string Version);
