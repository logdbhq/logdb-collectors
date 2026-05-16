using com.logdb.docker.collector.Models;

namespace com.logdb.docker.collector.Services;

public interface ILogDbExporter
{
    Task<bool> SendBatchAsync(List<LogRecord> batch, CancellationToken cancellationToken = default);
    Task<bool> SendMetricsBatchAsync(List<DockerMetricsRecord> batch, CancellationToken cancellationToken = default);
    ExporterStatus GetStatus();
    void SetEnabled(bool enabled);
}
