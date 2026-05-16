# Verification Checklist

Pre-release verification steps for LogDB Nginx Collector.

## 1. Build Verification

- [ ] `dotnet build com.logdb.nginx.collector/` succeeds with 0 warnings, 0 errors
- [ ] `dotnet build com.logdb.nginx.collector.ui/` succeeds with 0 warnings, 0 errors
- [ ] `dotnet build com.logdb.nginx.collector.tests/` succeeds with 0 warnings, 0 errors
- [ ] `dotnet test com.logdb.nginx.collector.tests/` - all 42 tests pass

## 2. Docker Build

- [ ] Unified image builds successfully:
  ```bash
  docker build -f com.logdb.nginx.collector/Dockerfile \
    --build-arg GITHUB_TOKEN=$GITHUB_TOKEN \
    --build-arg BUILD_DATE=$(date -u +%Y-%m-%dT%H:%M:%SZ) \
    --build-arg COMMIT_HASH=$(git rev-parse --short HEAD) \
    -t logdb/nginx-collector:test .
  ```

## 3. Local Dev Stack

- [ ] `docker compose -f docker-compose.dev.yml up --build` starts all services
- [ ] Nginx generates traffic (check `docker compose logs traffic-gen`)
- [ ] Collector reads logs (check `docker compose logs collector`)
- [ ] Dashboard loads at `http://localhost:8081`

## 4. Health Endpoints

- [ ] `GET /health` returns `200 OK`
- [ ] `GET /health/live` returns `200 OK`
- [ ] `GET /health/ready` returns `200 OK` when healthy
- [ ] `GET /health/ready` returns `503` with error details when degraded

## 5. API Endpoints

- [ ] `GET /api/status` returns version, state, build info, environment, warnings, errors
- [ ] `GET /api/dashboard` returns complete summary with all sections populated
- [ ] `GET /api/targets` shows configured targets with file existence flags
- [ ] `GET /api/pipeline/status` shows access/error record counts incrementing
- [ ] `GET /api/exporter/status` shows `apiKeyConfigured` field (never exposes actual key)

## 6. Operator Dashboard (UI)

- [ ] Dashboard loads and auto-refreshes
- [ ] Collector card shows version, uptime, environment, build hash
- [ ] Targets card shows active file count
- [ ] Pipeline card shows record counts incrementing
- [ ] Exporter card shows "API Key: Configured" or "API Key: Missing" badge
- [ ] Spool card shows utilization bar
- [ ] Alert banners appear for warnings and errors
- [ ] Theme toggle works (light/dark)

## 7. Startup Validation

- [ ] No targets configured -> error state, visible in dashboard
- [ ] All targets disabled -> error state, visible in dashboard
- [ ] Missing log file -> warning, visible in dashboard
- [ ] Exporter enabled without endpoint -> error state
- [ ] Exporter enabled without API key -> error state
- [ ] Exporter disabled -> warning (spool-only mode)
- [ ] Unwritable checkpoint directory -> error state
- [ ] Unwritable spool directory -> error state

## 8. Log Rotation

- [ ] `copytruncate` rotation: truncate access.log, write new data -> collector picks it up
- [ ] `rename/create` rotation: move access.log to access.log.1, create new access.log -> collector picks it up
- [ ] Rotation counter increments in pipeline status

## 9. Security

- [ ] API key never appears in any API response
- [ ] API key never appears in UI
- [ ] `GET /api/exporter/status` shows `apiKeyConfigured: true/false` only
- [ ] Container logs do not contain API key

## 10. Persistence

- [ ] Stop and restart container - checkpoint offsets preserved
- [ ] Stop and restart container - spool records preserved
- [ ] No duplicate records after restart (incremental tailing resumes)
