# Reliability Test Matrix

## A. Collector lifecycle

### A1. Clean start
- **Setup**: No prior state (no checkpoints.json, no spool directory)
- **Action**: Start collector container
- **Expected**: Collector starts, creates spool directory, discovers containers, begins tailing
- **Verify**: `GET /health/ready` returns 200. `GET /api/status` shows `agentState: "running"`. Logs show startup validation, discovery worker, tail worker started.

### A2. Restart during active logging
- **Setup**: Collector running, containers producing logs, spool has queued records
- **Action**: `docker restart logdb-docker-collector`
- **Expected**: Collector restarts, loads checkpoints, resumes from saved offsets, replays unsent spool records
- **Verify**: `GET /api/checkpoints` shows restored offsets. `GET /api/spool/status` shows queuedRecords from previous run. No duplicate reads in pipeline counters (modulo the checkpoint flush interval gap).

### A3. Shutdown with non-empty spool
- **Setup**: Exporter disabled or endpoint unreachable, logs accumulating in spool
- **Action**: `docker stop logdb-docker-collector`
- **Expected**: Final checkpoint flush attempted. Final spool replay attempted. Spool files persist on volume.
- **Verify**: Logs show "Final checkpoint flush completed". Spool directory still contains segment files on the volume. After restart, `GET /api/spool/status` shows restored queuedRecords.

## B. Docker / runtime behavior

### B1. Docker daemon unavailable at startup
- **Setup**: Stop Docker daemon before starting collector, or don't mount the socket
- **Action**: Start collector
- **Expected**: Startup validation reports Docker socket missing (error). Discovery worker logs error, retries every interval. Collector does not crash.
- **Verify**: `GET /health/ready` returns 503 with error. `GET /api/status` shows `agentState: "degraded"`. `GET /api/docker/status` shows `available: false`.

### B2. Docker daemon becomes unavailable after startup
- **Setup**: Collector running, Docker connected
- **Action**: Stop Docker daemon (e.g., `systemctl stop docker`)
- **Expected**: Discovery worker logs warning on next refresh. Docker status changes to unavailable. Existing spool records continue to replay. Tailing stops (no targets).
- **Verify**: `GET /api/docker/status` shows `available: false` with error. UI Overview shows "Docker is unavailable" banner. Pipeline targets list is empty.

### B3. Container starts after collector starts
- **Setup**: Collector running with 0 containers
- **Action**: `docker run -d nginx`
- **Expected**: Next discovery cycle (up to RefreshIntervalSeconds) picks up new container. Tailing starts from offset 0.
- **Verify**: `GET /api/containers` includes new container with `isIncluded: true`. `GET /api/pipeline/targets` shows new tail target.

### B4. Container restarts
- **Setup**: Collector tailing container A
- **Action**: `docker restart containerA`
- **Expected**: Container gets new log file path (Docker assigns new ID). Collector discovers new path, starts tailing from offset 0. Old checkpoint for previous path becomes stale.
- **Verify**: `GET /api/pipeline/targets` shows updated log path. Records continue flowing.

### B5. Container removed and recreated
- **Setup**: Collector tailing container A
- **Action**: `docker rm -f containerA && docker run -d --name containerA nginx`
- **Expected**: Same as B4 - old path disappears, new path discovered. Old checkpoint orphaned.
- **Verify**: `GET /api/containers` shows new container. Pipeline status shows new target.

### B6. Log file missing
- **Setup**: Collector tailing container A
- **Action**: Delete the container's log file manually
- **Expected**: Tail service logs "Log file not found" at Debug level. Skips this target for current cycle. Retries next cycle.
- **Verify**: No crash. ReadErrors counter does NOT increment (file-not-found is handled gracefully, not counted as error). File reappears on next Docker write.

### B7. Log rotation
- **Setup**: Collector tailing container A at offset 50000
- **Action**: Docker rotates the json-file log (new file size < saved offset)
- **Expected**: Tail service detects rotation (file size < offset), resets offset to 0, logs rotation event.
- **Verify**: Logs show "Log file rotated". Pipeline offset for this file resets. No data loss from new file; some data from end of old file may be lost.

