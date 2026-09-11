# HomeOps

HomeOps v1 is a small ASP.NET Core service that simulates home measurements, stores their history in SQL Server, and exposes read-only HTTP endpoints. It does not connect to SmartThings or any other external source and has no authentication.

## Configuration

The required SQL Server connection string uses standard .NET configuration. Set it locally with the `ConnectionStrings__HomeOps` environment variable (two underscores):

```powershell
$env:ConnectionStrings__HomeOps = "Server=localhost;Database=HomeOps;User Id=sa;Password=<password>;TrustServerCertificate=True"
```

The SQL login must be able to create or update the configured database. The service applies its checked-in Entity Framework Core migrations during startup. It stops with an error if the connection string is missing or SQL Server cannot be reached.

The simulator records temperature, humidity, and a 0/1 occupied state for one living-room device. It runs immediately after startup and every 30 seconds thereafter. Override the interval with `Simulator__IntervalSeconds`; values below one second are treated as one second.

## Run locally

.NET 8 SDK and an existing SQL Server are required.

```powershell
dotnet restore
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/HomeOps.Api
```

The address is printed by ASP.NET Core at startup. To select one explicitly, set `ASPNETCORE_URLS`, for example `http://localhost:8080`.

When running in the `Development` environment, interactive Swagger UI is available at `/swagger` and the OpenAPI document at `/swagger/v1/swagger.json`. These endpoints are not exposed in other environments.

## Run as a container

Build the Linux image from the repository root:

```console
docker build -t homeops .
```

Run it with a connection string that is reachable from inside the container. On Docker Desktop, `host.docker.internal` commonly addresses SQL Server on the host:

```console
docker run --rm -p 8080:8080 -e "ConnectionStrings__HomeOps=Server=host.docker.internal;Database=HomeOps;User Id=sa;Password=<password>;TrustServerCertificate=True" homeops
```

For a remote SQL Server, replace the server and credentials as appropriate. Keep real credentials in environment or secret configuration, not in source control.

## HTTP API

- `GET /api/devices` lists known devices and their measurement points.
- `GET /api/measurements/latest` returns the latest stored value for every known point.
- `GET /api/measurement-points/{pointId}/history` returns newest-first history for one point.

History accepts optional ISO 8601 `from` and `to` timestamps and a `limit`. The default limit is 500 and the maximum is 5,000. For example:

```text
GET /api/measurement-points/1/history?from=2026-09-11T00:00:00Z&limit=100
```

All values are numeric decimals. The `kind` and `unit` fields describe interpretation; the simulated boolean point uses `kind: "boolean"`, no unit, and values `0` or `1`.
