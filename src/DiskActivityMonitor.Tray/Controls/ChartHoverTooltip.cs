using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DiskActivityMonitor.Tray.Controls;

internal sealed class ChartHoverTooltip
{
    private readonly System.Windows.Controls.ToolTip _toolTip = new()
    {
        Placement = PlacementMode.MousePoint,
        HorizontalOffset = 14,
        VerticalOffset = 14,
        StaysOpen = true,
        HasDropShadow = false,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(0),
    };

    internal System.Windows.Controls.ToolTip ToolTip => _toolTip;

    public void Show(FrameworkElement owner, string text)
    {
        _toolTip.PlacementTarget = owner;
        _toolTip.Content = BuildContent(text);
        if (!_toolTip.IsOpen)
            _toolTip.IsOpen = true;
    }

    public void Hide() => _toolTip.IsOpen = false;

    internal static Border BuildContent(string text)
    {
        string[] lines = text.Split(['\n'], 2);
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = lines[0].Trim(),
            Foreground = new SolidColorBrush(Color.FromRgb(0xC7, 0xCD, 0xD4)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        if (lines.Length > 1)
        {
            content.Children.Add(new TextBlock
            {
                Text = lines[1].Trim(),
                Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF7, 0xFA)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x19)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x54, 0x60)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 9),
            MaxWidth = 380,
            SnapsToDevicePixels = true,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 12,
                ShadowDepth = 3,
                Opacity = 0.55,
            },
            Child = content,
        };
    }
}

internal sealed record PointChartHit(Point Point, string Text);