using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using com.logdb.windows.iis.Models;
using Microsoft.Extensions.Logging;

namespace com.logdb.windows.iis.Services;

/// <summary>
/// Reads and parses Azure App Service HTTP logs in NDJSON format.
/// Handles the time-partitioned directory structure: PREFIX_d=DD/h=HH/m=MM/PT1H.json
/// </summary>
public class AzureAppServiceJsonReader
{
    private readonly ILogger<AzureAppServiceJsonReader>? _logger;

    // Regex to extract day/hour/minute from directory path components
    private static readonly Regex DayPattern = new(@"_d=(\d{2})$", RegexOptions.Compiled);
    private static readonly Regex HourPattern = new(@"^h=(\d{2})$", RegexOptions.Compiled);
    private static readonly Regex MinutePattern = new(@"^m=(\d{2})$", RegexOptions.Compiled);

    public AzureAppServiceJsonReader(ILogger<AzureAppServiceJsonReader>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Read all NDJSON log entries from a file.
    /// Returns entries compatible with the IISLogReader pattern (position 0, int.MaxValue = read all).
    /// The NewPosition and CurrentFieldMap are returned for API compatibility but not used for Azure JSON.
    /// </summary>
    public async Task<(List<IISLogEntry> Entries, long NewPosition, Dictionary<string, int> CurrentFieldMap)> ReadEntriesAsync(
        string filePath,
        long startPosition,
        int maxEntries,
        Dictionary<string, int>? existingFieldMap = null,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<IISLogEntry>();
        var emptyFieldMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(filePath))
        {
            _logger?.LogWarning("JSON log file not found: {FilePath}", filePath);
            return (entries, 0, emptyFieldMap);
        }

        try
        {
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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

                var entry = ParseJsonLine(line, filePath, lineNumber);
                if (entry != null)
                {
                    entries.Add(entry);
                    entryCount++;
                }
            }

            return (entries, fileStream.Position, emptyFieldMap);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error reading JSON log file: {FilePath}", filePath);
            return (entries, 0, emptyFieldMap);
        }
    }

    /// <summary>
    /// Parse a single NDJSON line into an IISLogEntry.
    /// </summary>
    private IISLogEntry? ParseJsonLine(string line, string sourceFile, long lineNumber)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var entry = new IISLogEntry
            {
                SourceFile = sourceFile,
                LineNumber = lineNumber
            };

            // Parse timestamp from top-level "time" field
            if (root.TryGetProperty("time", out var timeElem))
            {
                if (DateTime.TryParse(timeElem.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
                {
                    entry.Timestamp = timestamp;
                }
            }

            // Extract site name from resourceId
            // e.g. ".../MICROSOFT.WEB/SITES/OPTIMIDOCCLOUD-WEBAPP-UK" -> "OPTIMIDOCCLOUD-WEBAPP-UK"
            if (root.TryGetProperty("resourceId", out var resourceElem))
            {
                var resourceId = resourceElem.GetString();
                if (!string.IsNullOrEmpty(resourceId))
                {
                    var sitesIdx = resourceId.IndexOf("/SITES/", StringComparison.OrdinalIgnoreCase);
                    if (sitesIdx >= 0)
                    {
                        entry.SiteName = resourceId.Substring(sitesIdx + "/SITES/".Length);
                    }
                }
            }

            // Parse properties object
            if (root.TryGetProperty("properties", out var props))
            {
                entry.Method = GetJsonString(props, "CsMethod");
                entry.UriStem = GetJsonString(props, "CsUriStem");
                entry.UriQuery = GetJsonString(props, "CsUriQuery");
                entry.ClientIp = GetJsonString(props, "CIp");
                entry.UserAgent = GetJsonString(props, "UserAgent");
                entry.Host = GetJsonString(props, "CsHost");
                entry.Username = GetJsonString(props, "CsUsername");
                entry.Referer = GetJsonString(props, "Referer");
                entry.Cookie = GetJsonString(props, "Cookie");
                entry.ServerName = GetJsonString(props, "ComputerName");

                // Port - can be string or number in Azure JSON
                entry.ServerPort = GetJsonInt(props, "SPort");

                // Status codes - ScStatus is typically a number, Sub/Win32 are strings
                entry.StatusCode = GetJsonInt(props, "ScStatus");
                entry.SubStatus = GetJsonInt(props, "ScSubStatus");
                entry.Win32Status = GetJsonInt(props, "ScWin32Status");

                // Bytes and time
                entry.BytesSent = GetJsonLong(props, "ScBytes");
                entry.BytesReceived = GetJsonLong(props, "CsBytes");
                entry.TimeTaken = GetJsonInt(props, "TimeTaken");

                // Additional fields not in standard IISLogEntry
                var result = GetJsonString(props, "Result");
                if (!string.IsNullOrEmpty(result))
                    entry.AdditionalFields["result"] = result;
            }

            // Store category as additional field if present
            if (root.TryGetProperty("category", out var categoryElem))
            {
                var category = categoryElem.GetString();
                if (!string.IsNullOrEmpty(category))
                    entry.AdditionalFields["category"] = category;
            }

            return entry;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Failed to parse JSON log line #{LineNumber} in {File}", lineNumber, sourceFile);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Unexpected error parsing line #{LineNumber} in {File}", lineNumber, sourceFile);
            return null;
        }
    }

    /// <summary>
    /// Get all JSON log files from a path, handling the Azure time-partitioned directory structure.
    /// Supports: direct file, flat directory with *.json, and nested d=/h=/m= structure.
    /// Files are returned in chronological order based on directory names.
    /// </summary>
    /// <param name="pathPattern">Path to scan for JSON log files</param>
    /// <param name="strictDetection">When true (Auto mode), only match Azure _d=/h=/m= structure.
    /// When false (explicit AzureJson mode), also do flat *.json fallback.</param>
    public List<string> GetJsonLogFiles(string pathPattern, bool strictDetection = false)
    {
        var result = new List<(string Path, int Day, int Hour, int Minute)>();

        if (File.Exists(pathPattern))
        {
            return new List<string> { pathPattern };
        }

        if (!Directory.Exists(pathPattern))
        {
            // Only warn when explicitly configured as AzureJson (not during auto-detection)
            if (!strictDetection)
                _logger?.LogWarning("Azure JSON log path not found: {Path}", pathPattern);
            return new List<string>();
        }

        // Look for the Azure time-partitioned structure: *_d=DD/h=HH/m=MM/PT1H.json
        var dayDirs = Directory.GetDirectories(pathPattern)
            .Select(d => new { Path = d, Name = Path.GetFileName(d), Match = DayPattern.Match(Path.GetFileName(d)) })
            .Where(d => d.Match.Success)
            .ToList();

        if (dayDirs.Any())
        {
            foreach (var dayDir in dayDirs)
            {
                var day = int.Parse(dayDir.Match.Groups[1].Value);

                var hourDirs = Directory.GetDirectories(dayDir.Path)
                    .Select(d => new { Path = d, Name = Path.GetFileName(d), Match = HourPattern.Match(Path.GetFileName(d)) })
                    .Where(d => d.Match.Success)
                    .ToList();

                foreach (var hourDir in hourDirs)
                {
                    var hour = int.Parse(hourDir.Match.Groups[1].Value);

                    var minuteDirs = Directory.GetDirectories(hourDir.Path)
                        .Select(d => new { Path = d, Name = Path.GetFileName(d), Match = MinutePattern.Match(Path.GetFileName(d)) })
                        .Where(d => d.Match.Success)
                        .ToList();

                    if (minuteDirs.Any())
                    {
                        foreach (var minuteDir in minuteDirs)
                        {
                            var minute = int.Parse(minuteDir.Match.Groups[1].Value);
                            foreach (var jsonFile in Directory.GetFiles(minuteDir.Path, "*.json"))
                            {
                                result.Add((jsonFile, day, hour, minute));
                            }
                        }
                    }
                    else
                    {
                        // No minute directories - check for JSON files directly in hour dir
                        foreach (var jsonFile in Directory.GetFiles(hourDir.Path, "*.json"))
                        {
                            result.Add((jsonFile, day, hour, 0));
                        }
                    }
                }
            }

            return result
                .OrderBy(r => r.Day)
                .ThenBy(r => r.Hour)
                .ThenBy(r => r.Minute)
                .ThenBy(r => r.Path)
                .Select(r => r.Path)
                .ToList();
        }

        // No time-partitioned structure found.
        // In strict mode (Auto-detection), don't fall back to flat scan - could misidentify W3C dirs.
        if (strictDetection)
            return new List<string>();

        // Explicit AzureJson mode: fall back to flat directory scan
        var flatFiles = Directory.GetFiles(pathPattern, "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f)
            .ToList();

        if (flatFiles.Any())
        {
            _logger?.LogDebug("Found {Count} JSON files in flat directory scan: {Path}", flatFiles.Count, pathPattern);
        }

        return flatFiles;
    }

    #region JSON Helpers

    private static string? GetJsonString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var elem))
            return null;

        var value = elem.ValueKind switch
        {
            JsonValueKind.String => elem.GetString(),
            JsonValueKind.Number => elem.GetRawText(),
            _ => null
        };

        return value == "-" ? null : value;
    }

    private static int GetJsonInt(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var elem))
            return 0;

        return elem.ValueKind switch
        {
            JsonValueKind.Number => elem.GetInt32(),
            JsonValueKind.String when int.TryParse(elem.GetString(), out var val) => val,
            _ => 0
        };
    }

    private static long GetJsonLong(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var elem))
            return 0;

        return elem.ValueKind switch
        {
            JsonValueKind.Number => elem.GetInt64(),
            JsonValueKind.String when long.TryParse(elem.GetString(), out var val) => val,
            _ => 0
        };
    }

    #endregion
}
