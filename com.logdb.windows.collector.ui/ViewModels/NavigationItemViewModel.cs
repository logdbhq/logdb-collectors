using com.logdb.windows.collector.ui.ViewModels.Infrastructure;
using com.logdb.windows.collector.ui.ViewModels.Pages;

namespace com.logdb.windows.collector.ui.ViewModels;

public sealed class NavigationItemViewModel : ObservableObject
{
    private bool _isSelected;

    public NavigationItemViewModel(string label, string iconGlyph, PageViewModelBase page, string iconPath)
    {
        Label = label;
        IconGlyph = iconGlyph;
        Page = page;
        IconPath = iconPath;
    }

    public string Label { get; }
    public string IconGlyph { get; }
    public string IconPath { get; }
    public PageViewModelBase Page { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
