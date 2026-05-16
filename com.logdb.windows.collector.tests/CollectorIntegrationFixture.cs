using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.shared.Services;

namespace com.logdb.windows.collector.tests;

public sealed class CollectorIntegrationFixture : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private const int CapturedOutputLimit = 1600;

    private Process? _process;
    private Task<string>? _stdoutTask;
    private Task<string>? _stderrTask;
    public string PipeName { get; } = $"LogDB.Windows.Collector.Control.Tests.{Guid.NewGuid():N}";
    public string BaseDirectory { get; } = Path.Combine(Path.GetTempPath(), "logdb-collector-tests", Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(BaseDirectory);

        Environment.SetEnvironmentVariable(CollectorPathDefaults.BaseDirectoryEnvironmentVariable, BaseDirectory);
        Environment.SetEnvironmentVariable("LOGDB_COLLECTOR_PIPE_NAME", PipeName);

        var configPath = Path.Combine(BaseDirectory, "appsettings.json");
        var config = new CollectorConfigDto
        {
            LogDB = new LogDbConfigDto
            {
                ApiKey = string.Empty,
                Endpoint = "https://localhost.invalid",
                DiscoveryUrl = string.Empty
            },
            Modules = new ModulesConfigDto
            {
                EventLog = new EventLogModuleConfigDto { Enabled = false },
                IIS = new IisModuleConfigDto { Enabled = false },
                Metrics = new MetricsModuleConfigDto { Enabled = false }
            },
            Firewall = new FirewallConfigDto { Enabled = false }
        };

        await CollectorConfigPersistence.SaveAsync(config, configPath);

        var repoRoot = ResolveRepoRoot();
        var collectorAssemblyPath = ResolveCollectorAssemblyPath(repoRoot);

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{collectorAssemblyPath}\" --console",
            WorkingDirectory = Path.GetDirectoryName(collectorAssemblyPath) ?? repoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["LOGDB_COLLECTOR_BASE_DIR"] = BaseDirectory;
        startInfo.Environment["LOGDB_COLLECTOR_PIPE_NAME"] = PipeName;

        _process = Process.Start(startInfo);
        if (_process == null)
        {
            throw new InvalidOperationException("Failed to start collector process.");
        }

        _stdoutTask = _process.StandardOutput.ReadToEndAsync();
        _stderrTask = _process.StandardError.ReadToEndAsync();

        await WaitForPipeAsync(TimeSpan.FromSeconds(25));
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch
        {
            // best effort shutdown
        }

        Environment.SetEnvironmentVariable("LOGDB_COLLECTOR_PIPE_NAME", null);
        Environment.SetEnvironmentVariable(CollectorPathDefaults.BaseDirectoryEnvironmentVariable, null);

        try
        {
            if (Directory.Exists(BaseDirectory))
            {
                Directory.Delete(BaseDirectory, recursive: true);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }

    public async Task<ControlResponseDto> SendAsync(string command, string? payloadJson = null)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(cts.Token);

        using var reader = new StreamReader(pipe);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };

        var request = new ControlRequestDto
        {
            Command = command,
            PayloadJson = payloadJson
        };

        await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
        var responseLine = await reader.ReadLineAsync(cts.Token);
        if (string.IsNullOrWhiteSpace(responseLine))
        {
            throw new InvalidOperationException("Collector returned an empty response.");
        }

        var response = JsonSerializer.Deserialize<ControlResponseDto>(responseLine, JsonOptions);
        if (response == null)
        {
            throw new InvalidOperationException("Failed to parse collector response.");
        }

        return response;
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var collectorProjectPath = Path.Combine(
                current.FullName,
                "com.logdb.windows.collector",
                "com.logdb.windows.collector.csproj");

            if (File.Exists(collectorProjectPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Repository root containing com.logdb.windows.collector/com.logdb.windows.collector.csproj was not found.");
    }

    private static string ResolveCollectorAssemblyPath(string repoRoot)
    {
        var candidates = new[]
        {
            Path.Combine(repoRoot, "com.logdb.windows.collector", "bin", "Release", "net10.0-windows", "com.logdb.windows.collector.dll"),
            Path.Combine(repoRoot, "com.logdb.windows.collector", "bin", "Debug", "net10.0-windows", "com.logdb.windows.collector.dll")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "Built collector assembly was not found. Build com.logdb.windows.collector before running integration tests.");
    }

    private async Task WaitForPipeAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await pipe.ConnectAsync(cts.Token);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                await Task.Delay(200);
            }
        }

        throw new TimeoutException(
            $"Collector named pipe did not become available. Last error: {lastError?.Message}{await BuildProcessDiagnosticsAsync()}");
    }

    private async Task<string> BuildProcessDiagnosticsAsync()
    {
        if (_process == null)
        {
            return string.Empty;
        }

        if (!_process.HasExited)
        {
            return string.Empty;
        }

        var stdout = await ReadCompletedOutputAsync(_stdoutTask);
        var stderr = await ReadCompletedOutputAsync(_stderrTask);

        return $" Process exited with code {_process.ExitCode}.{FormatCapturedOutput("Stdout", stdout)}{FormatCapturedOutput("Stderr", stderr)}";
    }

    private static async Task<string> ReadCompletedOutputAsync(Task<string>? task)
    {
        if (task == null || !task.IsCompleted)
        {
            return string.Empty;
        }

        return await task;
    }

    private static string FormatCapturedOutput(string label, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        var normalized = output.ReplaceLineEndings(" ").Trim();
        if (normalized.Length > CapturedOutputLimit)
        {
            normalized = normalized[..CapturedOutputLimit] + "...";
        }

        return $" {label}: {normalized}";
    }
}
