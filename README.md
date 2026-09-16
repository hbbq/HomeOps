# HomeOps

HomeOps v1 is a small ASP.NET Core service that collects home measurements, stores their history in SQL Server, and exposes read-only HTTP endpoints. It includes a simulator plus optional SmartThings and SMHI weather polling sources, which can run at the same time.

## Configuration

The required SQL Server connection string uses standard .NET configuration. Set it locally with the `ConnectionStrings__HomeOps` environment variable (two underscores):

```powershell
$env:ConnectionStrings__HomeOps = "Server=localhost;Database=HomeOps;User Id=sa;Password=<password>;TrustServerCertificate=True"
```

The SQL login must be able to create or update the configured database. The service applies its checked-in Entity Framework Core migrations during startup. It stops with an error if the connection string is missing or SQL Server cannot be reached.

All enabled sources run immediately after startup. Sources without their own interval use `Acquisition__IntervalSeconds` (default 30 seconds; values below one second are treated as one second). Source polling is concurrent, with `Acquisition__MaxConcurrentSourceReads` controlling the limit (default 4, clamped to 1-16). The simulator is enabled by default and records temperature, humidity, and a 0/1 occupied state for one living-room device. Disable it with `Simulator__Enabled=false`.

### SmartThings

SmartThings uses polling and a bearer/PAT token. It automatically discovers all devices the token can access across its authorized locations; device IDs do not need to be configured. It does not implement OAuth refresh or webhooks. Configure the token outside source control:

```powershell
dotnet user-secrets set --project src/HomeOps.Api "SmartThings:Token" "<token>"
$env:SmartThings__Enabled = "true"
```

For deployments, supply the same keys through environment or secret configuration. Never put the token in `appsettings.json` or an image layer. Optional settings are `SmartThings__BaseUrl` (default `https://api.smartthings.com/v1/`), `SmartThings__TimeoutSeconds` (default 15), and `SmartThings__MaxConcurrentDeviceReads` (default 4, clamped to 1-16).

On every poll, HomeOps follows the SmartThings device-list pagination and then queries every discovered device through its full-status endpoint. This picks up devices added to or removed from an authorized location without a configuration change. HomeOps ingests numeric values and supported sensor states for these capability attributes:

- `temperatureMeasurement/temperature`
- `relativeHumidityMeasurement/humidity`
- `battery/battery`
- `powerMeter/power`
- `energyMeter/energy`
- `motionSensor/motion` (`active` = 1, `inactive` = 0)
- `contactSensor/contact` (`open` = 1, `closed` = 0)

Point keys have the form `component/capability/attribute`, so the same capability on different device components remains distinct. Attribute timestamps and units are preserved when present; collection time is used when the timestamp is absent or invalid. Motion and contact are exposed as boolean points without units. Other non-numeric values, unknown sensor states, and unlisted capabilities are ignored.

By default, HomeOps stores a new history row only when a point's numeric value differs from its latest stored value. This avoids repeated unchanged simulator and SmartThings observations. Sources whose observations have provider timestamps can opt into the timestamp-based history semantics described below for SMHI.

### SMHI weather observations

The SMHI source is disabled by default and retrieves actual observations for one explicitly configured Swedish meteorological station. Choose a station that exposes the measurements you need, then enable it with its numeric SMHI station identifier:

```powershell
$env:SmhiWeather__Enabled = "true"
$env:SmhiWeather__StationId = "<station-id>"
$env:SmhiWeather__StationName = "<display-name>" # optional
```

No API key is required. HomeOps requests the latest-hour observation for outdoor temperature (SMHI parameter 1), relative humidity (6), sea-level air pressure (9), and wind speed (4). A measurement that the selected station does not expose, or whose response has no numeric observation, is omitted. Values use canonical units `°C`, `%`, `hPa`, and `m/s`, and retain the provider's observation timestamp.

SMHI weather polling defaults to every 15 minutes, independently of the simulator and other sources. Override it with `SmhiWeather__PollingIntervalMinutes`; the HTTP timeout is controlled by `SmhiWeather__TimeoutSeconds` (default 15). `SmhiWeather__BaseUrl` is also configurable for testing or compatible mirrors. Interval and timeout values must be greater than zero.

The persisted source is `smhi`, and the configured station ID is its stable device identity; changing the optional display name does not create a new device. Weather history stores each distinct provider timestamp even when its value is unchanged, while repeated polls of the same timestamp are ignored.

SMHI open data is provided under Creative Commons Attribution 4.0 terms. Review the current [SMHI open-data conditions](https://www.smhi.se/data/om-smhis-data/villkor-for-anvandning) when redistributing the data.

## Run locally

.NET 8 SDK and an existing SQL Server are required.

```powershell
dotnet restore
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/HomeOps.Api
```

The address is printed by ASP.NET Core at startup. To select one explicitly, set `ASPNETCORE_URLS`, for example `http://localhost:8080`.

Open `/dashboard/` on that address (for example, `http://localhost:8080/dashboard/`) to view the latest measurements grouped by device. The dashboard refreshes every 30 seconds and displays timestamps in the browser's local timezone. It shows only measurement points that have at least one recorded value. Devices can be disabled and re-enabled from the dashboard; disabled devices remain visible there but are hidden from the public device and measurement endpoints.
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
- `GET /api/measurements/latest` returns the latest stored value for every known point that has recorded data.
- `GET /api/measurement-points/{pointId}/history` returns newest-first history for one point.
- `POST /api/displays/{id}/messages` queues `{ "text": "..." }` for a display.
- `GET /api/displays/{id}/messages/next` returns and removes the oldest queued message, or returns `204 No Content` when none is queued.

Disabled devices are omitted from the device and latest-measurement lists, and their measurement-point history returns `404 Not Found`. Existing endpoint URLs and response shapes are unchanged.

History accepts optional ISO 8601 `from` and `to` timestamps and a `limit`. The default limit is 500 and the maximum is 5,000. For example:

```text
GET /api/measurement-points/1/history?from=2026-09-11T00:00:00Z&limit=100
```

All values are numeric decimals. The `kind` and `unit` fields describe interpretation; the simulated boolean point uses `kind: "boolean"`, no unit, and values `0` or `1`.

Display queues are in memory and are cleared when HomeOps restarts. Each case-sensitive display ID has an independent FIFO queue limited to 20 messages. Text is limited to 1,024 UTF-8 bytes. A full queue rejects a new message with `429 Too Many Requests`; consuming a message provides at-most-once delivery.
