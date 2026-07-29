using System.Diagnostics;
using LogDB.Client.Models;
using LogDB.Extensions.Logging;
using Microsoft.Extensions.Logging;
using com.logdb.windows.eventviewer.Models;

namespace com.logdb.windows.eventviewer.Services;

public class EventViewerExportService : BackgroundService
{
    private readonly ILogger<EventViewerExportService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ILogDBClient _logDBClient;
    private readonly EventLogReader _eventLogReader;
    private readonly EventLogFilter _eventLogFilter;
    private readonly EventStateTracker _stateTracker;
    private readonly EventViewerExportConfig _config;

    // When batching is on, events are accumulated and shipped via
    // SendLogBatchAsync in groups of _batchSize instead of one LogAsync per
    // event. The source state checkpoint is advanced only after each sub-batch
    // confirms, so a send failure re-reads from the last confirmed event next
    // cycle (bounded re-delivery; see ExportLogSourceStreamingAsync).
    private readonly bool _enableBatching;
    private readonly int _batchSize;

    // Command-line flags (set from Program.cs)
    public static DateTime? InitialStartDate { get; set; }
    public static bool ResetState { get; set; }

    private bool _stateResetDone;

    // Dead-letter guard: per-source tracking of the event the watermark is frozen
    // on. If the SAME event fails MaxFreezeAttemptsBeforeSkip cycles in a row it is
    // skipped (watermark advanced past it) so one genuinely-poison event can never
    // wedge the whole channel forever.
    private const int MaxFreezeAttemptsBeforeSkip = 5;
    private readonly Dictionary<string, (string Key, int FailCount)> _stuckHead =
        new(StringComparer.OrdinalIgnoreCase);

    public EventViewerExportService(
        ILogger<EventViewerExportService> logger,
        IConfiguration configuration,
        ILogDBClient logDBClient,
        EventLogReader eventLogReader,
        EventLogFilter eventLogFilter,
        EventStateTracker stateTracker)
    {
        _logger = logger;
        _configuration = configuration;
        _logDBClient = logDBClient;
        _eventLogReader = eventLogReader;
        _eventLogFilter = eventLogFilter;
        _stateTracker = stateTracker;

        _config = new EventViewerExportConfig();
        configuration.GetSection("EventViewer").Bind(_config);

        _enableBatching = configuration.GetValue("Batch:EnableBatching", false);
        _batchSize = Math.Max(1, configuration.GetValue("Batch:Size", 100));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Event Viewer Export Service starting...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Handle --reset flag (once)
                if (ResetState && !_stateResetDone)
                {
                    _logger.LogInformation("Resetting all EventViewer exporter state");
                    _stateTracker.ClearAll();
                    _stateResetDone = true;
                }

                // Determine which log sources to process
                var logSources = GetEffectiveLogSources();

                if (logSources.Count == 0)
                {
                    _logger.LogWarning("No log sources configured. Check EventViewer:LogSources in appsettings.json");
                    await Task.Delay(TimeSpan.FromMinutes(_config.ExportIntervalMinutes), stoppingToken);
                    continue;
                }

                _logger.LogInformation("Processing {Count} log source(s): [{Sources}]",
                    logSources.Count, string.Join(", ", logSources));

                var totalExported = 0;

                foreach (var logSource in logSources)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    try
                    {
                        var exported = await ExportLogSourceStreamingAsync(logSource, stoppingToken);
                        totalExported += exported;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break; // shutting down / config reload — not an error
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error reading events from {LogSource}", logSource);
                    }
                }

                _logger.LogInformation("Export cycle complete: {Count} events exported", totalExported);

