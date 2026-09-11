# HomeOps

HomeOps v1 is a small ASP.NET Core service that collects home measurements, stores their history in SQL Server, and exposes read-only HTTP endpoints. It includes a simulator and an optional SmartThings polling source; both can run at the same time.

## Configuration

The required SQL Server connection string uses standard .NET configuration. Set it locally with the `ConnectionStrings__HomeOps` environment variable (two underscores):

```powershell
$env:ConnectionStrings__HomeOps = "Server=localhost;Database=HomeOps;User Id=sa;Password=<password>;TrustServerCertificate=True"
```

The SQL login must be able to create or update the configured database. The service applies its checked-in Entity Framework Core migrations during startup. It stops with an error if the connection string is missing or SQL Server cannot be reached.

All enabled sources run immediately after startup and every 30 seconds thereafter. Override the shared interval with `Acquisition__IntervalSeconds`; values below one second are treated as one second. The simulator is enabled by default and records temperature, humidity, and a 0/1 occupied state for one living-room device. Disable it with `Simulator__Enabled=false`.

### SmartThings

SmartThings is disabled by default. The initial integration uses polling, a bearer/PAT token, and an explicit device-ID allowlist. It does not implement OAuth refresh, automatic discovery, or webhooks. Configure a token outside source control and add one or more device IDs:

```powershell
dotnet user-secrets set --project src/HomeOps.Api "SmartThings:Token" "<token>"
$env:SmartThings__Enabled = "true"
$env:SmartThings__DeviceIds__0 = "<device-id>"
$env:SmartThings__DeviceIds__1 = "<another-device-id>"
```

For deployments, supply the same keys through environment or secret configuration. Never put the token in `appsettings.json` or an image layer. Optional settings are `SmartThings__BaseUrl` (default `https://api.smartthings.com/v1/`) and `SmartThings__TimeoutSeconds` (default 15).

Each selected device is queried through the SmartThings device and full-status endpoints. HomeOps ingests only numeric values for these capability attributes:

- `temperatureMeasurement/temperature`
- `relativeHumidityMeasurement/humidity`
- `battery/battery`
- `powerMeter/power`
- `energyMeter/energy`

Point keys have the form `component/capability/attribute`, so the same capability on different device components remains distinct. Attribute timestamps and units are preserved when present; collection time is used when the timestamp is absent or invalid. Non-numeric values and unlisted capabilities are ignored.

HomeOps stores a new history row only when a point's numeric value differs from its latest stored value. This applies to every source and avoids repeated unchanged SmartThings observations.

## Run locally

.NET 8 SDK and an existing SQL Server are required.

```powershell
dotnet restore
dotnet run --project src/HomeOps.Api
```

The address is printed by ASP.NET Core at startup. To select one explicitly, set `ASPNETCORE_URLS`, for example `http://localhost:8080`.

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
