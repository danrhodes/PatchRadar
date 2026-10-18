using System.Windows.Media;
using PatchRadar.Core;
using PatchRadar.Settings;

namespace PatchRadar.Views;

public static class SeverityBrushes
{
    public static Brush For(Severity s, ColorSettings c)
    {
        var hex = s switch { Severity.Red => c.Red, Severity.Orange => c.Orange, Severity.Green => c.Green, _ => c.Grey };
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch (FormatException) { return Brushes.Gray; }
    }
}
