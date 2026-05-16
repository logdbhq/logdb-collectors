using com.logdb.docker.collector.Configuration;
using com.logdb.docker.collector.Models;
using Microsoft.Extensions.Options;

namespace com.logdb.docker.collector.Services;

public class ContainerFilterService
{
    private readonly CollectorFilterOptions _filters;
    private readonly ContainerToggleService _toggleService;

    public ContainerFilterService(
        IOptions<CollectorFilterOptions> options,
        ContainerToggleService toggleService)
    {
        _filters = options.Value;
        _toggleService = toggleService;
    }

    public void Apply(IList<ContainerInfo> containers)
    {
        foreach (var c in containers)
        {
            c.IsIncluded = true;
            c.ExclusionReason = null;

            if (_toggleService.IsDisabled(c.Id))
            {
                Exclude(c, "disabled by user");
                continue;
            }

            if (!IsRunning(c))
            {
                Exclude(c, $"container state is '{c.State}'");
                continue;
            }

            if (string.IsNullOrEmpty(c.LogPath))
            {
                Exclude(c, "no log path available");
                continue;
            }

            if (MatchesExcludeContainerName(c))
            {
                Exclude(c, $"name '{c.Name}' matches exclude list");
                continue;
            }

            if (MatchesExcludeImage(c))
            {
                Exclude(c, $"image '{c.Image}' matches exclude list");
                continue;
            }

            if (MatchesExcludeLabel(c, out var excludeLabel))
            {
                Exclude(c, $"label '{excludeLabel}' matches exclude list");
                continue;
            }

            if (HasIncludeContainerNames() && !MatchesIncludeContainerName(c))
            {
                Exclude(c, $"name '{c.Name}' not in include list");
                continue;
            }

            if (HasIncludeImages() && !MatchesIncludeImage(c))
            {
                Exclude(c, $"image '{c.Image}' not in include list");
                continue;
            }

            if (HasIncludeLabels() && !MatchesIncludeLabel(c))
            {
                Exclude(c, "no matching label in include list");
            }
        }
    }

    private static bool IsRunning(ContainerInfo c) =>
        c.State.Equals("running", StringComparison.OrdinalIgnoreCase);

    private static void Exclude(ContainerInfo c, string reason)
    {
        c.IsIncluded = false;
        c.ExclusionReason = reason;
    }

    private bool HasIncludeContainerNames() => _filters.IncludeContainerNames.Count > 0;
    private bool HasIncludeImages() => _filters.IncludeImages.Count > 0;
    private bool HasIncludeLabels() => _filters.IncludeLabels.Count > 0;

    private bool MatchesExcludeContainerName(ContainerInfo c) =>
        _filters.ExcludeContainerNames.Any(p => MatchesWildcard(c.Name, p));

    private bool MatchesIncludeContainerName(ContainerInfo c) =>
        _filters.IncludeContainerNames.Any(p => MatchesWildcard(c.Name, p));

    private bool MatchesExcludeImage(ContainerInfo c) =>
        _filters.ExcludeImages.Any(p => MatchesWildcard(c.Image, p));

    private bool MatchesIncludeImage(ContainerInfo c) =>
        _filters.IncludeImages.Any(p => MatchesWildcard(c.Image, p));

    private bool MatchesExcludeLabel(ContainerInfo c, out string matchedLabel)
    {
        foreach (var (key, value) in _filters.ExcludeLabels)
        {
            if (c.Labels.TryGetValue(key, out var actual) &&
                (value == "*" || actual.Equals(value, StringComparison.OrdinalIgnoreCase)))
            {
                matchedLabel = $"{key}={value}";
                return true;
            }
        }
        matchedLabel = "";
        return false;
    }

    private bool MatchesIncludeLabel(ContainerInfo c)
    {
        foreach (var (key, value) in _filters.IncludeLabels)
        {
            if (c.Labels.TryGetValue(key, out var actual) &&
                (value == "*" || actual.Equals(value, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static bool MatchesWildcard(string input, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.StartsWith('*') && pattern.EndsWith('*'))
            return input.Contains(pattern[1..^1], StringComparison.OrdinalIgnoreCase);
        if (pattern.StartsWith('*'))
            return input.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        if (pattern.EndsWith('*'))
            return input.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return input.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
