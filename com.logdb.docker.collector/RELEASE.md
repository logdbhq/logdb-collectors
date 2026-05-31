# Release Checklist

## Version: 1.0.0

First GA release after RC1. Set `VERSION` to the tag you are publishing; the
commands below pick it up. The collector ships as **two images** — backend
(`logdb/docker-collector`) and UI (`logdb/docker-collector-ui`).

### What's new since RC1

- **Sent Records** page — per-record view of what the exporter actually handed
  to grpc-logger (logs **and** metrics), with delivery outcome and the captured
  LogDB event GUID. Pairs with Live Console (caught) to show caught-vs-sent.
- **Durable metrics** — container metrics are now spooled before sending
  (at-least-once), so a LogDB outage or a crash mid-cycle no longer loses them.
  Bounded by `Spool:MetricsMaxRecords` (drop-oldest). Surfaced on the dashboard
  Exporter card ("Metrics queued" / "Metrics dropped").
- **Chunked log replay** — the spool drains in `Spool:SendChunkSize` slices,
  committing each as it lands, so a slow/failed send can't wedge a backlog.
- **Honest Live Metrics** — a data-age badge warns when a collection cycle is
  overdue; the refresh indicator is labeled "poll Ns" (it's the UI poll, not the
  data age — metrics only change once per collection interval).

## Version and metadata

- [ ] Build date passed via `--build-arg BUILD_DATE=$(date -u +%Y-%m-%d)`
- [ ] Commit hash passed via `--build-arg COMMIT_HASH=$(git rev-parse --short HEAD)`
- [ ] `GET /api/status` returns correct version, buildDate, commitHash

## Run tests

```bash
dotnet test com.logdb.docker.collector.tests/ --verbosity normal
```

All tests must pass (metrics-spool FIFO / cap / restart, chunked-drain forward progress).

## Build the images

Run from the repository root. Each image builds from its own project directory.

```bash
VERSION=1.0.0
COMMIT=$(git rev-parse --short HEAD)
DATE=$(date -u +%Y-%m-%d)

# Backend (collector API)
docker build -f com.logdb.docker.collector/Dockerfile \
  --build-arg BUILD_DATE=$DATE \
  --build-arg COMMIT_HASH=$COMMIT \
  -t logdb/docker-collector:$VERSION \
  com.logdb.docker.collector

# Operator UI
docker build -f com.logdb.docker.collector.ui/Dockerfile \
  -t logdb/docker-collector-ui:$VERSION \
  com.logdb.docker.collector.ui
```

- [ ] Both images build successfully
- [ ] Health check works: backend reaches "healthy" within 30 seconds
- [ ] Image sizes reasonable
- [ ] No build artifacts or source code in the images

## Tag and push

```bash
docker tag logdb/docker-collector:$VERSION logdb/docker-collector:latest
docker tag logdb/docker-collector-ui:$VERSION logdb/docker-collector-ui:latest
docker push logdb/docker-collector:$VERSION
docker push logdb/docker-collector:latest
docker push logdb/docker-collector-ui:$VERSION
docker push logdb/docker-collector-ui:latest
```

## Compose example

- [ ] `docker compose -f docker-compose.docker.collector.yaml config` validates
- [ ] `docker compose up -d` starts both collector and UI
- [ ] Collector connects to Docker via socket mount
- [ ] UI connects to collector API
- [ ] Volume persists across `docker compose down && docker compose up -d`

## Functional smoke tests

- [ ] Containers discovered via the mounted Docker socket; appear in `/api/containers`
- [ ] Logs tailed: `/api/pipeline/status` recordsRead > 0
- [ ] Checkpoints flush: `/api/checkpoints/status` lastFlushUtc populated
- [ ] Spool receives records: `/api/spool/status` queuedRecords > 0
- [ ] Enable exporter, verify delivery: `/api/exporter/status` recordsSent > 0
- [ ] **Sent Records** page lists delivered log + metric records with GUIDs
- [ ] **Metrics durability**: disable the exporter, confirm "Metrics queued" climbs
      on the dashboard, re-enable, confirm it drains
- [ ] **Live Metrics** data-age badge turns amber/red when collection is overdue
- [ ] Restart collector, verify checkpoint, log-spool, and metrics-spool recovery

## Security

- [ ] API key never appears in any status endpoint response, the UI, or logs
- [ ] Sample `appsettings.json` / compose use placeholders, no real secrets
- [ ] appsettings.json has the exporter disabled by default; appsettings.Docker.json enabled

## Known limitations

- [ ] Only Docker json-file log driver supported
- [ ] At-least-once delivery (duplicates possible) — now applies to metrics too
- [ ] Single-host only (no multi-host coordination)
- [ ] No log content parsing beyond the Docker JSON wrapper
- [ ] Spool segment file name has millisecond resolution (theoretical collision under extreme throughput)

## Release artifacts

- [ ] Backend image pushed
- [ ] UI image pushed
- [ ] Release notes drafted
- [ ] Version tag created in git
