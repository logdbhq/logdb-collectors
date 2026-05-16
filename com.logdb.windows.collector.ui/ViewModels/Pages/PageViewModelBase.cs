using com.logdb.windows.collector.ui.ViewModels.Infrastructure;

namespace com.logdb.windows.collector.ui.ViewModels.Pages;

public abstract class PageViewModelBase : ObservableObject
{
    protected PageViewModelBase(string title)
    {
        Title = title;
    }

    public string Title { get; }

    public virtual Task RefreshAsync() => Task.CompletedTask;
}
