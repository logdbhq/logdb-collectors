using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Net;
using System.Security.Principal;
using System.Text.RegularExpressions;
using com.logdb.windows.eventviewer.Models;

namespace com.logdb.windows.eventviewer.Services;

// Reads Windows Event Log channels via the modern Eventing.Reader API. The
// legacy System.Diagnostics.EventLog API only enumerates the three classic logs
// (Application, Security, System); modern channels like Setup, ForwardedEvents,
// and everything under "Applications and Services Logs" return an empty
// Entries collection there. This implementation uses EventLogReader +
// EventLogQuery, which works uniformly across classic and modern channels.
public class EventLogReader
{
    private readonly ILogger<EventLogReader>? _logger;

    public EventLogReader(ILogger<EventLogReader>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Streams events incrementally (since last export), calling the processor for each event immediately.
    /// Returns the total number of events passed to the processor.
    /// </summary>
    public async Task<int> ReadEventsAsync(
        string logName,
        DateTime? since,
        long? afterEventId,
        List<string> levels,
        int maxCount,
        Func<EventLogEntryModel, Task> processor)
    {
        int processedCount = 0;
        var levelFilter = levels.Select(ParseLevel).Where(l => l.HasValue).Select(l => l!.Value).ToHashSet();

        try
        {
            var query = BuildIncrementalQuery(logName, since, afterEventId);
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);

            while (processedCount < maxCount)
            {
                using var record = ReadNext(reader, logName);
                if (record == null) break;

                try
                {
                    if (!ShouldIncludeEvent(record, since, afterEventId, levelFilter))
                        continue;

                    var model = CreateModel(record, logName);
                    await processor(model);
                    processedCount++;
                    WriteProgress(processedCount, logName);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error processing event in {LogName}", logName);
                }
            }

            if (processedCount > 0) Console.WriteLine();
            _logger?.LogDebug(
                "Read {Count} events from {LogName} (since: {Since}, afterEventId: {AfterEventId})",
                processedCount, logName, since, afterEventId);
        }
        catch (EventLogNotFoundException)
        {
            _logger?.LogWarning("Event log '{LogName}' does not exist", logName);
        }
        catch (System.Security.SecurityException ex)
        {
            _logger?.LogError(ex,
                "SECURITY ERROR: Access denied reading Event Log '{LogName}'. Current user: {CurrentUser}.",
                logName, Environment.UserName);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogError(ex,
                "UNAUTHORIZED ACCESS: Cannot read Event Log '{LogName}'. Current user: {CurrentUser}.",
                logName, Environment.UserName);
        }
        catch (EventLogException ex)
        {
            _logger?.LogError(ex,
                "EVENT LOG ERROR: Failed to read Event Log '{LogName}'. Current user: {CurrentUser}.",
                logName, Environment.UserName);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Error reading events from {LogName}. Current user: {CurrentUser}",
                logName, Environment.UserName);
        }

