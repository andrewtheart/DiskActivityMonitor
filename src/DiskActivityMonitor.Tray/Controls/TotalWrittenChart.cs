using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DiskActivityMonitor.Core;

namespace DiskActivityMonitor.Tray.Controls;

/// <summary>Plots an absolute total-written value over a selected UTC time range.</summary>
public sealed class TotalWrittenChart : FrameworkElement
{
    private IReadOnlyList<Trends.TotalWrittenPoint> _points = [];
    private Func<double, string> _valueFormatter = value => value.ToString(CultureInfo.InvariantCulture);
    private readonly List<PointChartHit> _hits = [];
    private readonly ChartHoverTooltip _hoverTooltip = new();

    public Brush LineBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0xFF, 0xA7, 0x26));
    public Brush AxisBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x55, 0x5A, 0x60));
    public Brush TextBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0xB8, 0xBE, 0xC6));

    public void SetData(
        IReadOnlyList<Trends.TotalWrittenPoint>? points,
        Func<double, string> valueFormatter)
    {
        _points = points ?? [];
        _valueFormatter = valueFormatter;
        _hoverTooltip.Hide();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        _hits.Clear();
        if (!HasRenderArea(width, height))
            return;

        drawingContext.DrawRectangle(
            new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x25)),
            null,
            new Rect(0, 0, width, height));

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");
        if (_points.Count < 2)
        {
            FormattedText empty = MakeText(
                "No write history in this range yet.", typeface, 12, TextBrush, dpi);
            drawingContext.DrawText(
                empty,
                new Point((width - empty.Width) / 2, (height - empty.Height) / 2));
            return;
        }

        const double left = 82;
        const double right = 12;
        const double top = 18;
        const double bottom = 24;
        double plotWidth = width - left - right;
        double plotHeight = height - top - bottom;
        if (plotWidth <= 0 || plotHeight <= 0)
            return;

        double minimum = _points.Min(point => (double)point.TotalBytes);
        double maximum = _points.Max(point => (double)point.TotalBytes);
        double valueRange = maximum - minimum;
        bool flat = valueRange <= 0;
        if (flat)
            valueRange = Math.Max(1, maximum * 0.01);

        long firstTicks = _points[0].TimestampUtc.Ticks;
        long lastTicks = _points[^1].TimestampUtc.Ticks;
        double tickRange = Math.Max(1, lastTicks - firstTicks);

        var gridPen = new Pen(AxisBrush, 0.5);
        for (int index = 0; index <= 2; index++)
        {
            double y = top + (plotHeight * index / 2.0);
            drawingContext.DrawLine(gridPen, new Point(left, y), new Point(width - right, y));
        }

        DrawRightAlignedText(
            drawingContext, _valueFormatter(maximum), typeface, 9.5, TextBrush, dpi, left - 7, top - 7);
        DrawRightAlignedText(
            drawingContext, _valueFormatter(minimum), typeface, 9.5, TextBrush, dpi, left - 7, top + plotHeight - 7);

        Point PlotPoint(Trends.TotalWrittenPoint point)
        {
            double x = left + ((point.TimestampUtc.Ticks - firstTicks) / tickRange * plotWidth);
            double normalized = flat ? 0.5 : (point.TotalBytes - minimum) / valueRange;
            double y = top + plotHeight - (normalized * plotHeight);
            return new Point(x, y);
        }

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(PlotPoint(_points[0]), isFilled: false, isClosed: false);
            context.PolyLineTo(_points.Skip(1).Select(PlotPoint).ToArray(), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, new Pen(LineBrush, 2), geometry);

        foreach (Trends.TotalWrittenPoint point in _points)
        {
            Point rendered = PlotPoint(point);
            _hits.Add(new PointChartHit(
                rendered,
                $"{LocalTimeDisplay.FormatUtcWithZone(point.TimestampUtc, "MMM d, yyyy h:mm:ss tt")}\n{_valueFormatter(point.TotalBytes)} written"));
            drawingContext.DrawEllipse(LineBrush, null, rendered, 2.4, 2.4);
        }

        Point latest = PlotPoint(_points[^1]);
        drawingContext.DrawEllipse(LineBrush, null, latest, 3.5, 3.5);

        TimeSpan span = _points[^1].TimestampUtc - _points[0].TimestampUtc;
        string timeFormat = span <= TimeSpan.FromDays(2) ? "h:mm tt" : "MMM d";
        string firstLabel = LocalTimeDisplay.FormatUtc(_points[0].TimestampUtc, timeFormat);
        string lastLabel = LocalTimeDisplay.FormatUtc(_points[^1].TimestampUtc, timeFormat);
        drawingContext.DrawText(MakeText(firstLabel, typeface, 9.5, TextBrush, dpi), new Point(left, top + plotHeight + 4));
        FormattedText lastText = MakeText(lastLabel, typeface, 9.5, TextBrush, dpi);
        drawingContext.DrawText(lastText, new Point(width - right - lastText.Width, top + plotHeight + 4));
    }

    internal static bool HasRenderArea(double width, double height) => width > 0 && height > 0;

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

    private static void DrawRightAlignedText(
        DrawingContext drawingContext,
        string text,
        Typeface typeface,
        double size,
        Brush brush,
        double dpi,
        double right,
        double top)
    {
        FormattedText formatted = MakeText(text, typeface, size, brush, dpi);
        drawingContext.DrawText(formatted, new Point(Math.Max(0, right - formatted.Width), top));
    }

    private static FormattedText MakeText(
        string text,
        Typeface typeface,
        double size,
        Brush brush,
        double dpi)
        => new(
            text,
            CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            size,
            brush,
            dpi);
}