# LogDB Nginx Collector UI

`com.logdb.nginx.collector.ui` is a Blazor Server dashboard for `com.logdb.nginx.collector`.

## Purpose

- Show dashboard summary from collector API
- Inspect configured targets and file existence
- Inspect pipeline, checkpoint, spool, and exporter state

## Pages

- `Overview`
- `Targets`
- `Pipeline`
- `Checkpoints`
- `Spool`
- `Exporter`

## Configuration

Environment variables:

- `COLLECTOR_API_URL` (default: `http://localhost:5000`)
- `PathBase` (optional base-path hosting)

Container defaults:

- `ASPNETCORE_URLS=http://+:8081`
- `COLLECTOR_API_URL=http://logdb-nginx-collector:8080`

## Run from Source

```bash
dotnet run --project ./com.logdb.nginx.collector.ui.csproj
```

Pair with:

- [`../com.logdb.nginx.collector/README.md`](../com.logdb.nginx.collector/README.md)
