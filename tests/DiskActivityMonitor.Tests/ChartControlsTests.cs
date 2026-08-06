using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiskActivityMonitor.Tray;
using DiskActivityMonitor.Tray.Controls;

namespace DiskActivityMonitor.Tests;

[Collection("WPF")]
public sealed class ChartControlsTests
{
    [Fact]
    public void BarChart_RendersEmptyAndDataStates()
    {
        RunSta(() =>
        {
            EnsureApplication();

            var chart = new BarChart { Width = 320, Height = 160 };
            chart.Measure(new Size(320, 160));
            chart.Arrange(new Rect(0, 0, 320, 160));
            chart.UpdateLayout();

            Render(chart);

            chart.SetData(
            [
                new ChartBar("00:00", 0),
                new ChartBar("00:01", 10, Highlight: true),
                new ChartBar("00:02", 5),
            ],
            v => $"{v:0} B");

            chart.Measure(new Size(320, 160));
            chart.Arrange(new Rect(0, 0, 320, 160));
            chart.UpdateLayout();

            Render(chart);
        });
    }

    [Fact]
    public void PieChart_RendersEmptyPartialAndFullCircleSlices()
    {
        RunSta(() =>
        {
            EnsureApplication();

            // The render guard must tolerate an element that has not been given layout space.
            Render(new PieChart());
            Assert.False(PieChart.HasRenderArea(0, 10));
            Assert.False(PieChart.HasRenderArea(10, 0));
            Assert.True(PieChart.HasRenderArea(10, 10));

            var chart = new PieChart { Width = 420, Height = 200 };
            chart.Measure(new Size(420, 200));
            chart.Arrange(new Rect(0, 0, 420, 200));
            chart.UpdateLayout();

            // Null, empty, zero and negative values all produce the empty state.
            chart.SetData(null!, v => v.ToString(), null!);
            Render(chart);
            chart.SetData([new PieSlice("Zero", 0), new PieSlice("Negative", -1)], v => v.ToString(), "none");
            Render(chart);

            // A majority wedge covers the large-arc path; enough slices also wrap the palette.
            chart.SetData(
                Enumerable.Range(0, PieChart.Palette.Count + 1)
                    .Select(index => new PieSlice(
                        $"Slice {index} with a deliberately long legend label",
                        index == 0 ? 70 : 3,
                        Highlight: index == 0))
                    .ToArray(),
            v => $"{v:0}",
            new string('W', 100));
            Render(chart);

            // A constrained surface shows only one legend row and clamps its text width.
            chart.Width = 100;
            chart.Height = 18;
            chart.Measure(new Size(100, 18));
            chart.Arrange(new Rect(0, 0, 100, 18));
            chart.UpdateLayout();
            chart.SetData([new PieSlice("A", 2), new PieSlice("B", 1)], v => $"{v:0}");
            Render(chart);

            // A fresh full-size element covers the short centre-caption draw path.
            var captionChart = new PieChart { Width = 420, Height = 200 };
            captionChart.SetData([new PieSlice("All", 100)], v => $"{v:0}", "all");
            captionChart.Measure(new Size(420, 200));
            captionChart.Arrange(new Rect(0, 0, 420, 200));
            captionChart.UpdateLayout();
            Render(captionChart);

            // Exercise the full-circle geometry path without depending on WPF render invalidation timing.
            Geometry fullWedge = PieChart.BuildWedge(new Point(50, 50), 40, 20, 0, Math.PI * 2);
            Assert.IsType<CombinedGeometry>(fullWedge);
            Assert.True(fullWedge.IsFrozen);
        });
    }

    [Fact]
    public void PieChart_PaletteHasExpectedStops()
    {
        Assert.True(PieChart.Palette.Count >= 10);
        Assert.Equal(Color.FromRgb(0x4F, 0xC3, 0xF7), PieChart.Palette[0]);
    }

    [Fact]
    public void LiveDiskChart_RendersEmptyAndTwoSeriesStates()
    {
        RunSta(() =>
        {
            EnsureApplication();
            Render(new LiveDiskChart());
            Assert.False(LiveDiskChart.HasRenderArea(0, 0));
            Assert.False(LiveDiskChart.HasRenderArea(0, 10));
            Assert.False(LiveDiskChart.HasRenderArea(10, 0));
            Assert.True(LiveDiskChart.HasRenderArea(10, 10));

            RenderChart(null, 420, 170);
            RenderChart([new LiveDiskPoint(DateTime.UtcNow, 2, 3)], 12, 12);
            RenderChart([new LiveDiskPoint(DateTime.UtcNow, -2, 3)], 420, 170);
            RenderChart(
            [
                new LiveDiskPoint(DateTime.UtcNow.AddSeconds(-5), 0, 5),
                new LiveDiskPoint(DateTime.UtcNow, 12.5, 150),
            ], 420, 170);

            Assert.Equal("150 MB/s", LiveDiskChart.FormatRate(150));
            Assert.Equal("12.5 MB/s", LiveDiskChart.FormatRate(12.5));
            Assert.Equal("1.25 MB/s", LiveDiskChart.FormatRate(1.25));
        });
    }

    private static void RenderChart(IReadOnlyList<LiveDiskPoint>? points, double width, double height)
    {
        var chart = new LiveDiskChart { Width = width, Height = height };
        chart.SetData(points);
        chart.Measure(new Size(width, height));
        chart.Arrange(new Rect(0, 0, width, height));
        chart.UpdateLayout();
        Render(chart);
    }

    private static void Render(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)element.ActualWidth),
            Math.Max(1, (int)element.ActualHeight),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(element);
    }

    private static void EnsureApplication()
    {
        if (Application.Current is null)
        {
            var app = new App();
            app.InitializeComponent();
        }
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new System.Reflection.TargetInvocationException(error);
    }
}
