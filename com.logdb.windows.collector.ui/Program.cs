using Avalonia;
using com.logdb.windows.collector.ui.Services;
using Velopack;

namespace com.logdb.windows.collector.ui;

class Program
{
    /// <summary>
    /// CLI flag the user-mode UI uses to re-launch itself elevated when the operator
    /// accepts an available update. The elevated instance runs the full apply-update
    /// flow (download if not already staged, stop service, ApplyUpdatesAndRestart,
    /// start service via the Velopack OnAfter hook), then exits.
    /// </summary>
    public const string ApplyUpdateArg = "--apply-update";

    [STAThread]
    public static void Main(string[] args)
    {
        // VelopackApp.Build().Run() handles all the lifecycle "fast" callbacks
        // (--veloapp-* CLI flags that Velopack's installer/updater invokes the exe
        // with) and exits early if any matched. The OnBeforeUpdate / OnAfterUpdate
        // hooks fire from a child updater process during the apply phase — the
        // service stop/start MUST live there because the main UI process has
        // already exited by the time Velopack starts swapping files. They run
        // with whatever permissions the apply was kicked off with, which is why
        // we re-launch elevated before calling ApplyUpdatesAndRestart.
        VelopackApp.Build()
            .OnFirstRun(version =>
            {
                Console.WriteLine($"Collector UI first run: {version}");
            })
            .OnRestarted(version =>
            {
                Console.WriteLine($"Collector UI restarted after update: {version}");
            })
            .OnBeforeUpdateFastCallback(version =>
            {
                // Stop the LogDBWindowsCollector service before Velopack swaps files.
                // Wherever SCM has the service registered (Velopack-managed
                // <install>\current\service\, or a custom path like
                // C:\LogDB.Collector\service\), the running service holds file locks
                // that prevent overwrite. Stopping it now lets BOTH the Velopack swap
                // AND the ServiceBundleSync.SyncFromCurrentBundle() copy in OnAfter
                // succeed.
                Console.WriteLine($"Velopack pre-update hook: stopping {ServiceController.ServiceName} before swap to {version}");
                var stopped = ServiceController.Stop();
                Console.WriteLine(stopped
                    ? $"Service stopped — proceeding with file swap to {version}"
                    : $"WARNING: failed to stop service cleanly; Velopack will attempt swap anyway and may fail");
            })
            .OnAfterUpdateFastCallback(version =>
            {
                // Velopack has swapped <install>\current\ to the new version. The
                // service binaries inside <install>\current\service\ are now also new,
                // but if SCM points at a path OUTSIDE that dir (e.g. a custom-installer
                // location like C:\LogDB.Collector\service), Velopack's swap never
                // reached the actually-running service. ServiceBundleSync overlays the
                // fresh in-bundle binaries onto the SCM-registered location while the
                // service is still stopped, so the restart below picks up the new bits.
                // No-op when SCM already points inside the bundle.
                Console.WriteLine($"Velopack post-update hook: syncing service binaries for {version}");
                var sync = ServiceBundleSync.SyncFromCurrentBundle(msg => Console.WriteLine($"  {msg}"));
                Console.WriteLine($"  -> [{sync.Outcome}] {sync.Message}");

                Console.WriteLine($"Velopack post-update hook: starting {ServiceController.ServiceName} on {version}");
                var started = ServiceController.Start();
                Console.WriteLine(started
                    ? $"Service restarted on {version}"
                    : $"WARNING: service failed to start after update — operator must investigate");
            })
            .Run();

        // The --apply-update path: re-launched-as-admin instance. Runs the full
        // download+apply flow synchronously and exits. The OnBeforeUpdate /
        // OnAfterUpdate hooks above will fire inside the child updater process
        // that Velopack spawns from ApplyUpdatesAndRestart, inheriting our admin
        // token so sc stop/start succeed.
        if (args.Length > 0 && args[0].Equals(ApplyUpdateArg, StringComparison.OrdinalIgnoreCase))
        {
            ApplyPendingUpdateBlocking();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void ApplyPendingUpdateBlocking()
    {
        try
        {
            var updater = new UpdaterService();
            var info = updater.CheckAsync().GetAwaiter().GetResult();
            if (info == null)
            {
                Console.WriteLine("No update available — nothing to apply.");
                return;
            }

            Console.WriteLine($"Downloading update: {info.TargetFullRelease.Version}");
            updater.DownloadAsync(info).GetAwaiter().GetResult();

            Console.WriteLine("Applying update — service will be stopped, files swapped, service restarted, UI relaunched.");
            // ApplyUpdatesAndRestart never returns under normal conditions — Velopack
            // exits this process and starts the updated UI exe afterwards.
            updater.ApplyAndRestart(info);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Apply-update failed: {ex.Message}");
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
