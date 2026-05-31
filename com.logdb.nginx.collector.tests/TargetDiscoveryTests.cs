using com.logdb.nginx.collector.Configuration;
using com.logdb.nginx.collector.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace com.logdb.nginx.collector.tests;

public class TargetDiscoveryTests : IDisposable
{
    private readonly string _logDir;
    private readonly string _stateDir;
    private readonly string _checkpointFile;

    public TargetDiscoveryTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "nginx-discovery-tests", Guid.NewGuid().ToString("N"));
        _logDir = Path.Combine(root, "log");
        _stateDir = Path.Combine(root, "state");
        Directory.CreateDirectory(_logDir);
        Directory.CreateDirectory(_stateDir);
        _checkpointFile = Path.Combine(_stateDir, "checkpoints.json");
    }

    private TargetDiscoveryService Build(DiscoveryOptions opts)
    {
        var checkpointOpts = Options.Create(new CheckpointOptions { FilePath = _checkpointFile });
        return new TargetDiscoveryService(NullLogger<TargetDiscoveryService>.Instance, Options.Create(opts), checkpointOpts);
    }

    private void Touch(string fileName) => File.WriteAllText(Path.Combine(_logDir, fileName), "x");

    [Fact]
    public void Discovery_Disabled_ReturnsNothing()
    {
        Touch("prod-01.motivp.com.access.log");
        var svc = Build(new DiscoveryOptions { Enabled = false, Directory = _logDir });

        Assert.Empty(svc.DiscoverTargets(new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Discovery_MatchesAccessAndErrorLogs_ByPattern()
    {
        Touch("prod-01.motivp.com.access.log");
        Touch("player.motivp.com.ssl.access.log");
        Touch("auth.motivp.com.error.log");
        Touch("something-else.txt");          // not a .log
        Touch("access.log.1");                // rotated -> excluded by *.[0-9]
        Touch("access.log.2.gz");             // compressed -> excluded by *.gz

        var svc = Build(new DiscoveryOptions { Enabled = true, Directory = _logDir });
        var targets = svc.DiscoverTargets(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(3, targets.Count);
        Assert.Contains(targets, t => t.AccessLogPath.EndsWith("prod-01.motivp.com.access.log"));
        Assert.Contains(targets, t => t.AccessLogPath.EndsWith("player.motivp.com.ssl.access.log"));
        Assert.Contains(targets, t => t.ErrorLogPath.EndsWith("auth.motivp.com.error.log"));
        // rotated/compressed/non-log files are not picked up
        Assert.DoesNotContain(targets, t =>
            t.AccessLogPath.Contains(".log.1") || t.AccessLogPath.EndsWith(".gz") || t.AccessLogPath.EndsWith(".txt"));
    }

    [Fact]
    public void Discovery_SkipsFilesAlreadyTrackedByExplicitTargets()
    {
        Touch("access.log");
        Touch("vac.motivp.com.access.log");

        var svc = Build(new DiscoveryOptions { Enabled = true, Directory = _logDir });
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(Path.Combine(_logDir, "access.log"))
        };

        var targets = svc.DiscoverTargets(tracked);

        Assert.Single(targets);
        Assert.EndsWith("vac.motivp.com.access.log", targets[0].AccessLogPath);
    }

    [Fact]
    public void Settings_PersistAcrossInstances()
    {
        var svc = Build(new DiscoveryOptions { Enabled = false, Directory = _logDir });
        svc.UpdateSettings(new DiscoveryOptions
        {
            Enabled = true,
            Directory = _logDir,
            AccessLogPatterns = new() { "*.custom.log" },
            StartAtEnd = false
        });

        // A fresh instance must load the persisted state, not the seed defaults.
        var reloaded = Build(new DiscoveryOptions { Enabled = false, StartAtEnd = true });
        var settings = reloaded.GetSettings();

        Assert.True(settings.Enabled);
        Assert.False(settings.StartAtEnd);
        Assert.Contains("*.custom.log", settings.AccessLogPatterns);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_logDir)!, recursive: true); } catch { }
    }
}
