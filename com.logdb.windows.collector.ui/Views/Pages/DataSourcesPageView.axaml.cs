using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using com.logdb.windows.collector.ui.Services;
using com.logdb.windows.collector.ui.ViewModels.Pages;

namespace com.logdb.windows.collector.ui.Views.Pages;

public partial class DataSourcesPageView : UserControl
{
    private const string RulesGridKey = "dataSources.firewallRules";
    private const string HistoryGridKey = "dataSources.firewallHistory";

    private bool _columnWidthsApplied;

    public DataSourcesPageView()
    {
        InitializeComponent();
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
    }

    private void OnViewLoaded(object? sender, RoutedEventArgs e)
    {
        if (_columnWidthsApplied)
        {
            return;
        }

        ApplyGridColumnWidths(FirewallRulesDataGrid, RulesGridKey);
        ApplyGridColumnWidths(FirewallHistoryDataGrid, HistoryGridKey);
        _columnWidthsApplied = true;
    }

    private void OnViewUnloaded(object? sender, RoutedEventArgs e)
    {
        PersistFirewallGridColumns();
    }

    private void FirewallGrid_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        PersistFirewallGridColumns();
    }

    private void FirewallHistoryDataGrid_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is DataSourcesPageViewModel viewModel)
        {
            viewModel.OpenFirewallHistoryDetail();
        }
    }

    private async void FirewallDetailCopy_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DataSourcesPageViewModel viewModel)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(viewModel.BuildFirewallDetailClipboardText());
        }
    }

    private void PersistFirewallGridColumns()
    {
        PersistGridColumnWidths(FirewallRulesDataGrid, RulesGridKey);
        PersistGridColumnWidths(FirewallHistoryDataGrid, HistoryGridKey);
    }

    private static void ApplyGridColumnWidths(DataGrid grid, string gridKey)
    {
        var saved = WindowPlacementStore.LoadGridColumnWidths(gridKey);
        if (saved == null || saved.Length != grid.Columns.Count)
        {
            return;
        }

        for (var i = 0; i < grid.Columns.Count; i++)
        {
            if (saved[i] > 16)
            {
                grid.Columns[i].Width = new DataGridLength(saved[i], DataGridLengthUnitType.Pixel);
            }
        }
    }

    private static void PersistGridColumnWidths(DataGrid grid, string gridKey)
    {
        if (grid.Columns.Count == 0)
        {
            return;
        }

        var widths = new double[grid.Columns.Count];
        for (var i = 0; i < grid.Columns.Count; i++)
        {
            widths[i] = GetColumnWidth(grid.Columns[i]);
        }

        // All zeros means the grid has never been measured (tab never opened) —
        // don't overwrite a good saved layout with garbage.
        if (widths.All(w => w <= 0))
        {
            return;
        }

        WindowPlacementStore.SaveGridColumnWidths(gridKey, widths);
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

    private async void OnPickFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DataSourcesPageViewModel viewModel)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Select IIS log directory"
        });

        var folderPath = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            viewModel.AddIisDirectoryFromPicker(folderPath);
        }
    }
}
