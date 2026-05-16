# Known Limitations (RC1)

## Log format

- Only Nginx combined log format is supported. Custom `log_format` directives are not parsed. The optional `$request_time` at the end of the line is recognized; other appended fields are not.
- Error log format is standard Nginx only. Third-party modules writing non-standard error formats may produce parse errors.

## File discovery

- Static target config only. Log file paths must be pre-configured. The collector does not auto-discover new log files or glob patterns.
- File-based tailing only. The collector reads log files from the filesystem; it does not read from Docker stdout/stderr, syslog, or journald. For Docker container stdout, use the LogDB Docker Collector.

## Log rotation

- Two rotation styles are detected: `copytruncate` (file shrinks) and `rename/create` (new file creation time). Other mechanisms (e.g. compression-in-place) may cause missed lines or duplicate reads until the next rotation.
- A small gap is possible during rotation. Lines written between the last tail cycle and the rotation may be lost if the old file is removed before the next tail cycle. The gap is at most one tail cycle (default 5 seconds).

## Exporter

- Native LogDB protocol only. No OTLP, Elasticsearch, or other output targets. REST mode is available as an alternative to the native gRPC protocol.
- No authentication beyond API key. mTLS and OAuth are not supported.

## Scalability

- Single-instance only. Running multiple collectors against the same log files will produce duplicate records. Run one collector per set of files.
- In-process spool: a local NDJSON file store, not a distributed queue. If the spool fills, records are dropped (oldest first by default).

## Operator UI

- Local operator UI only. The Blazor dashboard is for local operator use, not a customer-facing production dashboard. No authentication.
- No persistent settings. UI theme preference is stored in browser localStorage only.

## Platform

- Linux containers only. The Docker image is Linux-based; Windows container support is untested.
- .NET 10 preview runtime. Behavior may change with the GA release.
