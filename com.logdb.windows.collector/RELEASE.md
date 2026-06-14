# Release Checklist

## Version: 1.4.16

### What's new since 1.4.15

- **Silence the "ApiKey was missing on log in batch, set from options" warnings.**
  Bumps LogDB.Client 5.1.5 → 5.1.6. Published 5.1.5 predated the SDK change that
  stops warning on the (intended) ApiKey-from-options fallback, so every batch
  send logged a benign Warning. 5.1.6 drops it. (ApiKey was always set correctly;
  this was log noise only.)

## Version: 1.4.15

### What's new since 1.4.14

- **SENT / NOT SENT colour chip in the Online Console.** Console rows that carry
  a "&lt;N&gt; sent to server" result (IIS per-file scans) now show a coloured chip
  in the Message column: green **SENT** when rows reached the server, grey
  **NOT SENT** when a scan shipped nothing (reset / future start date). Lines
  without a send result show no chip. Makes "is this actually going to the
  server?" answerable at a glance instead of by reading each message.

## Version: 1.4.14

### What's new since 1.4.13

- **IIS per-file lines now state "sent to server" explicitly.** Rather than
  hiding scans that shipped nothing, each per-file line says how many rows
  actually went to the server: `read N, M sent to server` for a real send, and
  `read N, 0 sent to server (filtered — not shipped)` for a reset / future-
  start-date scan. So a scan stays visible but can't be mistaken for delivery.
  ("New file detected" stays at Debug — pure scan mechanics.)

## Version: 1.4.13

### What's new since 1.4.12

- **Quiet "New file detected" console flood on reset.** A reset makes IIS walk
  every historical log file, and the per-file "New file detected" line was at
  Information and flooded the Online Console; it now logs at Debug.

## Version: 1.4.12

### What's new since 1.4.11

- **Windows Events now honors a start date set on an already-running source.**
  The EventViewer applied `InitialStartDate` only on first run (no saved state),
  so changing the date on a collector that already had a watermark was ignored —
  a future "start tomorrow" never held off and today's events kept shipping.
  The date is now a floor on the read watermark (`max(watermark, startDate)`): a
  future date jumps the watermark forward (holds off until then); a past date is
  a no-op once the watermark has passed it (use ResetState to backfill earlier).
  This matches the IIS behaviour. Audited Metrics/Heartbeat — real-time polls,
  no start-date concept by design.

## Version: 1.4.11

### What's new since 1.4.10

- **Fix IIS/EventLog "start date" being silently discarded.** Picking a date in
  the Data Sources calendar did not turn off the "resume from last position"
  toggle, and the save path (`ResumeFromLast ? null : InitialStartDate`) then
  nulled the date — so the module kept sending from where it left off and the
  chosen start date was ignored. Selecting a date now clears "resume from last"
  for both IIS and Windows Events, so the date actually persists to config.

## Version: 1.4.10

### What's new since 1.4.9

- **"Clear Stats" button** on the Throughput tab. Zeroes the Sent/Failed totals
  and the Throughput history via a new `reset-send-activity` control command
  (`SendActivityTracker.Reset()` clears the in-memory buckets and deletes
  `send-activity.json`). Resets both the Throughput charts and the Modules grid
  Sent/Failed columns (shared tracker); sending is unaffected and counts restart
  from zero on the next batch.

## Version: 1.4.9

### What's new since 1.4.8

- **Server rejection reasons in the Errors console.** Bumps LogDB.Client to
  5.1.5, which logs `Server rejected record: <reason>` whenever the server
  declines a send instead of silently collapsing it to a bare Failed. Combined
  with the ingress fix that returns real failure reasons, the Online Console's
  Errors tab now shows WHY a send failed, not just that it did.

## Version: 1.4.8

### What's new since 1.4.7

