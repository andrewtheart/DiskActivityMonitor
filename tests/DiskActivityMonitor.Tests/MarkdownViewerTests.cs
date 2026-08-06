using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using DiskActivityMonitor.Core.Text;
using DiskActivityMonitor.Tray.Controls;

namespace DiskActivityMonitor.Tests;

[Collection("WPF")]
public sealed class MarkdownViewerTests
{
    [Fact]
    public void Viewer_ProjectsEveryMarkdownElementAndGuardsLinkLaunches()
    {
        RunSta(() =>
        {
            EnsureApplication();
            const string markdown = """
                # First
                ## Second
                ### Third
                #### Fourth

                Plain **bold** *italic* `code` [release](https://example.test/release)

                - top
                  - nested

                ```powershell
                dam status
                ```

                ---
                """;

            var body = new SolidColorBrush(Colors.Silver);
            var heading = new SolidColorBrush(Colors.White);
            var code = new SolidColorBrush(Colors.LightGreen);
            var codeBackground = new SolidColorBrush(Colors.Black);
            var linkBrush = new SolidColorBrush(Colors.LightBlue);
            var rule = new SolidColorBrush(Colors.Gray);
            var viewer = new MarkdownViewer
            {
                Width = 420,
                BodyBrush = body,
                HeadingBrush = heading,
                CodeBrush = code,
                CodeBackground = codeBackground,
                LinkBrush = linkBrush,
                RuleBrush = rule,
                Markdown = markdown,
            };

            Assert.Equal(markdown, viewer.Markdown);
            Assert.False(viewer.Focusable);
            Assert.Equal(HorizontalAlignment.Stretch, viewer.HorizontalContentAlignment);

            var host = Assert.IsType<StackPanel>(viewer.Content);
            Assert.Equal(8, host.Children.Count);
            Assert.Equal(new Thickness(0), Assert.IsType<TextBlock>(host.Children[0]).Margin);
            Assert.All(host.Children.Cast<FrameworkElement>().Skip(1),
                element => Assert.Equal(new Thickness(0, 8, 0, 0), element.Margin));

            var headings = host.Children.Cast<FrameworkElement>().OfType<TextBlock>().Take(4).ToArray();
            Assert.Equal(new[] { 15d, 13.5d, 12.5d, 12d }, headings.Select(value => value.FontSize));
            Assert.All(headings, value =>
            {
                Assert.Equal(FontWeights.SemiBold, value.FontWeight);
                Assert.Same(heading, value.Foreground);
            });

            var paragraph = Assert.IsType<TextBlock>(host.Children[4]);
            Assert.Same(body, paragraph.Foreground);
            var paragraphRuns = paragraph.Inlines.OfType<Run>().ToArray();
            Assert.Contains(paragraphRuns, value => value.Text == "bold" && value.FontWeight == FontWeights.SemiBold);
            Assert.Contains(paragraphRuns, value => value.Text == "italic" && value.FontStyle == FontStyles.Italic);
            var codeRun = paragraphRuns.Single(value => value.Text == "code");
            Assert.Equal("Cascadia Mono, Consolas, Courier New", codeRun.FontFamily.Source);
            Assert.Same(code, codeRun.Foreground);
            Assert.Same(codeBackground, codeRun.Background);

            var hyperlink = Assert.Single(paragraph.Inlines.OfType<Hyperlink>());
            Assert.Same(linkBrush, hyperlink.Foreground);
            Assert.Equal("https://example.test/release", hyperlink.ToolTip);
            var navigate = new RequestNavigateEventArgs(new Uri("https://example.test/release"), "")
            {
                RoutedEvent = Hyperlink.RequestNavigateEvent,
            };
            hyperlink.RaiseEvent(navigate);
            Assert.True(navigate.Handled);

            string? launched = null;
            viewer.LinkLauncher = value => launched = value;
            hyperlink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Assert.Equal("https://example.test/release", launched);

            var list = Assert.IsType<StackPanel>(host.Children[5]);
            Assert.Equal(2, list.Children.Count);
            var topRow = Assert.IsType<Grid>(list.Children[0]);
            var nestedRow = Assert.IsType<Grid>(list.Children[1]);
            Assert.Equal(0, topRow.Margin.Left);
            Assert.Equal(14, nestedRow.Margin.Left);
            Assert.Equal("•", Assert.IsType<TextBlock>(topRow.Children[0]).Text);
            Assert.Equal(1, Grid.GetColumn(Assert.IsType<TextBlock>(topRow.Children[1])));

            var codeBorder = Assert.IsType<Border>(host.Children[6]);
            Assert.Same(codeBackground, codeBorder.Background);
            var codeBox = Assert.IsType<TextBox>(codeBorder.Child);
            Assert.Contains("dam status", codeBox.Text);
            Assert.True(codeBox.IsReadOnly);
            Assert.Same(code, codeBox.Foreground);

            var ruleBorder = Assert.IsType<Border>(host.Children[7]);
            Assert.Equal(1, ruleBorder.Height);
            Assert.Same(rule, ruleBorder.Background);
            Assert.Contains("First", AutomationProperties.GetName(viewer));
            Assert.Contains("dam status", AutomationProperties.GetName(viewer));

            viewer.Measure(new Size(420, 600));
            viewer.Arrange(new Rect(0, 0, 420, 600));
            viewer.UpdateLayout();
            var bitmap = new RenderTargetBitmap(420, 600, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(viewer);

            Assert.Null(viewer.CreateBlock(new UnsupportedMarkdownBlock()));
            launched = null;
            viewer.OpenLink("javascript:alert(1)");
            Assert.Null(launched);

            viewer.LinkLauncher = _ => throw new Win32Exception("no browser");
            viewer.OpenLink("https://example.test/win32");
            viewer.LinkLauncher = _ => throw new InvalidOperationException("shell refused");
            viewer.OpenLink("https://example.test/invalid-operation");
            viewer.LinkLauncher = _ => throw new IOException("association failed");
            viewer.OpenLink("https://example.test/io");

            viewer.Markdown = null;
            Assert.Empty(Assert.IsType<StackPanel>(viewer.Content).Children);
            Assert.Equal("", AutomationProperties.GetName(viewer));
        });
    }

    private sealed record UnsupportedMarkdownBlock : MarkdownBlock;

    private static void EnsureApplication()
    {
        if (Application.Current is null)
        {
            var app = new DiskActivityMonitor.Tray.App();
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