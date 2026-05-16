using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using com.logdb.windows.collector.ui.ViewModels.Pages;

namespace com.logdb.windows.collector.ui.Views.Pages;

public partial class ServiceManagementPageView : UserControl
{
    public ServiceManagementPageView()
    {
        InitializeComponent();
    }

    private async void OnBrowseCollectorExeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ServiceManagementPageViewModel viewModel)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Select collector executable",
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Executable") { Patterns = new[] { "*.exe" } }
            }
        });

        var filePath = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            viewModel.CollectorExePath = filePath;
        }
    }
}
