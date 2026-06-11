# Release Checklist

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