### B8. Malformed Docker json log line
- **Setup**: Collector tailing active container
- **Action**: Corrupt a log line in the Docker json file (e.g., append invalid JSON)
- **Expected**: ParseDockerJsonLog catches JsonException, increments parseErrors, logs at Debug level, continues processing remaining lines.
- **Verify**: `GET /api/pipeline/status` shows parseErrors > 0. Subsequent valid lines are still processed.

## C. Export behavior

### C1. Exporter disabled
- **Setup**: `LOGDB_EXPORTER_ENABLED=false` or default appsettings
- **Action**: Start collector, containers produce logs
- **Expected**: Logs flow through pipeline into spool. Spool replay worker calls SendBatchAsync which returns false (disabled). Records accumulate in spool indefinitely.
- **Verify**: `GET /api/exporter/status` shows `enabled: false`. `GET /api/spool/status` shows growing queuedRecords. Startup validation shows warning about disabled exporter.

### C2. Exporter endpoint unreachable
- **Setup**: Exporter enabled, endpoint points to non-existent host
- **Action**: Logs flow into spool, replay worker attempts delivery
- **Expected**: SendBatchAsync retries up to MaxRetries with exponential backoff, then fails. Records remain in spool. Exporter marked unhealthy.
- **Verify**: `GET /api/exporter/status` shows `healthy: false`, `sendErrors > 0`, `lastError` populated. Spool queuedRecords remain. Next replay cycle retries.

### C3. Exporter endpoint slow
- **Setup**: Exporter enabled, LogDB endpoint responds after 20+ seconds
- **Action**: Replay worker sends batch
- **Expected**: Request times out after RequestTimeoutSeconds (default 30s). Treated as send failure, retried.
- **Verify**: `GET /api/exporter/status` shows retryCount increasing. Eventual timeout error in lastError.

### C4. Exporter API key invalid
- **Setup**: Exporter enabled, wrong API key
- **Action**: Replay worker sends batch
- **Expected**: LogDB returns 401/403. Exporter retries (treats as HTTP error), eventually fails. Exporter marked unhealthy.
- **Verify**: `GET /api/exporter/status` shows `lastError` containing "HTTP 401" or "HTTP 403". sendErrors increments.

### C5. Temporary 5xx response
- **Setup**: LogDB temporarily returns 500/503
- **Action**: Replay worker sends batch
- **Expected**: Exporter retries with exponential backoff. If LogDB recovers within MaxRetries, batch succeeds. If not, batch fails, records stay in spool for next cycle.
- **Verify**: retryCount increments. If recovered, batchesSent increments and spool queuedRecords decreases.

### C6. Duplicate delivery
- **Setup**: Exporter sends batch successfully, but collector restarts before spool commit
- **Action**: Restart collector immediately after a successful batch send
- **Expected**: On restart, spool replays the same records again (they were never committed). LogDB receives duplicates.
- **Verify**: This is expected at-least-once behavior. LogDB shows duplicate entries with different GUIDs.

## D. Storage behavior

### D1. Checkpoints restored after restart
- **Setup**: Collector running, checkpoints.json has been flushed with offsets for 3 containers
- **Action**: Stop and restart collector
- **Expected**: FileCheckpointStore.Load() reads checkpoints.json. When tail encounters known file paths, it uses restored offsets instead of 0.
- **Verify**: Logs show "Loaded N checkpoints from path". `GET /api/checkpoints` shows entries matching pre-restart state. Pipeline does not re-read already-processed data.

### D2. Spool replay after restart
- **Setup**: Spool contains segment files from previous run
- **Action**: Restart collector
- **Expected**: FileSpoolStore.Initialize() scans spool directory, counts existing segments and records. Replay worker begins sending from oldest segment.
- **Verify**: Logs show "Spool initialized at path with N segments, M records". `GET /api/spool/status` shows queuedRecords > 0. Records delivered to LogDB.

### D3. Spool directory unwritable
- **Setup**: Set spool directory to read-only path
- **Action**: Start collector
- **Expected**: Startup validation catches the error. Spool Initialize() logs warning. Pipeline still runs but Append() will fail with logged errors.
- **Verify**: `GET /health/ready` returns 503 with spool directory error. `GET /api/status` shows error in errors list.

### D4. Checkpoint directory unwritable
- **Setup**: Set checkpoint file path to read-only directory
- **Action**: Start collector
- **Expected**: Startup validation catches the error. Checkpoint flush will fail with logged warnings. Offsets still tracked in memory but not persisted.
- **Verify**: `GET /health/ready` returns 503 with checkpoint directory error.

