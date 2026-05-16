namespace com.logdb.docker.collector.Models;

public class ToggleAllRequest
{
    public bool Enabled { get; set; }
}

public class LogModeAllRequest
{
    public string LogMode { get; set; } = "all";
}
