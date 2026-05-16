using System.Text.Json;
using com.logdb.windows.iis.Models;
using Microsoft.Extensions.Logging;

namespace com.logdb.windows.iis.Services;

/// <summary>
/// Filters IIS log entries based on configuration criteria.
/// </summary>
public class IISLogFilter
{
    private readonly ILogger<IISLogFilter>? _logger;
    private readonly Dictionary<string, AdvancedFilter?> _filterCache = new();

    public IISLogFilter(ILogger<IISLogFilter>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Filter log entries based on configuration.
    /// </summary>
    public List<IISLogEntry> FilterEntries(List<IISLogEntry> entries, IISExportConfig config)
    {
        return entries.Where(e => ShouldIncludeEntry(e, config)).ToList();
    }

    /// <summary>
    /// Check if a single entry should be included based on configuration.
    /// </summary>
    public bool ShouldIncludeEntry(IISLogEntry entry, IISExportConfig config)
    {
        // Filter by status codes (include list)
        if (config.IncludeStatusCodes?.Any() == true)
        {
            if (!config.IncludeStatusCodes.Contains(entry.StatusCode))
                return false;
        }

        // Filter by status codes (exclude list)
        if (config.ExcludeStatusCodes?.Any() == true)
        {
            if (config.ExcludeStatusCodes.Contains(entry.StatusCode))
                return false;
        }

        // Filter by file extensions
        if (config.ExcludeExtensions?.Any() == true && !string.IsNullOrEmpty(entry.UriStem))
        {
            var extension = Path.GetExtension(entry.UriStem)?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(extension) &&
                config.ExcludeExtensions.Any(ext => ext.Equals(extension, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        // Filter by URL paths
        if (config.ExcludePaths?.Any() == true && !string.IsNullOrEmpty(entry.UriStem))
        {
            if (config.ExcludePaths.Any(path =>
                entry.UriStem.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                entry.UriStem.StartsWith(path, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        // Filter by site names
        if (config.SiteNames?.Any() == true && !string.IsNullOrEmpty(entry.SiteName))
        {
            if (!config.SiteNames.Any(s =>
                s.Equals(entry.SiteName, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        // Apply advanced filter conditions (JSON) - deserialized once and cached
        if (!string.IsNullOrEmpty(config.FilterConditions) && config.FilterConditions != "{}")
        {
            var advancedFilter = GetOrParseAdvancedFilter(config.FilterConditions);
            if (advancedFilter != null && !ApplyAdvancedFilters(entry, advancedFilter))
                return false;
        }

        return true;
    }

    private AdvancedFilter? GetOrParseAdvancedFilter(string filterJson)
    {
        if (_filterCache.TryGetValue(filterJson, out var cached))
            return cached;

        try
        {
            var filter = JsonSerializer.Deserialize<AdvancedFilter>(filterJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            _filterCache[filterJson] = filter;
            return filter;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse advanced filter JSON: {FilterJson}", filterJson);
            _filterCache[filterJson] = null;
            return null;
        }
    }

    private bool ApplyAdvancedFilters(IISLogEntry entry, AdvancedFilter filter)
    {
        if (filter.MinTimeTaken.HasValue && entry.TimeTaken < filter.MinTimeTaken.Value)
            return false;

        if (filter.MaxTimeTaken.HasValue && entry.TimeTaken > filter.MaxTimeTaken.Value)
            return false;

        if (filter.IncludeUserAgents?.Any() == true && !string.IsNullOrEmpty(entry.UserAgent))
        {
            if (!filter.IncludeUserAgents.Any(ua => MatchWildcard(entry.UserAgent, ua)))
                return false;
        }

        if (filter.ExcludeUserAgents?.Any() == true && !string.IsNullOrEmpty(entry.UserAgent))
        {
            if (filter.ExcludeUserAgents.Any(ua => MatchWildcard(entry.UserAgent, ua)))
                return false;
        }

        if (filter.ExcludeIps?.Any() == true && !string.IsNullOrEmpty(entry.ClientIp))
        {
            if (filter.ExcludeIps.Contains(entry.ClientIp))
                return false;
        }

        if (filter.ExcludeIpRanges?.Any() == true && !string.IsNullOrEmpty(entry.ClientIp))
        {
            if (filter.ExcludeIpRanges.Any(range => entry.ClientIp.StartsWith(range)))
                return false;
        }

        if (filter.IncludeMethods?.Any() == true && !string.IsNullOrEmpty(entry.Method))
        {
            if (!filter.IncludeMethods.Contains(entry.Method, StringComparer.OrdinalIgnoreCase))
                return false;
        }

        if (filter.ExcludeMethods?.Any() == true && !string.IsNullOrEmpty(entry.Method))
        {
            if (filter.ExcludeMethods.Contains(entry.Method, StringComparer.OrdinalIgnoreCase))
                return false;
        }

        if (filter.UrlContains?.Any() == true && !string.IsNullOrEmpty(entry.UriStem))
        {
            if (!filter.UrlContains.Any(s => entry.UriStem.Contains(s, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Simple wildcard matching (* and ?)
    /// </summary>
    private bool MatchWildcard(string text, string pattern)
    {
        if (pattern == "*")
            return true;

        // Convert wildcard pattern to regex
        var regexPattern = "^" +
            System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") +
            "$";

        return System.Text.RegularExpressions.Regex.IsMatch(
            text, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Advanced filter options stored in FilterConditions JSON
    /// </summary>
    private class AdvancedFilter
    {
        public int? MinTimeTaken { get; set; }
        public int? MaxTimeTaken { get; set; }
        public List<string>? IncludeUserAgents { get; set; }
        public List<string>? ExcludeUserAgents { get; set; }
        public List<string>? ExcludeIps { get; set; }
        public List<string>? ExcludeIpRanges { get; set; }
        public List<string>? IncludeMethods { get; set; }
        public List<string>? ExcludeMethods { get; set; }
        public List<string>? UrlContains { get; set; }
    }
}
