using com.logdb.docker.collector.Configuration;
using com.logdb.docker.collector.Models;
using com.logdb.docker.collector.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace com.logdb.docker.collector.tests;

public class SpoolReplayChunkedDrainTests : IDisposable
{
    private readonly string _tempDir;

    public SpoolReplayChunkedDrainTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "docker-collector-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // Records the size of each send and starts failing after a configured number of
    // successful sends, so the test can prove the worker commits the slices that landed
    // and leaves the rest spooled.
    private sealed class ChunkRecordingExporter : ILogDbExporter
    {
        private readonly int _failAfterChunks;
        public List<int> ChunkSizes { get; } = new();
        public ChunkRecordingExporter(int failAfterChunks) => _failAfterChunks = failAfterChunks;

        public Task<bool> SendBatchAsync(List<LogRecord> batch, CancellationToken cancellationToken = default)
        {
            if (ChunkSizes.Count >= _failAfterChunks) return Task.FromResult(false);
            ChunkSizes.Add(batch.Count);
            return Task.FromResult(true);
        }

        public Task<bool> SendMetricsBatchAsync(List<DockerMetricsRecord> batch, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public ExporterStatus GetStatus() => new();
        public void SetEnabled(bool enabled) { }
    }

    [Fact]
    public async Task DrainChunked_CommitsLandedChunksWhenLaterSendFails()
    {
        var spoolOpts = Options.Create(new SpoolOptions
        {
            Enabled = true,
            DirectoryPath = _tempDir,
            MaxDiskBytes = 10_485_760,
            MaxSegmentBytes = 1_048_576,
            ReplayBatchSize = 50_000,
            SendChunkSize = 5
        });
        var spool = new FileSpoolStore(NullLogger<FileSpoolStore>.Instance, spoolOpts);
        spool.Initialize();

        for (int i = 0; i < 12; i++)
            spool.Append(new LogRecord
            {
                Timestamp = new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i),
                Message = $"line {i}",
                Stream = "stdout",
                ContainerName = "app"
            });

        var exporter = new ChunkRecordingExporter(failAfterChunks: 2); // 3rd send fails
        var worker = new SpoolReplayWorker(spool, exporter, NullLogger<SpoolReplayWorker>.Instance, spoolOpts);

        var batch = spool.ReadBatch(spoolOpts.Value.ReplayBatchSize);
        Assert.Equal(12, batch.Count);

        var committed = await worker.DrainChunkedAsync(batch, CancellationToken.None);

        Assert.Equal(10, committed);                       // first two 5-record slices landed
        Assert.Equal(new[] { 5, 5 }, exporter.ChunkSizes); // bounded to SendChunkSize; stopped on failure
        Assert.Equal(2, spool.GetStatus().QueuedRecords);  // remainder kept for next cycle
    }
}
