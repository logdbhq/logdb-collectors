using Avalonia;
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
    private const string DetailDrawerKey = "dataSources.firewallDetailDrawer";
    private const double DetailDrawerMinWidth = 280;

    private bool _columnWidthsApplied;
    private double _detailDrawerWidth = 460;
    private DataSourcesPageViewModel? _attachedViewModel;
    private TopLevel? _topLevel;

    public DataSourcesPageView()
    {
        InitializeComponent();
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
        DataContextChanged += (_, _) => AttachViewModel();
    }

    private void OnViewLoaded(object? sender, RoutedEventArgs e)
    {
        // (Re)hooked on every load — the unload handler detaches it.
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel != null)
        {
            _topLevel.PropertyChanged += OnTopLevelPropertyChanged;
            UpdateDetailDrawerMaxHeight();
        }

        if (_columnWidthsApplied)
        {
            return;
        }

        ApplyGridColumnWidths(FirewallRulesDataGrid, RulesGridKey);
        ApplyGridColumnWidths(FirewallHistoryDataGrid, HistoryGridKey);

        var savedDrawer = WindowPlacementStore.LoadGridColumnWidths(DetailDrawerKey);
        if (savedDrawer is { Length: 1 } && savedDrawer[0] >= DetailDrawerMinWidth && savedDrawer[0] <= 2000)
        {
            _detailDrawerWidth = savedDrawer[0];
        }
        UpdateDetailDrawerColumn();

        _columnWidthsApplied = true;
    }

    private void OnViewUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_topLevel != null)
        {
            _topLevel.PropertyChanged -= OnTopLevelPropertyChanged;
            _topLevel = null;
        }

        PersistFirewallGridColumns();
    }

    private void OnTopLevelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.ClientSizeProperty)
        {
            UpdateDetailDrawerMaxHeight();
        }
    }

    /// <summary>
    /// Caps the drawer at the window's viewport height. The page host offers
    /// the tab unbounded height, so without this cap the drawer's IP lists
    /// inflate the whole tab's measured size — dragging the underlying grids
    /// taller with it. Capped, the drawer's lists scroll internally instead.
    /// 180 px accounts for navbar, tab strip, margins, and footer.
    /// </summary>
    private void UpdateDetailDrawerMaxHeight()
    {
        if (_topLevel == null)
        {
            return;
        }

        var maxHeight = Math.Max(320, _topLevel.ClientSize.Height - 180);
        FirewallDetailDrawer.MaxHeight = maxHeight;
        FirewallIpsDrawer.MaxHeight = maxHeight;
    }

    private void AttachViewModel()
    {
        if (_attachedViewModel != null)
        {
            _attachedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _attachedViewModel = DataContext as DataSourcesPageViewModel;
        if (_attachedViewModel != null)
        {
            _attachedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdateDetailDrawerColumn();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DataSourcesPageViewModel.FirewallSidePanelVisible))
        {
            UpdateDetailDrawerColumn();
        }
    }

    /// <summary>The drawer column is width-managed here rather than by the
    /// Border, so the GridSplitter has a pixel column to resize and a closed
    /// drawer truly takes zero space (a pixel column with invisible children
    /// still occupies its width; MinWidth must drop with it).</summary>
    private void UpdateDetailDrawerColumn()
    {
        var visible = _attachedViewModel?.FirewallSidePanelVisible == true;
        var column = FirewallTabLayout.ColumnDefinitions[2];
        column.MinWidth = visible ? DetailDrawerMinWidth : 0;
        column.Width = visible ? new GridLength(_detailDrawerWidth) : new GridLength(0);
    }

    private void FirewallDetailSplitter_OnDragCompleted(object? sender, VectorEventArgs e)
    {
        var width = FirewallTabLayout.ColumnDefinitions[2].ActualWidth;
        if (double.IsNaN(width) || width < DetailDrawerMinWidth)
        {
            return;
        }

        _detailDrawerWidth = Math.Round(width, 2);
        WindowPlacementStore.SaveGridColumnWidths(DetailDrawerKey, new[] { _detailDrawerWidth });
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

    private void FirewallRulesDataGrid_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is DataSourcesPageViewModel viewModel)
        {
            _ = viewModel.OpenSelectedFirewallRuleIpsAsync();
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
