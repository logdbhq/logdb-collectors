# Release Process

## Version: 1.0.0

First GA release after RC1. Set `VERSION` to the tag you are publishing
(e.g. `1.0.0`) and the commands below pick it up.

### What's new since RC1

- **Sent Records / delivery console** — per-record delivery outcome
  (delivered / failed / skipped) plus an Activity time series.
- **Target auto-discovery** and a Targets page.
- **Auth** — API-key middleware on the backend and a UI login gate.
  New env vars now matter: `LOGDB_API_KEY`, `LOGDB_UI_USERNAME`,
  `LOGDB_UI_PASSWORD` (see the compose template).
- **Spool delivery fixes** — freshly-ingested records now ship in the next
  batch instead of waiting for a 10 MB segment to fill, and replay drains in
  bounded, individually-committed slices so an error storm can't wedge delivery.

### Prerequisites

- .NET 10 SDK (preview)
- Docker with BuildKit

### Run Tests

```bash
dotnet test com.logdb.nginx.collector.tests/ --verbosity normal
```

All 55 tests must pass.

### Build the Image

Run from the repository root:

```bash
VERSION=1.0.0

docker build -f com.logdb.nginx.collector/Dockerfile \
  --build-arg BUILD_DATE=$(date -u +%Y-%m-%dT%H:%M:%SZ) \
  --build-arg COMMIT_HASH=$(git rev-parse --short HEAD) \
  -t logdb/nginx-collector:$VERSION .
```

### Verification

Complete the checklist in [VERIFICATION.md](VERIFICATION.md).

### Tag and Push

```bash
docker tag logdb/nginx-collector:$VERSION logdb/nginx-collector:latest
docker push logdb/nginx-collector:$VERSION
docker push logdb/nginx-collector:latest
```

### Release Artifacts

| Artifact | Description |
|----------|-------------|
| Docker image `logdb/nginx-collector:$VERSION` | Unified image (backend + UI) |
| `docker-compose.nginx.collector.yml` | Customer install template |
| `README.md` | Customer documentation |

### Post-Release

- [ ] Verify image runs on clean host with only Docker installed
- [ ] Verify health endpoint responds within 15 seconds of startup
- [ ] Verify dashboard loads and shows correct version
- [ ] Verify the UI login gate and API-key auth work with the deployed env
- [ ] Test with real LogDB endpoint (staging)
