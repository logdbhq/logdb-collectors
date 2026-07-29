using System.Text.Json;
using com.logdb.windows.collector.shared.Services;

namespace com.logdb.windows.collector.ui.Services;

/// <summary>
/// Persists when the automatic update check last ran (and when it was last
/// rate-limited) so the UI doesn't hammer the GitHub Releases API. Anonymous
/// GitHub API access allows only 60 requests/hour; the old behaviour checked
/// twice per launch with no memory, which burned the quota and produced the
/// "403 rate limit exceeded" popup/install loop.
///
/// Manual "Check Updates" clicks are NOT throttled — only the automatic
/// startup check consults this.
/// </summary>
internal static class UpdateCheckThrottle
{
    private static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RateLimitBackoff = TimeSpan.FromHours(2);

    private static string StampPath =>
        Path.Combine(CollectorPathDefaults.BaseDirectory, "update-check.json");

    private sealed class Stamp
    {
        public DateTime LastAutoCheckUtc { get; set; }
        public DateTime LastRateLimitUtc { get; set; }
    }

    public static bool ShouldAutoCheck()
    {
        var s = Load();
        if (s == null) return true;
        var now = DateTime.UtcNow;
        if (now - s.LastRateLimitUtc < RateLimitBackoff) return false;
        return now - s.LastAutoCheckUtc >= AutoCheckInterval;
    }

    public static void MarkChecked() => Save(s => s.LastAutoCheckUtc = DateTime.UtcNow);

    public static void MarkRateLimited() => Save(s =>
    {
        s.LastAutoCheckUtc = DateTime.UtcNow;
        s.LastRateLimitUtc = DateTime.UtcNow;
    });

    public static bool LooksRateLimited(string? error) =>
        !string.IsNullOrEmpty(error)
        && (error.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || error.Contains("403", StringComparison.Ordinal));

    private static Stamp? Load()
    {
        try
        {
            if (!File.Exists(StampPath)) return null;
            return JsonSerializer.Deserialize<Stamp>(File.ReadAllText(StampPath));
        }
        catch
        {
            return null;
        }
    }

    private static void Save(Action<Stamp> mutate)
    {
        try
        {
            var s = Load() ?? new Stamp();
            mutate(s);
            Directory.CreateDirectory(CollectorPathDefaults.BaseDirectory);
            File.WriteAllText(StampPath, JsonSerializer.Serialize(s));
        }
        catch
        {
            // Best-effort: a failed stamp write must never break the UI.
        }
    }
}
