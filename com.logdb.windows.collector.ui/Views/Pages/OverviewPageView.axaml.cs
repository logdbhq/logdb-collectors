using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace com.logdb.windows.collector.ui.Views.Pages;

public partial class OverviewPageView : UserControl
{
    public OverviewPageView()
    {
        InitializeComponent();

        // The "Recent Failures" drill-down opens at the bottom of a tall,
        // scrollable page — below the fold. When it becomes visible (e.g. via
        // the clickable "Critical Issues" card) scroll it into view so the
        // click produces an obvious, on-screen result.
        FailuresPanel.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty
                && e.NewValue is true)
            {
                Dispatcher.UIThread.Post(
                    () => FailuresPanel.BringIntoView(),
                    DispatcherPriority.Loaded);
            }
        };
    }
}
