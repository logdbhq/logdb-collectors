using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Net.NetworkInformation;
using System.Security.Principal;
using com.logdb.windows.collector.shared.Contracts;

namespace com.logdb.windows.collector.Services;

public interface ICollectorControlInspector
{
    Task<ValidationResultDto> ValidateEventLogAccessAsync(CollectorConfigDto config, CancellationToken cancellationToken);
    Task<ValidationResultDto> ValidateIisPathsAsync(CollectorConfigDto config, CancellationToken cancellationToken);
    Task<ValidationResultDto> ValidateDestinationConnectionAsync(CollectorConfigDto config, CancellationToken cancellationToken);
    Task<PreviewResultDto<EventLogPreviewRowDto>> PreviewEventLogsAsync(CollectorConfigDto config, int max, CancellationToken cancellationToken);
    Task<PreviewResultDto<IisPreviewRowDto>> PreviewIisLogsAsync(CollectorConfigDto config, int max, CancellationToken cancellationToken);
    Task<PreviewResultDto<MetricPreviewRowDto>> PreviewMetricsAsync(CollectorConfigDto config, CancellationToken cancellationToken);
}

public sealed class CollectorControlInspector : ICollectorControlInspector
{
    private readonly ILogDbConnectionTester _connectionTester;
    private readonly ILogger<CollectorControlInspector> _logger;

    public CollectorControlInspector(
        ILogDbConnectionTester connectionTester,
        ILogger<CollectorControlInspector> logger)
    {
        _connectionTester = connectionTester;
        _logger = logger;
    }

