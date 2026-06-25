using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DiskActivityMonitor.Tray;

/// <summary>
/// Builds a tray icon at runtime: a small SSD glyph with a colored status dot, so no .ico
/// asset needs shipping. The dot color reflects current alert severity (green/amber/red).
/// </summary>
internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static readonly Color Ok = Color.FromArgb(0x3F, 0xB9, 0x50);
    public static readonly Color Warning = Color.FromArgb(0xF0, 0xA0, 0x20);
    public static readonly Color Critical = Color.FromArgb(0xE0, 0x4A, 0x4A);

    public static Icon Create(Color status)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Drive body.
            var body = new Rectangle(3, 7, 26, 18);
            using (var path = RoundedRect(body, 4))
            using (var fill = new SolidBrush(Color.FromArgb(0x33, 0x37, 0x3B)))
            using (var pen = new Pen(Color.FromArgb(0x6A, 0x70, 0x76), 1.5f))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            // Activity "lanes" hinting at data.
            using (var lane = new Pen(Color.FromArgb(0x8A, 0x92, 0x9A), 1.4f))
            {
                g.DrawLine(lane, 7, 13, 25, 13);
                g.DrawLine(lane, 7, 17, 22, 17);
                g.DrawLine(lane, 7, 21, 19, 21);
            }

            // Status dot.
            using var dot = new SolidBrush(status);
            using var ring = new Pen(Color.FromArgb(0x20, 0x20, 0x20), 1.2f);
            var d = new Rectangle(19, 17, 11, 11);
            g.FillEllipse(dot, d);
            g.DrawEllipse(ring, d);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    /// <summary>
    /// Builds a WPF <see cref="System.Windows.Media.ImageSource"/> from the exact same glyph as
    /// <see cref="Create"/>, so the window/taskbar icon is pixel-identical to the tray icon.
    /// </summary>
    public static System.Windows.Media.ImageSource CreateImageSource(Color status)
    {
        using var icon = Create(status);
        var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            System.Windows.Int32Rect.Empty,
            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
