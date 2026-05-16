namespace com.logdb.docker.collector.Configuration;

public class LogDbExporterOptions
{
    public const string Section = "LogDbExporter";

    public bool Enabled { get; set; } = true;
    public string Endpoint { get; set; } = "http://localhost:5000";
    public string ApiKey { get; set; } = "";
    public string Collection { get; set; } = "docker-logs";
    public string Protocol { get; set; } = "Native"; // Native (gRPC) or REST
    public int FlushIntervalSeconds { get; set; } = 5;
    public int MaxBatchRecords { get; set; } = 50_000;
    public int MaxBatchBytes { get; set; } = 1_048_576; // 1 MB
    public int MaxRetries { get; set; } = 3;
    public bool EnableCompression { get; set; } = true;
    public int RequestTimeoutSeconds { get; set; } = 30;
}
