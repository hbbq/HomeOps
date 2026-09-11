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

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<HomeOpsDbContext>>();
    await using var db = await dbContextFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/", () => Results.Ok(new { service = "HomeOps", version = "v1" }));
app.MapGet("/dashboard", () => Results.Redirect("/dashboard/"));
app.MapHomeOpsEndpoints();

await app.RunAsync();

public partial class Program;
