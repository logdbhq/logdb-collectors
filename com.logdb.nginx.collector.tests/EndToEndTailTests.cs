using com.logdb.nginx.collector.Configuration;
using com.logdb.nginx.collector.Models;
using com.logdb.nginx.collector.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace com.logdb.nginx.collector.tests;

public class EndToEndTailTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _accessLog;
    private readonly string _errorLog;
    private readonly string _checkpointFile;
    private readonly string _spoolDir;

    public EndToEndTailTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nginx-collector-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _accessLog = Path.Combine(_tempDir, "access.log");
        _errorLog = Path.Combine(_tempDir, "error.log");
        _checkpointFile = Path.Combine(_tempDir, "checkpoints.json");
        _spoolDir = Path.Combine(_tempDir, "spool");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private (NginxFileTailService tail, FileCheckpointStore checkpoint, FileSpoolStore spool, CollectingSink sink) CreateServices()
    {
        var targetOpts = Options.Create(new NginxTargetOptions
        {
            Targets = new List<NginxTarget>
            {
                new() { Name = "test", AccessLogPath = _accessLog, ErrorLogPath = _errorLog, Enabled = true }
            }
        });

        var checkpointOpts = Options.Create(new CheckpointOptions
        {
            Enabled = true,
            FilePath = _checkpointFile,
            FlushIntervalSeconds = 1
        });

        var spoolOpts = Options.Create(new SpoolOptions
        {
            Enabled = true,
            DirectoryPath = _spoolDir,
            MaxDiskBytes = 10_485_760,
            MaxSegmentBytes = 1_048_576
        });

        var checkpoint = new FileCheckpointStore(NullLogger<FileCheckpointStore>.Instance, checkpointOpts);
        checkpoint.Load();

        var spool = new FileSpoolStore(NullLogger<FileSpoolStore>.Instance, spoolOpts);
        spool.Initialize();

        var sink = new CollectingSink();
        var toggleService = new TargetToggleService(NullLogger<TargetToggleService>.Instance, checkpointOpts);
        var filterService = new FilterRuleService(NullLogger<FilterRuleService>.Instance, targetOpts, checkpointOpts);

        var tail = new NginxFileTailService(
            NullLogger<NginxFileTailService>.Instance,
            targetOpts,
            checkpoint,
            sink,
            toggleService,
            filterService);

        return (tail, checkpoint, spool, sink);
    }

    [Fact]
    public async Task TailAsync_ReadsAccessLog_ParsesRecords()
    {
        File.WriteAllText(_accessLog, string.Join("\n",
            @"10.0.0.1 - - [10/Mar/2026:14:22:01 +0000] ""GET / HTTP/1.1"" 200 512 ""-"" ""curl/7.88.1""",
            @"10.0.0.2 - - [10/Mar/2026:14:22:02 +0000] ""POST /api HTTP/1.1"" 201 64 ""-"" ""Python/3.11""",
            "") // trailing newline
        );

        var (tail, checkpoint, _, sink) = CreateServices();

        await tail.TailAsync();

        Assert.Equal(2, sink.Records.Count);
        Assert.Equal("GET", sink.Records[0].Method);
        Assert.Equal("POST", sink.Records[1].Method);
        Assert.Equal(NginxLogType.Access, sink.Records[0].LogType);
        Assert.Equal("test", sink.Records[0].TargetName);
        Assert.Equal(_accessLog, sink.Records[0].SourceFile);

        var status = tail.GetPipelineStatus();
        Assert.Equal(2, status.AccessRecordsRead);
        Assert.Equal(0, status.ErrorRecordsRead);
        Assert.Equal(0, status.ParseErrors);
    }

    [Fact]
    public async Task TailAsync_ReadsErrorLog_ParsesRecords()
    {
        File.WriteAllText(_errorLog, string.Join("\n",
            @"2026/03/10 14:22:01 [error] 1234#0: *99 open() ""/var/www/missing.html"" failed (2: No such file or directory)",
            @"2026/03/10 14:22:02 [warn] 1234#0: *100 upstream response buffered",
            "")
        );

        var (tail, _, _, sink) = CreateServices();

        await tail.TailAsync();

        Assert.Equal(2, sink.Records.Count);
        Assert.Equal("error", sink.Records[0].Severity);
        Assert.Equal("warn", sink.Records[1].Severity);
        Assert.Equal(NginxLogType.Error, sink.Records[0].LogType);

        var status = tail.GetPipelineStatus();
        Assert.Equal(0, status.AccessRecordsRead);
        Assert.Equal(2, status.ErrorRecordsRead);
    }

    [Fact]
    public async Task TailAsync_CheckpointPersistsOffset()
    {
        File.WriteAllText(_accessLog,
            @"10.0.0.1 - - [10/Mar/2026:14:22:01 +0000] ""GET / HTTP/1.1"" 200 512 ""-"" ""curl/7.88.1""" + "\n"
        );

        var (tail, checkpoint, _, sink) = CreateServices();

        await tail.TailAsync();
        Assert.Single(sink.Records);

        // Checkpoint should record the file position
        var cp = checkpoint.GetCheckpoints();
        Assert.Contains(cp, c => c.FilePath == _accessLog && c.Offset > 0);

        // Append more data
        File.AppendAllText(_accessLog,
            @"10.0.0.2 - - [10/Mar/2026:14:22:02 +0000] ""GET /page2 HTTP/1.1"" 200 256 ""-"" ""curl/7.88.1""" + "\n"
        );

        // Tail again - should only get the new record
        await tail.TailAsync();
        Assert.Equal(2, sink.Records.Count);
        Assert.Equal("/page2", sink.Records[1].Path);
    }

    [Fact]
    public async Task TailAsync_IncrementalReads_DoNotReprocessOldLines()
    {
        File.WriteAllText(_accessLog,
            @"10.0.0.1 - - [10/Mar/2026:14:22:01 +0000] ""GET /first HTTP/1.1"" 200 512 ""-"" ""curl/7.88.1""" + "\n"
        );

        var (tail, _, _, sink) = CreateServices();

        await tail.TailAsync();
        await tail.TailAsync();
        await tail.TailAsync();

        // Should still only have 1 record despite 3 tail cycles
        Assert.Single(sink.Records);
    }

    [Fact]
    public async Task TailAsync_CopyTruncateRotation_ResetsOffset()
    {
        // Write initial data
        File.WriteAllText(_accessLog,
            @"10.0.0.1 - - [10/Mar/2026:14:22:01 +0000] ""GET /before HTTP/1.1"" 200 512 ""-"" ""curl/7.88.1""" + "\n"
        );

        var (tail, _, _, sink) = CreateServices();
        await tail.TailAsync();
        Assert.Single(sink.Records);

        // Simulate copytruncate: file is truncated to zero, then new data written
        File.WriteAllText(_accessLog, ""); // truncate
        File.WriteAllText(_accessLog,
            @"10.0.0.2 - - [10/Mar/2026:14:23:01 +0000] ""GET /after HTTP/1.1"" 200 256 ""-"" ""curl/7.88.1""" + "\n"
        );

        await tail.TailAsync();
        Assert.Equal(2, sink.Records.Count);
        Assert.Equal("/after", sink.Records[1].Path);

        var status = tail.GetPipelineStatus();
        Assert.Equal(1, status.RotationsDetected);
    }

    [Fact]
    public async Task TailAsync_RenameCreateRotation_ResetsOffset()
    {
        // Write initial data
        File.WriteAllText(_accessLog,
            @"10.0.0.1 - - [10/Mar/2026:14:22:01 +0000] ""GET /old HTTP/1.1"" 200 512 ""-"" ""curl/7.88.1""" + "\n"
        );

        var (tail, _, _, sink) = CreateServices();
        await tail.TailAsync();
        Assert.Single(sink.Records);

        // Simulate rename/create rotation: delete old file, create new one
        // Explicitly set a newer creation time (Windows NTFS tunneling can
        // reuse the old creation time if the file is recreated quickly)
        File.Delete(_accessLog);
        File.WriteAllText(_accessLog,
            @"10.0.0.3 - - [10/Mar/2026:14:25:01 +0000] ""GET /new HTTP/1.1"" 200 128 ""-"" ""curl/7.88.1""" + "\n"
        );
        File.SetCreationTimeUtc(_accessLog, DateTime.UtcNow.AddMinutes(5));

        await tail.TailAsync();
        Assert.Equal(2, sink.Records.Count);
        Assert.Equal("/new", sink.Records[1].Path);
    }

    [Fact]
    public async Task TailAsync_MalformedLines_IncrementParseErrors()
    {
        File.WriteAllText(_accessLog, string.Join("\n",
            @"10.0.0.1 - - [10/Mar/2026:14:22:01 +0000] ""GET / HTTP/1.1"" 200 512 ""-"" ""curl/7.88.1""",
            "this is garbage",
            "another bad line",
            @"10.0.0.2 - - [10/Mar/2026:14:22:02 +0000] ""GET /ok HTTP/1.1"" 200 256 ""-"" ""curl/7.88.1""",
            "")
        );

        var (tail, _, _, sink) = CreateServices();
        await tail.TailAsync();

        Assert.Equal(2, sink.Records.Count);

        var status = tail.GetPipelineStatus();
        Assert.Equal(2, status.ParseErrors);
        Assert.Equal(2, status.AccessRecordsRead);
    }

    [Fact]
    public async Task TailAsync_MissingFile_DoesNotCrash()
    {
        // Don't create any files
        var (tail, _, _, sink) = CreateServices();

        await tail.TailAsync(); // should not throw

        Assert.Empty(sink.Records);
        var status = tail.GetPipelineStatus();
        Assert.Equal(0, status.ReadErrors);
    }

    [Fact]
    public async Task TailAsync_BothAccessAndError_ReadBothFiles()
    {
        File.WriteAllText(_accessLog,
            @"10.0.0.1 - - [10/Mar/2026:14:22:01 +0000] ""GET / HTTP/1.1"" 200 512 ""-"" ""curl/7.88.1""" + "\n"
        );
        File.WriteAllText(_errorLog,
            @"2026/03/10 14:22:01 [error] 1#0: *1 test error message" + "\n"
        );

        var (tail, _, _, sink) = CreateServices();
        await tail.TailAsync();

        Assert.Equal(2, sink.Records.Count);
        Assert.Contains(sink.Records, r => r.LogType == NginxLogType.Access);
        Assert.Contains(sink.Records, r => r.LogType == NginxLogType.Error);

        var status = tail.GetPipelineStatus();
        Assert.Equal(1, status.AccessRecordsRead);
        Assert.Equal(1, status.ErrorRecordsRead);
    }

    [Fact]
    public async Task TailAsync_GetTargets_ReflectsConfig()
    {
        var (tail, _, _, _) = CreateServices();

        var targets = tail.GetTargets();
        Assert.Equal(2, targets.Count); // access + error
        Assert.Contains(targets, t => t.LogType == NginxLogType.Access && t.FilePath == _accessLog);
        Assert.Contains(targets, t => t.LogType == NginxLogType.Error && t.FilePath == _errorLog);
    }

    [Fact]
    public void SpoolStore_AppendAndReadBatch()
    {
        var (_, _, spool, _) = CreateServices();

        var record = new NginxLogRecord
        {
            Timestamp = DateTime.UtcNow,
            Message = "test log line",
            LogType = NginxLogType.Access,
            TargetName = "test",
            Method = "GET",
            Path = "/test",
            StatusCode = 200
        };

        spool.Append(record);
        spool.Append(record);

        var status = spool.GetStatus();
        Assert.Equal(2, status.QueuedRecords);
        Assert.True(status.DiskBytesUsed > 0);
    }

    [Fact]
    public async Task CheckpointStore_FlushAndReload()
    {
        var opts = Options.Create(new CheckpointOptions
        {
            Enabled = true,
            FilePath = _checkpointFile,
            FlushIntervalSeconds = 1
        });

        var store1 = new FileCheckpointStore(NullLogger<FileCheckpointStore>.Instance, opts);
        store1.Load();
        store1.UpdateCheckpoint("/var/log/nginx/access.log", "default", 12345, 12345, DateTime.UtcNow);
        await store1.FlushAsync();

        // Load in a new instance
        var store2 = new FileCheckpointStore(NullLogger<FileCheckpointStore>.Instance, opts);
        store2.Load();

        Assert.Equal(12345, store2.GetOffset("/var/log/nginx/access.log"));
        var cps = store2.GetCheckpoints();
        Assert.Single(cps);
        Assert.Equal("default", cps[0].TargetName);
    }

    /// <summary>Simple sink that collects records in memory for testing.</summary>
    private class CollectingSink : ILogRecordSink
    {
        public List<NginxLogRecord> Records { get; } = new();
        public void Write(NginxLogRecord record)
        {
            lock (Records) Records.Add(record);
        }
        public void WriteBatch(IReadOnlyList<NginxLogRecord> records)
        {
            lock (Records) Records.AddRange(records);
        }
    }
}
