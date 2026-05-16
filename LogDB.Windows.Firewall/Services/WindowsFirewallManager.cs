using System.Diagnostics;
using System.Text;

namespace LogDB.Windows.Firewall.Services;

public class WindowsFirewallManager
{
    private readonly ILogger<WindowsFirewallManager> _logger;
    private readonly string _rulePrefix;
    private readonly string _direction;
    private readonly bool _dryRun;
    private readonly int _maxIpsPerRule;

    public WindowsFirewallManager(
        ILogger<WindowsFirewallManager> logger,
        string rulePrefix,
        string direction,
        bool dryRun,
        int maxIpsPerRule)
    {
        _logger = logger;
        _rulePrefix = rulePrefix;
        _direction = direction;
        _dryRun = dryRun;
        _maxIpsPerRule = maxIpsPerRule > 0 ? maxIpsPerRule : 5000;
    }

    /// <summary>
    /// Syncs a source's IPs into Windows Firewall rules.
    /// Creates, updates, or removes rules as needed.
    /// Returns the number of active sub-rules after sync.
    /// </summary>
    public async Task<int> SyncSourceAsync(string sourceId, string displayName, HashSet<string> ips)
    {
        var baseRuleName = $"{_rulePrefix} - {displayName}";

        if (ips.Count == 0)
        {
            // Remove all rules for this source
            var existing = await GetManagedRuleNamesForSourceAsync(baseRuleName);
            foreach (var ruleName in existing)
            {
                await RemoveRuleAsync(ruleName);
            }
            return 0;
        }

        // Split IPs into chunks
        var ipList = ips.ToList();
        var chunks = new List<List<string>>();
        for (int i = 0; i < ipList.Count; i += _maxIpsPerRule)
        {
            chunks.Add(ipList.GetRange(i, Math.Min(_maxIpsPerRule, ipList.Count - i)));
        }

        var totalChunks = chunks.Count;

        for (int i = 0; i < totalChunks; i++)
        {
            var ruleName = totalChunks == 1
                ? baseRuleName
                : $"{baseRuleName} ({i + 1}/{totalChunks})";

            var chunk = chunks[i];
            var existingIps = await GetRuleRemoteAddressesAsync(ruleName);

            if (existingIps.SetEquals(chunk.ToHashSet(StringComparer.OrdinalIgnoreCase)))
            {
                _logger.LogDebug("  Rule '{RuleName}' unchanged ({Count} IPs)", ruleName, chunk.Count);
                continue;
            }

            if (existingIps.Count > 0)
            {
                // Rule exists but IPs changed - update
                await UpdateRuleAsync(ruleName, chunk);
                _logger.LogInformation("  Updated rule '{RuleName}' with {Count} IPs", ruleName, chunk.Count);
            }
            else
            {
                // Rule doesn't exist - create
                await CreateRuleAsync(ruleName, chunk);
                _logger.LogInformation("  Created rule '{RuleName}' with {Count} IPs", ruleName, chunk.Count);
            }
        }

        // Remove surplus sub-rules (e.g., list shrank from 3 chunks to 2)
        var allExisting = await GetManagedRuleNamesForSourceAsync(baseRuleName);
        foreach (var existing in allExisting)
        {
            // Keep the base rule name and any (n/totalChunks) rules we just created
            bool isActive = existing == baseRuleName;
            if (!isActive)
            {
                for (int i = 0; i < totalChunks; i++)
                {
                    if (existing == $"{baseRuleName} ({i + 1}/{totalChunks})")
                    {
                        isActive = true;
                        break;
                    }
                }
            }

            if (!isActive)
            {
                await RemoveRuleAsync(existing);
                _logger.LogInformation("  Removed surplus rule '{RuleName}'", existing);
            }
        }

        return totalChunks;
    }

    /// <summary>
    /// Removes all firewall rules managed by this service.
    /// </summary>
    public async Task RemoveAllManagedRulesAsync()
    {
        var rules = await GetAllManagedRuleNamesAsync();
        foreach (var rule in rules)
        {
            await RemoveRuleAsync(rule);
        }
        _logger.LogInformation("Removed {Count} managed firewall rules", rules.Count);
    }

