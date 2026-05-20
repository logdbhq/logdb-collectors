using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using com.logdb.windows.collector.ui.Services;

namespace com.logdb.windows.collector.ui;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();

            // Background update check fires once the main window is visible —
            // we wait for MainWindow.Opened so the prompt has something to be
            // owned by (and so the prompt doesn't blink before the UI even paints).
            desktop.MainWindow.Opened += async (_, _) => await CheckForUpdatesAsync(desktop.MainWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task CheckForUpdatesAsync(Window owner)
    {
        // Give the UI a beat to settle before doing network I/O.
        await Task.Delay(5000);

        var updater = new UpdaterService();
        var info = await updater.CheckAsync();
        if (info == null) return; // up-to-date, not in a Velopack install, or feed unreachable

        var newVersion = info.TargetFullRelease.Version.ToString();
        var currentVersion = updater.CurrentVersion ?? "(unknown)";

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var accepted = await ShowUpdatePromptAsync(owner, currentVersion, newVersion);
            if (!accepted) return;

            // Re-launch elevated. The elevated instance does the full download+apply
            // via Program.ApplyPendingUpdateBlocking (CLI flag --apply-update). The
            // user instance can exit immediately so file-locks aren't an issue;
            // Velopack will restart the (now-updated) UI after the swap.
            if (UpdaterService.RelaunchElevatedToApplyUpdate())
            {
                if (Application.Current?.ApplicationLifetime
                    is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
            }
        });
    }

    /// <summary>
    /// Minimal in-code modal — avoids a new .axaml file. Two buttons: "Install now"
    /// (which triggers UAC + apply), and "Later" (dismiss; the check fires again next
    /// UI launch).
    /// </summary>
    private static Task<bool> ShowUpdatePromptAsync(Window owner, string currentVersion, string newVersion)
    {
        var tcs = new TaskCompletionSource<bool>();

        var window = new Window
        {
            Title = "LogDB Collector — Update available",
            Width = 460,
            Height = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SystemDecorations = SystemDecorations.Full,
            ShowInTaskbar = false
        };

        var installButton = new Button { Content = "Install now", MinWidth = 120, IsDefault = true };
        var laterButton = new Button { Content = "Later", MinWidth = 120, IsCancel = true };

        installButton.Click += (_, _) => { tcs.TrySetResult(true); window.Close(); };
        laterButton.Click += (_, _) => { tcs.TrySetResult(false); window.Close(); };
        window.Closed += (_, _) => tcs.TrySetResult(false);

        window.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"A new version of the LogDB Windows Collector is available.",
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 15
                },
                new TextBlock
                {
                    Text = $"Currently installed: {currentVersion}\nAvailable: {newVersion}",
                    Foreground = Brushes.Gray
                },
                new TextBlock
                {
                    Text = "Installing requires Administrator privileges. The Windows service " +
                           "will be stopped briefly, updated, and restarted automatically.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Gray
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { laterButton, installButton }
                }
            }
        };

        _ = window.ShowDialog(owner);
        return tcs.Task;
    }
}
