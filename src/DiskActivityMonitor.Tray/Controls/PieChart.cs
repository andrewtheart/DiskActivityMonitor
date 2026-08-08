using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DiskActivityMonitor.Tray.Controls;

/// <summary>One wedge of a <see cref="PieChart"/>.</summary>
/// <param name="Label">Legend text.</param>
/// <param name="Value">Magnitude; wedges are sized by share of the total.</param>
/// <param name="Highlight">Draws the wedge pulled out and brighter, marking the focused item.</param>
public sealed record PieSlice(string Label, double Value, bool Highlight = false);

/// <summary>
/// A dependency-free donut chart with an inline legend, drawn directly in <see cref="OnRender"/>.
/// </summary>
/// <remarks>
/// Matches <see cref="BarChart"/>'s approach so the app keeps no charting dependency. A donut is
/// used rather than a full pie because the centre is a natural place for the total, and thin
/// wedges stay readable against the dark surface.
/// </remarks>
public sealed class PieChart : FrameworkElement
{
    private IReadOnlyList<PieSlice> _slices = Array.Empty<PieSlice>();
    private Func<double, string> _valueFormatter = v => v.ToString(CultureInfo.InvariantCulture);
    private string _centerCaption = "";
    private readonly List<(Geometry Geometry, string Text)> _hits = [];
    private readonly ChartHoverTooltip _hoverTooltip = new();

    /// <summary>Categorical palette; wedges cycle through it in order.</summary>
    public static IReadOnlyList<Color> Palette { get; } =
    [
        Color.FromRgb(0x4F, 0xC3, 0xF7),
        Color.FromRgb(0xF0, 0xA0, 0x20),
        Color.FromRgb(0x81, 0xC7, 0x84),
        Color.FromRgb(0xBA, 0x68, 0xC8),
        Color.FromRgb(0xFF, 0x8A, 0x65),
        Color.FromRgb(0x4D, 0xD0, 0xE1),
        Color.FromRgb(0xE5, 0x73, 0x73),
        Color.FromRgb(0x9F, 0xA8, 0xDA),
        Color.FromRgb(0xDC, 0xE7, 0x75),
        Color.FromRgb(0x90, 0xA4, 0xAE),
    ];

