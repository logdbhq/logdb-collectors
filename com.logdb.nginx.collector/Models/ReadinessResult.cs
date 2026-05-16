namespace com.logdb.nginx.collector.Models;

public class ReadinessResult
{
    public bool Ready { get; set; }
    public List<string> Errors { get; set; } = new();
}
