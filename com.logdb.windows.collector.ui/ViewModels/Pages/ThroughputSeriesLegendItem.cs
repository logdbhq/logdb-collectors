using Avalonia.Media;
using com.logdb.windows.collector.ui.ViewModels.Infrastructure;

namespace com.logdb.windows.collector.ui.ViewModels.Pages;

/// <summary>
/// One row in the Throughput chart's custom legend: a series name plus its colour.
/// The colour swatch is clickable (a <c>ColorView</c> flyout in the view); changing
/// <see cref="Color"/> raises <see cref="ColorChanged"/> so the page view model can
/// recolour the live series and persist the choice.
/// </summary>
public sealed class ThroughputSeriesLegendItem : ObservableObject
{
    private Color _color;

    public ThroughputSeriesLegendItem(string name, Color color)
    {
        Name = name;
        _color = color;
    }

    public string Name { get; }

    /// <summary>Bound two-way to the legend's colour picker.</summary>
    public Color Color
    {
        get => _color;
        set
        {
            if (SetProperty(ref _color, value))
            {
                NotifyPropertyChanged(nameof(SwatchBrush));
                ColorChanged?.Invoke(this);
            }
        }
    }

    /// <summary>Fill brush for the swatch shown in the legend.</summary>
    public IBrush SwatchBrush => new SolidColorBrush(_color);

    /// <summary>Raised when the user picks a new colour for this series.</summary>
    public event Action<ThroughputSeriesLegendItem>? ColorChanged;
}
