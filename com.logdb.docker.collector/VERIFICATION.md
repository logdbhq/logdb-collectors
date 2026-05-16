# Manual Verification Checklist

Use this to validate a fresh install.

## Prerequisites

- Docker host with at least one running container
- LogDB instance reachable (for exporter tests)
- Docker socket accessible

## 1. Container Starts

```bash
docker run -d \
  --name logdb-docker-collector \
  -p 8080:8080 \
  -v /var/lib/docker/containers:/var/lib/docker/containers:ro \
  -v /var/run/docker.sock:/var/run/docker.sock:ro \
  -v logdb-collector:/var/lib/logdb-collector \
  -e LOGDB_EXPORTER_ENDPOINT=http://logdb:5080 \
  -e LOGDB_EXPORTER_APIKEY=your-key \
  logdb/docker-collector:0.1
```

- [ ] Container starts without errors: `docker logs logdb-docker-collector`
- [ ] No crash loops: `docker ps` shows status "Up"
- [ ] Startup validation logged: look for "Startup validation" in logs

## 2. Mounts Present

- [ ] Docker socket mounted: `docker exec logdb-docker-collector ls -la /var/run/docker.sock`
- [ ] Container logs mounted: `docker exec logdb-docker-collector ls /var/lib/docker/containers/`
- [ ] State volume mounted: `docker exec logdb-docker-collector ls /var/lib/logdb-collector/`

## 3. Health Endpoints

- [ ] Liveness: `curl -s http://localhost:8080/health/live` returns `{"status":"ok"}`
- [ ] Readiness: `curl -s http://localhost:8080/health/ready` returns `{"ready":true,...}`
- [ ] Docker healthcheck: `docker inspect --format='{{.State.Health.Status}}' logdb-docker-collector` shows "healthy"

## 4. Docker Discovered

- [ ] Docker status: `curl -s http://localhost:8080/api/docker/status | jq .available` returns `true`
- [ ] Containers listed: `curl -s http://localhost:8080/api/containers | jq length` returns > 0
- [ ] Self-excluded (if configured): collector container excluded or self-collection accepted

## 5. Logs Being Tailed

- [ ] Tail targets present: `curl -s http://localhost:8080/api/pipeline/targets | jq length` returns > 0
- [ ] Records read: `curl -s http://localhost:8080/api/pipeline/status | jq .recordsRead` increasing
- [ ] Parse errors zero: `curl -s http://localhost:8080/api/pipeline/status | jq .parseErrors` returns 0

## 6. Exporter Working

- [ ] Exporter enabled: `curl -s http://localhost:8080/api/exporter/status | jq .enabled` returns `true`
- [ ] Exporter healthy: `curl -s http://localhost:8080/api/exporter/status | jq .healthy` returns `true`
- [ ] Records sent: `curl -s http://localhost:8080/api/exporter/status | jq .recordsSent` increasing
- [ ] Send errors zero: `curl -s http://localhost:8080/api/exporter/status | jq .sendErrors` returns 0
- [ ] No API key in response: `curl -s http://localhost:8080/api/exporter/status` contains no apiKey field

## 7. Spool Working

- [ ] Spool enabled: `curl -s http://localhost:8080/api/spool/status | jq .enabled` returns `true`
- [ ] Records replaying: `curl -s http://localhost:8080/api/spool/status | jq .replayedRecords` increasing
- [ ] No drops: `curl -s http://localhost:8080/api/spool/status | jq .droppedRecords` returns 0
- [ ] Utilization reasonable: `curl -s http://localhost:8080/api/spool/status | jq .utilizationPercent` < 80

## 8. Checkpoints Working

- [ ] Checkpoints enabled: `curl -s http://localhost:8080/api/checkpoints/status | jq .enabled` returns `true`
- [ ] Flush happening: `curl -s http://localhost:8080/api/checkpoints/status | jq .lastFlushUtc` is recent
- [ ] File exists: `docker exec logdb-docker-collector cat /var/lib/logdb-collector/checkpoints.json | jq length` > 0

## 9. UI Reachable

If running the UI container:

```bash
docker run -d \
  --name logdb-docker-collector-ui \
  -p 5010:8080 \
  -e COLLECTOR_API_URL=http://logdb-docker-collector:8080 \
  logdb/docker-collector-ui:0.1
```

- [ ] UI loads: open http://localhost:5010 in browser
- [ ] Overview shows all 5 cards (Collector, Docker, Pipeline, Exporter, Spool)
- [ ] No error banners (on healthy install)
- [ ] Auto-refresh updates counters every 5 seconds
- [ ] All sidebar links work: Containers, Pipeline, Checkpoints, Spool, Exporter, Diagnostics

## 10. Restart Resilience

- [ ] `docker restart logdb-docker-collector`
- [ ] Container comes back healthy
- [ ] Checkpoints restored (offsets match pre-restart)
- [ ] Spool records from previous run replayed
- [ ] No duplicate logs visible (modulo at-least-once gap)
