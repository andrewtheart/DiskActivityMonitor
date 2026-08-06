using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DiskActivityMonitor.Tray.Controls;

/// <summary>One column in a <see cref="BarChart"/>.</summary>
public sealed record ChartBar(string Label, double Value, bool Highlight = false);

/// <summary>
/// A lightweight, dependency-free bar chart drawn directly via <see cref="OnRender"/>.
/// Used to plot per-hour / per-day / per-week write volumes. Values are bytes; the Y axis
/// auto-scales and is labelled with human-readable units.
/// </summary>
public sealed class BarChart : FrameworkElement
{
    private IReadOnlyList<ChartBar> _bars = Array.Empty<ChartBar>();
    private Func<double, string> _valueFormatter = v => v.ToString(CultureInfo.InvariantCulture);

    public Brush BarBrush { get; set; } = new LinearGradientBrush(
        Color.FromRgb(0x4F, 0xC3, 0xF7), Color.FromRgb(0x29, 0x79, 0xFF), 90);
    public Brush HighlightBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0x20));
    public Brush AxisBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x55, 0x5A, 0x60));
    public Brush TextBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0xB8, 0xBE, 0xC6));

    public void SetData(IReadOnlyList<ChartBar> bars, Func<double, string> valueFormatter)
    {
        _bars = bars ?? Array.Empty<ChartBar>();
        _valueFormatter = valueFormatter;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Background.
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x25)), null, new Rect(0, 0, w, h));

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");

        if (_bars.Count == 0)
        {
            DrawCenteredText(dc, "No data yet - the collector needs a few minutes of history.", typeface, dpi, w, h);
            return;
        }

        const double padLeft = 8, padRight = 8, padTop = 18, padBottomLabels = 18;
        double plotW = w - padLeft - padRight;
        double plotH = h - padTop - padBottomLabels;
        if (plotW <= 0 || plotH <= 0) return;

        double max = _bars.Max(b => b.Value);
        if (max <= 0) max = 1;

        // Max-value gridline + label.
        var gridPen = new Pen(AxisBrush, 0.5);
        dc.DrawLine(gridPen, new Point(padLeft, padTop), new Point(w - padRight, padTop));
        DrawText(dc, _valueFormatter(max), typeface, 10, TextBrush, dpi, padLeft, 2);

        // Baseline.
        double baseY = padTop + plotH;
        dc.DrawLine(new Pen(AxisBrush, 1), new Point(padLeft, baseY), new Point(w - padRight, baseY));

        int n = _bars.Count;
        double slot = plotW / n;
        double barW = Math.Max(2, slot * 0.62);
        int labelEvery = Math.Max(1, (int)Math.Ceiling(n / (plotW / 42.0)));
        double lastLabelRight = double.NegativeInfinity;

        for (int i = 0; i < n; i++)
        {
            var bar = _bars[i];
            double x = padLeft + i * slot + (slot - barW) / 2;
            double barH = bar.Value <= 0 ? 0 : Math.Max(1, bar.Value / max * plotH);
            double y = baseY - barH;

            if (barH > 0)
            {
                var brush = bar.Highlight ? HighlightBrush : BarBrush;
                dc.DrawRectangle(brush, null, new Rect(x, y, barW, barH));
            }

            // X-axis label (sparse to avoid crowding).
            if (i % labelEvery == 0 || i == n - 1)
            {
                var ft = MakeText(bar.Label, typeface, 9.5, TextBrush, dpi);
                double tx = x + barW / 2 - ft.Width / 2;
                tx = Math.Clamp(tx, 0, w - ft.Width);

                // The final label is forced, so it can land on top of the previous one; drop it
                // rather than overprinting.
                if (tx > lastLabelRight + 4)
                {
                    dc.DrawText(ft, new Point(tx, baseY + 3));
                    lastLabelRight = tx + ft.Width;
                }
            }
        }
    }

    private void DrawCenteredText(DrawingContext dc, string text, Typeface tf, double dpi, double w, double h)
    {
        var ft = MakeText(text, tf, 12, TextBrush, dpi);
        dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
    }

    private void DrawText(DrawingContext dc, string text, Typeface tf, double size, Brush brush, double dpi, double x, double y)
        => dc.DrawText(MakeText(text, tf, size, brush, dpi), new Point(x, y));

    private static FormattedText MakeText(string text, Typeface tf, double size, Brush brush, double dpi)
        => new(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, tf, size, brush, dpi);
}
