using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Styling;

namespace com.logdb.windows.collector.ui.Classes.Converters;

public sealed class IconCssBySelectedConverter : IValueConverter
{
    private const string ActiveCss = "svg { color: #007ACC; } .Icon { fill: #007ACC; }";
    private const string DarkDefaultCss = "svg { color: #A1A1A1; } .Icon { fill: #A1A1A1; }";
    private const string LightDefaultCss = "svg { color: #6B7F96; } .Icon { fill: #6B7F96; }";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isSelected && isSelected)
        {
            return ActiveCss;
        }

        var isLight = Application.Current?.ActualThemeVariant == ThemeVariant.Light;
        return isLight ? LightDefaultCss : DarkDefaultCss;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
