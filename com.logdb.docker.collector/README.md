# LogDB Docker Collector

`com.logdb.docker.collector` collects Docker container logs and exports them to LogDB.

## What It Does

- Discovers containers via Docker API/socket
- Tails container json-file logs from `/var/lib/docker/containers`
- Tracks offsets in checkpoint storage
- Buffers records in on-disk spool
- Exports to LogDB (Native/REST)
- Exposes HTTP API for health, control, and dashboard

## Quick Start (Compose)

```bash
docker compose -f docker-compose.docker.collector.yaml up -d
```

Default compose services:

- `logdb-docker-collector` on `8080`
- `logdb-docker-collector-ui` on `5010`

### Snap-installed Docker

If Docker was installed via snap, its data lives under `/var/snap/docker/common/var-lib-docker` instead of `/var/lib/docker`. Open the compose file, comment out the standard host-path line, and uncomment the snap one (they're labeled inline). Confirm your host's path with `docker info | grep "Docker Root Dir"`.

## Required Mounts

| Host source | Container target | Mode | Purpose |
|---|---|---|---|
| `/var/lib/docker/containers` (or snap equivalent) | `/var/lib/docker/containers` | `ro` | Read Docker json-file logs |
| `/var/run/docker.sock` | `/var/run/docker.sock` | `ro` | Container discovery |
| `logdb-collector` volume | `/var/lib/logdb-collector` | `rw` | Checkpoints + spool |

## State Paths

- Checkpoints: `/var/lib/logdb-collector/checkpoints.json`
- Spool directory: `/var/lib/logdb-collector/spool`

Removing the state volume can cause re-read/duplicate delivery.

## Environment Variables

| Variable | Config Mapping |
|---|---|
| `LOGDB_EXPORTER_ENDPOINT` | `LogDbExporter:Endpoint` |
| `LOGDB_EXPORTER_APIKEY` | `LogDbExporter:ApiKey` |
| `LOGDB_EXPORTER_ENABLED` | `LogDbExporter:Enabled` |
| `LOGDB_EXPORTER_MAX_RETRIES` | `LogDbExporter:MaxRetries` |
| `LOGDB_EXPORTER_TIMEOUT` | `LogDbExporter:RequestTimeoutSeconds` |
| `LOGDB_EXPORTER_PROTOCOL` | `LogDbExporter:Protocol` |
| `LOGDB_EXPORTER_COMPRESSION` | `LogDbExporter:EnableCompression` |
| `LOGDB_DISCOVERY_INTERVAL` | `DockerDiscovery:RefreshIntervalSeconds` |
| `LOGDB_CHECKPOINT_FLUSH_INTERVAL` | `Checkpoint:FlushIntervalSeconds` |
| `LOGDB_SPOOL_MAX_DISK_MB` | `Spool:MaxDiskBytes` (MB to bytes) |

## API Endpoints

Health:

- `GET /health`
- `GET /health/live`
- `GET /health/ready`

Status and dashboard:

- `GET /api/status`
- `GET /api/docker/status`
- `GET /api/dashboard`

Containers and collection control:

- `GET /api/containers`
- `GET|POST /api/containers/{id}/toggle`
- `GET|POST /api/containers/{id}/log-mode`
- `GET|POST /api/containers/{id}/start-date`
- `GET|POST /api/start-date/global`

Pipeline/checkpoints/spool/exporter:

- `GET /api/pipeline/targets`
- `GET /api/pipeline/status`
- `POST /api/pipeline/clear`
- `POST /api/pipeline/estimate-size`
- `GET /api/checkpoints`
- `GET /api/checkpoints/status`
- `GET /api/exporter/status`
- `POST /api/exporter/toggle`
- `GET /api/spool/status`
- `POST /api/spool/max-size`

## Data Markers

Exported records include:

- `_sys_type=docker`
- `collection=compose_service_or_container_name`

## Delivery Semantics

- At-least-once delivery (duplicates are possible)
- Spool overflow policy is configurable (`DropOldest`, `DropNewest`, `RejectWrites`)

## UI

Operator dashboard project:

- [`../com.logdb.docker.collector.ui/README.md`](../com.logdb.docker.collector.ui/README.md)

## Additional Docs

- [`KNOWN_LIMITATIONS.md`](./KNOWN_LIMITATIONS.md)
- [`TESTING.md`](./TESTING.md)
- [`VERIFICATION.md`](./VERIFICATION.md)
- [`RELEASE.md`](./RELEASE.md)
