using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using PatchRadar.Core;

namespace PatchRadar.Tray;

public static class IconFactory
{
    private static readonly Dictionary<(Severity, int), Icon> Cache = new();

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>The severity icon, with a small count badge when updates are waiting (9 shows as "9+" past nine).</summary>
    public static Icon For(Severity s, int pending = 0)
    {
        pending = Math.Clamp(pending, 0, 10);
        // The tray control disposes the icon it is replaced with, so it must never be handed the cached instance:
        // the cache keeps the master copy and every caller gets its own clone.
        if (Cache.TryGetValue((s, pending), out var icon)) return (Icon)icon.Clone();
        var name = s switch { Severity.Red => "red", Severity.Orange => "orange", Severity.Green => "green", _ => "grey" };
        var info = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/tray-{name}.ico"));
        var plain = new Icon(info.Stream);
        icon = pending == 0 ? plain : WithBadge(plain, pending);
        Cache[(s, pending)] = icon;
        return (Icon)icon.Clone();
    }

    private static Icon WithBadge(Icon plain, int count)
    {
        try
        {
            using var bmp = plain.ToBitmap();
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                var d = Math.Max(8, bmp.Width * 11 / 16);
                var rect = new Rectangle(bmp.Width - d, bmp.Height - d, d, d);
                using var fill = new SolidBrush(Color.FromArgb(0x1F, 0x6F, 0xEB));
                using var ring = new Pen(Color.White, Math.Max(1, bmp.Width / 16f));
                g.FillEllipse(fill, rect);
                g.DrawEllipse(ring, rect);
                var text = count > 9 ? "9+" : count.ToString();
                using var font = new Font("Segoe UI", Math.Max(5f, d * (text.Length > 1 ? 0.42f : 0.58f)), System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(text, font, Brushes.White, new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), format);
            }
            var handle = bmp.GetHicon();
            try { return (Icon)Icon.FromHandle(handle).Clone(); }
            finally { DestroyIcon(handle); }
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException)
        {
            return plain; // a missing badge is better than a missing icon
        }
    }
}
