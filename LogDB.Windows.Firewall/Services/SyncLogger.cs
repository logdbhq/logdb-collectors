using System.Text;

namespace LogDB.Windows.Firewall.Services;

/// <summary>
/// Logs blocked IPs to local files for easy review.
/// - logs/blocked/{Source}.txt  - current snapshot of IPs per source
/// - logs/sync.log              - append-only activity log with diffs
/// </summary>
public class SyncLogger
{
    private readonly string _logsDir;
    private readonly string _blockedDir;
    private readonly string _syncLogPath;
    private readonly ILogger<SyncLogger> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Track previous IPs per source for diff calculation
    private readonly Dictionary<string, HashSet<string>> _previousIps = new();

    public SyncLogger(ILogger<SyncLogger> logger)
    {
        _logger = logger;
        _logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
        _blockedDir = Path.Combine(_logsDir, "blocked");
        _syncLogPath = Path.Combine(_logsDir, "sync.log");

        Directory.CreateDirectory(_blockedDir);
    }

    /// <summary>
    /// Writes the current IP snapshot for a source and logs the diff.
    /// </summary>
    public async Task LogSourceSyncAsync(string sourceId, string displayName, HashSet<string> ips)
    {
        await _lock.WaitAsync();
        try
        {
            // Calculate diff
            _previousIps.TryGetValue(sourceId, out var previous);
            var added = previous != null ? ips.Except(previous, StringComparer.OrdinalIgnoreCase).Count() : ips.Count;
            var removed = previous != null ? previous.Except(ips, StringComparer.OrdinalIgnoreCase).Count() : 0;
            _previousIps[sourceId] = new HashSet<string>(ips, StringComparer.OrdinalIgnoreCase);

            // Write snapshot file
            var snapshotPath = Path.Combine(_blockedDir, $"{sourceId}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"# LogDB Firewall - {displayName}");
            sb.AppendLine($"# Last sync: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"# IPs: {ips.Count:N0}");
            sb.AppendLine();
            foreach (var ip in ips.OrderBy(x => x))
            {
                sb.AppendLine(ip);
            }
            await File.WriteAllTextAsync(snapshotPath, sb.ToString());

            // Append to sync log
            string diffStr;
            if (previous == null)
                diffStr = $"{ips.Count:N0} IPs (initial load)";
            else if (added == 0 && removed == 0)
                diffStr = $"{ips.Count:N0} IPs (unchanged)";
            else
                diffStr = $"{ips.Count:N0} IPs (+{added} added, -{removed} removed)";

            await AppendSyncLogAsync($"{displayName}: {diffStr}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write sync log for {SourceId}", sourceId);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Logs a source error to the sync log.
    /// </summary>
    public async Task LogSourceErrorAsync(string displayName, string error)
    {
        await AppendSyncLogAsync($"{displayName}: ERROR - {error}");
    }

    /// <summary>
    /// Logs the start of a sync cycle.
    /// </summary>
    public async Task LogSyncStartAsync()
    {
        await AppendSyncLogAsync("Sync started");
    }

    /// <summary>
    /// Logs the end of a sync cycle with summary.
    /// </summary>
    public async Task LogSyncCompleteAsync(int totalIps, int totalRules, long elapsedMs)
    {
        await AppendSyncLogAsync($"Sync complete: {totalIps:N0} total IPs across {totalRules} rules ({elapsedMs}ms)");
        await AppendSyncLogAsync("");
    }

    /// <summary>
    /// Loads previous IP snapshots from disk so diffs work across service restarts.
    /// </summary>
    public void LoadPreviousSnapshots()
    {
        try
        {
            if (!Directory.Exists(_blockedDir)) return;

            foreach (var file in Directory.GetFiles(_blockedDir, "*.txt"))
            {
                var sourceId = Path.GetFileNameWithoutExtension(file);
                var ips = File.ReadAllLines(file)
                    .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (ips.Count > 0)
                    _previousIps[sourceId] = ips;
            }

            _logger.LogInformation("Loaded previous snapshots for {Count} sources", _previousIps.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load previous snapshots");
        }
    }

    private async Task AppendSyncLogAsync(string message)
    {
        try
        {
            var line = string.IsNullOrEmpty(message)
                ? Environment.NewLine
                : $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            await File.AppendAllTextAsync(_syncLogPath, line);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append to sync log");
        }
    }
}
