using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace NewsWatch;

/// <summary>
/// Draws the app icon at runtime (no .ico asset): a cyan page with headline
/// bars, so it sits next to PULSE//WATCH's cyan ring without being mistaken for it.
/// </summary>
public static class AppIcon
{
    /// <summary>32x32 for title bar and taskbar.</summary>
    public static readonly ImageSource WindowIcon = CreateImageSource(32);

    /// <summary>16x16 GDI icon for the notification-area tray icon; red page while a feed is failing.</summary>
    public static Drawing.Icon CreateTrayIcon(bool alert = false)
        => CreateGdiIcon(16, alert ? Drawing.Color.FromArgb(255, 59, 92) : null);

    private static ImageSource CreateImageSource(int size)
    {
        using var icon = CreateGdiIcon(size);
        var source = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static Drawing.Icon CreateGdiIcon(int size, Drawing.Color? color = null)
    {
        using var bmp = new Drawing.Bitmap(size, size);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.None; // crisp bars at 16 px
            g.Clear(Drawing.Color.FromArgb(10, 14, 23));
            var accent = color ?? Drawing.Color.FromArgb(0, 229, 255);
            float unit = size / 16f;
            using var pen = new Drawing.Pen(accent, Math.Max(1, unit * 1.5f));
            g.DrawRectangle(pen, 2 * unit, 2 * unit, 12 * unit - 1, 12 * unit - 1);
            using var bar = new Drawing.SolidBrush(accent);
            g.FillRectangle(bar, 4.5f * unit, 4.5f * unit, 7 * unit, 2 * unit);   // headline
            g.FillRectangle(bar, 4.5f * unit, 8f * unit, 7 * unit, 1 * unit);     // body lines
            g.FillRectangle(bar, 4.5f * unit, 10.5f * unit, 4.5f * unit, 1 * unit);
        }

        // GetHicon hands us an HICON that Icon.FromHandle does NOT own; clone
        // to an owned Icon, then release the native handle ourselves.
        var hIcon = bmp.GetHicon();
        try
        {
            using var unowned = Drawing.Icon.FromHandle(hIcon);
            return (Drawing.Icon)unowned.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}
