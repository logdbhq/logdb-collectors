using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using com.logdb.windows.collector.ui.Services;
using com.logdb.windows.collector.ui.ViewModels.Pages;

namespace com.logdb.windows.collector.ui.Views.Pages;

public partial class DiagnosticsPageView : UserControl
{
    private const string ConsoleGridKey = "diagnostics.console";

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

    /// <summary>
    /// Loads the selected tab's data on entry. Throughput and Recent records
    /// are separate view models that the page's own RefreshAsync doesn't touch,
    /// so without this they showed whatever was fetched when the app last
    /// happened to refresh them — usually an empty chart until you pressed
    /// Refresh yourself.
    /// </summary>
    private void DiagnosticsTabs_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not DiagnosticsPageViewModel viewModel)
        {
            return;
        }

        var header = (DiagnosticsTabs.SelectedItem as TabItem)?.Header as string;
        switch (header)
        {
            case "Throughput":
                _ = viewModel.Throughput.RefreshAsync();
                break;
            case "Recent records":
                _ = viewModel.RecentRecords.RefreshAsync();
                break;
        }
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

    /// <summary>
    /// Widths are persisted positionally through the shared grid store, which
    /// only restores when the saved count still matches the grid. The previous
    /// scheme mapped four named fields onto columns 0-3 and had already drifted
    /// (an "Event Time" column was inserted at index 1), so Level's width was
    /// being applied to Event Time, Category's to Level, and so on. Adding the
    /// Collection column would have skewed it further.
    /// </summary>
    private void ApplyOnlineConsoleColumnWidths()
    {
        var saved = WindowPlacementStore.LoadGridColumnWidths(ConsoleGridKey);
        if (saved == null || saved.Length != OnlineConsoleDataGrid.Columns.Count)
        {
            return;
        }

        for (var i = 0; i < OnlineConsoleDataGrid.Columns.Count; i++)
        {
            if (saved[i] > 16)
            {
                OnlineConsoleDataGrid.Columns[i].Width = new DataGridLength(saved[i], DataGridLengthUnitType.Pixel);
            }
        }
    }

    private void PersistOnlineConsoleColumnWidths()
    {
        if (OnlineConsoleDataGrid.Columns.Count == 0)
        {
            return;
        }

        var widths = new double[OnlineConsoleDataGrid.Columns.Count];
        for (var i = 0; i < widths.Length; i++)
        {
            widths[i] = GetColumnWidth(OnlineConsoleDataGrid.Columns[i]);
        }

        // All zeros means the grid was never measured — don't overwrite a good
        // saved layout with garbage.
        if (widths.All(w => w <= 0))
        {
            return;
        }

        WindowPlacementStore.SaveGridColumnWidths(ConsoleGridKey, widths);
    }

    private static double GetColumnWidth(DataGridColumn column)
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

        return 0;
    }
}
