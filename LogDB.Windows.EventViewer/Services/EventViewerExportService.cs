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

    // Command-line flags (set from Program.cs)
    public static DateTime? InitialStartDate { get; set; }
    public static bool ResetState { get; set; }

    private bool _stateResetDone;

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
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error reading events from {LogSource}", logSource);
                    }
                }

                _logger.LogInformation("Export cycle complete: {Count} events exported", totalExported);

                await Task.Delay(TimeSpan.FromMinutes(_config.ExportIntervalMinutes), stoppingToken);
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

        if (sourceIsFirstRun && InitialStartDate.HasValue)
        {
            sourceLastTimestamp = InitialStartDate.Value;
            sourceIsFirstRun = false;
            _logger.LogInformation("Using InitialStartDate: {Date} for {LogSource}", InitialStartDate.Value, logSource);
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

        // Callback: called for each event as it's read from the Event Log
        async Task ProcessEvent(EventLogEntryModel eventEntry)
        {
            // Apply filter
            if (!_eventLogFilter.MatchesFilter(eventEntry, _config.FilterConditions))
            {
                filteredOut++;
                return;
            }

            try
            {
                var log = ConvertToLogDto(eventEntry, logSource);
                var result = await _logDBClient.LogAsync(log, cancellationToken);

                if (result == LogResponseStatus.Success)
                {
                    successCount++;
                    latestProcessed = eventEntry;

                    _logger.LogInformation("► [{LogSource}] EventID {EventId} from {Source} at {Time:HH:mm:ss}",
                        logSource, eventEntry.EventID, eventEntry.Source, eventEntry.TimeGenerated);

                    // Save state every 500 successful sends for crash resilience
                    if (successCount % 500 == 0)
                    {
                        SaveSourceState(logSource, latestProcessed, sourceTotalExported + successCount);
                        _logger.LogInformation(
                            "Progress {LogSource}: {Success} sent, {Failed} failed, {Filtered} filtered ({Elapsed:F1}s)",
                            logSource, successCount, failCount, filteredOut, sw.Elapsed.TotalSeconds);
                    }
                }
                else
                {
                    failCount++;
                    if (failCount <= 10 || failCount % 100 == 0)
                    {
                        _logger.LogWarning(
                            "Failed to send event (EventID: {EventId}, Time: {Time}): {Status}",
                            eventEntry.EventID, eventEntry.TimeGenerated, result);
                    }
                }
            }
            catch (Exception ex)
            {
                failCount++;
                if (failCount <= 10 || failCount % 100 == 0)
                {
                    _logger.LogError(ex,
                        "Exception sending event (EventID: {EventId}, Time: {Time})",
                        eventEntry.EventID, eventEntry.TimeGenerated);
                }
            }
        }

        // Stream events: read -> filter -> send inline
        if (sourceIsFirstRun)
        {
            var latestDate = _eventLogReader.GetLatestEventDate(logSource);
            if (!latestDate.HasValue)
            {
                _logger.LogWarning("Could not get latest date for {LogSource}, skipping", logSource);
                return 0;
            }

            _logger.LogInformation(
                "FIRST RUN for {LogSource}: streaming all events up to {LatestDate}",
                logSource, latestDate.Value);

            await _eventLogReader.ReadAllEventsUpToDateAsync(
                logSource, latestDate.Value, _config.EventLevels, ProcessEvent);
        }
        else
        {
            var sinceTimestamp = sourceLastTimestamp ?? DateTime.UtcNow.AddHours(-1);
            await _eventLogReader.ReadEventsAsync(
                logSource, sinceTimestamp, sourceLastEventId, _config.EventLevels,
                _config.MaxEventsPerExport, ProcessEvent);
        }

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
            Guid = Guid.NewGuid().ToString(),
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
