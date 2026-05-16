namespace com.logdb.nginx.collector.Configuration;

public class TailOptions
{
    public const string Section = "Tail";

    /// <summary>
    /// Polling interval (seconds) when the previous cycle found new log data.
    /// </summary>
    public int ActiveIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Polling interval (seconds) when the previous cycle found no new data.
    /// Backs off to reduce CPU when log files are quiet.
    /// </summary>
    public int IdleIntervalSeconds { get; set; } = 15;
}
