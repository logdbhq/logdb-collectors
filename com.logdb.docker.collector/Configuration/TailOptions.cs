namespace com.logdb.docker.collector.Configuration;

public class TailOptions
{
    public const string Section = "Tail";

    /// <summary>
    /// Polling interval (seconds) when the previous cycle found new log data.
    /// </summary>
    public int ActiveIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Polling interval (seconds) when the previous cycle found no new data.
    /// Backs off to reduce CPU when containers are quiet.
    /// </summary>
    public int IdleIntervalSeconds { get; set; } = 15;
}
