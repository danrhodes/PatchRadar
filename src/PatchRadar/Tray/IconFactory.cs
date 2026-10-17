using System.Windows;
using PatchRadar.Core;

namespace PatchRadar.Tray;

public static class IconFactory
{
    private static readonly Dictionary<Severity, System.Drawing.Icon> Cache = new();

    public static System.Drawing.Icon For(Severity s)
    {
        if (Cache.TryGetValue(s, out var icon)) return icon;
        var name = s switch { Severity.Red => "red", Severity.Orange => "orange", Severity.Green => "green", _ => "grey" };
        var info = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/tray-{name}.ico"));
        icon = new System.Drawing.Icon(info.Stream);
        Cache[s] = icon;
        return icon;
    }
}
