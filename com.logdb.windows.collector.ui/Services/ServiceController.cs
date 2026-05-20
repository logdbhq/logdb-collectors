using System.Diagnostics;
using System.Security.Principal;

namespace com.logdb.windows.collector.ui.Services;

/// <summary>
/// Thin wrapper over `sc.exe` for the LogDB Windows Collector service. Used by the
/// Velopack update flow (in <see cref="Program.Main"/>) to stop the service before
/// the file swap and restart it after — without that step Velopack fails to replace
/// the running service exe (Windows file lock).
///
/// All mutating operations require admin. Callers must check <see cref="IsElevated"/>
/// first — the update flow re-launches the UI elevated specifically so these methods
/// succeed. We invoke sc.exe rather than referencing System.ServiceProcess.ServiceController
/// so the UI doesn't pull in an extra NuGet just for two RPC calls.
/// </summary>
public static class ServiceController
{
    public const string ServiceName = "LogDBWindowsCollector";

    public static bool IsElevated
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static bool IsInstalled()
    {
        // `sc query <name>` exits 0 if the service exists, 1060 if not.
        return RunSc("query " + ServiceName, out _) == 0;
    }

    public static bool Stop(TimeSpan? timeout = null)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!IsInstalled()) return true; // nothing to stop
        // `sc stop` is async; poll until STOPPED or timeout.
        _ = RunSc("stop " + ServiceName, out _);
        return WaitForState("STOPPED", timeout ?? TimeSpan.FromSeconds(30));
    }

    public static bool Start(TimeSpan? timeout = null)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!IsInstalled()) return false;
        _ = RunSc("start " + ServiceName, out _);
        return WaitForState("RUNNING", timeout ?? TimeSpan.FromSeconds(30));
    }

    private static bool WaitForState(string targetState, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (RunSc("query " + ServiceName, out var output) == 0 &&
                output.Contains(targetState, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            Thread.Sleep(500);
        }
        return false;
    }

    private static int RunSc(string args, out string stdoutAndErr)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            stdoutAndErr = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            stdoutAndErr = ex.Message;
            return -1;
        }
    }
}
