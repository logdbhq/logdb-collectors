# LogDB Exporters

`LogDB.Exporters` contains collectors that ingest local logs/metrics and export them to LogDB.

This repository currently exposes three primary collector families:

1. `com.logdb.windows.collector` + `com.logdb.windows.collector.ui`
2. `com.logdb.docker.collector` + `com.logdb.docker.collector.ui`
3. `com.logdb.nginx.collector` + `com.logdb.nginx.collector.ui`

## Repository Layout

| Project | Runtime | Purpose |
|---|---|---|
| `com.logdb.windows.collector` | `net10.0-windows` | Unified Windows collector host (Event Log, IIS, Metrics, Firewall module) |
| `com.logdb.windows.collector.ui` | `net10.0-windows` (Avalonia) | Local admin UI for Windows collector service/console |
| `com.logdb.windows.collector.shared` | `net10.0` | Shared DTOs + path/config helpers |
| `com.logdb.docker.collector` | `net10.0` | Docker container log collector API/worker |
| `com.logdb.docker.collector.ui` | `net10.0` (Blazor Server) | Docker collector operator dashboard |
| `com.logdb.nginx.collector` | `net10.0` | Nginx file-tail collector API/worker |
| `com.logdb.nginx.collector.ui` | `net10.0` (Blazor Server) | Nginx collector operator dashboard |

Additional projects such as `LogDB.Windows.EventViewer`, `LogDB.Windows.IIS`, and `LogDB.Windows.Tracker` remain in the solution and are used by the Windows unified collector host.

## Quick Start

### Windows collector

- Source: [`com.logdb.windows.collector`](./com.logdb.windows.collector)
- UI: [`com.logdb.windows.collector.ui`](./com.logdb.windows.collector.ui)

```powershell
dotnet run --project .\com.logdb.windows.collector\com.logdb.windows.collector.csproj -- --console
dotnet run --project .\com.logdb.windows.collector.ui\com.logdb.windows.collector.ui.csproj
```

### Docker collector

- Source: [`com.logdb.docker.collector`](./com.logdb.docker.collector)
- UI: [`com.logdb.docker.collector.ui`](./com.logdb.docker.collector.ui)

```bash
docker compose -f com.logdb.docker.collector/docker-compose.yaml up -d
```

### Nginx collector

- Source: [`com.logdb.nginx.collector`](./com.logdb.nginx.collector)
- UI: [`com.logdb.nginx.collector.ui`](./com.logdb.nginx.collector.ui)

```bash
docker compose -f com.logdb.nginx.collector/docker-compose.yml up -d
```

## Build Prerequisites

- .NET SDK 10.0 (preview line, per project target frameworks)
- Docker Engine (for Docker/Nginx collectors)
- Windows (for `com.logdb.windows.collector*`)

## Operational Model

- Windows collector control plane is local-only via named pipes.
- Docker and Nginx collectors expose HTTP APIs for local/operator dashboards.
- All collectors use outbound delivery to LogDB and local checkpoint/spool persistence.

## Project Docs

- [`com.logdb.windows.collector/README.md`](./com.logdb.windows.collector/README.md)
- [`com.logdb.windows.collector.ui/README.md`](./com.logdb.windows.collector.ui/README.md)
- [`com.logdb.docker.collector/README.md`](./com.logdb.docker.collector/README.md)
- [`com.logdb.docker.collector.ui/README.md`](./com.logdb.docker.collector.ui/README.md)
- [`com.logdb.nginx.collector/README.md`](./com.logdb.nginx.collector/README.md)
- [`com.logdb.nginx.collector.ui/README.md`](./com.logdb.nginx.collector.ui/README.md)
