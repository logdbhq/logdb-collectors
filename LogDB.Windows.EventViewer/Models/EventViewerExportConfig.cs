namespace com.logdb.windows.eventviewer.Models;

/// <summary>
/// Local configuration for EventViewer log export, read from appsettings.json.
/// Replaces the remote event_viewer_config database model.
/// </summary>
public class EventViewerExportConfig
{
    /// <summary>Event log sources to monitor (e.g. "System", "Application", "Security")</summary>
    public List<string> LogSources { get; set; } = new() { "System", "Application" };

    /// <summary>Event levels to capture (error, warning, information, successaudit, failureaudit)</summary>
    public List<string> EventLevels { get; set; } = new() { "error", "warning", "information" };

    /// <summary>Export cycle interval in minutes</summary>
    public int ExportIntervalMinutes { get; set; } = 1;

    /// <summary>Max events to read per source per cycle</summary>
    public int MaxEventsPerExport { get; set; } = 1000;

    /// <summary>Application name in LogDB</summary>
    public string ApplicationName { get; set; } = "Windows Event Viewer";

    /// <summary>Collection name in LogDB (if empty, auto-generates per source e.g. windows-eventlog-system)</summary>
    public string? Collection { get; set; }

    /// <summary>Map log source names to specific LogDB collections</summary>
    public Dictionary<string, string>? CollectionMap { get; set; }

    /// <summary>Labels attached to each log entry</summary>
    public List<string> Labels { get; set; } = new() { "event-viewer", "windows" };

    /// <summary>Include XML event details in attributes</summary>
    public bool IncludeXmlDetails { get; set; }

    /// <summary>Advanced filter conditions as JSON (eventIds, excludeEventIds, sourceContains, etc.)</summary>
    public string? FilterConditions { get; set; }

    // --- Source filters ---

    /// <summary>Only process these log sources (empty = use LogSources list)</summary>
    public List<string>? IncludeSources { get; set; }

    /// <summary>Skip these log sources</summary>
    public List<string>? ExcludeSources { get; set; }

    /// <summary>
    /// Optional Provider (Source) name override. When set, every emitted event
    /// uses this value instead of the original Windows event Source. Used to
    /// tag which server / collector instance the logs are coming from.
    /// </summary>
    public string? ProviderNameOverride { get; set; }
}
