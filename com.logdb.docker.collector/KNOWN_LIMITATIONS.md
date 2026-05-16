# Known Limitations

## Log driver support

Only the Docker `json-file` log driver is supported. Containers using `journald`, `syslog`, `fluentd`, `gelf`, `awslogs`, etc. appear in the container list but their logs cannot be read. The collector reads Docker's on-disk JSON log files directly from `/var/lib/docker/containers/<id>/<id>-json.log`.

## Delivery semantics

At-least-once delivery. Every log line is delivered at least once under normal operation. Duplicates can occur when:

- A batch is sent successfully but the collector restarts before the spool commit completes
- The collector restarts between a successful export and the next checkpoint flush (default 15 seconds)
- The spool replay worker replays records from a partially committed segment

Each exported record has a unique GUID, so duplicates can be identified but are not deduplicated automatically. For exact-once semantics, deduplicate downstream.

## Log rotation

When Docker rotates a container's log file, the collector detects that the file size is smaller than the saved offset and resets to the beginning of the new file. Log lines written between the last read and the rotation may be lost. The window is bounded by the tail poll interval (2 seconds).

## Spool overflow

When the spool reaches its configured maximum disk usage:

- `DropOldest` (default): the oldest segment is deleted; records in that segment are lost. Prioritizes recent logs over old ones.
- `DropNewest`: incoming records are silently discarded; existing spool data is preserved until replayed.
- `RejectWrites`: identical to `DropNewest` in the current implementation.

The `droppedRecords` counter tracks how many records have been lost to overflow.

## Single-host architecture

The collector operates on a single Docker host. There is no multi-host coordination, distributed checkpoint sync, cross-host dedup, or fleet config management. Each host runs an independent collector with its own checkpoints and spool.

## Operator UI

The UI is a local operator console only:

- Connects to a single collector backend
- No authentication or authorization
- Should not be exposed to the public internet
- Intended for operational monitoring, not log browsing or querying

## Container filtering

- Wildcard patterns support `*` prefix, suffix, and wrap (e.g. `*nginx*`, `redis*`, `*proxy`)
- Full regex is not supported
- Label filtering matches exact key-value pairs or key-with-any-value (`*`)
- Filter changes require a restart (config is read at startup)

## File tailing

- Files are polled every 2 seconds (hardcoded in `FileTailWorker`)
- Each tail cycle processes included containers sequentially
- Very high-throughput containers (>10,000 lines/sec) may experience tail lag
- File reads use `FileShare.ReadWrite | FileShare.Delete` to coexist with Docker's log writer
- Network-mounted log directories may cause slow or blocked reads

## Checkpoint persistence

- Checkpoints are flushed to disk every 15 seconds (configurable)
- On unclean shutdown (kill -9, power loss), up to 15 seconds of checkpoint progress is lost
- After an unclean shutdown, some log lines may be re-read and re-exported (bounded by the flush interval)
- Checkpoint file uses atomic write (temp file + rename)

## Spool segment files

- Segment file names use millisecond-resolution timestamps (`seg-yyyyMMddHHmmssfff.ndjson`)
- Under extreme throughput (>1000 rotations/ms), file name collision is theoretically possible
- Spool scanning at startup reads every line of every segment to count records; large existing spools slow startup

## Network and connectivity

- TLS verification follows .NET defaults
- No proxy configuration support (uses system-level proxy settings if configured)
- `HttpClient` is created once at startup; endpoint changes require restart
- Connection pooling follows .NET `HttpClient` defaults

## Resource usage

- Memory grows linearly with the number of tracked containers (offset map, checkpoint map)
- Spool disk usage is bounded by `MaxDiskBytes`
- No CPU/memory limits enforced by the app itself; use Docker resource constraints
