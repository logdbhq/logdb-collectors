# Testing Guide

## Unit Tests

The test project `com.logdb.nginx.collector.tests` contains 42 tests covering parsing, tailing, checkpointing, spooling, and rotation.

### Run Tests

```bash
dotnet test com.logdb.nginx.collector.tests/
```

### Test Categories

| Category | Tests | What's covered |
|----------|-------|----------------|
| `NginxAccessLogParserTests` | 15 | Combined log format: status codes, request time, IPv6, timezones, edge cases |
| `NginxErrorLogParserTests` | 14 | Error log format: all severity levels, upstream extraction, pid/tid, edge cases |
| `EndToEndTailTests` | 13 | Full pipeline: tail -> parse -> checkpoint -> spool, rotation detection, incremental reads |

### Key Test Scenarios

**Parser tests:**
- Standard HTTP 200/301/404/500 responses
- Request time field (optional)
- IPv6 addresses, long query strings
- Missing user agent, missing bytes
- Malformed, empty, and partial lines return `null`
- All Nginx error severity levels (emerg, alert, crit, error, warn, notice, debug)
- Upstream error extraction

**End-to-end tests:**
- Access and error log reading with correct record types
- Checkpoint offset persistence across tail cycles
- Incremental reads (no reprocessing of old lines)
- `copytruncate` rotation (file shrinks -> offset resets)
- `rename/create` rotation (new creation time -> offset resets)
- Malformed lines increment parse error counters without crashing
- Missing log files handled gracefully
- Spool append and read batch operations
- Checkpoint flush to disk and reload in new instance

## Local Dev Stack

```bash
cd com.logdb.nginx.collector
docker compose -f docker-compose.dev.yml up --build
```

This starts Nginx + collector + UI + traffic generator. Visit `http://localhost:8081` for the dashboard.

## Manual Verification

See [VERIFICATION.md](VERIFICATION.md) for the manual verification checklist.
