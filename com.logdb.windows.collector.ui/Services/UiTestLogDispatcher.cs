using System.Text.Json;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.ui.ViewModels;
using LogDB.Client.Models;
using LogDB.Extensions.Logging;
using Microsoft.Extensions.Logging;
using LogLevel = LogDB.Client.Models.LogLevel;

namespace com.logdb.windows.collector.ui.Services;

/// <summary>
/// UI-local test-log sender. Resolves the gRPC endpoint via discovery (or the on-disk
/// service cache) and ships one log/beat straight from the desktop process. Mirrors the
/// per-module Log/Beat shape used by the running modules so test rows look like real
/// traffic.
///
/// Streams diagnostic steps through <see cref="IProgress{TestReportStep}"/> so the UI
/// can show a live timeline modal with what is happening at each stage. Also captures
/// the SDK's internal warnings/errors (which it normally swallows into a generic
/// "Failed" status) and emits them as a final diagnostics step.
/// </summary>
internal sealed class UiTestLogDispatcher
{
    private static readonly JsonSerializerOptions PreviewJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<(bool Success, string Message)> SendAsync(
        string moduleName,
        CollectorConfigDto config,
        string serviceUrl,
        IProgress<TestReportStep>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            return Fail(progress, "Module name", "Module name is required.");
        }

        if (string.IsNullOrWhiteSpace(config.LogDB.ApiKey))
        {
            return Fail(progress, "API key check", "LogDB API key is missing — set it on the Destination page first.");
        }
        Emit(progress, "API key check", $"present ({Mask(config.LogDB.ApiKey)})", TestReportStepStatus.Ok);

        if (string.IsNullOrWhiteSpace(serviceUrl))
        {
            return Fail(progress, "Endpoint check", "Could not resolve gRPC endpoint (discovery + on-disk cache both empty).");
        }

        // GrpcChannel.ForAddress strips the path — show the user the actual wire-level host+port
        // and the path component that the gRPC framework will REPLACE with /{Service}/{Method}.
        // This is the difference that often confuses reverse-proxy / ingress routing.
        Uri? parsed = null;
        try { parsed = new Uri(serviceUrl); } catch { /* malformed URL handled below */ }
        if (parsed != null)
        {
            var note = string.IsNullOrEmpty(parsed.AbsolutePath) || parsed.AbsolutePath == "/"
                ? $"effective wire-level base: {parsed.Scheme}://{parsed.Authority}"
                : $"effective wire-level base: {parsed.Scheme}://{parsed.Authority}  (note: gRPC IGNORES path '{parsed.AbsolutePath}' — RPC URI is /{{Service}}/{{Method}})";
            Emit(progress, "Endpoint check", $"{serviceUrl}\n{note}", TestReportStepStatus.Ok);
        }
        else
        {
            Emit(progress, "Endpoint check", serviceUrl, TestReportStepStatus.Ok);
        }

        // Force EnableCompression=false for Test so we exercise the plain Log / LogBeat RPCs
        // on the server. If the user's deployment routes /Log differently than
        // /SendCompressedLog, this isolates that. The compression default of the running
        // modules is unchanged.
        var testConfig = CloneWithCompressionOff(config);
        var compressionInUse = testConfig.LogDB.Batch.EnableCompression;
        Emit(progress, "Compression mode",
            compressionInUse
                ? "compression=ON → gRPC method: SendCompressedLog / SendCompressedLogBeat"
                : "compression=OFF (forced for Test) → gRPC method: Log / LogBeat",
            TestReportStepStatus.Info);

        using var capture = new CapturingLoggerFactory();
        var client = UiLogDbClientFactory.Create(testConfig.LogDB, serviceUrl, capture);
        config = testConfig;

