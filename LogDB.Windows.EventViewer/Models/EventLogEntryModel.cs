using System.Diagnostics;

namespace com.logdb.windows.eventviewer.Models;

public class EventLogEntryModel
{
    public long Index { get; set; }
    public DateTime TimeGenerated { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public EventLogEntryType EntryType { get; set; }
    public int EventID { get; set; }
    public string? Category { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string? UserName { get; set; }
    public string Log { get; set; } = string.Empty;
    public string? IP { get; set; }
}

