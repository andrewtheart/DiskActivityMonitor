using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DiskActivityMonitor.Core;

namespace DiskActivityMonitor.Tray.Controls;

public sealed record LiveDiskPoint(DateTime TimestampUtc, double ReadMbps, double WriteMbps);

/// <summary>Rolling physical-disk read/write throughput rendered as two bounded line series.</summary>
public sealed class LiveDiskChart : FrameworkElement
{
    private IReadOnlyList<LiveDiskPoint> _points = [];

    public Brush ReadBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
    public Brush WriteBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0xFF, 0xA7, 0x26));
    public Brush AxisBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x55, 0x5A, 0x60));
    public Brush TextBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0xB8, 0xBE, 0xC6));

    public void SetData(IReadOnlyList<LiveDiskPoint>? points)
    {
        _points = points ?? [];
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (!HasRenderArea(width, height)) return;

        drawingContext.DrawRectangle(
            new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x25)),
            null,
            new Rect(0, 0, width, height));

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");
        if (_points.Count == 0)
        {
            FormattedText empty = MakeText("Waiting for granular collector samples...", typeface, 11, TextBrush, dpi);
            drawingContext.DrawText(empty, new Point((width - empty.Width) / 2, (height - empty.Height) / 2));
            return;
        }

        const double left = 8;
        const double right = 8;
        const double top = 20;
        const double bottom = 20;
        double plotWidth = width - left - right;
        double plotHeight = height - top - bottom;
        if (plotWidth <= 0 || plotHeight <= 0) return;

        double max = Math.Max(1, _points.Max(point => Math.Max(point.ReadMbps, point.WriteMbps)));
        drawingContext.DrawLine(new Pen(AxisBrush, 0.5), new Point(left, top), new Point(width - right, top));
        drawingContext.DrawLine(new Pen(AxisBrush, 1), new Point(left, top + plotHeight), new Point(width - right, top + plotHeight));
        drawingContext.DrawText(MakeText(FormatRate(max), typeface, 9.5, TextBrush, dpi), new Point(left, 2));

        DrawSeries(drawingContext, _points.Select(point => point.ReadMbps), ReadBrush, max, left, top, plotWidth, plotHeight);
        DrawSeries(drawingContext, _points.Select(point => point.WriteMbps), WriteBrush, max, left, top, plotWidth, plotHeight);

        string first = LocalTimeDisplay.FormatUtc(_points[0].TimestampUtc, "HH:mm:ss");
        string last = LocalTimeDisplay.FormatUtc(_points[^1].TimestampUtc, "HH:mm:ss");
        drawingContext.DrawText(MakeText(first, typeface, 9.5, TextBrush, dpi), new Point(left, height - bottom + 3));
        FormattedText lastText = MakeText(last, typeface, 9.5, TextBrush, dpi);
        drawingContext.DrawText(lastText, new Point(width - right - lastText.Width, height - bottom + 3));
    }

    private static void DrawSeries(
        DrawingContext drawingContext,
        IEnumerable<double> values,
        Brush brush,
        double max,
        double left,
        double top,
        double plotWidth,
        double plotHeight)
    {
        double[] series = values.ToArray();
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            for (int index = 0; index < series.Length; index++)
            {
                double x = left + (series.Length == 1 ? plotWidth : index * plotWidth / (series.Length - 1));
                double y = top + plotHeight - Math.Max(0, series[index]) / max * plotHeight;
                var point = new Point(x, y);
                if (index == 0) context.BeginFigure(point, false, false);
                else context.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, new Pen(brush, 1.8), geometry);
    }

    internal static string FormatRate(double mbps) => mbps switch
    {
        >= 100 => $"{mbps:0} MB/s",
        >= 10 => $"{mbps:0.#} MB/s",
        _ => $"{mbps:0.##} MB/s",
    };

    internal static bool HasRenderArea(double width, double height)
        => width > 0 && height > 0;

    private static FormattedText MakeText(string text, Typeface typeface, double size, Brush brush, double dpi)
        => new(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, typeface, size, brush, dpi);
}