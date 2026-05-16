using System.Diagnostics;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace com.logdb.windows.collector.ui.Services;

public enum ServiceStateKind
{
    NotInstalled = 0,
    Stopped = 1,
    Running = 2,
    StartPending = 3,
    StopPending = 4,
    Unknown = 5
}

public sealed class ServiceQueryResult
{
    public bool Installed { get; init; }
    public ServiceStateKind State { get; init; }
    public string StartupType { get; init; } = "Unknown";
    public string BinaryPath { get; init; } = string.Empty;
    public string BinaryVersion { get; init; } = "Unknown";
    public string Message { get; init; } = string.Empty;
}

public static class ServiceControl
{
    public const string ServiceName = "LogDBWindowsCollector";
    public const string DisplayName = "LogDB Windows Collector";

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static async Task<(bool Success, string Message)> InstallAsync(string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            return (false, $"Collector executable not found: {executablePath}");
        }

        var createArgs =
            $"create \"{ServiceName}\" " +
            $"binPath= \"\\\"{executablePath}\\\"\" " +
            "start= auto " +
            $"DisplayName= \"{DisplayName}\"";

        var createResult = await RunAsync("sc.exe", createArgs);
        if (createResult.ExitCode != 0 && !createResult.Output.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            return (false, createResult.Output);
        }

        _ = await RunAsync("sc.exe", $"description \"{ServiceName}\" \"Unified LogDB Windows collector service\"");
        return (true, createResult.Output);
    }

    public static async Task<(bool Success, string Message)> UninstallAsync()
    {
        _ = await RunAsync("sc.exe", $"stop \"{ServiceName}\"");
        var deleteResult = await RunAsync("sc.exe", $"delete \"{ServiceName}\"");
        var success = deleteResult.ExitCode == 0 || deleteResult.Output.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase);
        return (success, deleteResult.Output);
    }

    public static async Task<(bool Success, string Message)> StartAsync()
    {
        var result = await RunAsync("sc.exe", $"start \"{ServiceName}\"");
        var success = result.ExitCode == 0 || result.Output.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase);
        return (success, result.Output);
    }

    public static async Task<(bool Success, string Message)> StopAsync()
    {
        var result = await RunAsync("sc.exe", $"stop \"{ServiceName}\"");
        var success = result.ExitCode == 0 || result.Output.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase);
        return (success, result.Output);
    }

    public static async Task<ServiceQueryResult> QueryAsync()
    {
        var result = await RunAsync("sc.exe", $"query \"{ServiceName}\"");
        var output = result.Output;
        var notInstalled = output.Contains("FAILED 1060", StringComparison.OrdinalIgnoreCase)
                           || output.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

        if (notInstalled)
        {
            return new ServiceQueryResult
            {
                Installed = false,
                State = ServiceStateKind.NotInstalled,
                StartupType = "N/A",
                BinaryPath = string.Empty,
                BinaryVersion = "NotInstalled",
                Message = output
            };
        }

        var state = ServiceStateKind.Unknown;
        if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            state = ServiceStateKind.Running;
        }
        else if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            state = ServiceStateKind.Stopped;
        }
        else if (output.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase))
        {
            state = ServiceStateKind.StartPending;
        }
        else if (output.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase))
        {
            state = ServiceStateKind.StopPending;
        }

        var startupType = "Unknown";
        var binaryPath = string.Empty;
        try
        {
            var qcResult = await RunAsync("sc.exe", $"qc \"{ServiceName}\"");
            startupType = ParseStartupType(qcResult.Output);
            binaryPath = ParseBinaryPath(qcResult.Output);
            output = $"{output}{Environment.NewLine}{qcResult.Output}";
        }
        catch
        {
            // best effort startup-type inspection
        }

        return new ServiceQueryResult
        {
            Installed = true,
            State = state,
            StartupType = startupType,
            BinaryPath = binaryPath,
            BinaryVersion = ParseExecutableVersion(binaryPath),
            Message = output
        };
    }

    private static string ParseStartupType(string output)
    {
        var line = output.Split(Environment.NewLine)
            .FirstOrDefault(value => value.Contains("START_TYPE", StringComparison.OrdinalIgnoreCase));
        if (line == null)
        {
            return "Unknown";
        }

        if (line.Contains("AUTO_START", StringComparison.OrdinalIgnoreCase))
        {
            return "Automatic";
        }

        if (line.Contains("DEMAND_START", StringComparison.OrdinalIgnoreCase))
        {
            return "Manual";
        }

        if (line.Contains("DISABLED", StringComparison.OrdinalIgnoreCase))
        {
            return "Disabled";
        }

        return "Unknown";
    }

    private static string ParseBinaryPath(string output)
    {
        var line = output.Split(Environment.NewLine)
            .FirstOrDefault(value => value.Contains("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        var separatorIndex = line.IndexOf(':');
        if (separatorIndex < 0 || separatorIndex + 1 >= line.Length)
        {
            return string.Empty;
        }

        var raw = line[(separatorIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        if (raw.StartsWith('"'))
        {
            var endQuote = raw.IndexOf('"', 1);
            if (endQuote > 1)
            {
                return raw[1..endQuote];
            }
        }

        var exeMatch = Regex.Match(raw, @"^.+?\.exe", RegexOptions.IgnoreCase);
        if (exeMatch.Success)
        {
            return exeMatch.Value.Trim('"');
        }

        return raw.Trim('"');
    }

    private static string ParseExecutableVersion(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return "Unknown";
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            return ParseVersionToken(info.ProductVersion)
                   ?? ParseVersionToken(info.FileVersion)
                   ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private static string? ParseVersionToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(value, @"\d+\.\d+\.\d+(?:\.\d+)?");
        return match.Success ? match.Value : null;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var combined = $"{stdout}{Environment.NewLine}{stderr}".Trim();
        return (process.ExitCode, combined);
    }
}
