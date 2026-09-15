using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace RoboArm.App.Infrastructure;

/// <summary>bool → Brush (drive LED: enabled green, disabled gray).</summary>
public sealed class BoolToBrushConverter : System.Windows.Data.IValueConverter
{
    public Brush TrueBrush { get; set; } = Brushes.ForestGreen;
    public Brush FalseBrush { get; set; } = Brushes.Gray;

    public object Convert(object? value, Type targetType, object? parameter,
        System.Globalization.CultureInfo culture) =>
        value is true ? TrueBrush : FalseBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter,
        System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
