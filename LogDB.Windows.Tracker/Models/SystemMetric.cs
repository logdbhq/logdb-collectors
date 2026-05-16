namespace com.logdb.windows.tracker.Models;

/// <summary>
/// Represents a collected system metric point.
/// </summary>
public class SystemMetric
{
    /// <summary>
    /// The metric type: "cpu", "memory", "disk", "network", "process"
    /// </summary>
    public string Measurement { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when the metric was collected
    /// </summary>
    public DateTime Time { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Key-value metadata (server_name, environment, drive_letter, etc.)
    /// </summary>
    public Dictionary<string, string> Tags { get; set; } = new();

    /// <summary>
    /// Numeric values (usage_percent, free_gb, total_gb, etc.)
    /// </summary>
    public Dictionary<string, double> Fields { get; set; } = new();
}