    public Brush TextBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0xB8, 0xBE, 0xC6));
    public Brush StrongTextBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xEF));
    public Brush BackgroundBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x25));

    /// <summary>Supplies the wedges, a value formatter and an optional caption for the centre.</summary>
    public void SetData(IReadOnlyList<PieSlice> slices, Func<double, string> valueFormatter, string centerCaption = "")
    {
        _slices = slices ?? Array.Empty<PieSlice>();
        _valueFormatter = valueFormatter;
        _centerCaption = centerCaption ?? "";
        _hoverTooltip.Hide();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        _hits.Clear();
        if (!HasRenderArea(w, h)) return;

        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, w, h));

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");

        var visible = _slices.Where(s => s.Value > 0).ToList();
        double total = visible.Sum(s => s.Value);
        if (visible.Count == 0 || total <= 0)
        {
            var empty = MakeText("No write activity in this window.", typeface, 12, TextBrush, dpi);
            dc.DrawText(empty, new Point((w - empty.Width) / 2, (h - empty.Height) / 2));
            return;
        }

        // Reserve the right half for the legend; the donut takes the remaining square.
        double legendWidth = Math.Min(Math.Max(150, w * 0.46), w - 90);
        double chartWidth = w - legendWidth;
        double radius = Math.Max(20, Math.Min(chartWidth, h) / 2 - 12);
        var center = new Point(chartWidth / 2, h / 2);
        double innerRadius = radius * 0.58;

        double angle = -Math.PI / 2; // Start at 12 o'clock.
        for (int i = 0; i < visible.Count; i++)
        {
            double sweep = visible[i].Value / total * Math.PI * 2;
            var color = Palette[i % Palette.Count];
            var brush = new SolidColorBrush(visible[i].Highlight ? Lighten(color, 0.25) : color);

            // Nudge the focused wedge outward so it reads as selected without a separate legend cue.
            double offset = visible[i].Highlight ? 6 : 0;
            var wedgeCenter = offset == 0
                ? center
                : new Point(
                    center.X + Math.Cos(angle + sweep / 2) * offset,
                    center.Y + Math.Sin(angle + sweep / 2) * offset);

            Geometry geometry = BuildWedge(wedgeCenter, radius, innerRadius, angle, sweep);
            dc.DrawGeometry(brush, null, geometry);
            _hits.Add((geometry, $"{visible[i].Label}\n{_valueFormatter(visible[i].Value)} · {visible[i].Value / total:P1}"));
            angle += sweep;
        }

        // Centre caption sits inside the donut hole.
        if (_centerCaption.Length > 0)
        {
            var caption = MakeText(_centerCaption, typeface, 11, StrongTextBrush, dpi);
            if (caption.Width < innerRadius * 1.9)
                dc.DrawText(caption, new Point(center.X - caption.Width / 2, center.Y - caption.Height / 2));
        }

        DrawLegend(dc, visible, total, typeface, dpi, chartWidth + 8, legendWidth - 16, h);
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
        => _hits.LastOrDefault(hit => hit.Geometry.FillContains(pointer)).Text;

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        ClearHover();
        base.OnMouseLeave(e);
    }

    internal void ClearHover() => _hoverTooltip.Hide();

    internal static bool HasRenderArea(double width, double height) => width > 0 && height > 0;

    private void DrawLegend(
        DrawingContext dc,
        IReadOnlyList<PieSlice> slices,
        double total,
        Typeface typeface,
        double dpi,
        double x,
        double width,
        double height)
    {
        const double rowHeight = 18, swatch = 9;
        int maxRows = Math.Max(1, (int)(height / rowHeight));
        int shown = Math.Min(slices.Count, maxRows);
        double y = Math.Max(2, (height - shown * rowHeight) / 2);

        for (int i = 0; i < shown; i++)
        {
            var slice = slices[i];
            var color = Palette[i % Palette.Count];

            dc.DrawRoundedRectangle(
                new SolidColorBrush(slice.Highlight ? Lighten(color, 0.25) : color),
                null,
                new Rect(x, y + (rowHeight - swatch) / 2, swatch, swatch), 2, 2);

            double share = slice.Value / total;
            string text = $"{slice.Label}  {share:P0}  ({_valueFormatter(slice.Value)})";
            var brush = slice.Highlight ? StrongTextBrush : TextBrush;
            var ft = MakeText(text, typeface, 10.5, brush, dpi);
            ft.MaxTextWidth = Math.Max(20, width - swatch - 6);
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
            if (slice.Highlight) ft.SetFontWeight(FontWeights.SemiBold);

            dc.DrawText(ft, new Point(x + swatch + 6, y + (rowHeight - ft.Height) / 2));
            y += rowHeight;
        }
    }

    /// <summary>Builds one donut wedge as a closed figure between the inner and outer radii.</summary>
    internal static Geometry BuildWedge(Point center, double outer, double inner, double startAngle, double sweep)
    {
        // A full circle cannot be expressed as a single arc segment (start == end), so draw it as
        // two half sweeps.
        if (sweep >= Math.PI * 2 - 1e-6)
        {
            var full = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                new EllipseGeometry(center, outer, outer),
                new EllipseGeometry(center, inner, inner));
            full.Freeze();
            return full;
        }

        double endAngle = startAngle + sweep;
        var outerStart = OnCircle(center, outer, startAngle);
        var outerEnd = OnCircle(center, outer, endAngle);
        var innerEnd = OnCircle(center, inner, endAngle);
        var innerStart = OnCircle(center, inner, startAngle);
        bool large = sweep > Math.PI;

        var figure = new PathFigure { StartPoint = outerStart, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment(outerEnd, new Size(outer, outer), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(innerEnd, true));
        figure.Segments.Add(new ArcSegment(innerStart, new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private static Point OnCircle(Point center, double radius, double angle)
        => new(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);

    private static Color Lighten(Color color, double amount)
        => Color.FromRgb(
            (byte)Math.Clamp(color.R + (255 - color.R) * amount, 0, 255),
            (byte)Math.Clamp(color.G + (255 - color.G) * amount, 0, 255),
            (byte)Math.Clamp(color.B + (255 - color.B) * amount, 0, 255));

    private static FormattedText MakeText(string text, Typeface tf, double size, Brush brush, double dpi)
        => new(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, tf, size, brush, dpi);
}
