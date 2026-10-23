using System.Windows;
using System.Windows.Media;

namespace PatchRadar.Views;

/// <summary>Applies the user's text size to a window. Everything in it scales together, including fixed-size headings.</summary>
public static class UiScale
{
    /// <summary>The size WPF menus and the windows are designed around.</summary>
    public const double BaseSize = 12;

    public static double Factor(int size) => size <= 0 ? 1.0 : size / BaseSize;

    public static void Apply(Window window, int size)
    {
        if (window.Content is not FrameworkElement root) return;
        var f = Factor(size);
        root.LayoutTransform = Math.Abs(f - 1.0) < 0.001 ? Transform.Identity : new ScaleTransform(f, f);
    }
}