### D5. Spool full - DropOldest (default)
- **Setup**: Set `LOGDB_SPOOL_MAX_DISK_MB=1`. Exporter disabled. Produce lots of logs.
- **Action**: Logs fill spool past MaxDiskBytes
- **Expected**: Oldest segment deleted to make room. droppedRecords increments. New records continue to be accepted.
- **Verify**: `GET /api/spool/status` shows droppedRecords > 0. Logs show "Dropped oldest spool segment". queuedRecords stabilizes around capacity.

### D6. Spool full - DropNewest
- **Setup**: Set `Spool:WhenFull=DropNewest`. Small MaxDiskBytes. Exporter disabled.
- **Action**: Logs fill spool past MaxDiskBytes
- **Expected**: New incoming records are silently dropped. Existing spool data preserved. droppedRecords increments.
- **Verify**: `GET /api/spool/status` shows droppedRecords > 0. queuedRecords stays at max. Existing segments untouched.

### D7. Spool full - RejectWrites
- **Setup**: Set `Spool:WhenFull=RejectWrites`. Small MaxDiskBytes. Exporter disabled.
- **Action**: Logs fill spool past MaxDiskBytes
- **Expected**: Same as DropNewest - incoming records rejected, droppedRecords increments.
- **Verify**: Same as D6. (RejectWrites and DropNewest behave identically in current implementation.)

## E. Health and UI

### E1. /health/live returns 200
- **Setup**: Collector running in any state
- **Action**: `curl http://localhost:8080/health/live`
- **Expected**: Always returns `{"status":"ok"}` with HTTP 200
- **Verify**: Response body and status code

### E2. /health/ready returns 200 when healthy
- **Setup**: All startup validation passes
- **Action**: `curl http://localhost:8080/health/ready`
- **Expected**: Returns `{"ready":true,"warnings":[...],"errors":[]}` with HTTP 200
- **Verify**: Response body, status code, warnings may be present but errors empty

### E3. /health/ready returns 503 when degraded
- **Setup**: Docker socket not mounted, or spool directory unwritable
- **Action**: `curl http://localhost:8080/health/ready`
- **Expected**: Returns `{"ready":false,"warnings":[...],"errors":["..."]}` with HTTP 503
- **Verify**: Response body, status code 503, errors list non-empty

### E4. UI shows Docker unavailable
- **Setup**: Docker socket not mounted
- **Action**: Open Overview page in UI
- **Expected**: Red banner "Docker is unavailable - container discovery is not working". Docker card shows "Unavailable" badge.
- **Verify**: Visual inspection of Overview page

### E5. UI shows exporter unhealthy
- **Setup**: Exporter enabled but endpoint unreachable
- **Action**: Open Overview page after failed send attempts
- **Expected**: Red banner "Exporter is unhealthy - logs are not being delivered". Exporter card shows "Unhealthy" badge.
- **Verify**: Visual inspection of Overview page

### E6. UI shows spool pressure
- **Setup**: Spool utilization > 80%
- **Action**: Open Overview page
- **Expected**: Yellow banner showing spool utilization percentage. Spool card shows yellow badge. Utilization bar is yellow/red.
- **Verify**: Visual inspection of Overview and Spool pages

### E7. UI shows dropped records
- **Setup**: Spool has dropped records (droppedRecords > 0)
- **Action**: Open Overview page
- **Expected**: Yellow banner "N records have been dropped due to spool overflow". Spool card shows "Dropping" error badge. Dropped count in red.
- **Verify**: Visual inspection of Overview and Spool pages

### E8. No secrets exposed
- **Setup**: Exporter configured with API key
- **Action**: Check all API endpoints and UI pages
- **Expected**: API key never appears in any response or UI display. `GET /api/exporter/status` has no apiKey field. `GET /api/status` has no apiKey field.
- **Verify**: `curl` all endpoints, grep for API key value. Visual inspection of all UI pages.

### E9. Auto-refresh working
- **Setup**: Collector running, containers producing logs
- **Action**: Open Overview page, wait 15 seconds
- **Expected**: Metrics (records read, records sent, uptime) update every 5 seconds without manual page refresh.
- **Verify**: Watch pipeline recordsRead counter increase automatically
