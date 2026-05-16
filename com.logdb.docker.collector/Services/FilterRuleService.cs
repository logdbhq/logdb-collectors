using System.Text.Json;
using com.logdb.docker.collector.Configuration;
using com.logdb.docker.collector.Models;
using Microsoft.Extensions.Options;

namespace com.logdb.docker.collector.Services;

public class FilterRuleService
{
    private readonly ILogger<FilterRuleService> _logger;
    private readonly string _filePath;
    private readonly object _lock = new();

    private readonly List<string> _excludeMessagePatterns = new();
    private readonly List<string> _excludeCategories = new();

    private long _filteredByMessage;
    private long _filteredByCategory;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FilterRuleService(ILogger<FilterRuleService> logger, IOptions<CheckpointOptions> checkpointOptions)
    {
        _logger = logger;
        var checkpointDir = Path.GetDirectoryName(Path.GetFullPath(checkpointOptions.Value.FilePath)) ?? ".";
        _filePath = Path.Combine(checkpointDir, "filter-rules.json");
        Load();
    }

    public long FilteredByMessage => Interlocked.Read(ref _filteredByMessage);
    public long FilteredByCategory => Interlocked.Read(ref _filteredByCategory);

    public bool ShouldExclude(LogRecord record)
    {
        lock (_lock)
        {
            // Check category exclusion
            if (record.Category is not null && _excludeCategories.Count > 0)
            {
                foreach (var cat in _excludeCategories)
                {
                    if (record.Category.Equals(cat, StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref _filteredByCategory);
                        return true;
                    }
                }
            }

            // Check message pattern exclusion (substring match, case-insensitive)
            if (_excludeMessagePatterns.Count > 0)
            {
                foreach (var pattern in _excludeMessagePatterns)
                {
                    if (record.Message.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref _filteredByMessage);
                        return true;
                    }
                }
            }

            return false;
        }
    }

    /// <summary>Check exclusion for console entries (no counters incremented).</summary>
    public bool ShouldExcludeConsoleEntry(string message, string? category)
    {
        lock (_lock)
        {
            if (category is not null && _excludeCategories.Count > 0)
            {
                foreach (var cat in _excludeCategories)
                {
                    if (category.Equals(cat, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            if (_excludeMessagePatterns.Count > 0)
            {
                foreach (var pattern in _excludeMessagePatterns)
                {
                    if (message.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }
    }

    public FilterRulesDto GetRules()
    {
        lock (_lock)
        {
            return new FilterRulesDto
            {
                ExcludeMessagePatterns = new List<string>(_excludeMessagePatterns),
                ExcludeCategories = new List<string>(_excludeCategories)
            };
        }
    }

    public FilterRulesDto AddMessagePattern(string pattern)
    {
        lock (_lock)
        {
            if (!_excludeMessagePatterns.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            {
                _excludeMessagePatterns.Add(pattern);
                Save();
                _logger.LogInformation("Added message exclusion pattern: {Pattern}", pattern);
            }
            return GetRules();
        }
    }

    public FilterRulesDto RemoveMessagePattern(string pattern)
    {
        lock (_lock)
        {
            _excludeMessagePatterns.RemoveAll(p => p.Equals(pattern, StringComparison.OrdinalIgnoreCase));
            Save();
            _logger.LogInformation("Removed message exclusion pattern: {Pattern}", pattern);
            return GetRules();
        }
    }

    public FilterRulesDto AddCategory(string category)
    {
        lock (_lock)
        {
            if (!_excludeCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
            {
                _excludeCategories.Add(category);
                Save();
                _logger.LogInformation("Added category exclusion: {Category}", category);
            }
            return GetRules();
        }
    }

    public FilterRulesDto RemoveCategory(string category)
    {
        lock (_lock)
        {
            _excludeCategories.RemoveAll(c => c.Equals(category, StringComparison.OrdinalIgnoreCase));
            Save();
            _logger.LogInformation("Removed category exclusion: {Category}", category);
            return GetRules();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var state = JsonSerializer.Deserialize<FilterRulesDto>(json, JsonOpts);
            if (state is null) return;

            _excludeMessagePatterns.Clear();
            _excludeMessagePatterns.AddRange(state.ExcludeMessagePatterns);
            _excludeCategories.Clear();
            _excludeCategories.AddRange(state.ExcludeCategories);

            _logger.LogInformation("Loaded filter rules: {Patterns} message patterns, {Categories} categories",
                _excludeMessagePatterns.Count, _excludeCategories.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to load filter rules: {Msg}", ex.Message);
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (dir is not null) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(new FilterRulesDto
            {
                ExcludeMessagePatterns = new List<string>(_excludeMessagePatterns),
                ExcludeCategories = new List<string>(_excludeCategories)
            }, JsonOpts);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to save filter rules: {Msg}", ex.Message);
        }
    }
}

public class FilterRulesDto
{
    public List<string> ExcludeMessagePatterns { get; set; } = new();
    public List<string> ExcludeCategories { get; set; } = new();
}
