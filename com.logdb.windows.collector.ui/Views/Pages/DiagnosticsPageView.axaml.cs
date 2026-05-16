using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using com.logdb.windows.collector.ui.Services;
using com.logdb.windows.collector.ui.ViewModels.Pages;

namespace com.logdb.windows.collector.ui.Views.Pages;

public partial class DiagnosticsPageView : UserControl
{
    private bool _columnWidthsApplied;

    public DiagnosticsPageView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        OnlineConsoleDataGrid.LoadingRow += OnlineConsoleDataGrid_LoadingRow;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_columnWidthsApplied)
        {
            return;
        }

        ApplyOnlineConsoleColumnWidths();
        _columnWidthsApplied = true;
    }

    private void OnUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        PersistOnlineConsoleColumnWidths();
    }

    private void OnlineConsoleDataGrid_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is OnlineDiagnosticRowViewModel row && row.RowForeground is { } brush)
        {
            e.Row.Foreground = brush;
        }
    }

    private void OnlineConsoleDataGrid_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        PersistOnlineConsoleColumnWidths();
    }

    private void ApplyOnlineConsoleColumnWidths()
    {
        if (OnlineConsoleDataGrid.Columns.Count < 4)
        {
            return;
        }

        var saved = WindowPlacementStore.LoadDiagnosticsOnlineColumns();
        if (saved == null)
        {
            return;
        }

        SetColumnWidth(OnlineConsoleDataGrid.Columns[0], saved.Time, 170);
        SetColumnWidth(OnlineConsoleDataGrid.Columns[1], saved.Level, 90);
        SetColumnWidth(OnlineConsoleDataGrid.Columns[2], saved.Category, 90);
        SetColumnWidth(OnlineConsoleDataGrid.Columns[3], saved.Message, 640);
    }

    private void PersistOnlineConsoleColumnWidths()
    {
        if (OnlineConsoleDataGrid.Columns.Count < 4)
        {
            return;
        }

        var columns = new WindowPlacementStore.DiagnosticsOnlineColumnsDto
        {
            Time = GetColumnWidth(OnlineConsoleDataGrid.Columns[0], 170),
            Level = GetColumnWidth(OnlineConsoleDataGrid.Columns[1], 90),
            Category = GetColumnWidth(OnlineConsoleDataGrid.Columns[2], 90),
            Message = GetColumnWidth(OnlineConsoleDataGrid.Columns[3], 640),
            UpdatedAtUtc = DateTime.UtcNow
        };

        WindowPlacementStore.SaveDiagnosticsOnlineColumns(columns);
    }

    private static double GetColumnWidth(DataGridColumn column, double fallback)
    {
        var actualWidthProperty = column.GetType().GetProperty("ActualWidth");
        if (actualWidthProperty?.GetValue(column) is double actualWidth
            && !double.IsNaN(actualWidth)
            && actualWidth > 16)
        {
            return Math.Round(actualWidth, 2);
        }

        var width = column.Width;
        if (width.IsAbsolute && width.Value > 16)
        {
            return Math.Round(width.Value, 2);
        }

        return fallback;
    }

    private static void SetColumnWidth(DataGridColumn column, double requestedWidth, double fallback)
    {
        var width = requestedWidth > 16 ? requestedWidth : fallback;
        column.Width = new DataGridLength(width, DataGridLengthUnitType.Pixel);
    }
}
