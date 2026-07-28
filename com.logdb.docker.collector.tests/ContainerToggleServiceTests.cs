using com.logdb.docker.collector.Configuration;
using com.logdb.docker.collector.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace com.logdb.docker.collector.tests;

public class ContainerToggleServiceTests : IDisposable
{
    private readonly string _tempDir;
    private const string Cid = "container-1";

    public ContainerToggleServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "docker-collector-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private ContainerToggleService Create()
    {
        var opts = Options.Create(new CheckpointOptions
        {
            FilePath = Path.Combine(_tempDir, "checkpoints.json")
        });
        return new ContainerToggleService(NullLogger<ContainerToggleService>.Instance, opts);
    }

    [Fact]
    public void Disabled_container_is_never_exported()
    {
        var svc = Create();
        Assert.False(svc.ShouldExport(Cid, "stderr", "Error"));
    }

    [Fact]
    public void All_mode_exports_everything()
    {
        var svc = Create();
        svc.SetEnabled(Cid, true); // defaults to LogMode.All
        Assert.True(svc.ShouldExport(Cid, "stdout", "Info"));
        Assert.True(svc.ShouldExport(Cid, "stderr", "Info"));
    }

    [Fact]
    public void ErrorsOnly_drops_info_even_on_stderr()
    {
        // The regression: Postgres/CrowdSec emit Info on stderr. ErrorsOnly must drop it.
        var svc = Create();
        svc.SetEnabled(Cid, true);
        svc.ToggleLogMode(Cid); // All -> ErrorsOnly
        Assert.False(svc.ShouldExport(Cid, "stderr", "Info"));
    }

    [Theory]
    [InlineData("Warning")]
    [InlineData("Error")]
    [InlineData("Critical")]
    public void ErrorsOnly_keeps_warning_and_above(string level)
    {
        var svc = Create();
        svc.SetEnabled(Cid, true);
        svc.ToggleLogMode(Cid);
        Assert.True(svc.ShouldExport(Cid, "stderr", level));
    }

    [Fact]
    public void ErrorsOnly_falls_back_to_stderr_when_level_unknown()
    {
        // No parsed level -> stream is the only signal; stderr is treated as error.
        var svc = Create();
        svc.SetEnabled(Cid, true);
        svc.ToggleLogMode(Cid);
        Assert.True(svc.ShouldExport(Cid, "stderr", parsedLevel: null));
        Assert.False(svc.ShouldExport(Cid, "stdout", parsedLevel: null));
    }
}
