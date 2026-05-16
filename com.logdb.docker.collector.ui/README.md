# LogDB Docker Collector UI

`com.logdb.docker.collector.ui` is a Blazor Server operator dashboard for `com.logdb.docker.collector`.

## Purpose

- Read collector dashboard and diagnostics data
- Manage container include/disable state
- Toggle container log mode
- Manage per-container/global start date
- Inspect pipeline/checkpoint/spool/exporter status

## Pages

- `Overview`
- `Containers`
- `Pipeline`
- `Checkpoints`
- `Spool`
- `Exporter`
- `Diagnostics`

## Configuration

Environment variables:

- `COLLECTOR_API_URL` (default: `http://localhost:5000`)
- `PathBase` (optional base-path hosting)

## Docker Usage

The UI container defaults to:

- `ASPNETCORE_URLS=http://+:8080`
- `COLLECTOR_API_URL=http://logdb-docker-collector:8080`

Example:

```bash
docker run -d \
  --name logdb-docker-collector-ui \
  -p 5010:8080 \
  -e COLLECTOR_API_URL=http://logdb-docker-collector:8080 \
  logdb/docker-collector-ui:0.1
```

## Run from Source

```bash
dotnet run --project ./com.logdb.docker.collector.ui.csproj
```

Pair with:

- [`../com.logdb.docker.collector/README.md`](../com.logdb.docker.collector/README.md)
