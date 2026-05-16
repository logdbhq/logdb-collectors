using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using Avalonia.Styling;
using com.logdb.windows.collector.ui.Services;
using com.logdb.windows.collector.ui.ViewModels;

namespace com.logdb.windows.collector.ui;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly WindowPlacementStore.MainWindowPlacementDto? _loadedWindowPlacement;
    private PixelRect? _lastNormalBounds;
    private bool _windowPlacementApplied;

    public MainWindow()
    {
        InitializeComponent();
        _loadedWindowPlacement = WindowPlacementStore.LoadMainWindowPlacement();
        ApplyInitialSizeFromSettings(_loadedWindowPlacement);
        ApplyTheme(true);
        _viewModel = new MainWindowViewModel(ExportTextAsync, CopyToClipboardAsync, ApplyTheme, initialDarkTheme: true);
        _viewModel.RequestShutdown = () => Close();
        DataContext = _viewModel;
        Opened += OnOpenedAsync;
        Closing += OnClosing;
        PositionChanged += OnWindowGeometryChanged;
        SizeChanged += OnWindowGeometryChanged;
        PropertyChanged += OnWindowPropertyChanged;
    }

    private async void OnOpenedAsync(object? sender, EventArgs e)
    {
        try
        {
            ApplyWindowPlacementFromSettings();
            CaptureNormalBoundsIfNeeded();
            await _viewModel.InitializeAsync();
            await _viewModel.AutoCheckUpdatesOnStartupAsync();
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = ex.Message;
            _viewModel.StatusIsSuccess = false;
            _viewModel.StatusColor = "#F1A18F";
        }
    }

    private async Task<bool> ExportTextAsync(string suggestedFileName, string content)
    {
        if (StorageProvider == null)
        {
            return false;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedFileName,
            FileTypeChoices =
            [
                new FilePickerFileType("Text")
                {
                    Patterns = ["*.txt"]
                },
                new FilePickerFileType("JSON")
                {
                    Patterns = ["*.json"]
                }
            ]
        });

        if (file == null)
        {
            return false;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        await writer.WriteAsync(content);
        await writer.FlushAsync();
        return true;
    }

    private async Task CopyToClipboardAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard != null)
        {
            await TopLevel.GetTopLevel(this)!.Clipboard!.SetTextAsync(text);
        }
    }

    private static void ApplyTheme(bool isDark)
    {
        if (Application.Current == null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        BeginMoveDrag(e);
    }

    private void TitleBar_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestore_Click(object? sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        var placement = BuildPlacementForSave();
        WindowPlacementStore.SaveMainWindowPlacement(placement);
    }

    private void OnWindowGeometryChanged(object? sender, EventArgs e)
    {
        CaptureNormalBoundsIfNeeded();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty)
        {
            CaptureNormalBoundsIfNeeded();
        }
    }

    private void ApplyInitialSizeFromSettings(WindowPlacementStore.MainWindowPlacementDto? placement)
    {
        if (placement == null)
        {
            return;
        }

        if (placement.Width > 0)
        {
            Width = placement.Width;
        }

        if (placement.Height > 0)
        {
            Height = placement.Height;
        }
    }

    private void ApplyWindowPlacementFromSettings()
    {
        if (_windowPlacementApplied || _loadedWindowPlacement == null)
        {
            return;
        }

        var placement = _loadedWindowPlacement;
        var width = placement.Width > 0 ? placement.Width : Width;
        var height = placement.Height > 0 ? placement.Height : Height;

        if (placement.Width > 0)
        {
            Width = placement.Width;
        }

        if (placement.Height > 0)
        {
            Height = placement.Height;
        }

        if (IsPositionVisibleOnAnyScreen(placement.X, placement.Y, (int)width, (int)height))
        {
            Position = new PixelPoint(placement.X, placement.Y);
        }
        else
        {
            CenterOnPrimaryScreen((int)width, (int)height);
        }

        if (Enum.TryParse<WindowState>(placement.WindowState, ignoreCase: true, out var state))
        {
            WindowState = state == WindowState.Minimized ? WindowState.Normal : state;
        }

        _windowPlacementApplied = true;
    }

    private WindowPlacementStore.MainWindowPlacementDto BuildPlacementForSave()
    {
        var width = Width > 0 ? Width : Bounds.Width;
        var height = Height > 0 ? Height : Bounds.Height;
        var x = Position.X;
        var y = Position.Y;

        if (WindowState != WindowState.Normal)
        {
            if (_lastNormalBounds.HasValue)
            {
                var bounds = _lastNormalBounds.Value;
                width = bounds.Width;
                height = bounds.Height;
                x = bounds.X;
                y = bounds.Y;
            }
            else if (_loadedWindowPlacement != null)
            {
                width = _loadedWindowPlacement.Width;
                height = _loadedWindowPlacement.Height;
                x = _loadedWindowPlacement.X;
                y = _loadedWindowPlacement.Y;
            }
        }

        return new WindowPlacementStore.MainWindowPlacementDto
        {
            Width = Math.Max(MinWidth, width),
            Height = Math.Max(MinHeight, height),
            X = x,
            Y = y,
            WindowState = WindowState.ToString(),
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    private void CaptureNormalBoundsIfNeeded()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        var width = Width > 0 ? Width : Bounds.Width;
        var height = Height > 0 ? Height : Bounds.Height;
        _lastNormalBounds = new PixelRect(Position.X, Position.Y, Math.Max(1, (int)width), Math.Max(1, (int)height));
    }

    private bool IsPositionVisibleOnAnyScreen(int x, int y, int width, int height)
    {
        var screens = GetScreens();
        if (screens == null || screens.Count == 0)
        {
            // Without a screen list we can't verify visibility — refuse to trust
            // a saved position so a previously-disconnected monitor can't strand
            // the window off-screen. Caller will fall back to CenterOnPrimaryScreen.
            return false;
        }

        var windowRect = new PixelRect(x, y, Math.Max(1, width), Math.Max(1, height));
        foreach (var screen in screens)
        {
            var intersection = windowRect.Intersect(screen.WorkingArea);
            if (intersection.Width >= 100 && intersection.Height >= 100)
            {
                return true;
            }
        }

        return false;
    }

    private void CenterOnPrimaryScreen(int width, int height)
    {
        var screens = GetScreens();
        if (screens == null || screens.Count == 0)
        {
            Position = new PixelPoint(0, 0);
            return;
        }

        var primary = screens.FirstOrDefault(screen => screen.IsPrimary) ?? screens[0];
        var area = primary.WorkingArea;
        var x = area.X + (area.Width - width) / 2;
        var y = area.Y + (area.Height - height) / 2;
        Position = new PixelPoint(x, y);
    }

    private IReadOnlyList<Screen>? GetScreens()
    {
        if (Screens?.All is { Count: > 0 } screens)
        {
            return screens;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow?.Screens?.All;
        }

        return null;
    }
}
