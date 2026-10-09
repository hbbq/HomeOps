# Temporary SmartThings history probe

Run from the workspace root (the directory containing HomeOps):

    dotnet run --project HomeOps/tools/SmartThingsHistoryProbe/SmartThingsHistoryProbe.csproj

Requires the .NET 10 SDK and access to the existing HomeOps database and data-protection keys. Nothing is added to the solution or application startup.

Configuration precedence: HomeOps appsettings.json, appsettings for DOTNET_ENVIRONMENT (then ASPNETCORE_ENVIRONMENT, otherwise Production), HomeOps User Secrets, environment variables. User Secrets are deliberately loaded even outside Development for this local probe. Relative data-protection key paths resolve against src/HomeOps.Api; use the existing deployment's absolute path override if its working directory differs. Do not print configuration or credentials.

The probe reads authorization ID 1 with a SELECT and no tracking, refuses authorization requiring refresh or owner action, and decrypts only the existing access token using HomeOps' application name and protector purpose. It does not invoke the host or token provider. SaveChanges is rejected by an interceptor; automatic key generation is disabled. HTTP requests are GET only, redirects are disabled, and paging links must remain on the configured HTTPS API origin and match the configured device/history endpoints (the exact unversioned /history/devices alias is also allowed). HTTP 401 stops without refresh. Exceptions expose only their type; error response bodies are suppressed.

Discovery selects the first authorized contact sensor with valid device/location identifiers (at most ten device-list pages). History is capped at two pages of twenty items. Output contains contact records, numeric paging cursors, and per-component maximum opening/closing timestamps. Activity time and epoch are compared when both exist. Whole-second activity values (including strings with .000) are consistent with an epoch in that same UTC second; the millisecond epoch is preferred for output and maxima. Differences across a second boundary, or mismatched nonzero fractional timestamps, are rejected. A missing timestamp remains unknown; no current-state timestamp is fabricated. Output never includes arbitrary paging URLs, device labels, location IDs, tokens, or connection strings.

A successful second page establishes basic paging for this token. Both states with usable timestamps support independent last_opened/last_closed maxima for that component. This small sample does not prove rapid-transition completeness, retention, or absence of delayed events. Missing states may be on older pages. No HomeOps/Resident feature is implemented.

The probe runs six timestamp consistency self-checks before loading configuration. To run only those checks without any credentials or database access, append -- --self-test to the run command.
