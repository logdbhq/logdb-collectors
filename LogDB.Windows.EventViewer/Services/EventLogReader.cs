using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using com.logdb.windows.eventviewer.Models;

namespace com.logdb.windows.eventviewer.Services;

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

        try
        {
            using var eventLog = new EventLog(logName);

            if (!EventLog.Exists(logName))
            {
                _logger?.LogWarning("Event log '{LogName}' does not exist", logName);
                return 0;
            }

            var levelFilter = levels.Select(ParseLevel).Where(l => l.HasValue).Select(l => l!.Value).ToHashSet();

            for (int i = 0; i < eventLog.Entries.Count && processedCount < maxCount; i++)
            {
                try
                {
                    var entry = eventLog.Entries[i];

                    if (!ShouldIncludeEvent(entry, since, afterEventId, levelFilter))
                        continue;

                    var model = CreateModel(entry, logName);
                    await processor(model);
                    processedCount++;
                    WriteProgress(processedCount, logName);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error processing event at index {Index} in {LogName}", i, logName);
                }
            }

            if (processedCount > 0) Console.WriteLine();
            _logger?.LogDebug(
                "Read {Count} events from {LogName} (since: {Since}, afterEventId: {AfterEventId})",
                processedCount, logName, since, afterEventId);
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
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger?.LogError(ex,
                "WINDOWS ERROR: Failed to access Event Log '{LogName}'. Win32 Error Code: {ErrorCode}. Current user: {CurrentUser}.",
                logName, ex.NativeErrorCode, Environment.UserName);
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

        try
        {
            using var eventLog = new EventLog(logName);

            if (!EventLog.Exists(logName))
            {
                _logger?.LogWarning("Event log '{LogName}' does not exist", logName);
                return 0;
            }

            var levelFilter = levels.Select(ParseLevel).Where(l => l.HasValue).Select(l => l!.Value).ToHashSet();
            var totalEntries = eventLog.Entries.Count;

            _logger?.LogInformation(
                "Starting full export from {LogName} up to {UpToDate} (total entries in log: {TotalCount})",
                logName, upToDate, totalEntries);

            for (int i = 0; i < eventLog.Entries.Count; i++)
            {
                try
                {
                    var entry = eventLog.Entries[i];

                    if (entry.TimeGenerated > upToDate)
                    {
                        _logger?.LogInformation(
                            "Reached events newer than {UpToDate} at index {Index}, stopping",
                            upToDate, i);
                        break;
                    }

                    if (levelFilter.Count > 0 && !levelFilter.Contains(entry.EntryType))
                        continue;

                    var model = CreateModel(entry, logName);
                    await processor(model);
                    processedCount++;
                    WriteProgress(processedCount, logName);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error processing event at index {Index} in {LogName}", i, logName);
                }
            }

            if (processedCount > 0) Console.WriteLine();
            _logger?.LogInformation(
                "Completed reading {LogName}: streamed {Count} events up to {UpToDate}",
                logName, processedCount, upToDate);
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
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger?.LogError(ex,
                "WINDOWS ERROR: Failed to access Event Log '{LogName}'. Win32 Error Code: {ErrorCode}. Current user: {CurrentUser}.",
                logName, ex.NativeErrorCode, Environment.UserName);
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
            using var eventLog = new EventLog(logName);

            if (!EventLog.Exists(logName))
            {
                _logger?.LogWarning("Event log '{LogName}' does not exist", logName);
                return null;
            }

            if (eventLog.Entries.Count == 0)
            {
                _logger?.LogDebug("Event log '{LogName}' has no entries", logName);
                return null;
            }

            var latestEntry = eventLog.Entries[eventLog.Entries.Count - 1];
            var latestDate = latestEntry.TimeGenerated;

            _logger?.LogInformation(
                "Latest event in {LogName}: {LatestDate} (EventID: {EventId}, Index: {Index})",
                logName, latestDate, latestEntry.InstanceId, latestEntry.Index);

            return latestDate;
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
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger?.LogError(ex,
                "WINDOWS ERROR: Failed to access Event Log '{LogName}'. Win32 Error Code: {ErrorCode}. Current user: {CurrentUser}.",
                logName, ex.NativeErrorCode, Environment.UserName);
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

    private EventLogEntryModel CreateModel(EventLogEntry entry, string logName)
    {
        return new EventLogEntryModel
        {
            Index = entry.Index,
            TimeGenerated = entry.TimeGenerated,
            Source = entry.Source,
            Message = entry.Message,
            EntryType = entry.EntryType,
            EventID = (int)entry.InstanceId,
            Category = entry.Category,
            MachineName = entry.MachineName,
            UserName = entry.UserName,
            Log = logName,
            IP = ExtractIpFromMessage(entry.Message)
        };
    }

    private bool ShouldIncludeEvent(
        EventLogEntry entry,
        DateTime? since,
        long? afterEventId,
        HashSet<EventLogEntryType> levelFilter)
    {
        if (levelFilter.Count > 0 && !levelFilter.Contains(entry.EntryType))
            return false;

        if (since.HasValue)
        {
            var entryTimeUtc = entry.TimeGenerated.Kind == DateTimeKind.Utc
                ? entry.TimeGenerated
                : entry.TimeGenerated.ToUniversalTime();

            var sinceUtc = since.Value.Kind == DateTimeKind.Utc
                ? since.Value
                : since.Value.ToUniversalTime();

            if (entryTimeUtc < sinceUtc)
                return false;
        }

        if (afterEventId.HasValue && entry.Index <= afterEventId.Value)
            return false;

        return true;
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
