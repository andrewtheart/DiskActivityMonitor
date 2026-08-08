using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace DiskActivityMonitor.Tray.Controls;

internal sealed class ChartHoverTooltip
{
    private readonly System.Windows.Controls.ToolTip _toolTip = new()
    {
        Placement = PlacementMode.Mouse,
        StaysOpen = true,
        HasDropShadow = true,
    };

    public void Show(FrameworkElement owner, string text)
    {
        _toolTip.PlacementTarget = owner;
        _toolTip.Content = text;
        if (!_toolTip.IsOpen)
            _toolTip.IsOpen = true;
    }

    public void Hide() => _toolTip.IsOpen = false;
}

internal sealed record PointChartHit(Point Point, string Text);