namespace com.logdb.nginx.collector.Models;

public class ExporterStatus
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "";
    public bool ApiKeyConfigured { get; set; }
    public bool Healthy { get; set; }
    public long BatchesSent { get; set; }
    public long RecordsSent { get; set; }
    public long SendErrors { get; set; }
    public long RetryCount { get; set; }
    public DateTime? LastSendUtc { get; set; }
    public string? LastError { get; set; }
    public int FlushIntervalSeconds { get; set; }
    public int FlushIntervalMinSeconds { get; set; }
    public int FlushIntervalMaxSeconds { get; set; }
}
