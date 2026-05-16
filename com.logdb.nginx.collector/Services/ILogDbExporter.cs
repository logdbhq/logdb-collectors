using com.logdb.nginx.collector.Models;

namespace com.logdb.nginx.collector.Services;

public interface ILogDbExporter
{
    ExporterStatus GetStatus();
    void SetEnabled(bool enabled);
    int FlushIntervalSeconds { get; }
    void SetFlushIntervalSeconds(int seconds);
    Task<bool> SendBatchAsync(List<NginxLogRecord> batch, CancellationToken ct = default);
}
