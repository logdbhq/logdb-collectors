using System.Globalization;
using System.Text.RegularExpressions;
using com.logdb.windows.iis.Models;
using Microsoft.Extensions.Logging;

namespace com.logdb.windows.iis.Services;

/// <summary>
/// Reads and parses IIS W3C extended log files.
/// Handles dynamic field detection via the #Fields directive.
/// </summary>
public class IISLogReader
{
    private readonly ILogger<IISLogReader>? _logger;
    
    // Fallback default field map for standard IIS logs (W3C) if header is missing
    private static readonly Dictionary<string, int> _defaultFieldMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        { "date", 0 }, { "time", 1 }, { "s-ip", 2 }, { "cs-method", 3 }, { "cs-uri-stem", 4 },
        { "cs-uri-query", 5 }, { "s-port", 6 }, { "cs-username", 7 }, { "c-ip", 8 },
        { "cs(User-Agent)", 9 }, { "cs(Referer)", 10 }, { "sc-status", 11 },
        { "sc-substatus", 12 }, { "sc-win32-status", 13 }, { "time-taken", 14 }
    };

    // Fields that are explicitly mapped to IISLogEntry properties (used to detect "additional" fields)
    private static readonly HashSet<string> _knownFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "date", "time", "s-ip", "cs-method", "cs-uri-stem", "cs-uri-query",
        "cs-username", "c-ip", "cs(user-agent)", "cs(referer)", "cs-version",
        "cs-host", "cs(cookie)", "s-sitename", "s-computername",
        "s-port", "sc-status", "sc-substatus", "sc-win32-status", "time-taken",
        "sc-bytes", "cs-bytes"
    };
    
    public IISLogReader(ILogger<IISLogReader>? logger = null)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Read log entries from a file starting at a specific byte position.
    /// Returns entries and the new byte position after reading.
    /// </summary>
    public async Task<(List<IISLogEntry> Entries, long NewPosition, Dictionary<string, int> CurrentFieldMap)> ReadEntriesAsync(
        string filePath,
        long startPosition,
        int maxEntries,
        Dictionary<string, int>? existingFieldMap = null,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<IISLogEntry>();
        long newPosition = startPosition;
        
        var currentFieldMap = existingFieldMap != null 
            ? new Dictionary<string, int>(existingFieldMap, StringComparer.OrdinalIgnoreCase) 
            : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // If starting from 0, assume fresh start but don't force clear unless we see directive
        // If starting > 0 and no map, try to use default
        if (currentFieldMap.Count == 0 && startPosition > 0)
        {
             // Use default map as fallback
             foreach(var kvp in _defaultFieldMap) currentFieldMap[kvp.Key] = kvp.Value;
        }
        
        if (!File.Exists(filePath))
        {
            _logger?.LogWarning("Log file not found: {FilePath}", filePath);
            return (entries, startPosition, currentFieldMap);
        }
        
        try
        {
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            
            // If start position is beyond file length, file was likely rotated
            if (startPosition > fileStream.Length)
            {
                _logger?.LogInformation("File was rotated, starting from beginning: {FilePath}", filePath);
                startPosition = 0;
                currentFieldMap.Clear(); // Reset map for new file content
            }
            
            fileStream.Seek(startPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(fileStream);
            
            string? line;
            long lineNumber = 0;
            int entryCount = 0;
            
            while ((line = await reader.ReadLineAsync(cancellationToken)) != null && entryCount < maxEntries)
            {
                lineNumber++;
                
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                
                // Handle directives (lines starting with #)
                if (line.StartsWith('#'))
                {
                    ProcessDirective(line, currentFieldMap);
                    continue;
                }
                
                // Skip if we haven't seen a #Fields directive yet, unless we have a map
                if (currentFieldMap.Count == 0)
                {
                    _logger?.LogTrace("Skipping line before #Fields directive: {Line}", line);
                    continue;
                }
                
                var entry = ParseLogLine(line, filePath, lineNumber, currentFieldMap);
                if (entry != null)
                {
                    entries.Add(entry);
                    entryCount++;
                }
            }
            
            newPosition = fileStream.Position;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Read aborted by our own cancellation (module restart on config change,
            // or service shutdown) — expected, not an error. Return the progress made
            // so far; the next cycle resumes from the saved position.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error reading log file: {FilePath}", filePath);
        }
        
        return (entries, newPosition, currentFieldMap);
    }
    
    /// <summary>
    /// Process a directive line (starts with #)
    /// </summary>
    private void ProcessDirective(string line, Dictionary<string, int> fieldMap)
    {
        if (line.StartsWith("#Fields:", StringComparison.OrdinalIgnoreCase))
        {
            ParseFieldsDirective(line, fieldMap);
        }
    }
    
    private void ParseFieldsDirective(string line, Dictionary<string, int> fieldMap)
    {
        var fieldsStr = line.Substring("#Fields:".Length).Trim();
        var fields = fieldsStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        
        fieldMap.Clear();
        for (int i = 0; i < fields.Length; i++)
        {
            fieldMap[fields[i].ToLowerInvariant()] = i;
        }
    }

    private IISLogEntry? ParseLogLine(string line, string sourceFile, long lineNumber, Dictionary<string, int> fieldMap)
    {
        try
        {
            var parts = line.Split(' ', StringSplitOptions.None);
            
            var entry = new IISLogEntry
            {
                SourceFile = sourceFile,
                LineNumber = lineNumber
            };
            
            // Parse date and time
            var dateStr = GetFieldValue(parts, "date", fieldMap);
            var timeStr = GetFieldValue(parts, "time", fieldMap);
            if (!string.IsNullOrEmpty(dateStr) && !string.IsNullOrEmpty(timeStr))
            {
                if (DateTime.TryParse($"{dateStr} {timeStr}", CultureInfo.InvariantCulture, 
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
                {
                    entry.Timestamp = timestamp;
                }
            }
            
            // Parse standard fields
            entry.ServerIp = GetFieldValue(parts, "s-ip", fieldMap);
            entry.Method = GetFieldValue(parts, "cs-method", fieldMap);
            entry.UriStem = GetFieldValue(parts, "cs-uri-stem", fieldMap);
            entry.UriQuery = GetFieldValue(parts, "cs-uri-query", fieldMap);
            entry.Username = GetFieldValue(parts, "cs-username", fieldMap);
            entry.ClientIp = GetFieldValue(parts, "c-ip", fieldMap);
            entry.UserAgent = GetFieldValue(parts, "cs(user-agent)", fieldMap);
            entry.Referer = GetFieldValue(parts, "cs(referer)", fieldMap);
            entry.ProtocolVersion = GetFieldValue(parts, "cs-version", fieldMap);
            entry.Host = GetFieldValue(parts, "cs-host", fieldMap);
            entry.Cookie = GetFieldValue(parts, "cs(cookie)", fieldMap);
            entry.SiteName = GetFieldValue(parts, "s-sitename", fieldMap);
            entry.ServerName = GetFieldValue(parts, "s-computername", fieldMap);
            
            // Parse numeric fields
            if (int.TryParse(GetFieldValue(parts, "s-port", fieldMap), out var port))
                entry.ServerPort = port;
            if (int.TryParse(GetFieldValue(parts, "sc-status", fieldMap), out var status))
                entry.StatusCode = status;
            if (int.TryParse(GetFieldValue(parts, "sc-substatus", fieldMap), out var substatus))
                entry.SubStatus = substatus;
            if (int.TryParse(GetFieldValue(parts, "sc-win32-status", fieldMap), out var win32status))
                entry.Win32Status = win32status;
            if (int.TryParse(GetFieldValue(parts, "time-taken", fieldMap), out var timeTaken))
                entry.TimeTaken = timeTaken;
            if (long.TryParse(GetFieldValue(parts, "sc-bytes", fieldMap), out var bytesSent))
                entry.BytesSent = bytesSent;
            if (long.TryParse(GetFieldValue(parts, "cs-bytes", fieldMap), out var bytesReceived))
                entry.BytesReceived = bytesReceived;
            
            // Collect any additional fields not explicitly mapped
            foreach (var kvp in fieldMap)
            {
                if (!_knownFields.Contains(kvp.Key) && kvp.Value < parts.Length)
                {
                    var value = GetFieldValue(parts, kvp.Key, fieldMap);
                    if (!string.IsNullOrEmpty(value) && value != "-")
                    {
                        entry.AdditionalFields[kvp.Key] = value;
                    }
                }
            }
            
            return entry;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse log line: {Line}", line);
            return null;
        }
    }
    
    /// <summary>
    /// Get field value from parts array using field name
    /// </summary>
    private string? GetFieldValue(string[] parts, string fieldName, Dictionary<string, int> fieldMap)
    {
        if (fieldMap.TryGetValue(fieldName.ToLowerInvariant(), out var index) && index < parts.Length)
        {
            var value = parts[index];
            // IIS uses "-" for empty/null values
            return value == "-" ? null : value;
        }
        return null;
    }
    
    // Overload for backward compatibility (used in loop above)
    // Actually the loop uses GetFieldValue(parts, kvp.Key, fieldMap) now.
    // So this overload is not needed if I fix the call.
    
    /// <summary>
    /// Get all log files in a directory path (supports wildcards)
    /// </summary>
    public List<string> GetLogFiles(string pathPattern)
    {
        var result = new List<string>();
        
        if (pathPattern.Contains('*') || pathPattern.Contains('?'))
        {
            var directory = Path.GetDirectoryName(pathPattern) ?? ".";
            var pattern = Path.GetFileName(pathPattern);
            
            if (Directory.Exists(directory))
            {
                // For patterns like W3SVC*, we need to find matching directories first
                if (pattern.Contains('*') || pattern.Contains('?'))
                {
                    foreach (var dir in Directory.GetDirectories(directory, pattern))
                    {
                        // Get all .log files in matching directories
                        result.AddRange(Directory.GetFiles(dir, "*.log", SearchOption.TopDirectoryOnly)
                            .OrderBy(f => f));
                    }
                }
            }
        }
        else if (Directory.Exists(pathPattern))
        {
            // Direct directory path - get all .log files
            var topLevel = Directory.GetFiles(pathPattern, "*.log", SearchOption.TopDirectoryOnly);
            if (topLevel.Length > 0)
            {
                result.AddRange(topLevel.OrderBy(f => f));
            }
            else
            {
                // No .log files at top level - scan subdirectories (e.g. W3SVC1, W3SVC2)
                result.AddRange(Directory.GetFiles(pathPattern, "*.log", SearchOption.AllDirectories)
                    .OrderBy(f => f));
            }
        }
        else if (File.Exists(pathPattern))
        {
            // Direct file path
            result.Add(pathPattern);
        }
        
        return result;
    }
}
