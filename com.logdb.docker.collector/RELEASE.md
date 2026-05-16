# Release Checklist (RC1)

## Version and metadata

- [ ] Version set in `AgentStatus.cs` and `BuildInfo.cs` (currently `0.1`)
- [ ] Docker image tagged: `logdb/docker-collector:0.1`
- [ ] Build date passed via `--build-arg BUILD_DATE=$(date -u +%Y-%m-%d)`
- [ ] Commit hash passed via `--build-arg COMMIT_HASH=$(git rev-parse --short HEAD)`
- [ ] `GET /api/status` returns correct version, buildDate, commitHash

## Docker image

- [ ] Dockerfile builds successfully: `docker build -t logdb/docker-collector:0.1 .`
- [ ] Image runs: `docker run --rm logdb/docker-collector:0.1 --help` exits cleanly
- [ ] Health check works: container reaches "healthy" state within 30 seconds
- [ ] Image size reasonable (< 200 MB)
- [ ] No build artifacts or source code in image

## Compose example

- [ ] `docker compose -f docker-compose.yaml config` validates without errors
- [ ] `docker compose up -d` starts both collector and UI
- [ ] Collector connects to Docker via socket mount
- [ ] UI connects to collector API
- [ ] Volume persists across `docker compose down && docker compose up -d`

## Documentation

- [ ] README.md reviewed: install instructions, env vars, mounts
- [ ] TESTING.md reviewed: test matrix covers lifecycle, Docker, export, storage, health
- [ ] VERIFICATION.md reviewed: manual checklist covers fresh install
- [ ] KNOWN_LIMITATIONS.md reviewed: limitations are accurate and current
- [ ] No secrets in any documentation or sample config
- [ ] docker-compose.yaml uses placeholder API key (`your-api-key-here`)
- [ ] appsettings.json has exporter disabled by default
- [ ] appsettings.Docker.json has exporter enabled

## Security

- [ ] API key never appears in any status endpoint response
- [ ] API key never appears in UI
- [ ] API key never logged (grep logs for the actual key value)
- [ ] appsettings.json sample does not contain real secrets
- [ ] No hardcoded credentials in source code

## Functional smoke tests

- [ ] Start collector with Docker socket mounted - containers discovered
- [ ] Start a test container, verify it appears in /api/containers
- [ ] Verify logs are tailed: /api/pipeline/status recordsRead > 0
- [ ] Verify checkpoints flush: /api/checkpoints/status lastFlushUtc populated
- [ ] Verify spool receives records: /api/spool/status queuedRecords > 0
- [ ] Enable exporter, verify delivery: /api/exporter/status recordsSent > 0
- [ ] Stop collector, restart, verify checkpoint and spool recovery
- [ ] Disconnect Docker, verify degraded state and recovery

## Known limitations acknowledged

- [ ] Only Docker json-file log driver supported
- [ ] At-least-once delivery (duplicates possible)
- [ ] Single-host only (no multi-host coordination)
- [ ] No log content parsing beyond Docker JSON wrapper
- [ ] Spool segment file name has millisecond resolution (theoretical collision under extreme throughput)

## Release artifacts

- [ ] Docker image pushed to registry
- [ ] UI Docker image pushed to registry
- [ ] Release notes drafted
- [ ] Version tag created in git
