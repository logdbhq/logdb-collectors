using System.Diagnostics;
using System.Security.Principal;
using com.logdb.windows.collector.shared.Contracts;

namespace com.logdb.windows.collector.Services;

public sealed class FirewallRuleApplier
{
    private readonly ILogger<FirewallRuleApplier> _logger;

    public FirewallRuleApplier(ILogger<FirewallRuleApplier> logger)
    {
        _logger = logger;
    }

    public bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public async Task<FirewallSyncResult> SyncAsync(
        FirewallConfigDto config,
        IReadOnlyList<BlockedIpEntry> blockedIps,
        CancellationToken cancellationToken = default)
    {
        if (!IsElevated())
        {
            return new FirewallSyncResult(false, "Elevation required to manage firewall rules.", 0, 0, 0);
        }

        var prefix = config.RuleNamePrefix;
        var existingRules = await GetExistingRuleNamesAsync(prefix, cancellationToken);
        var existingIps = ParseIpsFromRuleNames(prefix, existingRules);

        var desiredIps = blockedIps
            .Select(ip => ip.IpAddress)
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toAdd = desiredIps.Except(existingIps, StringComparer.OrdinalIgnoreCase).ToList();
        var toRemove = existingIps.Except(desiredIps, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var ip in toRemove)
        {
            var ruleName = BuildRuleName(prefix, ip);
            await RemoveRuleAsync(ruleName, cancellationToken);
        }

        foreach (var ip in toAdd)
        {
            var ruleName = BuildRuleName(prefix, ip);
            await AddBlockRuleAsync(ruleName, ip, cancellationToken);
        }

        var totalActive = desiredIps.Count;
        _logger.LogInformation(
            "Firewall sync complete: {Total} active, {Added} added, {Removed} removed",
            totalActive, toAdd.Count, toRemove.Count);

        return new FirewallSyncResult(true, $"Synced {totalActive} blocked IP(s).", totalActive, toAdd.Count, toRemove.Count);
    }

    public async Task<(bool Success, string Message)> RemoveAllAsync(
        FirewallConfigDto config,
        CancellationToken cancellationToken = default)
    {
        if (!IsElevated())
        {
            return (false, "Elevation required to remove firewall rules.");
        }

        var prefix = config.RuleNamePrefix;
        await RunPowerShellAsync(
            $"Get-NetFirewallRule -DisplayName '{Escape(prefix)}*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule",
            cancellationToken);

        _logger.LogInformation("Removed all firewall rules with prefix {Prefix}", prefix);
        return (true, "All firewall block rules removed.");
    }

    private async Task<List<string>> GetExistingRuleNamesAsync(string prefix, CancellationToken cancellationToken)
    {
        var output = await RunPowerShellWithOutputAsync(
            $"Get-NetFirewallRule -DisplayName '{Escape(prefix)}*' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty DisplayName",
            cancellationToken);

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }

    private static HashSet<string> ParseIpsFromRuleNames(string prefix, IEnumerable<string> ruleNames)
    {
        var ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expectedPrefix = prefix + ": ";
        foreach (var name in ruleNames)
        {
            if (name.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var ip = name[expectedPrefix.Length..].Trim();
                if (!string.IsNullOrWhiteSpace(ip))
                {
                    ips.Add(ip);
                }
            }
        }

        return ips;
    }

    private static string BuildRuleName(string prefix, string ip)
    {
        return $"{prefix}: {ip}";
    }

    private async Task AddBlockRuleAsync(string ruleName, string ip, CancellationToken cancellationToken)
    {
        var command =
            "New-NetFirewallRule " +
            $"-DisplayName '{Escape(ruleName)}' " +
            "-Direction Inbound " +
            "-Action Block " +
            $"-RemoteAddress '{Escape(ip)}' " +
            "-Profile Any";

        await RunPowerShellAsync(command, cancellationToken);
    }

    private async Task RemoveRuleAsync(string ruleName, CancellationToken cancellationToken)
    {
        await RunPowerShellAsync(
            $"Get-NetFirewallRule -DisplayName '{Escape(ruleName)}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule",
            cancellationToken);
    }

    private async Task RunPowerShellAsync(string command, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        await process.WaitForExitAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"PowerShell command failed: {stderr}");
        }
    }

    private async Task<string> RunPowerShellWithOutputAsync(string command, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return stdout;
    }

    private static string Escape(string value)
    {
        return value.Replace("'", "''");
    }
}

public record BlockedIpEntry(string IpAddress, string AddedBy, DateTime AddedAt, string? Reason);

public record FirewallSyncResult(bool Success, string Message, int TotalActive, int Added, int Removed);
