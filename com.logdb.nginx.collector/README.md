# LogDB Nginx Collector

`com.logdb.nginx.collector` tails Nginx access/error log files and exports parsed records to LogDB.

## Runtime Packaging

Primary deployment is a unified container image that runs:

- Backend API on `8080`
- Operator UI on `8081`

via `entrypoint.sh`.

## Quick Start (Compose)

```bash
docker compose -f docker-compose.yml up -d
```

Required mounts in compose:

- `/var/log/nginx:/var/log/nginx:ro`
- `collector-data:/var/lib/logdb-nginx-collector`

## What It Does

- Tails configured Nginx access and error files
- Parses combined access format and standard error format
- Persists checkpoints for resume-after-restart behavior
- Buffers data in on-disk spool
- Exposes status/dashboard API and UI

## Default Targets

- `/var/log/nginx/access.log`
- `/var/log/nginx/error.log`

Configured under `NginxTargets:Targets` in `appsettings*.json`.

## State Paths

- Checkpoints: `/var/lib/logdb-nginx-collector/checkpoints.json`
- Spool directory: `/var/lib/logdb-nginx-collector/spool`

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
| `LOGDB_CHECKPOINT_FLUSH_INTERVAL` | `Checkpoint:FlushIntervalSeconds` |
| `LOGDB_SPOOL_MAX_DISK_MB` | `Spool:MaxDiskBytes` (MB to bytes) |
| `LOGDB_BUILD_DATE` | Build metadata only |
| `LOGDB_COMMIT_HASH` | Build metadata only |

## API Endpoints

- `GET /health`
- `GET /health/live`
- `GET /health/ready`
- `GET /api/status`
- `GET /api/targets`
- `GET /api/pipeline/targets`
- `GET /api/pipeline/status`
- `GET /api/checkpoints`
- `GET /api/checkpoints/status`
- `GET /api/exporter/status`
- `GET /api/spool/status`
- `POST /api/spool/max-size`
- `GET /api/dashboard`

## Data Markers

Exported records include:

- `_sys_type=nginx`
- `collection=target_name`

## UI Options

- Unified image UI is built-in (port `8081`)
- Separate UI project is also available:
  - [`../com.logdb.nginx.collector.ui/README.md`](../com.logdb.nginx.collector.ui/README.md)

## Additional Docs

- [`KNOWN_LIMITATIONS.md`](./KNOWN_LIMITATIONS.md)
- [`TESTING.md`](./TESTING.md)
- [`VERIFICATION.md`](./VERIFICATION.md)
- [`RELEASE.md`](./RELEASE.md)