    /// <summary>
    /// Gets all managed rule names (matching the prefix).
    /// </summary>
    public async Task<List<string>> GetAllManagedRuleNamesAsync()
    {
        var cmd = $"Get-NetFirewallRule | Where-Object {{ $_.DisplayName -like '{_rulePrefix}*' }} | Select-Object -ExpandProperty DisplayName";
        var output = await RunPowerShellAsync(cmd);
        return output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
    }

    /// <summary>
    /// Checks if the current process can manage firewall rules.
    /// </summary>
    public async Task<bool> TestAdminAccessAsync()
    {
        try
        {
            await RunPowerShellAsync("Get-NetFirewallRule -PolicyStore ActiveStore | Select-Object -First 1 | Out-Null");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<List<string>> GetManagedRuleNamesForSourceAsync(string baseRuleName)
    {
        // Match exact name or name with (n/m) suffix
        var cmd = $"Get-NetFirewallRule | Where-Object {{ $_.DisplayName -like '{EscapePs(baseRuleName)}*' }} | Select-Object -ExpandProperty DisplayName";
        var output = await RunPowerShellAsync(cmd);
        return output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
    }

    private async Task<HashSet<string>> GetRuleRemoteAddressesAsync(string ruleName)
    {
        try
        {
            var cmd = $"$r = Get-NetFirewallRule -DisplayName '{EscapePs(ruleName)}' -ErrorAction SilentlyContinue; if ($r) {{ (Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $r).RemoteAddress }}";
            var output = await RunPowerShellAsync(cmd);
            var addresses = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s) && s != "Any" && s != "LocalSubnet")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return addresses;
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task CreateRuleAsync(string ruleName, List<string> remoteAddresses)
    {
        if (_dryRun)
        {
            _logger.LogWarning("[DRY RUN] Would create rule '{RuleName}' with {Count} IPs", ruleName, remoteAddresses.Count);
            return;
        }

        // Write IPs to temp file to avoid command-line length limits
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(tempFile, remoteAddresses);
            var cmd = $"$ips = Get-Content '{tempFile}'; New-NetFirewallRule -DisplayName '{EscapePs(ruleName)}' -Direction {_direction} -Action Block -RemoteAddress $ips -Enabled True -Profile Any -Description 'Managed by LogDB Windows Firewall Service'";
            await RunPowerShellAsync(cmd);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private async Task UpdateRuleAsync(string ruleName, List<string> remoteAddresses)
    {
        if (_dryRun)
        {
            _logger.LogWarning("[DRY RUN] Would update rule '{RuleName}' with {Count} IPs", ruleName, remoteAddresses.Count);
            return;
        }

        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(tempFile, remoteAddresses);
            var cmd = $"$ips = Get-Content '{tempFile}'; Set-NetFirewallRule -DisplayName '{EscapePs(ruleName)}' -RemoteAddress $ips";
            await RunPowerShellAsync(cmd);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private async Task RemoveRuleAsync(string ruleName)
    {
        if (_dryRun)
        {
            _logger.LogWarning("[DRY RUN] Would remove rule '{RuleName}'", ruleName);
            return;
        }

        var cmd = $"Remove-NetFirewallRule -DisplayName '{EscapePs(ruleName)}' -ErrorAction SilentlyContinue";
        await RunPowerShellAsync(cmd);
        _logger.LogInformation("  Removed rule '{RuleName}'", ruleName);
    }

    private async Task<string> RunPowerShellAsync(string command)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -Command \"{command}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var error = stderr.ToString().Trim();
            if (!string.IsNullOrEmpty(error))
            {
                _logger.LogError("PowerShell error (exit {Code}): {Error}", process.ExitCode, error);
                throw new InvalidOperationException($"PowerShell command failed (exit {process.ExitCode}): {error}");
            }
        }

        return stdout.ToString();
    }

    /// <summary>
    /// Escapes single quotes for PowerShell string interpolation.
    /// </summary>
    private static string EscapePs(string value) => value.Replace("'", "''");
}
