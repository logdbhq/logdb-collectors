using com.logdb.windows.collector.shared.Contracts;

namespace com.logdb.windows.collector.ui.ViewModels;

public sealed class TargetOptionViewModel
{
    public TargetOptionViewModel(CollectorInstanceMode mode, string label)
    {
        Mode = mode;
        Label = label;
    }

    public CollectorInstanceMode Mode { get; }
    public string Label { get; }

    public override string ToString() => Label;
}
