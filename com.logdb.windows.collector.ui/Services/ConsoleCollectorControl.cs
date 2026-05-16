using System.Diagnostics;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.shared.Services;

namespace com.logdb.windows.collector.ui.Services;

public sealed class ConsoleCollectorControl
{
    private readonly ControlChannelClient _controlChannelClient;

    public ConsoleCollectorControl(ControlChannelClient controlChannelClient)
    {
        _controlChannelClient = controlChannelClient;
    }

    public async Task<(bool Success, string Message)> StartAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(executablePath))
        {
            return (false, $"Collector executable not found: {executablePath}");
        }

        if (await _controlChannelClient.IsEndpointAvailableAsync(CollectorInstanceMode.Service, cancellationToken: cancellationToken))
        {
            return (false, "Service instance is running. Stop the service before starting console mode.");
        }

        if (await _controlChannelClient.IsEndpointAvailableAsync(CollectorInstanceMode.Console, cancellationToken: cancellationToken))
        {
            return (true, "Console instance is already running.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = "--console",
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to start collector: {ex.Message}");
        }

        if (process == null)
        {
            return (false, "Failed to start collector console process.");
        }

        // Drain stdout/stderr immediately to prevent pipe buffer deadlock.
        // Without this, the child process blocks when the OS pipe buffer fills.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var ready = await WaitForEndpointAsync(process, CollectorInstanceMode.Console, TimeSpan.FromSeconds(20), cancellationToken);
        if (!ready)
        {
            var diagnostics = await ReadProcessDiagnosticsAsync(process, stdoutTask, stderrTask);
            return (false, $"Console instance did not become ready. {diagnostics}".Trim());
        }

        return (true, $"Console instance started (PID {process.Id}).");
    }

    public async Task<(bool Success, string Message)> StopAsync(CancellationToken cancellationToken = default)
    {
        var isReachable = await _controlChannelClient.IsEndpointAvailableAsync(CollectorInstanceMode.Console, cancellationToken: cancellationToken);
        if (!isReachable)
        {
            return (true, "Console instance is not running.");
        }

        var response = await _controlChannelClient.SendAsync(
            CollectorInstanceMode.Console,
            ControlCommands.StopHost,
            cancellationToken: cancellationToken);

        if (!response.Success)
        {
            return (false, response.Message ?? "Failed to stop console instance.");
        }

        var stopped = await WaitUntilEndpointUnavailableAsync(CollectorInstanceMode.Console, TimeSpan.FromSeconds(15), cancellationToken);
        if (stopped)
        {
            return (true, response.Message ?? "Console instance stopped.");
        }

        if (CollectorRuntimeInfoPersistence.TryLoad(CollectorInstanceMode.Console, out var runtimeInfo)
            && runtimeInfo != null)
        {
            TryKillProcess(runtimeInfo.ProcessId);
        }

        var eventuallyStopped = await WaitUntilEndpointUnavailableAsync(CollectorInstanceMode.Console, TimeSpan.FromSeconds(5), cancellationToken);
        return eventuallyStopped
            ? (true, "Console instance stopped.")
            : (false, "Failed to stop console instance.");
    }

    private async Task<bool> WaitForEndpointAsync(
        Process process,
        CollectorInstanceMode mode,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (process.HasExited)
            {
                return false;
            }

            if (await _controlChannelClient.IsEndpointAvailableAsync(mode, cancellationToken: cancellationToken))
            {
                return true;
            }

            await Task.Delay(300, cancellationToken);
        }

        return false;
    }

    private async Task<bool> WaitUntilEndpointUnavailableAsync(
        CollectorInstanceMode mode,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (!await _controlChannelClient.IsEndpointAvailableAsync(mode, cancellationToken: cancellationToken))
            {
                return true;
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    private static void TryKillProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }

    private static async Task<string> ReadProcessDiagnosticsAsync(Process process, Task<string> stdoutTask, Task<string> stderrTask)
    {
        TryKillProcess(process.Id);

        try
        {
            // Give the drain tasks a short deadline so we never hang here
            var completed = await Task.WhenAll(
                Task.WhenAny(stdoutTask, Task.Delay(2000)),
                Task.WhenAny(stderrTask, Task.Delay(2000)));

            var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "";
            var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "";
            var output = $"{stdout} {stderr}".Trim();

            if (string.IsNullOrWhiteSpace(output))
            {
                return process.HasExited ? $"Process exit code: {process.ExitCode}." : "Process did not respond.";
            }

            return process.HasExited
                ? $"Process exit code: {process.ExitCode}. Output: {output}"
                : $"Output: {output}";
        }
        catch
        {
            return "No process diagnostics available.";
        }
    }
}
