using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DiskActivityMonitor.Core;

namespace DiskActivityMonitor.Tray.Controls;

public sealed record LiveDiskPoint(DateTime TimestampUtc, double ReadMbps, double WriteMbps);
public sealed record TimeValuePoint(DateTime TimestampUtc, double Value);
public sealed record ChartSeries(string Key, string Label, Brush Brush, IReadOnlyList<TimeValuePoint> Points);

/// <summary>Rolling physical-disk read/write throughput rendered as two bounded line series.</summary>
public sealed class LiveDiskChart : FrameworkElement
{
    private IReadOnlyList<ChartSeries> _series = [];
    private readonly List<PointChartHit> _hits = [];
    private readonly ChartHoverTooltip _hoverTooltip = new();

    public Brush ReadBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
    public Brush WriteBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0xFF, 0xA7, 0x26));
    public Brush AxisBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x55, 0x5A, 0x60));
    public Brush TextBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0xB8, 0xBE, 0xC6));

    public void SetData(IReadOnlyList<LiveDiskPoint>? points)
    {
        IReadOnlyList<LiveDiskPoint> safePoints = points ?? [];
        _series =
        [
            new ChartSeries("read", "Read", ReadBrush,
                safePoints.Select(point => new TimeValuePoint(point.TimestampUtc, point.ReadMbps)).ToList()),
            new ChartSeries("write", "Write", WriteBrush,
                safePoints.Select(point => new TimeValuePoint(point.TimestampUtc, point.WriteMbps)).ToList()),
        ];
        _hoverTooltip.Hide();
        InvalidateVisual();
    }

    public void SetSeries(IReadOnlyList<ChartSeries>? series)
    {
        _series = series ?? [];
        _hoverTooltip.Hide();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        _hits.Clear();
        if (!HasRenderArea(width, height)) return;

        drawingContext.DrawRectangle(
            new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x25)),
            null,
            new Rect(0, 0, width, height));

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");
        IReadOnlyList<ChartSeries> populated = _series.Where(series => series.Points.Count > 0).ToList();
        if (populated.Count == 0)
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

        double max = Math.Max(1, populated.Max(series => series.Points.Max(point => point.Value)));
        drawingContext.DrawLine(new Pen(AxisBrush, 0.5), new Point(left, top), new Point(width - right, top));
        drawingContext.DrawLine(new Pen(AxisBrush, 1), new Point(left, top + plotHeight), new Point(width - right, top + plotHeight));
        drawingContext.DrawText(MakeText(FormatRate(max), typeface, 9.5, TextBrush, dpi), new Point(left, 2));

        DateTime firstTimestamp = populated.Min(series => series.Points[0].TimestampUtc);
        DateTime lastTimestamp = populated.Max(series => series.Points[^1].TimestampUtc);
        double tickRange = Math.Max(1, lastTimestamp.Ticks - firstTimestamp.Ticks);
        foreach (ChartSeries series in populated)
            DrawSeries(drawingContext, series, max, firstTimestamp, tickRange, left, top, plotWidth, plotHeight);

        string first = LocalTimeDisplay.FormatUtc(firstTimestamp, "h:mm:ss tt");
        string last = LocalTimeDisplay.FormatUtc(lastTimestamp, "h:mm:ss tt");
        drawingContext.DrawText(MakeText(first, typeface, 9.5, TextBrush, dpi), new Point(left, height - bottom + 3));
        FormattedText lastText = MakeText(last, typeface, 9.5, TextBrush, dpi);
        drawingContext.DrawText(lastText, new Point(width - right - lastText.Width, height - bottom + 3));
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateHover(e.GetPosition(this));
    }

    internal void UpdateHover(Point pointer)
    {
        string? text = HitTextAt(pointer);
        if (text is null) _hoverTooltip.Hide();
        else _hoverTooltip.Show(this, text);
    }

    public string? HitTextAt(Point pointer)
        => _hits
            .Select(candidate => (Hit: candidate, Distance: (candidate.Point - pointer).Length))
            .Where(candidate => candidate.Distance <= 9)
            .OrderBy(candidate => candidate.Distance)
            .Select(candidate => candidate.Hit)
            .FirstOrDefault()?.Text;

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        ClearHover();
        base.OnMouseLeave(e);
    }

    internal void ClearHover() => _hoverTooltip.Hide();

    private void DrawSeries(
        DrawingContext drawingContext,
        ChartSeries series,
        double max,
        DateTime firstTimestamp,
        double tickRange,
        double left,
        double top,
        double plotWidth,
        double plotHeight)
    {
        IReadOnlyList<TimeValuePoint> values = series.Points;
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            for (int index = 0; index < values.Count; index++)
            {
                TimeValuePoint value = values[index];
                double x = left + ((value.TimestampUtc.Ticks - firstTimestamp.Ticks) / tickRange * plotWidth);
                double y = top + plotHeight - Math.Max(0, value.Value) / max * plotHeight;
                var point = new Point(x, y);
                _hits.Add(new PointChartHit(
                    point,
                    $"{series.Label}\n{LocalTimeDisplay.FormatUtcWithZone(value.TimestampUtc, "MMM d, h:mm:ss tt")} · {FormatRate(value.Value)}"));
                if (index == 0) context.BeginFigure(point, false, false);
                else context.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, new Pen(series.Brush, 1.8), geometry);
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