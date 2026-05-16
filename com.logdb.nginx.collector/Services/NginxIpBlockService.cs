using System.Diagnostics;
using System.Text;
using System.Text.Json;
using com.logdb.nginx.collector.Configuration;
using Microsoft.Extensions.Options;

namespace com.logdb.nginx.collector.Services;

public class NginxIpBlockService
{
    private readonly ILogger<NginxIpBlockService> _logger;
    private readonly FilterRuleService _filterRules;
    private readonly NginxBlockOptions _options;
    private readonly string _statePath;
    private readonly object _lock = new();
    private readonly HashSet<string> _blockedIps = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public NginxIpBlockService(
        ILogger<NginxIpBlockService> logger,
        IOptions<NginxBlockOptions> options,
        IOptions<CheckpointOptions> checkpointOptions,
        FilterRuleService filterRules)
    {
        _logger = logger;
        _options = options.Value;
        _filterRules = filterRules;

        var checkpointDir = Path.GetDirectoryName(Path.GetFullPath(checkpointOptions.Value.FilePath)) ?? ".";
        _statePath = Path.Combine(checkpointDir, "nginx-blocked-ips.json");

        Load();
    }

    public bool IsEnabled => _options.Enabled;

    public List<string> GetBlockedIps()
    {
        lock (_lock)
        {
            return _blockedIps.OrderBy(ip => ip).ToList();
        }
    }

    public bool IsBlocked(string ip)
    {
        lock (_lock)
        {
            return _blockedIps.Contains(ip);
        }
    }

    public (bool Success, string? Error) BlockIp(string ip)
    {
        lock (_lock)
        {
            if (!_options.Enabled)
                return (false, "Nginx IP blocking is not enabled. Set NginxBlock:Enabled to true and ensure the container has access to nginx config.");

            if (!_blockedIps.Add(ip))
                return (true, null); // already blocked

            _filterRules.AddExcludeRemoteAddress(ip);

            var (ok, error) = WriteDenyFileAndReload();
            if (!ok)
            {
                _blockedIps.Remove(ip);
                return (false, error);
            }

            Save();
            _logger.LogInformation("Blocked IP at nginx level: {Ip}", ip);
            return (true, null);
        }
    }

    public (bool Success, string? Error) UnblockIp(string ip)
    {
        lock (_lock)
        {
            if (!_options.Enabled)
                return (false, "Nginx IP blocking is not enabled.");

            if (!_blockedIps.Remove(ip))
                return (true, null); // wasn't blocked

            _filterRules.RemoveExcludeRemoteAddress(ip);

            var (ok, error) = WriteDenyFileAndReload();
            if (!ok)
            {
                _blockedIps.Add(ip);
                return (false, error);
            }

            Save();
            _logger.LogInformation("Unblocked IP at nginx level: {Ip}", ip);
            return (true, null);
        }
    }

    private (bool Success, string? Error) WriteDenyFileAndReload()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Managed by LogDB Nginx Collector - do not edit manually");
            sb.AppendLine($"# Last updated: {DateTime.UtcNow:O}");
            sb.AppendLine();

            foreach (var ip in _blockedIps.OrderBy(i => i))
            {
                sb.AppendLine($"deny {ip};");
            }

            var dir = Path.GetDirectoryName(_options.DenyFilePath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(_options.DenyFilePath, sb.ToString());

            _logger.LogInformation("Wrote {Count} deny rules to {Path}", _blockedIps.Count, _options.DenyFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write nginx deny file at {Path}", _options.DenyFilePath);
            return (false, $"Failed to write deny file: {ex.Message}");
        }

        try
        {
            var parts = _options.ReloadCommand.Split(' ', 2);
            var psi = new ProcessStartInfo
            {
                FileName = parts[0],
                Arguments = parts.Length > 1 ? parts[1] : "",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
                return (false, "Failed to start nginx reload process");

            process.WaitForExit(TimeSpan.FromSeconds(10));

            if (process.ExitCode != 0)
            {
                var stderr = process.StandardError.ReadToEnd();
                _logger.LogError("Nginx reload failed (exit {Code}): {Error}", process.ExitCode, stderr);
                return (false, $"Nginx reload failed: {stderr}");
            }

            _logger.LogInformation("Nginx reloaded successfully");
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reload nginx");
            return (false, $"Failed to reload nginx: {ex.Message}");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var json = File.ReadAllText(_statePath);
            var ips = JsonSerializer.Deserialize<List<string>>(json, JsonOpts);
            if (ips is null) return;

            foreach (var ip in ips)
                _blockedIps.Add(ip);

            _logger.LogInformation("Loaded {Count} blocked IPs from state", _blockedIps.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load blocked IPs from {Path}", _statePath);
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_blockedIps.OrderBy(ip => ip).ToList(), JsonOpts);
            var dir = Path.GetDirectoryName(_statePath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(_statePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save blocked IPs to {Path}", _statePath);
        }
    }
}