                await Task.Delay(TimeSpan.FromMinutes(_config.ExportIntervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // shutdown requested — exit the cycle loop quietly
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in event export cycle");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        _logger.LogInformation("Event Viewer Export Service stopped");
    }

    /// <summary>
    /// Apply IncludeSources / ExcludeSources filtering on top of LogSources.
    /// </summary>
    private List<string> GetEffectiveLogSources()
    {
        var sources = _config.LogSources;

        if (_config.IncludeSources?.Any() == true)
        {
            sources = sources
                .Where(s => _config.IncludeSources.Any(inc =>
                    inc.Equals(s, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        if (_config.ExcludeSources?.Any() == true)
        {
            sources = sources
                .Where(s => !_config.ExcludeSources.Any(exc =>
                    exc.Equals(s, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        return sources;
    }

    /// <summary>
    /// Streams events from a single log source: read -> filter -> send -> save state.
    /// Each event is sent to LogDB the moment it's read - no memory accumulation.
    /// </summary>
    private async Task<int> ExportLogSourceStreamingAsync(
        string logSource,
        CancellationToken cancellationToken)
    {
        // Get per-source state from local tracker
        var sourceState = _stateTracker.GetSourceState(logSource);

        DateTime? sourceLastTimestamp = sourceState?.LastTimestamp;
        long sourceLastEventId = sourceState?.LastEventId ?? 0L;
        long sourceTotalExported = sourceState?.TotalExported ?? 0L;

        var sourceIsFirstRun = sourceLastTimestamp == null;

        // Treat the configured start date as a FLOOR on the read watermark, even
        // when saved state already exists. Previously it applied only on first run,
        // so an already-running source ignored a newly-set date — in particular a
        // FUTURE date ("start tomorrow") never held off and today's events kept
        // shipping. max() semantics: a future date jumps the watermark forward
        // (holds off until then); a past date is a no-op once the watermark has
        // advanced beyond it. Use ResetState/--reset to genuinely backfill earlier.
        if (InitialStartDate.HasValue)
        {
            if (sourceLastTimestamp == null || InitialStartDate.Value > sourceLastTimestamp.Value)
            {
                sourceLastTimestamp = InitialStartDate.Value;
                _logger.LogInformation(
                    "Applying InitialStartDate {Date} as the read floor for {LogSource}", InitialStartDate.Value, logSource);
            }
            sourceIsFirstRun = false;
        }

        _logger.LogInformation(
            "Processing {LogSource}: firstRun={FirstRun}, lastTimestamp={Timestamp}, lastEventId={EventId}, totalExported={Total}",
            logSource, sourceIsFirstRun, sourceLastTimestamp, sourceLastEventId, sourceTotalExported);

        // Streaming state - tracked during the callback
        EventLogEntryModel? latestProcessed = null;
        int successCount = 0;
        int failCount = 0;
        int filteredOut = 0;
        var sw = Stopwatch.StartNew();

        // Batched send state. `pending` accumulates filtered events; once a
        // sub-batch confirms, the checkpoint (latestProcessed) advances. On the
        // first failed sub-batch `aborted` stops further work so the unconfirmed
        // events are re-read next cycle.
        var pending = new List<(EventLogEntryModel Entry, Log Log)>();

        // Events read + filtered this cycle, buffered so the gRPC send happens
        // AFTER the thread-affine EventLogReader is fully drained. Sending inline
        // during the read enumeration tore down the in-flight gRPC request when the
        // await resumed on another thread → RpcException(Cancelled, "Call canceled
        // by the client"), which froze the watermark on the first event forever.
        var collected = new List<(EventLogEntryModel Entry, Log Log)>();
        var aborted = false;

        // Logs the per-row Online Console line + advances success bookkeeping
        // for one confirmed event.
        void RecordConfirmed(EventLogEntryModel eventEntry)
        {
            successCount++;
            latestProcessed = eventEntry;
            // Progress was made — clear any frozen-head tracking for this source.
            _stuckHead.Remove(logSource);

            // "LogEventTimestamp" is a well-known scope key the LogDB collector
            // reads to surface the record's own timestamp in the Online Console
            // (separate from when this line is logged).
            using (_logger.BeginScope(new Dictionary<string, object>
                   {
                       ["LogEventTimestamp"] = eventEntry.TimeGenerated
                   }))
            {
                _logger.LogInformation("► [{LogSource}] EventID {EventId} from {Source} at {Time:HH:mm:ss}",
                    logSource, eventEntry.EventID, eventEntry.Source, eventEntry.TimeGenerated);
            }

            // Save state every 500 successful sends for crash resilience
            if (successCount % 500 == 0)
            {
                SaveSourceState(logSource, latestProcessed, sourceTotalExported + successCount);
                _logger.LogInformation(
                    "Progress {LogSource}: {Success} sent, {Failed} failed, {Filtered} filtered ({Elapsed:F1}s)",
                    logSource, successCount, failCount, filteredOut, sw.Elapsed.TotalSeconds);
            }
        }

        // Ships the accumulated `pending` events as a single batch. On success
        // each event is checkpointed in read (ascending-time) order; on failure
        // nothing in the batch is checkpointed and `aborted` is set.
        async Task FlushPendingAsync()
        {
            if (pending.Count == 0)
                return;

            var logs = pending.Select(p => p.Log).ToList();
            try
            {
                var status = await _logDBClient.SendLogBatchAsync(logs, cancellationToken);
                if (status != LogResponseStatus.Success)
                {
                    aborted = true;
                    failCount += pending.Count;
                    _logger.LogWarning(
                        "Batch send returned {Status} for {Count} event(s) in {LogSource} — deferring; will retry from last checkpoint next cycle",
                        status, pending.Count, logSource);
                    pending.Clear();
                    return;
                }
            }
            catch (Exception ex)
            {
                aborted = true;
                failCount += pending.Count;
                _logger.LogError(ex,
                    "Batch send threw for {Count} event(s) in {LogSource} — deferring; will retry from last checkpoint next cycle",
                    pending.Count, logSource);
                pending.Clear();
                return;
            }

            foreach (var (entry, _) in pending)
                RecordConfirmed(entry);
            pending.Clear();
        }

        // READ-phase callback: filter + buffer only. Deliberately does NO gRPC
        // send (and no await), so nothing async happens while the thread-affine
        // EventLogReader is mid-enumeration. Sending is done by SendCollectedAsync
        // below, after the reader is fully drained.
        Task CollectEvent(EventLogEntryModel eventEntry)
        {
            // Never harvest the collector's own event-log Source — re-ingesting its
            // own status lines as data is a feedback loop that bloated the events DB.
            if (_config.SelfExcludeProviders?.Count > 0
                && !string.IsNullOrEmpty(eventEntry.Source)
                && _config.SelfExcludeProviders.Any(p => eventEntry.Source.Equals(p, StringComparison.OrdinalIgnoreCase)))
            {
                filteredOut++;
                return Task.CompletedTask;
            }

            if (!_eventLogFilter.MatchesFilter(eventEntry, _config.FilterConditions))
            {
                filteredOut++;
                return Task.CompletedTask;
            }

            collected.Add((eventEntry, ConvertToLogDto(eventEntry, logSource)));
            return Task.CompletedTask;
        }

        // SEND-phase: runs after the reader is drained/disposed, so gRPC awaits are
        // safe. Preserves the watermark semantics — events ship in ascending order;
        // the first failure freezes the watermark so unconfirmed events are re-read
        // next cycle.
        async Task SendCollectedAsync()
        {
            // Records a failure of the current head event; returns true once the
            // same event has failed MaxFreezeAttemptsBeforeSkip cycles in a row, in
            // which case the caller skips it instead of freezing the channel.
            bool ShouldSkipPoison(EventLogEntryModel e)
            {
                var key = GenerateDeterministicId(logSource, e);
                var count = (_stuckHead.TryGetValue(logSource, out var s) && s.Key == key)
                    ? s.FailCount + 1
                    : 1;
                if (count >= MaxFreezeAttemptsBeforeSkip)
                {
                    _stuckHead.Remove(logSource);
                    return true;
                }
                _stuckHead[logSource] = (key, count);
                return false;
            }

            foreach (var (entry, log) in collected)
            {
                if (aborted)
                    break;

                if (_enableBatching)
                {
                    pending.Add((entry, log));
                    if (pending.Count >= _batchSize)
                        await FlushPendingAsync();
                    continue;
                }

                try
                {
                    var result = await _logDBClient.LogAsync(log, cancellationToken);
                    if (result == LogResponseStatus.Success)
                    {
                        RecordConfirmed(entry);
                    }
                    else
                    {
                        failCount++;
                        if (ShouldSkipPoison(entry))
                        {
                            _logger.LogError(
                                "Dead-lettering {LogSource} event (EventID: {EventId}, Time: {Time}) after {Max} failed cycles — skipping to unblock the channel.",
                                logSource, entry.EventID, entry.TimeGenerated, MaxFreezeAttemptsBeforeSkip);
                            latestProcessed = entry; // advance the watermark past the poison event
                            continue;
                        }
                        aborted = true;
                        _logger.LogWarning(
                            "Send failed for {LogSource} (EventID: {EventId}, Time: {Time}): {Status}. " +
                            "Freezing watermark; will retry from this event next cycle.",
                            logSource, entry.EventID, entry.TimeGenerated, result);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Shutdown / reload — expected and transient, never counts toward
                    // the poison-event dead-letter. Just freeze and resume next start.
                    aborted = true;
                    _logger.LogDebug(
                        "Send cancelled during shutdown for {LogSource} (EventID: {EventId})",
                        logSource, entry.EventID);
                }
                catch (Exception ex)
                {
                    failCount++;
                    if (ShouldSkipPoison(entry))
                    {
                        _logger.LogError(ex,
                            "Dead-lettering {LogSource} event (EventID: {EventId}, Time: {Time}) after {Max} failed cycles — skipping to unblock the channel.",
                            logSource, entry.EventID, entry.TimeGenerated, MaxFreezeAttemptsBeforeSkip);
                        latestProcessed = entry; // advance the watermark past the poison event
                        continue;
                    }
                    aborted = true;
                    _logger.LogError(ex,
                        "Exception sending event for {LogSource} (EventID: {EventId}, Time: {Time}). " +
                        "Freezing watermark; will retry from this event next cycle.",
                        logSource, entry.EventID, entry.TimeGenerated);
                }
            }
        }

        // Phase 1 — read + filter into the buffer. No gRPC send happens during the
        // event-log enumeration (that mid-read send was what got cancelled).
        if (sourceIsFirstRun)
        {
            var latestDate = _eventLogReader.GetLatestEventDate(logSource);
            if (!latestDate.HasValue)
            {
                _logger.LogWarning("Could not get latest date for {LogSource}, skipping", logSource);
                return 0;
            }

            _logger.LogInformation(
                "FIRST RUN for {LogSource}: reading all events up to {LatestDate}",
                logSource, latestDate.Value);

            await _eventLogReader.ReadAllEventsUpToDateAsync(
                logSource, latestDate.Value, _config.EventLevels, CollectEvent);
        }
        else
        {
            var sinceTimestamp = sourceLastTimestamp ?? DateTime.UtcNow.AddHours(-1);
            await _eventLogReader.ReadEventsAsync(
                logSource, sinceTimestamp, sourceLastEventId, _config.EventLevels,
                _config.MaxEventsPerExport, CollectEvent);
        }

        // Phase 2 — the reader is fully drained; now ship the buffered events.
        await SendCollectedAsync();

        // Flush the final partial batch (no-op when batching is off or aborted).
        if (_enableBatching && !aborted)
            await FlushPendingAsync();

        sw.Stop();

        // Final state save
        if (latestProcessed != null)
        {
            SaveSourceState(logSource, latestProcessed, sourceTotalExported + successCount);
        }

        _logger.LogInformation(
            "Completed {LogSource}: {Success} sent, {Failed} failed, {Filtered} filtered in {Elapsed:F1}s",
            logSource, successCount, failCount, filteredOut, sw.Elapsed.TotalSeconds);

        return successCount;
    }

    /// <summary>
    /// Stable, content-derived row id so a re-read event (after a frozen
    /// watermark) maps to the same Guid instead of creating a duplicate.
    /// EventRecordId (Index) is unique per record within a machine's log;
    /// the other fields guard the rare case where Index is 0/unavailable.
    /// </summary>
    private static string GenerateDeterministicId(string logSource, EventLogEntryModel e)
    {
        const char sep = '';
        var input = string.Join(sep,
            e.MachineName ?? string.Empty,
            logSource,
            e.Index,
            e.EventID,
            e.Source ?? string.Empty,
            e.TimeGenerated.Ticks);

        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return new Guid(hash).ToString();
    }

    private void SaveSourceState(string logSource, EventLogEntryModel latestEvent, long totalExported)
    {
        var newTimestamp = latestEvent.TimeGenerated.Kind == DateTimeKind.Utc
            ? latestEvent.TimeGenerated
            : latestEvent.TimeGenerated.ToUniversalTime();

        _stateTracker.UpdateSourceState(logSource, newTimestamp, latestEvent.Index, totalExported);
    }

    private Log ConvertToLogDto(EventLogEntryModel eventEntry, string logSource)
    {
        var serverName = _configuration["Server:ServerName"] ?? Environment.MachineName;
        var serverEnvironment = _configuration["Server:ServerEnvironment"] ?? "Production";

        // Computer field is always the configured Server:ServerName — matches the working
        // pattern used by WindowsTrackerExportService (Metrics). The mapper rewrites
        // Server:ServerName when ANY of the user's override fields is set, so the typed
        // override flows here without needing a separate signal key. In the no-override
        // case Server:ServerName defaults to Environment.MachineName which equals
        // eventEntry.MachineName for local-EventLog collection — bit-for-bit identical
        // behaviour to the pre-1.1.15 code path when nothing is overridden.
        var computerName = serverName;

        // Build labels
        var allLabels = new List<string>(_config.Labels);
        if (!string.IsNullOrEmpty(serverName) && !allLabels.Contains(serverName.ToLower()))
        {
            allLabels.Add(serverName.ToLower());
        }
        allLabels = allLabels.Distinct().ToList();

        // Determine collection name
        string? collectionName = null;

        if (_config.CollectionMap != null &&
            _config.CollectionMap.TryGetValue(logSource, out var mappedCollection) &&
            !string.IsNullOrWhiteSpace(mappedCollection))
        {
            collectionName = mappedCollection;
        }

        if (string.IsNullOrWhiteSpace(collectionName) && !string.IsNullOrWhiteSpace(_config.Collection))
        {
            collectionName = _config.Collection;
        }

        if (string.IsNullOrWhiteSpace(collectionName))
        {
            collectionName = $"windows-eventlog-{logSource.ToLower()}";
        }

        // Build XML details if enabled
        string? xmlDetails = null;
        if (_config.IncludeXmlDetails)
        {
            try
            {
                xmlDetails = GetEventXmlDetails(eventEntry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get XML details for event {EventId}", eventEntry.EventID);
            }
        }

        var windowsEvent = new LogWindowsEvent
        {
            Guid = GenerateDeterministicId(logSource, eventEntry),
            Timestamp = eventEntry.TimeGenerated.Kind == DateTimeKind.Utc
                ? eventEntry.TimeGenerated
                : eventEntry.TimeGenerated.ToUniversalTime(),
            Application = _config.ApplicationName,
            Environment = serverEnvironment,
            Level = MapEventLevel(eventEntry.EntryType),
            Message = eventEntry.Message ?? $"Event {eventEntry.EventID} from {eventEntry.Source}",
            ProviderName = string.IsNullOrWhiteSpace(_config.ProviderNameOverride)
                ? eventEntry.Source
                : _config.ProviderNameOverride,
            Collection = collectionName,
            IpAddress = eventEntry.IP,
            EventId = eventEntry.EventID,
            Computer = computerName,
            UserId = eventEntry.UserName,
            Channel = logSource,
            XmlData = xmlDetails,
        };

        var log = windowsEvent.ToLog();

        // Labels
        foreach (var lbl in allLabels)
            if (!log.Label.Contains(lbl)) log.Label.Add(lbl);

        // Extra attributes not covered by the typed model
        log.AttributesS["category"] = eventEntry.Category ?? "";
        log.AttributesS["serverName"] = serverName;
        log.AttributesN["eventId"] = eventEntry.EventID;

        // When the user overrides Provider for tagging purposes, keep the
        // original Windows event Source on the row so it remains queryable.
        if (!string.IsNullOrWhiteSpace(_config.ProviderNameOverride) && !string.IsNullOrWhiteSpace(eventEntry.Source))
        {
            log.AttributesS["original_provider"] = eventEntry.Source;
        }

        // Preserve the raw machine name on the row whenever it differs from the
        // configured Computer (so post-hoc queries can still find the source host
        // even after override). Differs in two cases: (a) user typed an override,
        // or (b) collector is reading remote-machine events (rare).
        if (!string.IsNullOrWhiteSpace(eventEntry.MachineName)
            && !string.Equals(eventEntry.MachineName, computerName, StringComparison.OrdinalIgnoreCase))
        {
            log.AttributesS["original_computer"] = eventEntry.MachineName;
        }

        return log;
    }

    private static string MapEventLevel(EventLogEntryType entryType)
    {
        return entryType switch
        {
            EventLogEntryType.Error => "Error",
            EventLogEntryType.Warning => "Warning",
            EventLogEntryType.Information => "Information",
            EventLogEntryType.SuccessAudit => "Information",
            EventLogEntryType.FailureAudit => "Warning",
            _ => "Information"
        };
    }

    private string? GetEventXmlDetails(EventLogEntryModel eventEntry)
    {
        try
        {
            var query = $"*[System[EventID={eventEntry.EventID}]]";
            var eventLogQuery = new System.Diagnostics.Eventing.Reader.EventLogQuery(
                eventEntry.Log,
                System.Diagnostics.Eventing.Reader.PathType.LogName,
                query);
            using var eventReader = new System.Diagnostics.Eventing.Reader.EventLogReader(eventLogQuery);

            var eventRecord = eventReader.ReadEvent();
            if (eventRecord != null)
            {
                return eventRecord.ToXml();
            }
        }
        catch
        {
            // Ignore errors - XML details are optional
        }

        return null;
    }
}