    public Task<ValidationResultDto> ValidateEventLogAccessAsync(CollectorConfigDto config, CancellationToken cancellationToken)
    {
        var result = new ValidationResultDto
        {
            Success = true,
            Code = "OK",
            Message = "Event log channels are accessible."
        };

        var channels = config.Modules.EventLog.SourcesChannels
            .Where(channel => !string.IsNullOrWhiteSpace(channel))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (channels.Count == 0)
        {
            result.Success = false;
            result.Code = "NO_CHANNELS";
            result.Message = "No event log channels are configured.";
            return Task.FromResult(result);
        }

        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var query = new EventLogQuery(channel, PathType.LogName)
                {
                    ReverseDirection = true,
                    TolerateQueryErrors = true
                };

                using var reader = new EventLogReader(query);
                using var _ = reader.ReadEvent();
            }
            catch (EventLogNotFoundException ex)
            {
                AddIssue(result, "CHANNEL_NOT_FOUND", "Error", channel, SafeMessage(ex.Message));
            }
            catch (UnauthorizedAccessException ex)
            {
                AddIssue(result, "ACCESS_DENIED", "Error", channel, SafeMessage(ex.Message));
            }
            catch (Exception ex)
            {
                AddIssue(result, "VALIDATION_FAILED", "Error", channel, SafeMessage(ex.Message));
            }
        }

        if (channels.Any(channel => channel.Equals("Security", StringComparison.OrdinalIgnoreCase)) && !IsAdministrator())
        {
            AddIssue(
                result,
                "ELEVATION_RECOMMENDED",
                "Warning",
                "Security",
                "Reading Security channel often requires Administrator privileges.");
        }

        FinalizeValidation(result, "Event log validation completed.");
        return Task.FromResult(result);
    }

    public Task<ValidationResultDto> ValidateIisPathsAsync(CollectorConfigDto config, CancellationToken cancellationToken)
    {
        var result = new ValidationResultDto
        {
            Success = true,
            Code = "OK",
            Message = "IIS log directories are valid."
        };

        var directories = config.Modules.IIS.LogDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (directories.Count == 0)
        {
            result.Success = false;
            result.Code = "NO_PATHS";
            result.Message = "No IIS log directories are configured.";
            return Task.FromResult(result);
        }

        foreach (var path in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(path))
                {
                    AddIssue(result, "PATH_NOT_FOUND", "Error", path, "Directory does not exist.");
                    continue;
                }

                var logFiles = Directory.EnumerateFiles(path, "*.log", SearchOption.AllDirectories).Take(1).Any();
                if (!logFiles)
                {
                    AddIssue(result, "NO_LOG_FILES", "Warning", path, "No .log files found under this directory.");
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                AddIssue(result, "ACCESS_DENIED", "Error", path, SafeMessage(ex.Message));
            }
            catch (Exception ex)
            {
                AddIssue(result, "VALIDATION_FAILED", "Error", path, SafeMessage(ex.Message));
            }
        }

        FinalizeValidation(result, "IIS path validation completed.");
        return Task.FromResult(result);
    }

    public async Task<ValidationResultDto> ValidateDestinationConnectionAsync(CollectorConfigDto config, CancellationToken cancellationToken)
    {
        try
        {
            var connection = await _connectionTester.TestAsync(config, cancellationToken);
            return new ValidationResultDto
            {
                Success = connection.Success,
                Code = connection.Success ? "CONNECTED" : "CONNECTION_FAILED",
                Message = connection.Message
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Destination validation failed.");
            return new ValidationResultDto
            {
                Success = false,
                Code = "CONNECTION_FAILED",
                Message = "Destination validation failed. Check endpoint/discovery URL and API key."
            };
        }
    }

    public Task<PreviewResultDto<EventLogPreviewRowDto>> PreviewEventLogsAsync(
        CollectorConfigDto config,
        int max,
        CancellationToken cancellationToken)
    {
        var bounded = BoundPreviewMax(max);
        var channels = config.Modules.EventLog.SourcesChannels
            .Where(channel => !string.IsNullOrWhiteSpace(channel))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new PreviewResultDto<EventLogPreviewRowDto>
        {
            Success = true,
            Code = "OK",
            Message = "Preview generated."
        };

        if (channels.Count == 0)
        {
            return Task.FromResult(new PreviewResultDto<EventLogPreviewRowDto>
            {
                Success = false,
                Code = "NO_CHANNELS",
                Message = "No event log channels are configured."
            });
        }

        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Rows.Count >= bounded)
            {
                break;
            }

            try
            {
                var query = new EventLogQuery(channel, PathType.LogName)
                {
                    ReverseDirection = true,
                    TolerateQueryErrors = true
                };

                using var reader = new EventLogReader(query);
                while (result.Rows.Count < bounded)
                {
                    using var record = reader.ReadEvent();
                    if (record == null)
                    {
                        break;
                    }

                    result.Rows.Add(new EventLogPreviewRowDto
                    {
                        TimeUtc = record.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow,
                        Level = record.LevelDisplayName ?? record.Level?.ToString() ?? "Unknown",
                        Source = record.ProviderName ?? channel,
                        EventId = record.Id,
                        MessageSnippet = Truncate(SafeFormatEvent(record), 280)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Event log preview failed for channel {Channel}", channel);
            }
        }

        result.Rows = result.Rows
            .OrderByDescending(row => row.TimeUtc)
            .Take(bounded)
            .ToList();

        if (result.Rows.Count == 0)
        {
            result.Success = false;
            result.Code = "PREVIEW_UNAVAILABLE";
            result.Message = "No event log entries could be previewed.";
        }

        return Task.FromResult(result);
    }

    public Task<PreviewResultDto<IisPreviewRowDto>> PreviewIisLogsAsync(
        CollectorConfigDto config,
        int max,
        CancellationToken cancellationToken)
    {
        var bounded = BoundPreviewMax(max);
        var result = new PreviewResultDto<IisPreviewRowDto>
        {
            Success = true,
            Code = "OK",
            Message = "Preview generated."
        };

        var directories = config.Modules.IIS.LogDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (directories.Count == 0)
        {
            return Task.FromResult(new PreviewResultDto<IisPreviewRowDto>
            {
                Success = false,
                Code = "NO_PATHS",
                Message = "No IIS log directories are configured."
            });
        }

        var files = new List<FileInfo>();
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                files.AddRange(Directory.EnumerateFiles(directory, "*.log", SearchOption.AllDirectories)
                    .Select(path => new FileInfo(path)));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unable to enumerate IIS logs under {Directory}", directory);
            }
        }

        foreach (var file in files.OrderByDescending(file => file.LastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Rows.Count >= bounded)
            {
                break;
            }

            try
            {
                var parsedRows = ParseIisLogFile(
                        file.FullName,
                        config.Modules.IIS.SiteName,
                        config.Modules.IIS.Include4xx,
                        config.Modules.IIS.Include5xx)
                    .Take(bounded - result.Rows.Count);

                result.Rows.AddRange(parsedRows);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse IIS log file {Path}", file.FullName);
            }
        }

        result.Rows = result.Rows
            .OrderByDescending(row => row.TimeUtc)
            .Take(bounded)
            .ToList();

        if (result.Rows.Count == 0)
        {
            result.Success = false;
            result.Code = "PREVIEW_UNAVAILABLE";
            result.Message = "No IIS log entries available for preview.";
        }

        return Task.FromResult(result);
    }

    public Task<PreviewResultDto<MetricPreviewRowDto>> PreviewMetricsAsync(CollectorConfigDto config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = new PreviewResultDto<MetricPreviewRowDto>
        {
            Success = true,
            Code = "OK",
            Message = "Metrics snapshot generated."
        };

        var tags = new Dictionary<string, string>(config.Modules.Metrics.Tags, StringComparer.OrdinalIgnoreCase);

        if (config.Modules.Metrics.IncludeCpu)
        {
            result.Rows.Add(new MetricPreviewRowDto
            {
                Metric = "system.cpu.logical_processors",
                Value = Environment.ProcessorCount,
                Unit = "count",
                Tags = tags
            });
        }

        if (config.Modules.Metrics.IncludeMemory)
        {
            result.Rows.Add(new MetricPreviewRowDto
            {
                Metric = "dotnet.memory.managed_mb",
                Value = Math.Round(GC.GetTotalMemory(false) / (1024d * 1024d), 2),
                Unit = "MB",
                Tags = tags
            });

            result.Rows.Add(new MetricPreviewRowDto
            {
                Metric = "process.memory.working_set_mb",
                Value = Math.Round(Environment.WorkingSet / (1024d * 1024d), 2),
                Unit = "MB",
                Tags = tags
            });
        }

        if (config.Modules.Metrics.IncludeDisk)
        {
            foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed))
            {
                var usedPercent = drive.TotalSize == 0
                    ? 0
                    : Math.Round(((double)(drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize) * 100d, 2);

                result.Rows.Add(new MetricPreviewRowDto
                {
                    Metric = "disk.used.percent",
                    Value = usedPercent,
                    Unit = "percent",
                    Tags = WithTag(tags, "drive", drive.Name)
                });
            }
        }

        if (config.Modules.Metrics.IncludeNetwork)
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up))
            {
                var stats = networkInterface.GetIPv4Statistics();
                result.Rows.Add(new MetricPreviewRowDto
                {
                    Metric = "network.bytes.received.total",
                    Value = stats.BytesReceived,
                    Unit = "bytes",
                    Tags = WithTag(tags, "adapter", networkInterface.Name)
                });
                result.Rows.Add(new MetricPreviewRowDto
                {
                    Metric = "network.bytes.sent.total",
                    Value = stats.BytesSent,
                    Unit = "bytes",
                    Tags = WithTag(tags, "adapter", networkInterface.Name)
                });
            }
        }

        return Task.FromResult(result);
    }

    private static IEnumerable<IisPreviewRowDto> ParseIisLogFile(
        string path,
        string? siteNameFilter,
        bool include4xx,
        bool include5xx)
    {
        var lines = File.ReadLines(path).TakeLast(600).ToArray();
        var fields = Array.Empty<string>();

        var rows = new List<IisPreviewRowDto>();
        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            if (rawLine.StartsWith("#Fields:", StringComparison.OrdinalIgnoreCase))
            {
                fields = rawLine["#Fields:".Length..]
                    .Trim()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                continue;
            }

            if (rawLine.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (fields.Length == 0)
            {
                continue;
            }

            var tokens = rawLine.Split(' ', StringSplitOptions.None);
            if (tokens.Length < fields.Length)
            {
                continue;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < fields.Length; i++)
            {
                values[fields[i]] = tokens[i];
            }

            if (!string.IsNullOrWhiteSpace(siteNameFilter)
                && values.TryGetValue("s-sitename", out var siteName)
                && !siteName.Contains(siteNameFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var status = TryParseInt(values, "sc-status");
            if (status is >= 400 and < 500 && !include4xx)
            {
                continue;
            }

            if (status is >= 500 and < 600 && !include5xx)
            {
                continue;
            }

            var uriStem = TryGet(values, "cs-uri-stem");
            var uriQuery = TryGet(values, "cs-uri-query");
            var uri = string.IsNullOrWhiteSpace(uriQuery) || uriQuery == "-"
                ? uriStem
                : $"{uriStem}?{uriQuery}";

            rows.Add(new IisPreviewRowDto
            {
                TimeUtc = TryParseDateTime(values),
                Method = TryGet(values, "cs-method"),
                Uri = uri,
                Status = status,
                TimeTakenMs = TryParseLong(values, "time-taken"),
                ClientIp = TryGet(values, "c-ip")
            });
        }

        return rows
            .OrderByDescending(row => row.TimeUtc)
            .Take(100);
    }

    private static DateTime? TryParseDateTime(Dictionary<string, string> values)
    {
        if (!values.TryGetValue("date", out var date) || !values.TryGetValue("time", out var time))
        {
            return null;
        }

        return DateTime.TryParse($"{date} {time}", out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;
    }

    private static int? TryParseInt(Dictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static long? TryParseLong(Dictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) && long.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string TryGet(Dictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static int BoundPreviewMax(int max)
    {
        return Math.Clamp(max <= 0 ? 20 : max, 1, 50);
    }

    private static string SafeFormatEvent(EventRecord record)
    {
        try
        {
            return record.FormatDescription() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void AddIssue(
        ValidationResultDto result,
        string code,
        string severity,
        string? source,
        string message)
    {
        result.Issues.Add(new ValidationIssueDto
        {
            Code = code,
            Severity = severity,
            Source = source,
            Message = message
        });
    }

    private static void FinalizeValidation(ValidationResultDto result, string successMessage)
    {
        var hasErrors = result.Issues.Any(issue => issue.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase));
        result.Success = !hasErrors;
        result.Code = hasErrors ? "VALIDATION_FAILED" : "OK";
        result.Message = hasErrors
            ? "Validation found one or more errors."
            : successMessage;
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, string> WithTag(Dictionary<string, string> source, string key, string value)
    {
        var copy = new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase)
        {
            [key] = value
        };
        return copy;
    }

    private static string SafeMessage(string message)
    {
        return Truncate(message.Replace(Environment.NewLine, " "), 220);
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }
}
