# LogDB Windows Collector UI

`com.logdb.windows.collector.ui` is a local Windows admin console (Avalonia) for `com.logdb.windows.collector`.

## Scope

- Discovers local service/console collector endpoints
- Configures data sources and destination
- Runs validations and previews
- Manages service lifecycle and console mode
- Reads diagnostics and builds support bundle
- Supports UI auto-update (Velopack) and service binary update workflow

## Pages

- `OverviewPageView`
- `DataSourcesPageView`
- `DestinationPageView`
- `DiagnosticsPageView`
- `ServiceManagementPageView`
- `AdvancedPageView`

## Collector Communication

- Uses local named pipes via `ControlChannelClient`
- Targets:
  - `com.logdb.windows.collector.service`
  - `com.logdb.windows.collector.console`

No remote/multi-host control mode is implemented in this project.

## Environment Variables

- `LOGDB_COLLECTOR_UI_AUTO_UPDATE` (set `false` to disable startup auto-check)
- `LOGDB_COLLECTOR_UI_UPDATE_URL` (Velopack source override)
- `LOGDB_COLLECTOR_UI_UPDATE_TOKEN` (optional token for private update source)
- `LOGDB_COLLECTOR_SERVICE_RELEASES_API` (service release API override)
- `LOGDB_COLLECTOR_SERVICE_UPDATE_TOKEN` (service update token override)

## Service Update Workflow

`Service Management` checks GitHub releases and applies collector service updates from `win-col-v*` release assets (`*-win-x64.zip`):

1. Detect installed collector binary version
2. Query latest matching release tag
3. Download and extract service payload
4. Stop service (if running), replace binaries (preserve `appsettings.json`), restart service

Applying service updates requires Administrator privileges.

## Run from Source

```powershell
dotnet run --project .\com.logdb.windows.collector.ui.csproj
```

Pair this UI with:

- [`../com.logdb.windows.collector/README.md`](../com.logdb.windows.collector/README.md)
