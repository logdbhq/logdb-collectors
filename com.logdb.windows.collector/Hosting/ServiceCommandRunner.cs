using System.Diagnostics;

namespace com.logdb.windows.collector.Hosting;

public static class ServiceCommandRunner
{
    private const string ServiceName = "LogDBWindowsCollector";
    private const string DisplayName = "LogDB Windows Collector";

    public static async Task<int> ExecuteAsync(string[] args, string executablePath, TextWriter output, CancellationToken cancellationToken)
    {
        if (args.Any(arg => string.Equals(arg, "--install-service", StringComparison.OrdinalIgnoreCase)))
        {
            var installResult = await InstallAsync(executablePath);
            await output.WriteLineAsync(installResult.Message);
            return installResult.Success ? 0 : 1;
        }

        if (args.Any(arg => string.Equals(arg, "--uninstall-service", StringComparison.OrdinalIgnoreCase)))
        {
            var uninstallResult = await UninstallAsync();
            await output.WriteLineAsync(uninstallResult.Message);
            return uninstallResult.Success ? 0 : 1;
        }

        if (args.Any(arg => string.Equals(arg, "--start-service", StringComparison.OrdinalIgnoreCase)))
        {
            var startResult = await StartAsync();
            await output.WriteLineAsync(startResult.Message);
            return startResult.Success ? 0 : 1;
        }

        if (args.Any(arg => string.Equals(arg, "--stop-service", StringComparison.OrdinalIgnoreCase)))
        {
            var stopResult = await StopAsync();
            await output.WriteLineAsync(stopResult.Message);
            return stopResult.Success ? 0 : 1;
        }

        return -1;
    }

    private static async Task<(bool Success, string Message)> InstallAsync(string executablePath)
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

    private static async Task<(bool Success, string Message)> UninstallAsync()
    {
        _ = await RunAsync("sc.exe", $"stop \"{ServiceName}\"");
        var deleteResult = await RunAsync("sc.exe", $"delete \"{ServiceName}\"");
        var success = deleteResult.ExitCode == 0 || deleteResult.Output.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase);
        return (success, deleteResult.Output);
    }

    private static async Task<(bool Success, string Message)> StartAsync()
    {
        var result = await RunAsync("sc.exe", $"start \"{ServiceName}\"");
        var success = result.ExitCode == 0 || result.Output.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase);
        return (success, result.Output);
    }

    private static async Task<(bool Success, string Message)> StopAsync()
    {
        var result = await RunAsync("sc.exe", $"stop \"{ServiceName}\"");
        var success = result.ExitCode == 0 || result.Output.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase);
        return (success, result.Output);
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
