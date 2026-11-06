using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PatchRadar.Infra;

public static class WindowCapture
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect r);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out Rect r, int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    public static void SavePng(Window w, string path)
    {
        var png = ToBitmap(w);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(png));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>The window as the user sees it, title bar included, even if something overlaps it. Only this window, never the desktop.</summary>
    public static BitmapSource ToBitmap(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        GetWindowRect(hwnd, out var outer);
        var frame = DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var f, Marshal.SizeOf<Rect>()) == 0 ? f : outer;
        int width = outer.Right - outer.Left, height = outer.Bottom - outer.Top;

        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(dc, bmp);
        try
        {
            PrintWindow(hwnd, dc, 2 /* PW_RENDERFULLCONTENT */);
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var opaque = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
            var crop = new Int32Rect(Math.Max(0, frame.Left - outer.Left), Math.Max(0, frame.Top - outer.Top),
                Math.Min(frame.Right - frame.Left, width), Math.Min(frame.Bottom - frame.Top, height));
            var cropped = new CroppedBitmap(opaque, crop);
            cropped.Freeze();
            return cropped;
        }
        finally
        {
            SelectObject(dc, old); DeleteObject(bmp); DeleteDC(dc); ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
