namespace com.logdb.docker.collector.Models;

public class ReadinessResult
{
    public bool Ready { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}
