using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DiskActivityMonitor.Core.Text;

namespace DiskActivityMonitor.Tray.Controls;

/// <summary>
/// Renders a markdown document as themed WPF content.
/// </summary>
/// <remarks>
/// Release notes arrive as markdown and were previously shown as raw text. A WebView2 would drag a
/// browser into a small panel and would not inherit the app's dark styling, so the block model from
/// <see cref="MarkdownParser"/> is projected onto ordinary WPF elements instead. Text stays
/// selectable, links open in the default browser, and nothing external is loaded.
/// </remarks>
public sealed class MarkdownViewer : ContentControl
{
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Courier New");

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownViewer),
        new PropertyMetadata(null, (d, _) => ((MarkdownViewer)d).Rebuild()));

    /// <summary>Markdown source; setting it re-renders the control.</summary>
    public string? Markdown
    {
        get => (string?)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public Brush BodyBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0xB8, 0xBE, 0xC6));
    public Brush HeadingBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xEF));
    public Brush CodeBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0xD6, 0xDC, 0xE3));
    public Brush CodeBackground { get; set; } = new SolidColorBrush(Color.FromRgb(0x10, 0x13, 0x16));
    public Brush LinkBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x64, 0xA7, 0xFF));
    public Brush RuleBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x4A));

    /// <summary>Opens an accepted link; overridable so tests never launch a browser.</summary>
    internal Action<string> LinkLauncher { get; set; } = url =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public MarkdownViewer()
    {
        Focusable = false;
        HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch;
    }

    private void Rebuild()
    {
        var blocks = MarkdownParser.Parse(Markdown);
        var host = new StackPanel();

        for (int i = 0; i < blocks.Count; i++)
        {
            var element = CreateBlock(blocks[i]);
            if (element is null) continue;

            // Suppress the leading gap so the first block sits flush with the panel.
            element.Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0);
            host.Children.Add(element);
        }

        Content = host;
        AutomationPropertiesText(blocks);
    }

    private void AutomationPropertiesText(IReadOnlyList<MarkdownBlock> blocks)
        => System.Windows.Automation.AutomationProperties.SetName(this, MarkdownParser.ToPlainText(blocks));

    internal FrameworkElement? CreateBlock(MarkdownBlock block) => block switch
    {
        MarkdownHeading heading => CreateHeading(heading),
        MarkdownParagraph paragraph => CreateTextBlock(paragraph.Inlines, 12, FontWeights.Normal, BodyBrush),
        MarkdownList list => CreateList(list),
        MarkdownCodeBlock code => CreateCode(code),
        MarkdownRule => new Border { Height = 1, Background = RuleBrush, Margin = new Thickness(0, 10, 0, 0) },
        _ => null,
    };

    private FrameworkElement CreateHeading(MarkdownHeading heading)
    {
        // Release notes start at level 2, so keep the on-screen sizes close together.
        double size = heading.Level switch { 1 => 15, 2 => 13.5, 3 => 12.5, _ => 12 };
        return CreateTextBlock(heading.Inlines, size, FontWeights.SemiBold, HeadingBrush);
    }

    private FrameworkElement CreateList(MarkdownList list)
    {
        var panel = new StackPanel();
        foreach (var item in list.Items)
        {
            var row = new Grid { Margin = new Thickness(item.Depth * 14, 2, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var marker = new TextBlock
            {
                Text = item.Marker,
                Foreground = BodyBrush,
                FontSize = 12,
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                MinWidth = 14,
            };
            row.Children.Add(marker);

            var text = CreateTextBlock(item.Inlines, 12, FontWeights.Normal, BodyBrush);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            panel.Children.Add(row);
        }
        return panel;
    }

    private FrameworkElement CreateCode(MarkdownCodeBlock code)
    {
        return new Border
        {
            Background = CodeBackground,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Child = new System.Windows.Controls.TextBox
            {
                Text = code.Text,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = CodeBrush,
                FontFamily = MonoFont,
                FontSize = 11.5,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
        };
    }

    private FrameworkElement CreateTextBlock(IReadOnlyList<MarkdownInline> inlines, double size, FontWeight weight, Brush brush)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            FontWeight = weight,
            Foreground = brush,
        };

        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MarkdownRun run when run.Code:
                    block.Inlines.Add(new Run(run.Text)
                    {
                        FontFamily = MonoFont,
                        Foreground = CodeBrush,
                        Background = CodeBackground,
                    });
                    break;

                case MarkdownRun run:
                    block.Inlines.Add(new Run(run.Text)
                    {
                        FontWeight = run.Bold ? FontWeights.SemiBold : weight,
                        FontStyle = run.Italic ? FontStyles.Italic : FontStyles.Normal,
                    });
                    break;

                case MarkdownLink link:
                    block.Inlines.Add(CreateHyperlink(link));
                    break;
            }
        }

        return block;
    }

    private Hyperlink CreateHyperlink(MarkdownLink link)
    {
        var hyperlink = new Hyperlink(new Run(link.Text))
        {
            Foreground = LinkBrush,
            ToolTip = link.Url,
        };

        hyperlink.RequestNavigate += (_, e) => e.Handled = true;
        hyperlink.Click += (_, _) => OpenLink(link.Url);
        return hyperlink;
    }

    internal void OpenLink(string url)
    {
        // The parser already rejected non-http(s) schemes; re-check before handing it to the shell.
        if (!MarkdownParser.IsSafeUrl(url)) return;

        try
        {
            LinkLauncher(url);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException)
        {
            // No browser association, or the shell refused; nothing useful to show here.
        }
    }
}