        return processedCount;
    }

    /// <summary>
    /// Streams ALL events up to a given date, calling the processor for each event immediately.
    /// Used for initial full export when no state exists.
    /// Returns the total number of events passed to the processor.
    /// </summary>
    public async Task<int> ReadAllEventsUpToDateAsync(
        string logName,
        DateTime upToDate,
        List<string> levels,
        Func<EventLogEntryModel, Task> processor)
    {
        int processedCount = 0;
        var levelFilter = levels.Select(ParseLevel).Where(l => l.HasValue).Select(l => l!.Value).ToHashSet();

        try
        {
            var query = BuildUpToDateQuery(logName, upToDate);
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);

            _logger?.LogInformation(
                "Starting full export from {LogName} up to {UpToDate}",
                logName, upToDate);

            while (true)
            {
                using var record = ReadNext(reader, logName);
                if (record == null) break;

                try
                {
                    var entryType = MapToEntryType(record);
                    if (levelFilter.Count > 0 && !levelFilter.Contains(entryType))
                        continue;

                    var model = CreateModel(record, logName);
                    await processor(model);
                    processedCount++;
                    WriteProgress(processedCount, logName);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error processing event in {LogName}", logName);
                }
            }

            if (processedCount > 0) Console.WriteLine();
            _logger?.LogInformation(
                "Completed reading {LogName}: streamed {Count} events up to {UpToDate}",
                logName, processedCount, upToDate);
        }
        catch (EventLogNotFoundException)
        {
            _logger?.LogWarning("Event log '{LogName}' does not exist", logName);
        }
        catch (System.Security.SecurityException ex)
        {
            _logger?.LogError(ex,
                "SECURITY ERROR: Access denied reading Event Log '{LogName}'. Current user: {CurrentUser}.",
                logName, Environment.UserName);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogError(ex,
                "UNAUTHORIZED ACCESS: Cannot read Event Log '{LogName}'. Current user: {CurrentUser}.",
                logName, Environment.UserName);
        }
        catch (EventLogException ex)
        {
            _logger?.LogError(ex,
                "EVENT LOG ERROR: Failed to read Event Log '{LogName}'. Current user: {CurrentUser}.",
                logName, Environment.UserName);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Error reading all events from {LogName}. Current user: {CurrentUser}",
                logName, Environment.UserName);
        }

        return processedCount;
    }

    /// <summary>
    /// Gets the latest event date from the specified log source.
    /// Returns null if the log doesn't exist or has no events.
    /// </summary>
    public DateTime? GetLatestEventDate(string logName)
    {
        try
        {
            // ReverseDirection makes the reader return newest-first, so the first
            // record is the latest. Works for both classic and modern channels.
            var query = new EventLogQuery(logName, PathType.LogName) { ReverseDirection = true };
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);

            using var record = reader.ReadEvent();
            if (record == null)
            {
                _logger?.LogDebug("Event log '{LogName}' has no entries", logName);
                return null;
            }

            if (!record.TimeCreated.HasValue)
            {
                _logger?.LogDebug("Latest event in '{LogName}' has no TimeCreated", logName);
                return null;
            }

            var latestDate = record.TimeCreated.Value;
            _logger?.LogInformation(
                "Latest event in {LogName}: {LatestDate} (EventID: {EventId}, RecordID: {RecordId})",
                logName, latestDate, record.Id, record.RecordId);

            return latestDate;
        }
        catch (EventLogNotFoundException)
        {
            _logger?.LogWarning("Event log '{LogName}' does not exist", logName);
            return null;
        }
        catch (System.Security.SecurityException ex)
        {
            _logger?.LogError(ex,
                "SECURITY ERROR: Access denied reading Event Log '{LogName}'. Current user: {CurrentUser}.",
                logName, Environment.UserName);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogError(ex,
                "UNAUTHORIZED ACCESS: Cannot read Event Log '{LogName}'. Current user: {CurrentUser}.",
                logName, Environment.UserName);
            return null;
        }
        catch (EventLogException ex)
        {
            _logger?.LogError(ex,
                "EVENT LOG ERROR: Failed to read Event Log '{LogName}'. Current user: {CurrentUser}.",
                logName, Environment.UserName);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Error getting latest event date from {LogName}. Current user: {CurrentUser}",
                logName, Environment.UserName);
            return null;
        }
    }

    private static EventLogQuery BuildIncrementalQuery(string logName, DateTime? since, long? afterEventId)
    {
        var conditions = new List<string>();

        if (since.HasValue)
        {
            var sinceUtc = since.Value.Kind == DateTimeKind.Utc ? since.Value : since.Value.ToUniversalTime();
            conditions.Add($"TimeCreated[@SystemTime>='{sinceUtc:yyyy-MM-ddTHH:mm:ss.fffZ}']");
        }

        if (afterEventId.HasValue && afterEventId.Value > 0)
        {
            conditions.Add($"EventRecordID>{afterEventId.Value}");
        }

        string? xpath = conditions.Count > 0
            ? $"*[System[{string.Join(" and ", conditions)}]]"
            : null;

        return new EventLogQuery(logName, PathType.LogName, xpath);
    }

    private static EventLogQuery BuildUpToDateQuery(string logName, DateTime upToDate)
    {
        var upToDateUtc = upToDate.Kind == DateTimeKind.Utc ? upToDate : upToDate.ToUniversalTime();
        var xpath = $"*[System[TimeCreated[@SystemTime<='{upToDateUtc:yyyy-MM-ddTHH:mm:ss.fffZ}']]]";
        return new EventLogQuery(logName, PathType.LogName, xpath);
    }

    private EventRecord? ReadNext(System.Diagnostics.Eventing.Reader.EventLogReader reader, string logName)
    {
        try
        {
            return reader.ReadEvent();
        }
        catch (EventLogException ex)
        {
            _logger?.LogWarning(ex, "Error reading next event from {LogName}", logName);
            return null;
        }
    }

    /// <summary>
    /// Visual progress: green X every 10 events, summary count every 500.
    /// </summary>
    private void WriteProgress(int processedCount, string logName)
    {
        if (processedCount % 10 == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("X");
            Console.ResetColor();
        }

        if (processedCount % 500 == 0)
        {
            Console.Write($" [{logName}: {processedCount:N0}]");
            Console.WriteLine();
        }
    }

    private EventLogEntryModel CreateModel(EventRecord record, string logName)
    {
        var message = SafeFormat(record);

        return new EventLogEntryModel
        {
            Index = record.RecordId ?? 0L,
            TimeGenerated = record.TimeCreated ?? DateTime.UtcNow,
            Source = record.ProviderName ?? string.Empty,
            Message = message ?? string.Empty,
            EntryType = MapToEntryType(record),
            EventID = record.Id,
            Category = SafeTaskName(record),
            MachineName = record.MachineName ?? string.Empty,
            UserName = TranslateUser(record),
            Log = logName,
            IP = ExtractIpFromMessage(message)
        };
    }

    private static string? SafeFormat(EventRecord record)
    {
        // FormatDescription throws or returns null for events whose provider
        // metadata isn't installed locally (common for ForwardedEvents).
        try { return record.FormatDescription(); }
        catch { return null; }
    }

    private static string? SafeTaskName(EventRecord record)
    {
        try { return record.TaskDisplayName; }
        catch { return null; }
    }

    private static string? TranslateUser(EventRecord record)
    {
        try
        {
            return record.UserId?.Translate(typeof(NTAccount))?.Value;
        }
        catch
        {
            // Identity not mappable to an NT account — fall back to SID string.
            return record.UserId?.Value;
        }
    }

    private bool ShouldIncludeEvent(
        EventRecord record,
        DateTime? since,
        long? afterEventId,
        HashSet<EventLogEntryType> levelFilter)
    {
        if (levelFilter.Count > 0 && !levelFilter.Contains(MapToEntryType(record)))
            return false;

        if (since.HasValue && record.TimeCreated.HasValue)
        {
            var entryTimeUtc = record.TimeCreated.Value.Kind == DateTimeKind.Utc
                ? record.TimeCreated.Value
                : record.TimeCreated.Value.ToUniversalTime();

            var sinceUtc = since.Value.Kind == DateTimeKind.Utc
                ? since.Value
                : since.Value.ToUniversalTime();

            if (entryTimeUtc < sinceUtc)
                return false;
        }

        if (afterEventId.HasValue && record.RecordId.HasValue && record.RecordId.Value <= afterEventId.Value)
            return false;

        return true;
    }

    private static EventLogEntryType MapToEntryType(EventRecord record)
    {
        // Security-channel audit events carry their success/failure flag in
        // Keywords, not Level. Check those first.
        const long AUDIT_SUCCESS = 0x0020000000000000L;
        const long AUDIT_FAILURE = 0x0010000000000000L;
        var keywords = record.Keywords ?? 0L;

        if ((keywords & AUDIT_SUCCESS) != 0) return EventLogEntryType.SuccessAudit;
        if ((keywords & AUDIT_FAILURE) != 0) return EventLogEntryType.FailureAudit;

        // Modern API Level: 1=Critical, 2=Error, 3=Warning, 4=Information, 5=Verbose, 0=LogAlways.
        return record.Level switch
        {
            1 or 2 => EventLogEntryType.Error,
            3 => EventLogEntryType.Warning,
            _ => EventLogEntryType.Information
        };
    }

    private EventLogEntryType? ParseLevel(string level)
    {
        return level.ToLowerInvariant() switch
        {
            "error" => EventLogEntryType.Error,
            "warning" => EventLogEntryType.Warning,
            "information" or "info" => EventLogEntryType.Information,
            "successaudit" or "success" => EventLogEntryType.SuccessAudit,
            "failureaudit" or "failure" => EventLogEntryType.FailureAudit,
            _ => null
        };
    }

    /// <summary>
    /// Extracts IP address from event message text using regex patterns.
    /// Fast - only parses the already-loaded message string.
    /// </summary>
    private string? ExtractIpFromMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var ipv4Pattern = @"(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)";

        // Priority 1: "Source Network Address:" (Security logon events)
        var sourceMatch = Regex.Match(message, @"Source Network Address[:\s]+(" + ipv4Pattern + @")", RegexOptions.IgnoreCase);
        if (sourceMatch.Success && sourceMatch.Groups.Count > 1)
        {
            var ip = sourceMatch.Groups[1].Value.Trim();
            if (IsValidIpAddress(ip)) return ip;
        }

        // Priority 2: "Client Address:" (RDP/logon events)
        var clientMatch = Regex.Match(message, @"Client Address[:\s]+(" + ipv4Pattern + @")", RegexOptions.IgnoreCase);
        if (clientMatch.Success && clientMatch.Groups.Count > 1)
        {
            var ip = clientMatch.Groups[1].Value.Trim();
            if (IsValidIpAddress(ip)) return ip;
        }

        // Priority 3: "IP Address:"
        var ipMatch = Regex.Match(message, @"IP Address[:\s]+(" + ipv4Pattern + @")", RegexOptions.IgnoreCase);
        if (ipMatch.Success && ipMatch.Groups.Count > 1)
        {
            var ip = ipMatch.Groups[1].Value.Trim();
            if (IsValidIpAddress(ip)) return ip;
        }

        // Priority 4: First valid IP found anywhere in message
        var matches = Regex.Matches(message, @"\b" + ipv4Pattern + @"\b");
        foreach (Match match in matches)
        {
            var ip = match.Value;
            if (ip != "0.0.0.0" && ip != "255.255.255.255" && IsValidIpAddress(ip))
                return ip;
        }

        return null;
    }

    private bool IsValidIpAddress(string? ipString)
    {
        if (string.IsNullOrWhiteSpace(ipString))
            return false;

        if (IPAddress.TryParse(ipString, out var ipAddress))
        {
            var ipBytes = ipAddress.GetAddressBytes();
            if (ipBytes[0] == 0 && ipBytes[1] == 0 && ipBytes[2] == 0 && ipBytes[3] == 0)
                return false;
            if (ipBytes[0] == 255 && ipBytes[1] == 255 && ipBytes[2] == 255 && ipBytes[3] == 255)
                return false;

            return true;
        }

        return false;
    }
}
