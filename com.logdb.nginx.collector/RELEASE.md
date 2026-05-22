# Release Process

## Version: RC1

### Prerequisites

- .NET 10 SDK (preview)
- Docker with BuildKit

### Build the Image

```bash
cd LogDB.Exporters

docker build -f com.logdb.nginx.collector/Dockerfile \
  --build-arg BUILD_DATE=$(date -u +%Y-%m-%dT%H:%M:%SZ) \
  --build-arg COMMIT_HASH=$(git rev-parse --short HEAD) \
  -t logdb/nginx-collector:rc1 .
```

### Run Tests

```bash
dotnet test com.logdb.nginx.collector.tests/ --verbosity normal
```

All 42 tests must pass.

### Verification

Complete the checklist in [VERIFICATION.md](VERIFICATION.md).

### Tag and Push

```bash
docker tag logdb/nginx-collector:rc1 logdb/nginx-collector:latest
docker push logdb/nginx-collector:rc1
docker push logdb/nginx-collector:latest
```

### Release Artifacts

| Artifact | Description |
|----------|-------------|
| Docker image `logdb/nginx-collector:rc1` | Unified image (backend + UI) |
| `docker-compose.yml` | Customer install template |
| `README.md` | Customer documentation |

### Post-Release

- [ ] Verify image runs on clean host with only Docker installed
- [ ] Verify health endpoint responds within 15 seconds of startup
- [ ] Verify dashboard loads and shows correct version
- [ ] Test with real LogDB endpoint (staging)