- **Errors detail console.** New "Errors (N)" sub-tab in the Online Console:
  Warning/Error/Critical entries only (ignores the Console's source filter so an
  error can never be hidden), live count in the tab header, and a full-detail
  pane showing the complete multi-line message — including exception stack
  traces — with a Copy Detail button. Previously the grid crushed messages to
  one line and the real error (e.g. the RpcException behind "Retry 3 after
  4000ms") was only findable by grepping the log files on disk.
- **Stop logging "<module> host stopped unexpectedly" on every service stop.**
  `ExporterModuleBase` raced `WaitForShutdownAsync(stoppingToken)` against
  `Task.Delay(..., stoppingToken)` — the same token cancels both, and whenever
  the wait task happened to win the `WhenAny`, a perfectly normal shutdown or
  update restart was recorded as a per-module failure (visible in the Recent
  Failures panel, typically all modules "failing" in the same second). A module
  host that stops while the service is NOT shutting down is still reported.

## Version: 1.4.7

### What's new since 1.4.6

- **Stop the update-popup / 403 rate-limit loop.** The UI checked for updates
  twice per launch (App popup path + a redundant VM auto-apply path) with no
  memory between launches, burning GitHub's anonymous 60 req/hour API quota;
  once rate-limited, "Install now" silently failed (the elevated instance
  swallowed the 403) and the popup returned forever. Now: a single, throttled
  auto-check (persisted stamp, 6h interval, 2h back-off after a rate-limit), the
  update package downloads in the user instance *before* the prompt, and the
  popup path honours `LOGDB_COLLECTOR_UI_UPDATE_TOKEN` (PAT → 5000 req/h).
- **Fix Destination "Resolved Endpoint" showing "Discovery unreachable" with a
  healthy instance selected.** The local-API-key guard short-circuited before
  the ask-the-running-instance path; asking a running service/console for its
  locked endpoint needs no key, so that path now runs first.

## Version: 1.4.6

The Windows collector ships as a **bundle** (service host + Avalonia admin UI +
install scripts), produced by `scripts/publish-windows-collector.ps1`. The
version is stamped from the csproj `<Version>` (CI overrides via
`-p:Version=<tag>`). Keep the service and UI csproj versions in lockstep.

### What's new since 1.4.5

- **Fix Windows Events never arriving (frozen watermark).** The EventViewer sent
  each event **inline while enumerating the thread-affine `EventLogReader`**; when
  the `await` resumed on another thread the in-flight gRPC request was torn down →
  `RpcException(Cancelled, "Call canceled by the client")`, which froze the
  watermark on the first event forever (metrics/IIS/heartbeat were unaffected
  because they send one small record per cycle). Reads now **buffer events first,
  then send after the reader is fully drained** — no gRPC await happens mid-read.
- **Dead-letter guard.** If a single event fails to send for 5 cycles in a row it
  is skipped (watermark advanced past it) so one genuinely-poison event can never
  wedge the whole channel again.

### What's new since 1.4.4

- **Fix the Destination "Refresh Discovery" target selector.** Rebuilding the
  instance list nulled the freshly auto-picked target through the ComboBox
  binding, leaving it unselected and forcing the page onto its own discovery
  lookup (which can disagree with the running collector's key-based endpoint).
  Refresh now auto-selects a reachable instance (service if installed, else
  console) and re-syncs it, in both console and installed-service run modes.
- **Modules grid "Sent"/"Failed" columns** now report real records shipped vs
  records that failed to ship (from the send-activity tracker), instead of a
  start-cycle counter / module-error count.
- **Stable Throughput chart colours.** Each series keeps a fixed colour across
  auto-refreshes (deterministic palette keyed by group name) instead of
  reshuffling on every reload.

### What's new since 1.4.3

- **Fix a startup/shutdown crash** (`SingleInstanceLock`). The single-instance
  mutex was released from a different thread than acquired it (async `Main`),
  throwing `ApplicationException` → unhandled → the process died and Windows
  logged an Application Error / .NET Runtime event each time (a flood under
  service auto-restart). Release is now safe, and a mutex abandoned by a prior
  crash is treated as acquired.
- **Stop logging expected cancellations as errors.** During config reload /
  shutdown, in-flight reads throw `TaskCanceledException`; the IIS reader and the
  EventViewer export cycle now treat that as expected instead of `LogError`.
- **Keep SDK chatter out of the Windows Event Log.** `LogDB.*` SDK categories
  (e.g. Polly "Retry N after Xms") no longer route to the Event Log; they remain
  in the collector's own file/UI sink.

### What's new since 1.4.2

- **Throughput charts** in the **Online Console** tab (new "Throughput"
  sub-tab). Time-series of records shipped to the server over a selected date
  range, grouped by log type / host / collection, split sent vs failed, with
  hour/day granularity and a 10-second auto-refresh.
- **Send-activity capture.** A `RecordingLogDbClient` wraps every module's log
  client and records per-batch sent/failed counts into a persisted
  `SendActivityTracker` (`send-activity.json`, hourly buckets, 90-day
  retention), exposed over the control channel via `send-activity`.
- **Modules grid "Sent" column now shows real records shipped** (from the
  send-activity totals) instead of a start-cycle counter that was always 1.

### What's new since 1.4.1

- **Fix runaway event-log feedback loop.** The EventLog module logged one
  Information line per harvested event into the Windows **Application** log, and
  also harvested that channel — so every shipped event spawned a new one,
  exponentially (observed: 21M+ rows / 12 GB in ~3 weeks on one host). Three
  independent guards now break it:
  - The Windows Event Log sink is limited to **Warning+** (`Program.cs`), so
    routine per-event Info no longer pollutes the Application channel.
  - The EventLog collector **never harvests its own event source**
    (`SelfExcludeProviders`, wired from the service name), which also stops
    re-ingesting the historical backlog already in the Application log.
  - The Firewall module's "no blocklists configured" state is now a benign
    **idle** (not an error), and module status is logged **only on change** —
    previously it wrote a Warning every poll cycle (tens of thousands of
    identical entries).

### What's new since 1.4.0

- **Critical Issues drill-down** — the Overview "Critical Issues" card is now
  clickable. Clicking it filters the Modules grid to modules with failures and
  opens a **Recent Failures** panel (Time / Module / Error), so you can see the
  individual failures behind the counter instead of just the per-module
  "Last Error". A "Show only modules with failures" checkbox toggles the same
  filter manually.
- **Persistent failure history** — the collector now keeps a bounded ring buffer
  of the last 250 failures (recorded in `CollectorStatusRegistry.MarkError`),
  exposed over the control channel via the new `failures` command. The buffer is
  written atomically to `%ProgramData%\LogDB\collector\failures.json` on every
  failure and restored on startup, so the history survives service restarts.

## Version and metadata

- [ ] `com.logdb.windows.collector.csproj` `<Version>` bumped
- [ ] `com.logdb.windows.collector.ui.csproj` `<Version>` matches the service
- [ ] `status` control command reports the matching `ProcessId` / version

## Run tests

```powershell
dotnet test com.logdb.windows.collector.tests\com.logdb.windows.collector.tests.csproj --verbosity normal
```

All tests must pass (shared-contract round-trips incl. `CollectorFailureDto`).

## Build the bundle

Run from the repository root.

```powershell
$VERSION = "1.4.1"
.\scripts\publish-windows-collector.ps1 -Version $VERSION
```

- [ ] Service and UI publish succeed (self-contained `win-x64`)
- [ ] Bundle zip created under `releases\windows-collector\`
- [ ] Assemblies stamped with the expected `FileVersion`

## Functional smoke tests

- [ ] Service installs and starts: `.\install-collector.bat`
- [ ] UI launches and discovers the local service instance (Named Pipes)
- [ ] Modules report Sent / Failed counts on the Overview
- [ ] **Critical Issues** card click filters the Modules grid and opens the
      Recent Failures panel
- [ ] Force a failure (e.g. point at an unreachable endpoint), confirm a row
      appears in Recent Failures with timestamp, module, and error text
- [ ] **Failure persistence**: restart the service, reopen the UI, confirm the
      Recent Failures history is still present
- [ ] `%ProgramData%\LogDB\collector\failures.json` exists and is valid JSON

## Security

- [ ] API key never appears in `status`/`get-config` responses, the UI, or logs
- [ ] Control plane remains local-only (named pipes, no HTTP admin endpoint)
- [ ] `failures.json` contains only error text already surfaced in the UI — no
      secrets in recorded error messages

## Known limitations

- [ ] Failure history is capped at the last 250 entries (drop-oldest); the
      "Critical Issues" counter can exceed the number of retained detail rows
- [ ] History starts accumulating from first run of this build — it does not
      backfill failures from earlier versions
- [ ] Single-host only (no multi-host coordination)

## Release artifacts

- [ ] Bundle zip published
- [ ] Velopack UI package built (`scripts\package-collector-ui-velopack.ps1`) if shipping auto-update
- [ ] Release notes drafted
- [ ] Version tag created in git