        try
        {
            // Pick the wire RPC name to surface in step labels so the user can grep
            // server-side logs for exactly which handler was hit.
            var rpcLog = config.LogDB.Batch.EnableCompression ? "LogGrpcService/SendCompressedLog" : "LogGrpcService/Log";
            var rpcBeat = config.LogDB.Batch.EnableCompression ? "LogGrpcService/SendCompressedLogBeat" : "LogGrpcService/LogBeat";

            if (IsHeartbeat(moduleName))
            {
                var beat = BuildBeat(config);
                Emit(progress, "Build beat payload",
                    $"measurement={beat.Measurement} collection={beat.Collection} tags={beat.Tag.Count} fields={beat.Field.Count}\n{Preview(beat)}",
                    TestReportStepStatus.Ok);

                Emit(progress, $"Send via gRPC → {rpcBeat}", "in flight…", TestReportStepStatus.Running);
                var beatStatus = await client.LogBeatAsync(beat, cancellationToken);
                Emit(progress, $"Send via gRPC → {rpcBeat}",
                    $"client status={beatStatus} (Note: gRPC Success ≠ server-side fanout — verify on the server with: rpc={rpcBeat})",
                    beatStatus == LogResponseStatus.Success ? TestReportStepStatus.Ok : TestReportStepStatus.Fail);

                await client.FlushAsync();
                Emit(progress, "Flush client buffer", "ok", TestReportStepStatus.Ok);

                EmitDiagnostics(progress, capture);
                return Summary(progress, beatStatus, $"beat {beat.Collection}/{beat.Measurement}", serviceUrl, capture);
            }

            var log = BuildLog(moduleName, config);
            var sysType = log.AttributesS.TryGetValue("_sys_type", out var st) ? st : "(unset)";
            Emit(progress, "Build log payload",
                $"application={log.Application} collection={log.Collection} level={log.Level}\n_sys_type={sysType}  ← server-side kafka fanout routes by this attribute\n{Preview(log)}",
                TestReportStepStatus.Ok);

            Emit(progress, $"Send via gRPC → {rpcLog}", "in flight…", TestReportStepStatus.Running);
            var logStatus = await client.LogAsync(log, cancellationToken);
            Emit(progress, $"Send via gRPC → {rpcLog}",
                $"client status={logStatus} (Note: gRPC Success ≠ server-side fanout — verify on the server with: rpc={rpcLog})",
                logStatus == LogResponseStatus.Success ? TestReportStepStatus.Ok : TestReportStepStatus.Fail);

            await client.FlushAsync();
            Emit(progress, "Flush client buffer", "ok", TestReportStepStatus.Ok);

            EmitDiagnostics(progress, capture);
            return Summary(progress, logStatus, $"log {log.Application} → {log.Collection}", serviceUrl, capture);
        }
        catch (Exception ex)
        {
            EmitDiagnostics(progress, capture);
            Emit(progress, "Exception", $"{ex.GetType().Name}: {ex.Message}", TestReportStepStatus.Fail);
            return (false, $"Test dispatch error: {ex.GetType().Name} — {ex.Message}. URL={serviceUrl}.");
        }
        finally
        {
            if (client is IDisposable d) d.Dispose();
        }
    }

    private static (bool Success, string Message) Summary(
        IProgress<TestReportStep>? progress,
        LogResponseStatus status,
        string what,
        string serviceUrl,
        CapturingLoggerFactory capture)
    {
        if (status == LogResponseStatus.Success)
        {
            var msg = $"Test {what} sent via {serviceUrl}.";
            Emit(progress, "Result", msg, TestReportStepStatus.Ok);
            return (true, msg);
        }

        var detail = capture.RenderForUser();
        var msg2 = $"Test {what} failed: status={status}. URL={serviceUrl}.{detail}";
        Emit(progress, "Result", msg2, TestReportStepStatus.Fail);
        return (false, msg2);
    }

    private static (bool Success, string Message) Fail(IProgress<TestReportStep>? progress, string title, string message)
    {
        Emit(progress, title, message, TestReportStepStatus.Fail);
        return (false, message);
    }

    private static void Emit(IProgress<TestReportStep>? progress, string title, string detail, TestReportStepStatus status)
    {
        progress?.Report(new TestReportStep(title, detail, status));
    }

    private static void EmitDiagnostics(IProgress<TestReportStep>? progress, CapturingLoggerFactory capture)
    {
        var snapshot = capture.Snapshot();
        if (snapshot.Count == 0)
        {
            Emit(progress, "SDK diagnostics", "no warnings or errors captured from the SDK", TestReportStepStatus.Info);
            return;
        }
        Emit(progress, "SDK diagnostics", string.Join(Environment.NewLine, snapshot), TestReportStepStatus.Info);
    }

    private static bool IsHeartbeat(string moduleName) =>
        moduleName.Equals("Heartbeat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a shallow clone of the config with Batch.EnableCompression forced to false.
    /// Test uses the uncompressed gRPC path so the user can isolate whether
    /// SendCompressedLog handler vs Log handler is broken server-side. The running
    /// modules still use whatever compression the config says.
    /// </summary>
    private static CollectorConfigDto CloneWithCompressionOff(CollectorConfigDto config)
    {
        return new CollectorConfigDto
        {
            LogDB = new LogDbConfigDto
            {
                ApiKey = config.LogDB.ApiKey,
                Endpoint = config.LogDB.Endpoint,
                DiscoveryUrl = config.LogDB.DiscoveryUrl,
                Protocol = config.LogDB.Protocol,
                Retry = new RetryOptionsDto
                {
                    MaxRetries = config.LogDB.Retry.MaxRetries,
                    EnableCircuitBreaker = config.LogDB.Retry.EnableCircuitBreaker
                },
                Batch = new BatchOptionsDto
                {
                    EnableBatching = false,
                    BatchSize = config.LogDB.Batch.BatchSize,
                    FlushIntervalSeconds = config.LogDB.Batch.FlushIntervalSeconds,
                    EnableCompression = false
                }
            },
            Modules = config.Modules,
            Firewall = config.Firewall,
            Server = config.Server
        };
    }

    private static string Mask(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return "(empty)";
        if (apiKey.Length <= 8) return new string('•', apiKey.Length);
        return $"{apiKey[..4]}••••{apiKey[^4..]}";
    }

    private static string Preview(Log log)
    {
        var preview = new
        {
            log.Application,
            log.Collection,
            log.Environment,
            Level = log.Level.ToString(),
            log.Message,
            log.Source,
            Labels = log.Label,
            Attributes = log.AttributesS
        };
        return JsonSerializer.Serialize(preview, PreviewJsonOptions);
    }

    private static string Preview(LogBeat beat)
    {
        var preview = new
        {
            beat.Application,
            beat.Environment,
            beat.Measurement,
            beat.Collection,
            Tags = beat.Tag.Select(t => new { t.Key, t.Value }),
            Fields = beat.Field.Select(f => new { f.Key, f.Value })
        };
        return JsonSerializer.Serialize(preview, PreviewJsonOptions);
    }

    /// <summary>
    /// Builds a per-module test log using the same SDK typed builder the running
    /// module uses. The typed builders set the correct _sys_type for kafka routing
    /// AND the type-specific attributes (Source, StatusCode, measurement, etc.)
    /// that the downstream consumers expect. Sending a plain Log with just _sys_type
    /// matched might still be silently dropped if the consumer validates schema.
    ///
    /// Test rows are tagged with `ui_test=1` so they can be filtered out from real
    /// traffic in queries.
    /// </summary>
    private static Log BuildLog(string moduleName, CollectorConfigDto config)
    {
        var environmentName = string.IsNullOrWhiteSpace(config.Server.ServerEnvironment) ? "Production" : config.Server.ServerEnvironment;
        var serverName = string.IsNullOrWhiteSpace(config.Server.ServerName)
            ? Environment.MachineName
            : config.Server.ServerName;
        var (application, collection, label, _) = ResolveTarget(moduleName, config);
        var testMessage = $"Test log from {moduleName} tab — collector UI verification.";

        // Per-module Server name override (currently used by Metrics). Falls back to
        // the global Server:ServerName / Environment.MachineName.
        var metricsServerName = !string.IsNullOrWhiteSpace(config.Modules.Metrics.ServerNameOverride)
            ? config.Modules.Metrics.ServerNameOverride!.Trim()
            : serverName;

        Log log = moduleName.ToLowerInvariant() switch
        {
            "eventlog" => new LogWindowsEvent
            {
                Timestamp = DateTime.UtcNow,
                Application = application,
                Environment = environmentName,
                Collection = collection,
                Level = "Information",
                Message = testMessage,
                ProviderName = string.IsNullOrWhiteSpace(config.Modules.EventLog.ProviderNameOverride)
                    ? "LogDB.UI.Test"
                    : config.Modules.EventLog.ProviderNameOverride!.Trim(),
                Channel = "Application",
                EventId = 0,
                Computer = serverName,
                UserId = Environment.UserName
            }.ToLog(),

            "iis" => new LogIISEvent
            {
                Timestamp = DateTime.UtcNow,
                Collection = collection,
                Method = "GET",
                UriStem = "/_ui_test",
                UriQuery = "source=ui-test",
                Status = 200,
                SubStatus = 0,
                Win32Status = 0,
                TimeTaken = 1,
                BytesSent = testMessage.Length,
                BytesReceived = 0,
                ClientIp = "127.0.0.1",
                ServerIp = "127.0.0.1",
                UserAgent = $"LogDB-Collector-UI-Test/{typeof(UiTestLogDispatcher).Assembly.GetName().Version}",
                Host = serverName,
                Port = 80,
                SiteName = application,
                ServerName = serverName
            }.ToLog(),

            "metrics" => new LogWindowsMetric
            {
                Timestamp = DateTime.UtcNow,
                Collection = collection,
                Environment = environmentName,
                ServerName = metricsServerName,
                Measurement = "cpu",
                CpuUsagePercent = 0.0,
                CpuIdlePercent = 100.0,
                CpuCoreCount = Environment.ProcessorCount
            }.ToLog(),

            _ => new Log
            {
                Guid = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow,
                Application = application,
                Environment = environmentName,
                Level = LogLevel.Info,
                Message = testMessage,
                Source = "collector/test",
                Collection = collection,
                AttributesS = new Dictionary<string, string>
                {
                    ["_sys_type"] = "collector_test",
                    ["serverName"] = serverName
                }
            }
        };

        // The typed builders sometimes leave Application/Environment/Message blank if
        // the source field is null — fill any gaps so every test row carries the same
        // identifying information regardless of module.
        if (string.IsNullOrEmpty(log.Application)) log.Application = application;
        if (string.IsNullOrEmpty(log.Environment)) log.Environment = environmentName;
        if (string.IsNullOrEmpty(log.Message)) log.Message = testMessage;

        // Common labels + a ui_test marker so test rows can be filtered out in queries.
        // Note: Log.Label and Log.AttributesS are init-only collections — already
        // initialized by the Log ctor / typed builder, so we mutate them in place.
        foreach (var l in config.Server.DefaultLabels)
            if (!log.Label.Contains(l)) log.Label.Add(l);
        if (!log.Label.Contains(label)) log.Label.Add(label);
        if (!log.Label.Contains("ui-test")) log.Label.Add("ui-test");

        log.AttributesS["ui_test"] = "1";
        log.AttributesS["module"] = moduleName;
        if (!log.AttributesS.ContainsKey("serverName")) log.AttributesS["serverName"] = serverName;

        return log;
    }

    private static LogBeat BuildBeat(CollectorConfigDto config)
    {
        var heartbeat = config.Modules.Heartbeat;
        var measurement = string.IsNullOrWhiteSpace(heartbeat.Measurement) ? "heartbeat" : heartbeat.Measurement;
        var collection = string.IsNullOrWhiteSpace(heartbeat.Collection) ? "beats" : heartbeat.Collection;

        // Per-Heartbeat-module overrides — fall back to global server / environment.
        var serverName = !string.IsNullOrWhiteSpace(heartbeat.ServerNameOverride)
            ? heartbeat.ServerNameOverride!.Trim()
            : (string.IsNullOrWhiteSpace(config.Server.ServerName) ? Environment.MachineName : config.Server.ServerName);
        var environmentName = !string.IsNullOrWhiteSpace(heartbeat.EnvironmentOverride)
            ? heartbeat.EnvironmentOverride!.Trim()
            : (string.IsNullOrWhiteSpace(config.Server.ServerEnvironment) ? "Production" : config.Server.ServerEnvironment);

        var beat = new LogBeat
        {
            Guid = Guid.NewGuid().ToString(),
            Timestamp = DateTime.UtcNow,
            Measurement = measurement,
            Collection = collection,
            Application = "LogDB Collector",
            Environment = environmentName
        };

        beat.Tag.Add(new LogMeta { Key = "host", Value = serverName });
        beat.Tag.Add(new LogMeta { Key = "source", Value = "ui-test" });
        beat.Field.Add(new LogMeta { Key = "uptime_seconds", Value = (Environment.TickCount64 / 1000L).ToString() });
        beat.Field.Add(new LogMeta { Key = "test", Value = "1" });
        return beat;
    }

    private static (string Application, string Collection, string Label, string Source) ResolveTarget(
        string moduleName, CollectorConfigDto config)
    {
        return moduleName.ToLowerInvariant() switch
        {
            "eventlog" => ("Windows Event Viewer", "windows-eventlog-application", "event-viewer", "collector/event-log/test"),
            "iis"      => ("IIS",                   "iis-events",                   "iis",           "collector/iis/test"),
            "metrics"  => ("Windows Tracker",       "windows-metrics",              "windows-tracker", "collector/metrics/test"),
            _          => (
                "LogDB Collector",
                "windows",
                "collector",
                $"collector/{moduleName.ToLowerInvariant()}/test")
        };
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _messages = new();
        private readonly object _gate = new();

        public void AddProvider(ILoggerProvider provider) { }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose() { }

        public IReadOnlyList<string> Snapshot()
        {
            lock (_gate) return _messages.ToArray();
        }

        public string RenderForUser()
        {
            var snap = Snapshot();
            if (snap.Count == 0) return string.Empty;
            return " Details: " + string.Join(" | ", snap);
        }

        private void Append(string line)
        {
            lock (_gate) _messages.Add(line);
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly CapturingLoggerFactory _owner;

            public CapturingLogger(CapturingLoggerFactory owner)
            {
                _owner = owner;
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) =>
                level >= Microsoft.Extensions.Logging.LogLevel.Warning;

            public void Log<TState>(
                Microsoft.Extensions.Logging.LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                var text = formatter(state, exception);
                if (exception != null)
                {
                    text = $"{text} ({exception.GetType().Name}: {exception.Message})";
                }
                _owner.Append($"{logLevel.ToString().ToLowerInvariant()}: {text}");
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }
}
