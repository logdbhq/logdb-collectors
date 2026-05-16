# LogDB Windows Collector

`com.logdb.windows.collector` is a unified Windows host that runs Event Log, IIS, Windows Metrics, and optional Firewall modules in one process.

## Components

- `com.logdb.windows.collector`: collector host (service or console mode)
- `com.logdb.windows.collector.shared`: shared DTOs/config/control contracts
- `com.logdb.windows.collector.ui`: local admin UI (Avalonia)

## Runtime Model

- Run modes:
  - Service mode (Windows SCM)
  - Console mode (`--console`) for local testing
- Local-only control plane via named pipes:
  - Service: `com.logdb.windows.collector.service`
  - Console: `com.logdb.windows.collector.console`
- No HTTP admin endpoint is exposed by this collector.

## Service Identity

- Service name: `LogDBWindowsCollector`
- Display name: `LogDB Windows Collector`

## Command Flags

```powershell
com.logdb.windows.collector.exe --console
com.logdb.windows.collector.exe --install-service
com.logdb.windows.collector.exe --start-service
com.logdb.windows.collector.exe --stop-service
com.logdb.windows.collector.exe --uninstall-service
```

## Configuration and Paths

- Default base dir: `%ProgramData%\LogDB\collector`
- Config file: `%ProgramData%\LogDB\collector\appsettings.json`
- UI settings: `%ProgramData%\LogDB\collector\ui-settings.json`
- Logs: `%ProgramData%\LogDB\collector\logs`

Environment variables:

- `LOGDB_COLLECTOR_BASE_DIR`
- `LOGDB_COLLECTOR_PIPE_NAME` (legacy override)
- `LOGDB_COLLECTOR_SERVICE_PIPE_NAME`
- `LOGDB_COLLECTOR_CONSOLE_PIPE_NAME`

## Hosted Modules

- `EventLogCollectorModule`
- `IisLogCollectorModule`
- `WindowsMetricsCollectorModule`
- `FirewallRuleModule`

## Control Commands (Named Pipe)

- `status`
- `get-config`
- `update-config`
- `reload-config`
- `enable-module` / `disable-module`
- `diagnostics`
- `validate-event-log-access`
- `validate-iis-paths`
- `validate-destination-connection`
- `preview-event-logs`
- `preview-iis-logs`
- `preview-metrics`
- `apply-firewall` / `remove-firewall`
- `stop-host`

## Run from Source

```powershell
dotnet run --project .\com.logdb.windows.collector.csproj -- --console
```

For service administration and operator workflow, use the UI project:

- [`../com.logdb.windows.collector.ui/README.md`](../com.logdb.windows.collector.ui/README.md)
