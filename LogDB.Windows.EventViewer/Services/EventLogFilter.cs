using System.Diagnostics;
using System.Text.Json;
using com.logdb.windows.eventviewer.Models;
using Microsoft.Extensions.Logging;

namespace com.logdb.windows.eventviewer.Services;

public class EventLogFilter
{
    private readonly ILogger<EventLogFilter>? _logger;

    public EventLogFilter(ILogger<EventLogFilter>? logger = null)
    {
        _logger = logger;
    }

    public bool MatchesFilter(EventLogEntryModel entry, string? filterJson)
    {
        if (string.IsNullOrWhiteSpace(filterJson) || filterJson == "{}")
            return true;

        try
        {
            var filter = JsonSerializer.Deserialize<EventFilter>(filterJson);

            if (filter == null)
                return true;

            // Event ID filters
            if (filter.EventIds?.Any() == true && !filter.EventIds.Contains(entry.EventID))
                return false;

            if (filter.ExcludeEventIds?.Contains(entry.EventID) == true)
                return false;

            // Event ID range filters
            if (filter.MinEventId.HasValue && entry.EventID < filter.MinEventId.Value)
                return false;

            if (filter.MaxEventId.HasValue && entry.EventID > filter.MaxEventId.Value)
                return false;

            // Source filters
            if (filter.SourceContains?.Any() == true)
            {
                if (!filter.SourceContains.Any(s => 
                    entry.Source.Contains(s, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }

            // Message filters
            if (filter.MessageContains?.Any() == true)
            {
                if (!filter.MessageContains.Any(m => 
                    (entry.Message ?? "").Contains(m, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }

            // Exclude keywords
            if (filter.ExcludeKeywords?.Any() == true)
            {
                if (filter.ExcludeKeywords.Any(k => 
                    (entry.Message ?? "").Contains(k, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error parsing filter JSON: {FilterJson}", filterJson);
            return true; // Include by default if filter is invalid
        }
    }
}

public class EventFilter
{
    public List<int>? EventIds { get; set; }
    public List<int>? ExcludeEventIds { get; set; }
    public int? MinEventId { get; set; }
    public int? MaxEventId { get; set; }
    public List<string>? SourceContains { get; set; }
    public List<string>? MessageContains { get; set; }
    public List<string>? ExcludeKeywords { get; set; }
}





