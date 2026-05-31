using com.logdb.docker.collector.Configuration;
using com.logdb.docker.collector.Models;
using com.logdb.docker.collector.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace com.logdb.docker.collector.tests;

public class MetricsSpoolStoreTests : IDisposable
{
    private readonly string _tempDir;

    public MetricsSpoolStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "docker-collector-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private MetricsSpoolStore CreateStore(int maxRecords = 200_000)
    {
        var opts = Options.Create(new SpoolOptions
        {
            Enabled = true,
            DirectoryPath = _tempDir,
            MetricsMaxRecords = maxRecords
        });
        var store = new MetricsSpoolStore(NullLogger<MetricsSpoolStore>.Instance, opts);
        store.Initialize();
        return store;
    }

    private static List<DockerMetricsRecord> Records(int count, string namePrefix = "c")
    {
        var list = new List<DockerMetricsRecord>(count);
        for (int i = 0; i < count; i++)
            list.Add(new DockerMetricsRecord
            {
                Timestamp = new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i),
                ContainerName = $"{namePrefix}{i}",
                CpuUsagePercent = i
            });
        return list;
    }

    [Fact]
    public void AppendThenReadBatch_ReturnsRecordsInFifoOrder()
    {
        var store = CreateStore();
        store.Append(Records(3));

        var batch = store.ReadBatch(100);

        Assert.Equal(3, batch.Count);
        Assert.Equal(new[] { "c0", "c1", "c2" }, batch.Select(r => r.ContainerName));
        Assert.Equal(3, store.QueuedRecords);
    }

    [Fact]
    public void CommitBatch_RemovesExactlyThatPrefix()
    {
        var store = CreateStore();
        store.Append(Records(5));

        var first = store.ReadBatch(2);
        Assert.Equal(new[] { "c0", "c1" }, first.Select(r => r.ContainerName));

        store.CommitBatch(first.Count);
        Assert.Equal(3, store.QueuedRecords);

        var rest = store.ReadBatch(100);
        Assert.Equal(new[] { "c2", "c3", "c4" }, rest.Select(r => r.ContainerName));
    }

    [Fact]
    public void Append_OverCap_DropsOldest()
    {
        var store = CreateStore(maxRecords: 4);
        store.Append(Records(6)); // c0..c5; cap 4 keeps the newest 4

        Assert.Equal(4, store.QueuedRecords);
        Assert.Equal(2, store.DroppedRecords);

        var batch = store.ReadBatch(100);
        Assert.Equal(new[] { "c2", "c3", "c4", "c5" }, batch.Select(r => r.ContainerName));
    }

    [Fact]
    public void Drain_FullThenEmpty()
    {
        var store = CreateStore();
        store.Append(Records(3));

        var batch = store.ReadBatch(100);
        store.CommitBatch(batch.Count);

        Assert.Equal(0, store.QueuedRecords);
        Assert.Empty(store.ReadBatch(100));
    }

    [Fact]
    public void QueuedRecords_SurvivesRestart()
    {
        var first = CreateStore();
        first.Append(Records(4));
        first.CommitBatch(1); // leave 3 queued on disk

        // A fresh instance over the same directory must recover the queued count.
        var reopened = CreateStore();
        Assert.Equal(3, reopened.QueuedRecords);
        Assert.Equal(new[] { "c1", "c2", "c3" }, reopened.ReadBatch(100).Select(r => r.ContainerName));
    }
}
