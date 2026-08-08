using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskActivityMonitor.Core;
using DiskActivityMonitor.Core.Ai;
using DiskActivityMonitor.Core.Collection;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Models;
using DiskActivityMonitor.Tray;

namespace DiskActivityMonitor.Tests;

[CollectionDefinition("WPF", DisableParallelization = true)]
public sealed class WpfCollectionDefinition;

[Collection("WPF")]
public sealed class MainWindowCoverageTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dam_wpf_{Guid.NewGuid():N}.db");
    private readonly string _cfg = Path.Combine(Path.GetTempPath(), $"dam_wpf_{Guid.NewGuid():N}.json");
    private readonly string _userSettings = Path.Combine(Path.GetTempPath(), $"dam_wpf_user_{Guid.NewGuid():N}.json");
    private readonly string _secrets = AiSecretsStore.FilePath;
    private readonly string? _secretBackup;

    public MainWindowCoverageTests() => _secretBackup = File.Exists(_secrets) ? File.ReadAllText(_secrets) : null;

    public void Dispose()
    {
        if (_secretBackup is null) { try { File.Delete(_secrets); } catch { } }
        else { Directory.CreateDirectory(Path.GetDirectoryName(_secrets)!); File.WriteAllText(_secrets, _secretBackup); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_db) + "*")) try { File.Delete(file); } catch { }
        try { File.Delete(_cfg); } catch { }
        try { File.Delete(_userSettings); } catch { }
    }

    [Fact]
    public void TotalWrittenTrend_SwitchesPresetsAndAppliesCustomDates()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db);
            repo.EnsureSchema();
            var now = DateTime.UtcNow;
            repo.UpsertDisks([new DiskInfo
            {
                DiskId = "0",
                InstanceName = "0 C:",
                FriendlyName = "Trend SSD",
                Volumes = "C:",
                MediaType = DiskMediaType.Ssd,
                LifetimeBytesWritten = 10_000,
            }]);
            repo.AddDiskSamples([
                new DiskSample { TimestampUtc = now.AddMinutes(-45), DiskId = "0", WriteBytes = 100 },
                new DiskSample { TimestampUtc = now.AddMinutes(-15), DiskId = "0", WriteBytes = 300 },
            ]);
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_userSettings));
            try
            {
                Assert.True(window.Btn1h.IsChecked);
                Assert.Equal("Total written over time", window.TrendTitle.Text);
                Assert.Contains("anchored to SMART", window.TrendCaption.Text);
                Assert.Contains("Increase: +400 B", window.TrendChangeText.Text);
                Assert.Equal(Visibility.Collapsed, window.TrendCustomRangePanel.Visibility);

                Invoke(window, "Range_Click", window.Btn7d, new RoutedEventArgs());
                Assert.True(window.Btn7d.IsChecked);
                Assert.False(window.Btn1h.IsChecked);

                Invoke(window, "Range_Click", window.BtnCustom, new RoutedEventArgs());
                Assert.True(window.BtnCustom.IsChecked);
                Assert.Equal(Visibility.Visible, window.TrendCustomRangePanel.Visibility);

                DateTime today = DateTime.Today;
                window.TrendStartDate.SelectedDate = today.AddDays(-2);
                window.TrendEndDate.SelectedDate = today;
                Invoke(window, "CustomTrendApply_Click", window.TrendApplyCustomButton, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.TrendCustomError.Visibility);
                Assert.Contains("Increase: +400 B", window.TrendChangeText.Text);

                window.TrendStartDate.SelectedDate = today;
                window.TrendEndDate.SelectedDate = today.AddDays(-1);
                Invoke(window, "CustomTrendApply_Click", window.TrendApplyCustomButton, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, window.TrendCustomError.Visibility);
                Assert.Contains("valid date range", window.TrendRangeText.Text);

                await Task.CompletedTask;
            }
            finally { window.ForceClose(); }
        });
    }

    [Fact]
    public void AllDisks_AggregatesStatisticsAndRendersPerDiskLiveSeries()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db);
            repo.EnsureSchema();
            var now = DateTime.UtcNow;
            repo.UpsertDisks([
                new DiskInfo
                {
                    DiskId = "0", InstanceName = "0 C:", FriendlyName = "System SSD", Volumes = "C:",
                    MediaType = DiskMediaType.Ssd, LifetimeBytesWritten = 10_000, LifetimeBytesRead = 4_000,
                },
                new DiskInfo
                {
                    DiskId = "1", InstanceName = "1 F:", FriendlyName = "Data HDD", Volumes = "F:",
                    MediaType = DiskMediaType.Hdd,
                },
            ]);
            repo.AddDiskSamples([
                new DiskSample { TimestampUtc = now.AddDays(-8), DiskId = "0", WriteBytes = 1 },
                new DiskSample { TimestampUtc = now.AddDays(-8), DiskId = "1", WriteBytes = 1 },
                new DiskSample { TimestampUtc = now.AddMinutes(-2), DiskId = "0", ReadBytes = 100, WriteBytes = 200 },
                new DiskSample { TimestampUtc = now.AddMinutes(-2), DiskId = "1", ReadBytes = 300, WriteBytes = 400 },
            ]);
            repo.AddLiveDiskSamples([
                new LiveDiskSample { TimestampUtc = now.AddSeconds(-5), DiskId = "0", ElapsedMilliseconds = 5000, ReadBytes = 5_000_000, WriteBytes = 10_000_000 },
                new LiveDiskSample { TimestampUtc = now.AddSeconds(-5), DiskId = "1", ElapsedMilliseconds = 5000, ReadBytes = 15_000_000, WriteBytes = 20_000_000 },
            ], now.AddMinutes(-15));

            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_userSettings));
            try
            {
                object allDisks = ((IEnumerable)window.DiskSelector.ItemsSource).Cast<object>()
                    .Single(choice => choice.GetType().GetProperty("Disk")!.GetValue(choice) is null);
                Assert.Equal("All disks", allDisks.GetType().GetProperty("Display")!.GetValue(allDisks));
                Assert.True(Property<bool>(allDisks, "IsAll"));

                window.DiskSelector.SelectedItem = allDisks;
                Invoke(window, "LoadDisks");

                Assert.Equal(ByteFormat.Humanize(600), window.TodayMetric.Text);
                Assert.Equal($"read {ByteFormat.Humanize(400)}", window.TodayReadSub.Text);
                Assert.Contains("All disks", window.TrendTitle.Text);
                Assert.False(window.EnduranceRatedBadge.IsEnabled);
                Assert.Equal(4, ((IEnumerable)window.LiveDiskLegend.ItemsSource).Cast<object>().Count());
                Assert.Contains("C:", window.LiveDiskCurrent.Text);
                Assert.Contains("F:", window.LiveDiskCurrent.Text);
                Assert.Contains("SMART lifetime anchors", window.TrendCaption.Text);
                Assert.Contains("read across", window.SmartWearLifeText.Text);

                IReadOnlyList<DiskInfo> repoDisks = repo.GetDisks();
                var allAnchored = repoDisks.Select(disk => new DiskInfo
                {
                    DiskId = disk.DiskId,
                    InstanceName = disk.InstanceName,
                    FriendlyName = disk.FriendlyName,
                    Volumes = disk.Volumes,
                    MediaType = disk.MediaType,
                    LifetimeBytesWritten = 100_000,
                }).ToList();
                typeof(MainWindow).GetMethod("UpdateChart", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [allAnchored]);
                Assert.Contains("Combined drive lifetime totals", window.TrendCaption.Text);

                DiskInfo unanchored = repoDisks.Single(disk => disk.DiskId == "1");
                typeof(MainWindow).GetMethod("UpdateChart", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [new List<DiskInfo> { unanchored }]);
                Assert.Contains("lifetime SMART total is unavailable", window.TrendCaption.Text);

                var recentDisk = new DiskInfo
                {
                    DiskId = "recent", InstanceName = "recent", FriendlyName = "Recent",
                    MediaType = DiskMediaType.Ssd,
                };
                repo.AddDiskSamples([new DiskSample
                {
                    TimestampUtc = now.AddMinutes(-1), DiskId = recentDisk.DiskId, WriteBytes = 1,
                }]);
                typeof(MainWindow).GetMethod("UpdateAggregateEndurance", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [new List<DiskInfo> { recentDisk }, now]);

                Invoke(window, "ConfigureChart_Click", new MenuItem { Tag = "total" }, new RoutedEventArgs());
                Assert.Equal("All disks total written", Property<string>(window.ChartColorList.Items[0], "Label"));
                Invoke(window, "ChartConfigClose_Click", window, new RoutedEventArgs());

                Assert.Equal("Friendly", InvokePrivateStatic<string>("DiskChartLabel", new DiskInfo
                {
                    DiskId = "9", InstanceName = "9", FriendlyName = "Friendly",
                }));
                Assert.Equal("Disk 10", InvokePrivateStatic<string>("DiskChartLabel", new DiskInfo
                {
                    DiskId = "10", InstanceName = "10",
                }));

                var liveZoom = new System.Windows.Input.MouseWheelEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount,
                    120)
                {
                    RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent,
                };
                Invoke(window, "LiveDiskChart_MouseWheel", window.LiveDiskActivityChart, liveZoom);
                Assert.Contains("last 10 min", window.LiveDiskCaption.Text);
                typeof(MainWindow).GetField("_liveGraphWindowMinutes", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, 1);
                var liveBoundary = new System.Windows.Input.MouseWheelEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120)
                { RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent };
                Invoke(window, "LiveDiskChart_MouseWheel", window.LiveDiskActivityChart, liveBoundary);
                Assert.False(liveBoundary.Handled);
                typeof(MainWindow).GetField("_liveGraphWindowMinutes", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, 999);
                var liveBeyond = new System.Windows.Input.MouseWheelEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
                { RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent };
                Invoke(window, "LiveDiskChart_MouseWheel", window.LiveDiskActivityChart, liveBeyond);

                var trendZoom = new System.Windows.Input.MouseWheelEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount,
                    -120)
                {
                    RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent,
                };
                Invoke(window, "TotalWrittenChart_MouseWheel", window.TotalWrittenTrendChart, trendZoom);
                var zoomWindow = (TimeSpan?)typeof(MainWindow)
                    .GetField("_trendZoomWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(window);
                Assert.Equal(TimeSpan.FromHours(6), zoomWindow);

                Invoke(window, "Range_Click", window.Btn24h, new RoutedEventArgs());
                Invoke(window, "Range_Click", window.Btn30d, new RoutedEventArgs());
                Invoke(window, "Range_Click", window.Btn1h, new RoutedEventArgs());
                var trendBoundary = new System.Windows.Input.MouseWheelEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120)
                { RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent };
                Invoke(window, "TotalWrittenChart_MouseWheel", window.TotalWrittenTrendChart, trendBoundary);
                Assert.False(trendBoundary.Handled);

                typeof(MainWindow).GetField("_trendZoomWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, TimeSpan.FromDays(500));
                typeof(MainWindow).GetField("_trendRange", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, Enum.Parse(typeof(MainWindow).GetNestedType("TrendRangeKind", BindingFlags.NonPublic)!, "Zoom"));
                var trendBeyond = new System.Windows.Input.MouseWheelEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120)
                { RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent };
                Invoke(window, "TotalWrittenChart_MouseWheel", window.TotalWrittenTrendChart, trendBeyond);

                typeof(MainWindow).GetField("_trendRange", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, Enum.Parse(typeof(MainWindow).GetNestedType("TrendRangeKind", BindingFlags.NonPublic)!, "Custom"));
                window.TrendStartDate.SelectedDate = DateTime.Today;
                window.TrendEndDate.SelectedDate = DateTime.Today.AddDays(-1);
                var invalidTrend = new System.Windows.Input.MouseWheelEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
                { RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent };
                Invoke(window, "TotalWrittenChart_MouseWheel", window.TotalWrittenTrendChart, invalidTrend);
                Assert.False(invalidTrend.Handled);

                Assert.Equal(long.MaxValue, MainWindow.SaturatingAdd(long.MaxValue - 1, 2));
                Assert.Equal(long.MinValue, MainWindow.SaturatingAdd(long.MinValue + 1, -2));
                Assert.Equal(7, MainWindow.SaturatingAdd(5, 2));
                Assert.Equal(3, MainWindow.SaturatingAdd(5, -2));
                Assert.Equal("No drives expose lifetime-write totals.", MainWindow.FormatAggregateLifetime(0, 0, 0, 0));
                Assert.DoesNotContain("read across", MainWindow.FormatAggregateLifetime(100, 1, 0, 0));
                Assert.Contains("read across", MainWindow.FormatAggregateLifetime(100, 1, 50, 1));

                window.DiskSelector.SelectedItem = ((IEnumerable)window.DiskSelector.ItemsSource).Cast<object>()
                    .First(choice => choice.GetType().GetProperty("Disk")!.GetValue(choice) is DiskInfo);
                Invoke(window, "LoadDisks");
                window.DiskSelector.SelectedItem = null;
                Invoke(window, "CustomTrendApply_Click", window, new RoutedEventArgs());

                await Task.CompletedTask;
            }
            finally { window.ForceClose(); }
        });
    }

    [Fact]
    public void AllDisks_NoHistoryOrLifetimeTelemetry_RendersSafeEmptyState()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db);
            repo.EnsureSchema();
            repo.UpsertDisks([
                new DiskInfo { DiskId = "0", InstanceName = "0", FriendlyName = "No SMART A", MediaType = DiskMediaType.Ssd },
                new DiskInfo { DiskId = "1", InstanceName = "1", FriendlyName = "No SMART B", MediaType = DiskMediaType.Ssd },
            ]);
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_userSettings));
            try
            {
                object allDisks = ((IEnumerable)window.DiskSelector.ItemsSource).Cast<object>()
                    .Single(choice => Property<object?>(choice, "Disk") is null);
                window.DiskSelector.SelectedItem = allDisks;

                Assert.Equal("No drives expose lifetime-write totals.", window.SmartWearLifeText.Text);
                Assert.Equal("-", window.EnduranceAvgHour.Text);
                Assert.Equal("-", window.EnduranceAvgDay.Text);
                Assert.Equal("No samples", window.TrendChangeText.Text);
                Assert.Contains("no samples", window.TrendCaption.Text);
                await Task.CompletedTask;
            }
            finally { window.ForceClose(); }
        });
    }

    [Fact]
    public void ChartColorsAndCollapsedPanels_PersistAcrossWindows()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db);
            repo.EnsureSchema();
            repo.UpsertDisks([new DiskInfo
            {
                DiskId = "0", InstanceName = "0 C:", FriendlyName = "System SSD", Volumes = "C:",
                MediaType = DiskMediaType.Ssd,
            }]);
            using var config = new ConfigStore(_cfg);
            var settings = new UserSettingsStore(_userSettings);
            var window = new MainWindow(repo, config, settings);
            try
            {
                var menuItem = new MenuItem { Tag = "live" };
                Invoke(window, "ConfigureChart_Click", menuItem, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, window.ChartConfigOverlay.Visibility);
                Assert.Equal(2, window.ChartColorList.Items.Count);

                object firstRow = window.ChartColorList.Items[0];
                Assert.Contains("read", Property<string>(firstRow, "Label"));
                Assert.Contains("Choose color", Property<string>(firstRow, "ChooseAutomationName"));
                Assert.NotNull(Property<Brush>(firstRow, "PreviewBrush"));
                string originalHex = Property<string>(firstRow, "Hex");
                firstRow.GetType().GetProperty("Hex")!.SetValue(firstRow, originalHex);
                ((System.ComponentModel.INotifyPropertyChanged)firstRow).PropertyChanged += (_, _) => { };
                firstRow.GetType().GetProperty("Hex")!.SetValue(firstRow, "#654321");
                firstRow.GetType().GetProperty("Hex")!.SetValue(firstRow, "invalid");
                Invoke(window, "ChartConfigSave_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, window.ChartConfigError.Visibility);
                Invoke(window, "ChartConfigReset_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.ChartConfigError.Visibility);

                var colorButton = new Button { DataContext = firstRow };
                window.ChartColorPicker = _ => Color.FromRgb(0x12, 0x34, 0x56);
                Invoke(window, "ChartColorChoose_Click", colorButton, new RoutedEventArgs());
                Assert.Equal("#123456", Property<string>(firstRow, "Hex"));
                window.ChartColorPicker = _ => null;
                Invoke(window, "ChartColorChoose_Click", colorButton, new RoutedEventArgs());
                Invoke(window, "ChartColorChoose_Click", window, new RoutedEventArgs());
                Invoke(window, "ChartColorChoose_Click", new object(), new RoutedEventArgs());

                Type chartColorRowType = firstRow.GetType();
                Brush fallbackPreview = (Brush)chartColorRowType
                    .GetMethod("Preview", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, ["invalid", Colors.Red])!;
                Assert.Equal(Colors.Red, Assert.IsType<SolidColorBrush>(fallbackPreview).Color);
                object rowWithoutSubscriber = Activator.CreateInstance(
                    chartColorRowType, "key", "label", Colors.Blue, null)!;
                chartColorRowType.GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(rowWithoutSubscriber, ["Hex"]);
                bool notified = false;
                ((System.ComponentModel.INotifyPropertyChanged)rowWithoutSubscriber).PropertyChanged += (_, _) => notified = true;
                chartColorRowType.GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(rowWithoutSubscriber, ["Hex"]);
                Assert.True(notified);

                firstRow.GetType().GetProperty("Hex")!.SetValue(firstRow, "#123456");
                Invoke(window, "ChartConfigSave_Click", window, new RoutedEventArgs());
                Assert.Equal("#123456", settings.Current.ChartColors["live:0:read"]);

                Invoke(window, "ConfigureChart_Click", new MenuItem { Tag = "total" }, new RoutedEventArgs());
                Assert.Single(window.ChartColorList.Items);
                Invoke(window, "ChartConfigClose_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.ChartConfigOverlay.Visibility);

                Invoke(window, "ConfigureChart_Click", new MenuItem { Tag = "throughput" }, new RoutedEventArgs());
                Assert.Equal(3, window.ChartColorList.Items.Count);
                var space = new System.Windows.Input.KeyEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice,
                    new FakePresentationSource(),
                    Environment.TickCount,
                    System.Windows.Input.Key.Space)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                Invoke(window, "ChartConfigOverlay_PreviewKeyDown", window.ChartConfigOverlay, space);
                Assert.False(space.Handled);
                var escape = new System.Windows.Input.KeyEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice,
                    new FakePresentationSource(),
                    Environment.TickCount,
                    System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                Invoke(window, "ChartConfigOverlay_PreviewKeyDown", window.ChartConfigOverlay, escape);
                Assert.True(escape.Handled);
                Assert.Equal(Visibility.Collapsed, window.ChartConfigOverlay.Visibility);

                Invoke(window, "ConfigureChart_Click", new MenuItem { Tag = "unknown" }, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.ChartConfigOverlay.Visibility);
                Invoke(window, "ConfigureChart_Click", window, new RoutedEventArgs());
                Invoke(window, "ConfigureChart_Click", new object(), new RoutedEventArgs());
                window.DiskSelector.SelectedItem = null;
                Invoke(window, "ConfigureChart_Click", new MenuItem { Tag = "live" }, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.ChartConfigOverlay.Visibility);

                window.SetPanelCollapsed("live-activity", true);
                Assert.True(window.IsPanelCollapsed("live-activity"));
                Assert.Contains("live-activity", settings.Current.CollapsedPanels);
                window.SetPanelCollapsed("live-activity", false);
                Assert.False(window.IsPanelCollapsed("live-activity"));
                window.SetPanelCollapsed("missing", true);
                Assert.False(window.IsPanelCollapsed("missing"));

                var cards = (System.Collections.IDictionary)typeof(MainWindow)
                    .GetField("_collapsibleCards", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(window)!;
                object liveCard = cards["live-activity"]!;
                var chevron = Property<System.Windows.Shapes.Path>(liveCard, "Chevron");
                Assert.IsType<Button>(chevron.Parent).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(window.IsPanelCollapsed("live-activity"));

                window.InitializeCollapsiblePanel(new Border { Tag = "missing", Child = new TextBlock() }, new HashSet<string>());
                window.InitializeCollapsiblePanel(new Border { Tag = "live-activity" }, new HashSet<string>());
                window.InitializeCollapsiblePanel(new Border { Child = new TextBlock() }, new HashSet<string>());
                object previousSecondary = window.Resources["TextSecondary"];
                window.Resources["TextSecondary"] = "not a brush";
                window.InitializeCollapsiblePanel(
                    new Border { Tag = "summary-24h", Child = new TextBlock() },
                    new HashSet<string> { "summary-24h" });
                window.Resources["TextSecondary"] = previousSecondary;

                MethodInfo logicalChildren = typeof(MainWindow)
                    .GetMethod("FindLogicalChildren", BindingFlags.Static | BindingFlags.NonPublic)!
                    .MakeGenericMethod(typeof(TextBlock));
                var textRoot = new TextBlock { Text = "logical text" };
                _ = ((IEnumerable)logicalChildren.Invoke(null, [textRoot])!).Cast<object>().ToList();
            }
            finally { window.ForceClose(); }

            var restored = new MainWindow(repo, config, new UserSettingsStore(_userSettings));
            try
            {
                Assert.True(restored.IsPanelCollapsed("live-activity"));
                Assert.Equal("#123456", new UserSettingsStore(_userSettings).Current.ChartColors["live:0:read"]);
                Invoke(restored, "ConfigureChart_Click", new MenuItem { Tag = "live" }, new RoutedEventArgs());
                Assert.Equal("#123456", Property<string>(restored.ChartColorList.Items[0], "Hex"));
                await Task.CompletedTask;
            }
            finally { restored.ForceClose(); }
        });
    }

    [Fact]
    public void EnduranceAlertSettings_EditDefaultsOverridesAndInAppSnooze()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db);
            repo.EnsureSchema();
            repo.UpsertDisks([new DiskInfo
            {
                DiskId = "0", InstanceName = "0 C:", FriendlyName = "System SSD", Volumes = "C:",
                MediaType = DiskMediaType.Ssd,
            }]);
            repo.InsertAlert(new AlertRecord
            {
                TimestampUtc = DateTime.UtcNow,
                Severity = AlertSeverity.Warning,
                RuleKey = "endurance-health:0",
                Title = "Endurance warning",
                Message = "20% remaining",
                Value = 20,
                Threshold = 20,
            });

            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_userSettings));
            try
            {
                object allDisks = ((IEnumerable)window.DiskSelector.ItemsSource).Cast<object>()
                    .Single(choice => choice.GetType().GetProperty("Disk")!.GetValue(choice) is null);
                object physicalDisk = ((IEnumerable)window.DiskSelector.ItemsSource).Cast<object>()
                    .Single(choice => choice.GetType().GetProperty("Disk")!.GetValue(choice) is DiskInfo);

                window.DiskSelector.SelectedItem = allDisks;
                Assert.Equal(1, window.HeaderSeparator.Height);
                Assert.Equal(34, window.TxtWarnHour.MinHeight);
                Assert.IsType<System.Windows.Controls.Primitives.UniformGrid>(window.TxtWarnHour.Parent is DockPanel dock
                    ? ((StackPanel)dock.Parent).Parent
                    : null);
                Assert.Equal(Visibility.Collapsed, window.ChkEnduranceAlertOverride.Visibility);
                Assert.Equal("1", window.TxtEnduranceLifeValue.Text);
                Assert.Equal("20", window.TxtEnduranceRemainingPercent.Text);
                window.TxtEnduranceLifeValue.Text = "6";
                window.EnduranceLifeUnitSelector.SelectedIndex = 1;
                window.TxtEnduranceRemainingPercent.Text = "15";
                Invoke(window, "Save_Click", window, new RoutedEventArgs());

                Assert.Equal(6, config.Current.DefaultEnduranceAlert.RemainingLifeValue);
                Assert.Equal(EnduranceAlertTimeUnit.Months, config.Current.DefaultEnduranceAlert.RemainingLifeUnit);
                Assert.Equal(15, config.Current.DefaultEnduranceAlert.RemainingPercent);

                window.DiskSelector.SelectedItem = physicalDisk;
                Assert.False(window.ChkEnduranceAlertOverride.IsChecked);
                Assert.False(window.TxtEnduranceLifeValue.IsEnabled);
                window.ChkEnduranceAlertOverride.IsChecked = true;
                window.TxtEnduranceLifeValue.Text = "45";
                window.EnduranceLifeUnitSelector.SelectedIndex = 0;
                window.TxtEnduranceRemainingPercent.Text = "5";
                Invoke(window, "Save_Click", window, new RoutedEventArgs());

                EnduranceAlertThreshold diskThreshold = config.Current.EffectiveEnduranceAlert("0");
                Assert.Equal(45, diskThreshold.RemainingLifeValue);
                Assert.Equal(EnduranceAlertTimeUnit.Days, diskThreshold.RemainingLifeUnit);
                Assert.Equal(5, diskThreshold.RemainingPercent);

                window.ChkEnduranceAlertOverride.IsChecked = false;
                Invoke(window, "Save_Click", window, new RoutedEventArgs());
                Assert.False(config.Current.DiskEnduranceAlertOverrides.ContainsKey("0"));
                Assert.Equal(6, config.Current.EffectiveEnduranceAlert("0").RemainingLifeValue);

                window.UpdateAlerts();
                object alertRow = ((IEnumerable)window.AlertList.ItemsSource).Cast<object>().Single();
                Assert.Equal(
                    Visibility.Visible,
                    alertRow.GetType().GetProperty("SnoozeVisibility")!.GetValue(alertRow));

                System.Windows.Controls.ContextMenu? snoozeMenu = null;
                window.AlertSnoozeMenuPresenter = (menu, _) => snoozeMenu = menu;
                var snoozeButton = new Button { CommandParameter = alertRow };
                Invoke(window, "SnoozeEnduranceAlert_Click", snoozeButton, new RoutedEventArgs());
                Assert.NotNull(snoozeMenu);
                Assert.Equal(SnoozeOptions.Choices.Length, snoozeMenu.Items.Count);
                Assert.All(snoozeMenu.Items.Cast<MenuItem>(), item => Assert.NotNull(item.Tag));
                Assert.IsType<MenuItem>(snoozeMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.True(repo.IsAlertRuleSnoozed("endurance-health:0", DateTime.UtcNow));
                Invoke(window, "SnoozeEnduranceAlert_Click", window, new RoutedEventArgs());
                Invoke(window, "SnoozeEnduranceAlert_Click", new Button(), new RoutedEventArgs());
                Invoke(window, "SnoozeEnduranceAlert_Click",
                    new Button { CommandParameter = CreateAlertRow("0", true) }, new RoutedEventArgs());

                Assert.False(MainWindow.TryParseEnduranceAlert(
                    false, "1", EnduranceAlertTimeUnit.Years, false, "20", out _, out string validationError));
                Assert.Contains("at least one", validationError);
                AssertEnduranceAlertValidationBranches();

                Invoke(window, "SelectEnduranceUnit", (EnduranceAlertTimeUnit)99);
                Assert.Equal(2, window.EnduranceLifeUnitSelector.SelectedIndex);
                window.EnduranceLifeUnitSelector.Items.Add("not an item");
                window.EnduranceLifeUnitSelector.Items.Add(new ComboBoxItem());
                Invoke(window, "SelectEnduranceUnit", (EnduranceAlertTimeUnit)99);
                window.EnduranceLifeUnitSelector.SelectedItem = null;
                Assert.Equal(
                    EnduranceAlertTimeUnit.Years,
                    typeof(MainWindow).GetMethod("SelectedEnduranceUnit", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(window, null));
                window.EnduranceLifeUnitSelector.SelectedItem = new ComboBoxItem { Tag = "invalid" };
                Assert.Equal(
                    EnduranceAlertTimeUnit.Years,
                    typeof(MainWindow).GetMethod("SelectedEnduranceUnit", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(window, null));
                    Assert.Equal(EnduranceAlertTimeUnit.Years, MainWindow.ParseEnduranceUnit(null));
                    Assert.Equal(EnduranceAlertTimeUnit.Years, MainWindow.ParseEnduranceUnit(new ComboBoxItem()));
                    Assert.Equal(EnduranceAlertTimeUnit.Years, MainWindow.ParseEnduranceUnit(new ComboBoxItem { Tag = "invalid" }));
                    Assert.Equal(EnduranceAlertTimeUnit.Months, MainWindow.ParseEnduranceUnit(new ComboBoxItem { Tag = "Months" }));

                window.DiskSelector.SelectedItem = allDisks;
                Invoke(window, "EnduranceAlertOverride_Changed", window, new RoutedEventArgs());
                window.TxtEnduranceLifeValue.Text = "bad";
                Invoke(window, "Save_Click", window, new RoutedEventArgs());
                Assert.Contains("greater than 0", window.SaveStatus.Text);
                await Task.CompletedTask;
            }
            finally { window.ForceClose(); }
        });
    }

    [Fact]
    public void DashboardChangedPaths_RenderAndPersistRealWpfState()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            repo.UpsertDisks([new DiskInfo
            {
                DiskId = "2", InstanceName = "2 F:", FriendlyName = "Test HDD", Volumes = "F:", MediaType = DiskMediaType.Hdd,
            }, new DiskInfo
            {
                DiskId = "8", InstanceName = "8 G:", FriendlyName = "", Volumes = "G:", MediaType = DiskMediaType.Ssd,
            }]);
            using var config = new ConfigStore(_cfg);
            var userSettings = new UserSettingsStore(_userSettings);
            UpdateUserSettings(userSettings, settings => settings.EnableNotifications = false);
            var now = DateTime.UtcNow;
            long ordinaryId = repo.InsertAlert(Alert(now.AddMinutes(-3), AlertSeverity.Info, "ordinary", 0));
            repo.InsertAlert(Alert(now.AddMinutes(-2), AlertSeverity.Warning, "disk-controller:2", 4));
            repo.InsertAlert(Alert(now.AddMinutes(-1), AlertSeverity.Critical, "disk-controller:2", 5));

            var window = new MainWindow(repo, config, userSettings);
            window.Resources["TextPrimary"] = new SolidColorBrush(Colors.White);
            window.Resources["Caption"] = new Style(typeof(TextBlock));
            window.Resources["ToolButton"] = new Style(typeof(Button));
            object hddChoice = ((IEnumerable)window.DiskSelector.ItemsSource).Cast<object>()
                .First(choice => choice.GetType().GetProperty("Disk")!.GetValue(choice) is DiskInfo disk && disk.DiskId == "2");
            window.DiskSelector.SelectedItem = hddChoice;
            try
            {
                Assert.Equal("Segoe UI", window.FontFamily.Source);
                Assert.NotNull(window.HeaderAppIcon.Source);
                Assert.Equal(42, window.HeaderAppIcon.Width);
                Assert.Equal(42, window.HeaderAppIcon.Height);
                var headerPanel = Assert.IsType<StackPanel>(window.HeaderAppIcon.Parent);
                Assert.Equal(Orientation.Horizontal, headerPanel.Orientation);
                Assert.Same(window.HeaderAppIcon, headerPanel.Children[0]);
                var titlePanel = Assert.IsType<StackPanel>(headerPanel.Children[1]);
                Assert.Equal("Disk Activity Monitor", Assert.IsType<TextBlock>(titlePanel.Children[0]).Text);

                Invoke(window, "SaveRules_Click", window, new RoutedEventArgs());
                Assert.False(userSettings.Current.EnableNotifications);

                using (var defaultPresenterController = new TrayController(repo, config, userSettings))
                {
                    typeof(TrayController).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(defaultPresenterController, window);
                    defaultPresenterController.TbwSetupPresenter();
                    Assert.Equal(Visibility.Visible, window.TbwSetupOverlay.Visibility);
                    window.TbwSetupOverlay.Visibility = Visibility.Collapsed;
                    typeof(TrayController).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(defaultPresenterController, null);
                }

                Invoke(window, "TbwLookupClose_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.TbwLookupPanel.Visibility);

                typeof(MainWindow).GetField("_helpInitialized", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, true);
                Invoke(window, "Help_Click", window.HelpButton, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, window.HelpOverlay.Visibility);
                Assert.Equal("Help and troubleshooting", window.HelpButton.ToolTip);
                Invoke(window, "HelpClose_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.HelpOverlay.Visibility);

                string fallbackMarkdown = Path.Combine(Path.GetTempPath(), $"dam_help_{Guid.NewGuid():N}.md");
                File.WriteAllText(fallbackMarkdown, "# Help\n\nUse **Refresh** and [open docs](https://example.test).\n\n```powershell\ndam status\n```");
                try
                {
                    window.ShowHelpFallback("fallback test", fallbackMarkdown);
                    Assert.Equal(Visibility.Visible, window.HelpFallbackPanel.Visibility);
                    Assert.Contains("Use Refresh and open docs", window.HelpFallbackText.Text);
                    Assert.Contains("dam status", window.HelpFallbackText.Text);
                }
                finally { File.Delete(fallbackMarkdown); }

                window.UpdateAlerts();
                var rows = ((IEnumerable)window.AlertList.ItemsSource).Cast<object>().ToList();
                Assert.Equal(2, rows.Count);
                var alertTemplateRoot = Assert.IsAssignableFrom<FrameworkElement>(window.AlertList.ItemTemplate.LoadContent());
                var alertDismissButton = Assert.IsType<Button>(alertTemplateRoot.FindName("AlertDismissButton"));
                Assert.Equal(28, alertDismissButton.Width);
                Assert.Equal(28, alertDismissButton.Height);
                Assert.Equal(new Thickness(1), alertDismissButton.BorderThickness);
                Assert.Equal(Color.FromRgb(0x3B, 0x24, 0x28), Assert.IsType<SolidColorBrush>(alertDismissButton.Background).Color);
                Assert.Equal("\u2715", alertDismissButton.Content);
                Assert.Equal("Segoe UI", alertDismissButton.FontFamily.Source);
                Assert.Equal("\u2715", window.AlertSearchClearButton.Content);
                Assert.Equal("Segoe UI", window.AlertSearchClearButton.FontFamily.Source);
                Assert.Equal(LocalTimeDisplay.ZoneLabel(), window.TrendTimeZoneText.Text);
                Assert.All(rows, row => Assert.EndsWith($"({LocalTimeDisplay.ZoneId()})",
                    (string)row.GetType().GetProperty("TimeText")!.GetValue(row)!));

                window.AlertSearchBox.Text = "ORDINARY";
                object searchResult = Assert.Single(((IEnumerable)window.AlertList.ItemsSource).Cast<object>());
                Assert.Equal("ordinary", searchResult.GetType().GetProperty("Title")!.GetValue(searchResult));
                Assert.Equal(Visibility.Visible, window.AlertSearchClearButton.Visibility);
                window.UpdateAlerts();
                Assert.Single(((IEnumerable)window.AlertList.ItemsSource).Cast<object>());
                window.AlertSearchBox.Text = "MESSAGE";
                Assert.Equal(2, ((IEnumerable)window.AlertList.ItemsSource).Cast<object>().Count());
                window.AlertSearchBox.Text = "no matching alert";
                Assert.Empty(((IEnumerable)window.AlertList.ItemsSource).Cast<object>());
                Assert.Equal(Visibility.Visible, window.AlertEmpty.Visibility);
                Assert.Equal("No alerts match your search.", window.AlertEmpty.Text);
                Invoke(window, "AlertSearchClear_Click", window.AlertSearchClearButton, new RoutedEventArgs());
                Assert.Equal("", window.AlertSearchBox.Text);
                Assert.Equal(2, ((IEnumerable)window.AlertList.ItemsSource).Cast<object>().Count());
                Assert.Equal(Visibility.Collapsed, window.AlertSearchClearButton.Visibility);

                foreach (object row in rows)
                    foreach (PropertyInfo property in row.GetType().GetProperties())
                        _ = property.GetValue(row);

                object ordinaryRow = rows.Single(row => !(bool)row.GetType().GetProperty("CanRunSmartScan")!.GetValue(row)!);
                Invoke(window, "DismissAlert_Click", new Button { CommandParameter = ordinaryRow }, new RoutedEventArgs());
                Assert.Single(((IEnumerable)window.AlertList.ItemsSource).Cast<object>());
                Assert.True(repo.GetRecentAlerts(10).Single(a => a.Id == ordinaryId).Acknowledged);

                Invoke(window, "ShowAllAlerts_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, window.AlertHistoryOverlay.Visibility);
                var historyRows = ((IEnumerable)window.AlertHistoryList.ItemsSource).Cast<object>().ToList();
                Assert.Equal(3, historyRows.Count);
                Assert.All(historyRows, row => Assert.EndsWith($"({LocalTimeDisplay.ZoneId()})",
                    (string)row.GetType().GetProperty("TimeText")!.GetValue(row)!));
                foreach (object row in historyRows)
                    foreach (PropertyInfo property in row.GetType().GetProperties())
                        _ = property.GetValue(row);
                object dismissedRow = historyRows.Single(row => (long)row.GetType().GetProperty("Id")!.GetValue(row)! == ordinaryId);
                Assert.Equal("Dismissed", dismissedRow.GetType().GetProperty("StatusText")!.GetValue(dismissedRow));

                Invoke(window, "RestoreHistoryAlert_Click", new Button { CommandParameter = dismissedRow }, new RoutedEventArgs());
                Assert.Equal(2, ((IEnumerable)window.AlertList.ItemsSource).Cast<object>().Count());
                Assert.False(repo.GetRecentAlerts(10).Single(a => a.Id == ordinaryId).Acknowledged);

                Invoke(window, "DismissAlert_Click", new Button { DataContext = ordinaryRow }, new RoutedEventArgs());
                Assert.True(repo.GetRecentAlerts(10).Single(a => a.Id == ordinaryId).Acknowledged);
                dismissedRow = ((IEnumerable)window.AlertHistoryList.ItemsSource).Cast<object>()
                    .Single(row => (long)row.GetType().GetProperty("Id")!.GetValue(row)! == ordinaryId);
                Invoke(window, "RestoreHistoryAlert_Click", new Button { CommandParameter = dismissedRow }, new RoutedEventArgs());

                Invoke(window, "DismissAlert_Click", new MenuItem { CommandParameter = ordinaryRow }, new RoutedEventArgs());
                repo.RestoreAlerts([ordinaryId]); window.UpdateAlerts(); window.UpdateAlertHistory();
                Invoke(window, "DismissAlert_Click", new MenuItem { DataContext = ordinaryRow }, new RoutedEventArgs());
                repo.RestoreAlerts([ordinaryId]); window.UpdateAlerts(); window.UpdateAlertHistory();
                Invoke(window, "DismissAlert_Click", new Button { CommandParameter = CreateAlertRow("", false, []) }, new RoutedEventArgs());

                object visibleHistoryRow = ((IEnumerable)window.AlertHistoryList.ItemsSource).Cast<object>()
                    .Single(row => (long)row.GetType().GetProperty("Id")!.GetValue(row)! == ordinaryId);
                Invoke(window, "DismissHistoryAlert_Click", new Button { CommandParameter = visibleHistoryRow }, new RoutedEventArgs());
                Assert.Single(((IEnumerable)window.AlertList.ItemsSource).Cast<object>());
                dismissedRow = ((IEnumerable)window.AlertHistoryList.ItemsSource).Cast<object>()
                    .Single(row => (long)row.GetType().GetProperty("Id")!.GetValue(row)! == ordinaryId);
                Invoke(window, "RestoreHistoryAlert_Click", new Button { CommandParameter = dismissedRow }, new RoutedEventArgs());
                Invoke(window, "DismissHistoryAlert_Click", new object(), new RoutedEventArgs());
                Invoke(window, "RestoreHistoryAlert_Click", new object(), new RoutedEventArgs());
                Invoke(window, "DismissAlert_Click", new object(), new RoutedEventArgs());
                Invoke(window, "AlertHistoryClose_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.AlertHistoryOverlay.Visibility);

                Assert.Equal("—", MainWindow.BlankAsDash(" "));
                Assert.Equal("x", MainWindow.BlankAsDash("x"));
                Assert.Equal("Not exposed", MainWindow.FormatLifetime(null, null));
                Assert.Contains("written", MainWindow.FormatLifetime(1, null));
                Assert.Contains("read", MainWindow.FormatLifetime(null, 1));
                Assert.Contains("·", MainWindow.FormatLifetime(1, 2));

                foreach (SmartScanGrade grade in Enum.GetValues<SmartScanGrade>())
                {
                    window.RenderSmartScanResult(Result(grade,
                        controllerErrors: grade == SmartScanGrade.Attention ? 2 : 0,
                        temperature: grade == SmartScanGrade.Critical ? 60 : grade == SmartScanGrade.Healthy ? 50 : null,
                        telemetry: grade != SmartScanGrade.Limited));
                    Assert.Equal(Visibility.Visible, window.SmartScanResultPanel.Visibility);
                    Assert.EndsWith($"({LocalTimeDisplay.ZoneId()})", window.SmartScanTimeValue.Text);
                }
                window.RenderSmartScanResult(Result(SmartScanGrade.Healthy, 0, 49, true, maximum: 70));
                window.RenderSmartScanFailure(Disk(), "boom");
                Assert.Contains("boom", window.SmartScanStatusText.Text);
                window.SmartScanOverlay.Visibility = Visibility.Collapsed;
                Assert.Equal(Visibility.Collapsed, window.SmartScanOverlay.Visibility);

                Invoke(window, "RunSmartScan_Click", new object(), new RoutedEventArgs());
                Invoke(window, "RunSmartScan_Click", new MenuItem { CommandParameter = CreateAlertRow(" ", true) }, new RoutedEventArgs());
                Invoke(window, "SmartScanAgain_Click", window, new RoutedEventArgs());
                window.SmartScanner = (disk, errors, progress) =>
                {
                    progress?.Report("fake progress");
                    return Result(SmartScanGrade.Attention, errors, 40, true);
                };
                await window.StartSmartScanAsync(Disk(), 3);
                Assert.Equal("fake progress", window.SmartScanProgressText.Text);
                var controllerRow = rows.First(row => (bool)row.GetType().GetProperty("CanRunSmartScan")!.GetValue(row)!);
                Invoke(window, "RunSmartScan_Click", new MenuItem { CommandParameter = controllerRow }, new RoutedEventArgs());
                await Task.Delay(50);
                Invoke(window, "RunSmartScan_Click", new MenuItem { DataContext = controllerRow }, new RoutedEventArgs());
                await Task.Delay(50);
                Invoke(window, "RunSmartScan_Click", new MenuItem { CommandParameter = CreateAlertRow("404", true) }, new RoutedEventArgs());
                await Task.Delay(50);
                Invoke(window, "SmartScanAgain_Click", window, new RoutedEventArgs());
                await Task.Delay(50);
                window.SmartScanner = (_, _, _) => throw new InvalidOperationException("scan failed");
                await window.StartSmartScanAsync(Disk(), 1);
                Assert.Contains("scan failed", window.SmartScanStatusText.Text);

                await ExerciseStaleScans(window);

                window.LoadSettingsFields();
                window.TxtWarnHour.Text = "invalid";
                window.TxtControllerWindow.Text = "0";
                window.TxtControllerWarn.Text = "8";
                window.TxtControllerCritical.Text = "2";
                window.TxtInterval.Text = "999";
                window.TxtRefresh.Text = "9999";
                window.TxtHighCoveragePercent.Text = "92.5";
                window.RadTbwRange.IsChecked = true;
                window.TxtTbw.Text = "900";
                window.TxtTbwUpper.Text = "1200";
                window.ChkControllerErrors.IsChecked = false;
                window.Save_Click(window, new RoutedEventArgs());
                Assert.Equal(1, config.Current.ControllerErrorWindowDays);
                Assert.Equal(8, config.Current.ControllerErrorCriticalCount);
                Assert.Equal(60, config.Current.SampleIntervalSeconds);
                Assert.False(config.Current.EnableControllerErrorAlerts);
                Assert.Equal(92.5, config.Current.HighCoveragePercent);
                Assert.Equal(900, config.Current.DiskTbwRatings["2"]);
                Assert.Equal(1200, config.Current.DiskTbwRatingsUpper["2"]);

                window.TxtHighCoveragePercent.Text = "0";
                window.Save_Click(window, new RoutedEventArgs());
                Assert.Contains("between 1 and 100", window.SaveStatus.Text);
                Assert.Equal(92.5, config.Current.HighCoveragePercent);
                window.TxtHighCoveragePercent.Text = "92.5";

                window.TxtTbw.Text = "bad";
                window.TxtTbwUpper.Text = "1";
                window.TxtControllerWarn.Text = "0";
                window.Save_Click(window, new RoutedEventArgs());
                Assert.Equal("Enter a minimum TBW greater than 0.", window.SaveStatus.Text);
                Assert.Equal(900, config.Current.DiskTbwRatings["2"]);
                Assert.Equal(1200, config.Current.DiskTbwRatingsUpper["2"]);

                window.RadTbwSingle.IsChecked = true;
                window.TxtTbw.Text = "900";
                window.Save_Click(window, new RoutedEventArgs());
                Assert.Equal(900, config.Current.DiskTbwRatings["2"]);
                Assert.False(config.Current.DiskTbwRatingsUpper.ContainsKey("2"));
                Assert.Equal(Visibility.Collapsed, window.TbwUpperPanel.Visibility);

                await window.StartTbwLookupAsync(null);
                await window.StartTbwLookupAsync(Disk(), userInitiated: false);
                await window.StartTbwLookupAsync(Disk(), userInitiated: true);
                Assert.Contains("available for SSDs", window.TbwLookupStatus.Text);
                Assert.Equal(Visibility.Visible, window.TbwLookupPanel.Visibility);
                Assert.Equal("TBW lookup is not available for this drive", window.TbwLookupHeadline.Text);
                Assert.Equal(Visibility.Collapsed, window.TbwLookupProgressBar.Visibility);

                var blankSsd = new DiskInfo { DiskId = "8", InstanceName = "8", FriendlyName = "", MediaType = DiskMediaType.Ssd };
                var virtualSsd = new DiskInfo { DiskId = "9", InstanceName = "9", FriendlyName = "Virtual SSD", MediaType = DiskMediaType.Ssd };
                await window.StartTbwLookupAsync(blankSsd, userInitiated: false);
                await window.StartTbwLookupAsync(blankSsd, userInitiated: true);
                await window.StartTbwLookupAsync(virtualSsd, userInitiated: true);
                Assert.Contains("usable model name", window.TbwLookupStatus.Text);

                var knownSsd = new DiskInfo { DiskId = "7", InstanceName = "7", FriendlyName = "Samsung SSD 870 EVO 1TB", MediaType = DiskMediaType.Ssd };
                Assert.True(window.HandleTbwReadiness(new TbwReadiness(true, null, false, null, true)));
                Assert.False(window.HandleTbwReadiness(new TbwReadiness(false, null, true, "model-gpu", true)));
                Assert.Contains("GPU was detected", window.TbwLookupStatus.Text);
                Assert.False(window.HandleTbwReadiness(new TbwReadiness(false, null, true, "model-cpu", false)));
                Assert.Contains("CPU-only", window.TbwLookupStatus.Text);
                Assert.False(window.HandleTbwReadiness(new TbwReadiness(
                    false,
                    "Install the official package.",
                    false,
                    null,
                    false,
                    NeedsFoundryInstall: true)));
                Assert.Equal("Foundry Local required", window.TbwLookupHeadline.Text);
                Assert.Equal("Install Foundry Local", window.TbwLookupAction.Content);
                Assert.Equal("install-foundry", window.TbwLookupAction.Tag);
                Assert.Equal(Visibility.Visible, window.TbwLookupAction.Visibility);
                Assert.Equal(Visibility.Collapsed, window.TbwLookupAgainButton.Visibility);
                Assert.False(window.HandleTbwReadiness(new TbwReadiness(false, "configured reason", false, null, false)));
                Assert.Equal("configured reason", window.TbwLookupStatus.Text);
                Assert.False(window.HandleTbwReadiness(new TbwReadiness(false, null, false, null, false)));
                Assert.Equal("Web TBW lookup is unavailable.", window.TbwLookupStatus.Text);

                UpdateUserSettings(userSettings, settings => settings.EnableTbwWebLookup = false);
                await window.StartTbwLookupAsync(knownSsd);
                UpdateUserSettings(userSettings, settings => settings.EnableTbwWebLookup = true);
                config.Update(settings => settings.DiskTbwRatings[knownSsd.DiskId] = 600);
                await window.StartTbwLookupAsync(knownSsd);
                config.Update(settings => settings.DiskTbwRatings.Remove(knownSsd.DiskId));
                TbwLookupCache.Put(new TbwLookupResult(knownSsd.FriendlyName,
                    [new TbwCandidate(600, 1, 1, ["example.com"], "https://example.com")], DateTime.UtcNow));
                await window.StartTbwLookupAsync(knownSsd);
                Assert.NotEmpty(window.TbwCandidateList.Children);
                Assert.Equal("Verified candidate found", window.TbwLookupHeadline.Text);
                var candidateBorder = Assert.IsType<Border>(Assert.Single(window.TbwCandidateList.Children));
                var candidateGrid = Assert.IsType<Grid>(candidateBorder.Child);
                var applyButton = Assert.IsType<Button>(candidateGrid.Children[1]);
                applyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.StartsWith("Applied 600 TBW", window.TbwLookupHeadline.Text);

                window.RenderTbwResult(knownSsd, new TbwLookupResult(knownSsd.FriendlyName, [], DateTime.UtcNow));
                Assert.Equal("No verified TBW rating found", window.TbwLookupHeadline.Text);
                Assert.Equal("No TBW rating was found on the web for this drive.", window.TbwLookupStatus.Text);
                window.RenderTbwResult(knownSsd, new TbwLookupResult(knownSsd.FriendlyName, [], DateTime.UtcNow, "No verified evidence."));
                Assert.Equal("No verified evidence.", window.TbwLookupStatus.Text);

                const string searchResponse = "{\"organic\":[{\"title\":\"Drive result\"}]}";
                const string modelResponse =
                    "{\"choices\":[{\"message\":{\"content\":\"<think>returned reasoning</think>\\n[]\"}}]}";
                window.RenderTbwResult(knownSsd, new TbwLookupResult(
                    knownSsd.FriendlyName,
                    [],
                    DateTime.UtcNow,
                    Diagnostics: new TbwLookupDiagnostics("Serper.dev", searchResponse, "qwen3-8b", modelResponse)));
                Assert.Equal(Visibility.Visible, window.TbwLookupRawResultsButton.Visibility);
                Invoke(window, "TbwLookupRawResults_Click", window.TbwLookupRawResultsButton, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, window.TbwRawResultsOverlay.Visibility);
                Assert.Same(window.TbwRawSearchTab, window.TbwRawResultsTabs.SelectedItem);
                Assert.Contains(Environment.NewLine, window.TbwRawSearchText.Text);
                Assert.Contains("Drive result", window.TbwRawSearchText.Text);
                window.TbwRawResultsTabs.SelectedItem = window.TbwRawModelTab;
                Assert.Contains("<think>returned reasoning</think>", window.TbwRawModelText.Text);
                Assert.Contains("qwen3-8b", window.TbwRawModelMeta.Text);
                Invoke(window, "TbwRawResultsClose_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.TbwRawResultsOverlay.Visibility);
                Assert.Empty(window.TbwRawSearchText.Text);
                Assert.Empty(window.TbwRawModelText.Text);

                window.RenderTbwResult(knownSsd, new TbwLookupResult(
                    knownSsd.FriendlyName,
                    [],
                    DateTime.UtcNow,
                    Diagnostics: new TbwLookupDiagnostics("Serper.dev", searchResponse)));
                Invoke(window, "TbwLookupRawResults_Click", window.TbwLookupRawResultsButton, new RoutedEventArgs());
                Assert.Contains("Serper-only lookups do not call a model", window.TbwRawModelText.Text);
                Invoke(window, "TbwRawResultsClose_Click", window, new RoutedEventArgs());

                window.RenderTbwResult(knownSsd, new TbwLookupResult(knownSsd.FriendlyName, [], DateTime.UtcNow));
                Assert.Equal(Visibility.Collapsed, window.TbwLookupRawResultsButton.Visibility);
                window.RenderTbwResult(knownSsd, new TbwLookupResult(knownSsd.FriendlyName,
                    [
                        new TbwCandidate(600, .75, 4, ["a.com", "b.com", "c.com", "d.com"], "https://a.com"),
                        new TbwCandidate(1200, .25, 2, ["e.com", "f.com"], "https://e.com"),
                    ], DateTime.UtcNow, LookupMethod: TbwLookupMethod.SerperOnly));
                Assert.Equal("Evidence candidates found", window.TbwLookupHeadline.Text);
                Assert.Equal("Serper evidence only", window.TbwLookupAnalysisTitle.Text);
                Assert.Contains("No local AI verification", window.TbwLookupStatus.Text);
                Assert.Equal(3, window.TbwCandidateList.Children.Count);

                var rangeBorder = Assert.IsType<Border>(window.TbwCandidateList.Children[0]);
                var rangeGrid = Assert.IsType<Grid>(rangeBorder.Child);
                var rangeButton = Assert.IsType<Button>(rangeGrid.Children[1]);
                Assert.Equal("Apply range", rangeButton.Content);
                rangeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(600, config.Current.DiskTbwRatings[knownSsd.DiskId]);
                Assert.Equal(1200, config.Current.DiskTbwRatingsUpper[knownSsd.DiskId]);
                Assert.StartsWith("Applied 600 to 1200 TBW", window.TbwLookupHeadline.Text);

                window.RenderTbwResult(knownSsd, new TbwLookupResult(knownSsd.FriendlyName,
                    [new TbwCandidate(600, 1, 2, ["a.com", "b.com"], "https://a.com")],
                    DateTime.UtcNow,
                    LookupMethod: TbwLookupMethod.SerperOnly));
                var singleBorder = Assert.IsType<Border>(Assert.Single(window.TbwCandidateList.Children));
                var singleGrid = Assert.IsType<Grid>(singleBorder.Child);
                var singleButton = Assert.IsType<Button>(singleGrid.Children[1]);
                Assert.Equal("Apply single", singleButton.Content);
                singleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(config.Current.DiskTbwRatingsUpper.ContainsKey(knownSsd.DiskId));

                UpdateUserSettings(userSettings, settings => settings.WebSearchProvider = "google");
                window.LoadSettingsFields();
                window.Save_Click(window, new RoutedEventArgs());
                await window.StartTbwLookupAsync(knownSsd, force: true, userInitiated: true);
                Assert.Equal("Lookup unavailable", window.TbwLookupHeadline.Text);

                UpdateUserSettings(userSettings, settings => settings.WebSearchProvider = "serper");
                window.LoadSettingsFields();
                window.TxtSerperKey.Password = AiSecretsStore.Load().SerperApiKey ?? "";
                window.Save_Click(window, new RoutedEventArgs());
                await window.StartTbwLookupAsync(knownSsd, force: true, userInitiated: true);
                Assert.DoesNotContain("not configured", window.TbwLookupStatus.Text, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(Visibility.Visible, window.TbwLookupPanel.Visibility);
                Assert.Equal(Visibility.Collapsed, window.TbwLookupProgressBar.Visibility);
                window.ShowTbwLookupPanel(false);
                window.ShowTbwLookupPanel(true);
                Invoke(window, "TbwLookupClose_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.TbwLookupPanel.Visibility);
                Invoke(window, "TbwLookupAgain_Click", window, new RoutedEventArgs());
                await Task.Delay(50);
                Assert.Equal(Visibility.Visible, window.TbwLookupPanel.Visibility);

                Invoke(window, "LookupRatedTbw_Click", window, new RoutedEventArgs());
                var action = new Button { Tag = "not-download" };
                Invoke(window, "TbwLookupAction_Click", action, new RoutedEventArgs());
                window.TbwModelDownloader = (_, _) => Task.CompletedTask;
                Invoke(window, "TbwLookupAction_Click", new Button { Tag = "download" }, new RoutedEventArgs());
                await Task.Delay(50);
                window.TbwModelDownloader = (_, _) => Task.FromException(new OperationCanceledException());
                Invoke(window, "TbwLookupAction_Click", new Button { Tag = "download" }, new RoutedEventArgs());
                await Task.Delay(50);
                window.TbwModelDownloader = (_, _) => Task.FromException(new InvalidOperationException("download failed"));
                Invoke(window, "TbwLookupAction_Click", new Button { Tag = "download" }, new RoutedEventArgs());
                await Task.Delay(50);
                Assert.Contains("download failed", window.TbwLookupStatus.Text);

                bool foundryInstallRetried = false;
                window.FoundryLocalInstaller = (progress, _) =>
                {
                    progress?.Report("Installing test package...");
                    return Task.CompletedTask;
                };
                window.TbwPostInstallLookup = disk =>
                {
                    Assert.Same(knownSsd, disk);
                    foundryInstallRetried = true;
                    return Task.CompletedTask;
                };
                await window.InstallFoundryLocalAndRetryAsync(knownSsd);
                Assert.True(foundryInstallRetried);
                Assert.Equal("Foundry Local installed", window.TbwLookupHeadline.Text);

                window.FoundryLocalInstaller = (_, _) =>
                    Task.FromException(new InvalidOperationException("installer failed"));
                foundryInstallRetried = false;
                await window.InstallFoundryLocalAndRetryAsync(knownSsd);
                Assert.False(foundryInstallRetried);
                Assert.Equal("Foundry Local installation failed", window.TbwLookupHeadline.Text);
                Assert.Contains("installer failed", window.TbwLookupStatus.Text);
                Assert.Equal("Try install again", window.TbwLookupAction.Content);
                Assert.Equal(Visibility.Visible, window.TbwLookupAction.Visibility);

                Assert.True(MainWindow.ShouldPromptTbwOnlineSetup(new UserSettings(), new AiSecrets()));
                Assert.False(MainWindow.ShouldPromptTbwOnlineSetup(
                    new UserSettings { SuppressTbwOnlineSetupPrompt = true }, new AiSecrets()));
                Assert.False(MainWindow.ShouldPromptTbwOnlineSetup(
                    new UserSettings(), new AiSecrets { SerperApiKey = "configured" }));

                window.ShowTbwOnlineSetup();
                Assert.Equal(Visibility.Visible, window.TbwSetupOverlay.Visibility);
                Assert.Equal(Visibility.Visible, window.TbwSetupIntroPanel.Visibility);
                Invoke(window, "TbwSetupConfigure_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, window.TbwSetupKeyPanel.Visibility);
                window.TbwSetupSerperKey.Password = "synthetic-hidden-key";
                window.TbwSetupSerperKeyRevealButton.IsChecked = true;
                Assert.Equal(Visibility.Collapsed, window.TbwSetupSerperKey.Visibility);
                Assert.Equal(Visibility.Visible, window.TbwSetupSerperKeyReveal.Visibility);
                Assert.Equal("synthetic-hidden-key", window.TbwSetupSerperKeyReveal.Text);
                Assert.Equal("Hide API key", window.TbwSetupSerperKeyRevealButton.ToolTip);
                window.TbwSetupSerperKeyReveal.Text = "synthetic-edited-key";
                window.TbwSetupSerperKeyRevealButton.IsChecked = false;
                Assert.Equal(Visibility.Visible, window.TbwSetupSerperKey.Visibility);
                Assert.Equal(Visibility.Collapsed, window.TbwSetupSerperKeyReveal.Visibility);
                Assert.Empty(window.TbwSetupSerperKeyReveal.Text);
                Assert.Equal("synthetic-edited-key", window.TbwSetupSerperKey.Password);
                Assert.Equal("Show API key", window.TbwSetupSerperKeyRevealButton.ToolTip);
                Invoke(window, "TbwSetupBack_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, window.TbwSetupIntroPanel.Visibility);
                Invoke(window, "TbwSetupShowFromSettings_Click", window, new RoutedEventArgs());

                string? openedUrl = null;
                window.TbwSetupUrlLauncher = url => openedUrl = url;
                Invoke(window, "TbwSetupOpenSerper_Click", window, new RoutedEventArgs());
                Assert.Equal("https://serper.dev/signup", openedUrl);
                Assert.Equal(Visibility.Collapsed, window.TbwSetupErrorText.Visibility);
                window.TbwSetupUrlLauncher = _ => throw new InvalidOperationException("browser blocked");
                Invoke(window, "TbwSetupOpenSerper_Click", window, new RoutedEventArgs());
                Assert.Contains("browser blocked", window.TbwSetupErrorText.Text);

                Invoke(window, "TbwSetupConfigure_Click", window, new RoutedEventArgs());
                window.TbwSetupSerperKey.Password = "short";
                Invoke(window, "TbwSetupSave_Click", window, new RoutedEventArgs());
                Assert.Contains("Paste the API key", window.TbwSetupErrorText.Text);

                const string onboardingKey = "synthetic-serper-onboarding-test-key";
                object ssdChoice = ((IEnumerable)window.DiskSelector.ItemsSource).Cast<object>()
                    .First(choice => choice.GetType().GetProperty("Disk")!.GetValue(choice) is DiskInfo disk && disk.DiskId == "8");
                window.DiskSelector.SelectedItem = ssdChoice;
                window.TbwSetupSerperKey.Password = "stale-before-reveal";
                window.TbwSetupSerperKeyRevealButton.IsChecked = true;
                window.TbwSetupSerperKeyReveal.Text = onboardingKey;
                window.TbwSetupDontShowAgain.IsChecked = true;
                Invoke(window, "TbwSetupSave_Click", window, new RoutedEventArgs());
                await Task.Delay(50);
                Assert.Equal(Visibility.Collapsed, window.TbwSetupOverlay.Visibility);
                Assert.False(window.TbwSetupSerperKeyRevealButton.IsChecked);
                Assert.Empty(window.TbwSetupSerperKeyReveal.Text);
                Assert.Empty(window.TbwSetupSerperKey.Password);
                Assert.Equal("serper", userSettings.Current.WebSearchProvider);
                Assert.True(userSettings.Current.EnableTbwWebLookup);
                Assert.True(userSettings.Current.SuppressTbwOnlineSetupPrompt);
                string encryptedJson = File.ReadAllText(AiSecretsStore.FilePath);
                Assert.DoesNotContain(onboardingKey, encryptedJson);
                Assert.Equal(onboardingKey, AiSecretsStore.Load().SerperApiKey);

                window.ShowTbwOnlineSetup();
                Invoke(window, "TbwSetupConfigure_Click", window, new RoutedEventArgs());
                window.TbwSetupSerperKey.Password = onboardingKey;
                window.TbwSetupSecretsSaver = _ => throw new IOException("DPAPI unavailable");
                Invoke(window, "TbwSetupSave_Click", window, new RoutedEventArgs());
                await Task.Delay(50);
                Assert.Contains("DPAPI unavailable", window.TbwSetupErrorText.Text);
                window.TbwSetupSecretsSaver = AiSecretsStore.Save;

                UpdateUserSettings(userSettings, settings => settings.SuppressTbwOnlineSetupPrompt = false);
                window.ShowTbwOnlineSetup();
                Invoke(window, "TbwSetupNotNow_Click", window, new RoutedEventArgs());
                Assert.False(userSettings.Current.SuppressTbwOnlineSetupPrompt);
                window.ShowTbwOnlineSetup();
                window.TbwSetupDontShowAgain.IsChecked = true;
                Invoke(window, "TbwSetupNotNow_Click", window, new RoutedEventArgs());
                Assert.True(userSettings.Current.SuppressTbwOnlineSetupPrompt);

                UpdateUserSettings(userSettings, settings => settings.SuppressTbwOnlineSetupPrompt = false);
                try { File.Delete(AiSecretsStore.FilePath); } catch { }
                using var controller = new TrayController(repo, config, userSettings);
                bool presented = false;
                controller.TbwSetupPresenter = () => { presented = true; window.ShowTbwOnlineSetup(); };
                Assert.True(controller.PromptTbwOnlineSetupIfNeeded());
                Assert.True(presented);
                Assert.Equal(Visibility.Visible, window.TbwSetupOverlay.Visibility);
                UpdateUserSettings(userSettings, settings => settings.SuppressTbwOnlineSetupPrompt = true);
                Assert.False(controller.PromptTbwOnlineSetupIfNeeded());

                UpdateUserSettings(userSettings, settings => settings.SuppressTbwOnlineSetupPrompt = true);
                controller.StartupPromptsRunner = () => { };
                controller.Initialize();
                var trayMenu = Assert.IsType<DarkTrayContextMenu>(
                    typeof(TrayController).GetField("_trayMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(controller));
                Assert.IsType<DarkTrayMenuRenderer>(trayMenu.Renderer);
                Assert.Equal(System.Drawing.Color.FromArgb(20, 22, 25), trayMenu.BackColor);
                Assert.Equal(System.Drawing.Color.FromArgb(242, 244, 247), trayMenu.ForeColor);
                Assert.False(trayMenu.ShowImageMargin);
                Assert.False(trayMenu.ShowCheckMargin);
                Assert.Collection(trayMenu.Items.Cast<System.Windows.Forms.ToolStripItem>(),
                    item => Assert.Equal("Open dashboard", item.Text),
                    item => Assert.Equal("Open data folder", item.Text),
                    item => Assert.IsType<System.Windows.Forms.ToolStripSeparator>(item),
                    item => Assert.Equal("Exit", item.Text));
                _ = trayMenu.Handle;
                Assert.NotNull(trayMenu.Region);
                Assert.False(trayMenu.Region.IsVisible(0, 0));
                Assert.True(trayMenu.Region.IsVisible(trayMenu.Width / 2, trayMenu.Height / 2));

                // Item text is inset from both edges instead of hugging the menu border.
                Assert.Equal(System.Drawing.SystemFonts.MenuFont!.Name, trayMenu.Font.Name);
                var textRect = DarkTrayContextMenu.GetTextRectangle(200, 30);
                Assert.Equal(DarkTrayContextMenu.TextInsetLeft, textRect.X);
                Assert.Equal(200 - DarkTrayContextMenu.TextInsetLeft - DarkTrayContextMenu.TextInsetRight, textRect.Width);
                Assert.Equal(30, textRect.Height);
                Assert.Equal(0, DarkTrayContextMenu.GetTextRectangle(4, 30).Width);
                var command = Assert.IsType<System.Windows.Forms.ToolStripMenuItem>(trayMenu.Items[0]);
                Assert.Equal(DarkTrayContextMenu.TextInsetLeft, command.Padding.Left);
                Assert.Equal(DarkTrayContextMenu.TextInsetRight, command.Padding.Right);
                Assert.True(command.Width > DarkTrayContextMenu.TextInsetLeft + DarkTrayContextMenu.TextInsetRight);

                repo.AcknowledgeAlerts();
                Invoke(controller, "Update");

                try { File.Delete(AiSecretsStore.FilePath); } catch { }
                window.LoadSettingsFields();
                Assert.Empty(window.TxtSerperKey.Password);
            }
            finally { window.ForceClose(); }
        });
    }

    [Fact]
    public void PendingMainWindowRowsAndProgressStates_AreDeterministic()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            using var config = new ConfigStore(_cfg);
            var userSettings = new UserSettingsStore(_userSettings);
            var window = new MainWindow(repo, config, userSettings);
            try
            {
                object tailRow = CreateFileTargetRow(canTail: true);
                Assert.Equal("Live tail this file", Property<string>(tailRow, "TailToolTip"));
                Assert.Equal("Live tail trace.log", Property<string>(tailRow, "TailAutomationName"));
                Assert.Equal("Copy full path for trace.log", Property<string>(tailRow, "CopyAutomationName"));
                Assert.Equal("Find processes holding trace.log open", Property<string>(tailRow, "TraceAutomationName"));
                Assert.Equal("Delete trace.log", Property<string>(tailRow, "DeleteAutomationName"));

                object binaryRow = CreateFileTargetRow(canTail: false);
                Assert.Contains("cannot be tailed as text", Property<string>(binaryRow, "TailToolTip"));

                object owner = CreateHandleOwnerRow();
                Assert.Equal("writer", Property<string>(owner, "ProcessName"));
                Assert.Equal("42", Property<string>(owner, "PidText"));
                Assert.Equal("File  C:\\data\\trace.log", Property<string>(owner, "Detail"));

                int automaticChecks = 0;
                window.AutomaticUpdateCheckRequested = () => automaticChecks++;
                window.LoadSettingsFields();
                window.AppUpdateModeSelector.SelectedItem = window.AppUpdateModeSelector.Items
                    .Cast<ComboBoxItem>()
                    .Single(item => Equals(item.Tag, "Automatic"));
                window.Save_Click(window, new RoutedEventArgs());
                Assert.Equal(1, automaticChecks);

                const string model = "Test SSD 2TB";
                Assert.Contains(model, window.UpdateTbwLookupProgress(TbwLookupStage.Searching, false, model));
                Assert.Equal("Searching web evidence", window.TbwLookupHeadline.Text);

                Assert.Contains("capacity-matched", window.UpdateTbwLookupProgress(TbwLookupStage.Analyzing, true, model));
                Assert.Equal("Parsing explicit TBW evidence", window.TbwLookupHeadline.Text);

                Assert.Contains("on-device model", window.UpdateTbwLookupProgress(TbwLookupStage.Analyzing, false, model));
                Assert.Equal("Verifying with the local model", window.TbwLookupHeadline.Text);

                string unchanged = window.TbwLookupStatus.Text;
                Assert.Equal(unchanged, window.UpdateTbwLookupProgress(TbwLookupStage.Idle, false, model));

                window.DispatchTbwLookupProgress(new TbwLookupProgress(TbwLookupStage.Searching), false, model);
                Assert.Equal("Searching web evidence", window.TbwLookupHeadline.Text);

                await Task.Run(() => window.DispatchTbwLookupProgress(
                    new TbwLookupProgress(TbwLookupStage.Analyzing), true, model));
                await window.Dispatcher.InvokeAsync(() => { });
                Assert.Equal("Parsing explicit TBW evidence", window.TbwLookupHeadline.Text);
            }
            finally { window.ForceClose(); }
        });
    }

    [Theory]
    [InlineData(2, "2%")]
    [InlineData(2.5, "2.5%")]
    [InlineData(2.555, "2.56%")]
    public void FormatPercent_ShowsUpToTwoDecimalPlaces(double value, string expected)
        => Assert.Equal(expected, MainWindow.FormatPercent(value));

    [Fact]
    public void MarkdownToPlainText_RemovesPresentationSyntax()
    {
        string markdown = "## Heading\n\n> Note with `code` and ![alt](image.png).\n\n| A | B |\n|---|---|\n| 1 | 2 |";
        string text = MainWindow.MarkdownToPlainText(markdown);
        Assert.Contains("Heading", text);
        Assert.Contains("Note with code and alt", text);
        Assert.DoesNotContain("##", text);
        Assert.DoesNotContain("image.png", text);
        Assert.DoesNotContain("---", text);
    }

    [Theory]
    [InlineData("https://example.test/help", true)]
    [InlineData("http://127.0.0.1/help", true)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("custom-protocol:payload", false)]
    [InlineData("not a uri", false)]
    public void ExternalHelpUriPolicy_AllowsOnlyHttpAndHttps(string value, bool expected)
        => Assert.Equal(expected, MainWindow.IsAllowedExternalHelpUri(value));

    [Fact]
    public void DriveLifeUsed_UsesPreciseLifetimeWritesPercentage()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            repo.UpsertDisks([new DiskInfo
            {
                DiskId = "0", InstanceName = "0 C:", FriendlyName = "Precise SSD", Volumes = "C:",
                MediaType = DiskMediaType.Ssd, WearPercent = 2,
                LifetimeBytesWritten = 70_800_000_000_000,
            }]);
            var now = DateTime.UtcNow;
            var coverageEnd = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
            var monitoredMinute = coverageEnd.AddMinutes(-4);
            repo.AddDiskSamples([new DiskSample
            {
                TimestampUtc = monitoredMinute, DiskId = "0", WriteBytes = 1_000_000,
            }]);
            repo.AddCollectorHeartbeat(monitoredMinute);
            using var config = new ConfigStore(_cfg);
            config.Update(settings => settings.DefaultSsdTbw = 750);

            var window = new MainWindow(repo, config, new UserSettingsStore(_userSettings));
            try
            {
                Invoke(window, "UpdateEndurance", new DiskInfo
                {
                    DiskId = "no-history", InstanceName = "no-history D:", MediaType = DiskMediaType.Ssd,
                }, coverageEnd);
                Assert.Contains("Collecting data", window.WearSub.Text);
                Assert.Contains("collecting data", window.EnduranceProjSub.Text);

                Invoke(window, "UpdateEndurance", repo.GetDisks().Single(), coverageEnd);
                Assert.Contains("Projection withheld", window.WearSub.Text);
                Assert.Contains("withheld at", window.EnduranceProjSub.Text);
                Assert.Equal("~9.44%", window.SmartWearValue.Text);
                Assert.Contains("drive SMART reports 2% used", window.SmartWearText.Text);

                config.Update(settings => settings.DefaultSsdTbwUpper = 1_000);
                Invoke(window, "UpdateEndurance", repo.GetDisks().Single(), DateTime.UtcNow);
                Assert.Equal("~7.08% to 9.44%", window.SmartWearValue.Text);

                Invoke(window, "UpdateEndurance", new DiskInfo
                {
                    DiskId = "0", InstanceName = "0 C:", MediaType = DiskMediaType.Ssd, WearPercent = 2,
                }, DateTime.UtcNow);
                Assert.Equal("2%", window.SmartWearValue.Text);
                Assert.Contains("whole-percent precision", window.SmartWearText.Text);

                Invoke(window, "UpdateEndurance", new DiskInfo
                {
                    DiskId = "0", InstanceName = "0 C:", MediaType = DiskMediaType.Ssd,
                    LifetimeBytesWritten = 70_800_000_000_000,
                }, DateTime.UtcNow);
                Assert.Contains("no SMART wear attribute", window.SmartWearText.Text);
            }
            finally { window.ForceClose(); }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Settings_UnchangedUnknownTbw_RemainsEstimatedAndModesAreExplicit()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            repo.UpsertDisks([new DiskInfo
            {
                DiskId = "estimate", InstanceName = "estimate C:", FriendlyName = "Unknown SSD", Volumes = "C:",
                MediaType = DiskMediaType.Ssd,
            }]);
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_userSettings));
            try
            {
                Assert.Equal("150 to 600 TBW estimated", window.EnduranceRatedText.Text);
                Assert.True(window.RadTbwRange.IsChecked);
                Assert.Equal("Minimum TBW (TB)", window.TbwLowerLabel.Text);
                Assert.Equal(Visibility.Visible, window.TbwUpperPanel.Visibility);

                window.RadTbwSingle.IsChecked = true;
                Assert.Equal("TBW rating (TB)", window.TbwLowerLabel.Text);
                Assert.Equal(Visibility.Collapsed, window.TbwUpperPanel.Visibility);
                window.RadTbwRange.IsChecked = true;

                window.RadTbwLookupSerperOnly.IsChecked = true;
                Assert.False(window.TbwProviderSelector.IsEnabled);
                Assert.Contains("not installed or started", window.TbwLookupMethodHint.Text);
                window.RadTbwLookupFoundry.IsChecked = true;
                Assert.True(window.TbwProviderSelector.IsEnabled);

                window.Save_Click(window, new RoutedEventArgs());

                Assert.False(config.Current.DiskTbwRatings.ContainsKey("estimate"));
                Assert.False(config.Current.DiskTbwRatingsUpper.ContainsKey("estimate"));
                Assert.Equal("150 to 600 TBW estimated", window.EnduranceRatedText.Text);
            }
            finally { window.ForceClose(); }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public void RatedTbwPill_EditorSavesRangeAndSingleAndRefreshesCalculations()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            repo.UpsertDisks([new DiskInfo
            {
                DiskId = "editable", InstanceName = "editable C:", FriendlyName = "Editable SSD", Volumes = "C:",
                MediaType = DiskMediaType.Ssd, LifetimeBytesWritten = 60_000_000_000_000,
            }]);
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_userSettings));
            try
            {
                Assert.Equal("150 to 600 TBW estimated", window.EnduranceRatedText.Text);

                window.EnduranceRatedBadge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Visible, window.TbwEditOverlay.Visibility);
                Assert.True(window.TbwEditRange.IsChecked);
                Assert.Contains("default estimate", window.TbwEditCurrentText.Text);

                window.TbwEditLowerText.Text = "600";
                window.TbwEditUpperText.Text = "1200";
                Assert.Contains("600 to 1200 TBW", window.TbwEditPreviewText.Text);
                window.TbwEditSaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(Visibility.Collapsed, window.TbwEditOverlay.Visibility);
                Assert.Equal(600, config.Current.DiskTbwRatings["editable"]);
                Assert.Equal(1200, config.Current.DiskTbwRatingsUpper["editable"]);
                Assert.Equal("600 to 1200 TBW rated", window.EnduranceRatedText.Text);
                Assert.Equal("~5% to 10%", window.SmartWearValue.Text);
                Assert.Equal("600", window.TxtTbw.Text);
                Assert.Equal("1200", window.TxtTbwUpper.Text);

                window.EnduranceRatedBadge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("saved for this drive", window.TbwEditCurrentText.Text);
                window.TbwEditSingle.IsChecked = true;
                Assert.Equal(Visibility.Collapsed, window.TbwEditUpperPanel.Visibility);
                window.TbwEditLowerText.Text = "800";
                window.TbwEditSaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(800, config.Current.DiskTbwRatings["editable"]);
                Assert.False(config.Current.DiskTbwRatingsUpper.ContainsKey("editable"));
                Assert.Equal("800 TBW rated", window.EnduranceRatedText.Text);
                Assert.Equal("~7.5%", window.SmartWearValue.Text);
                Assert.True(window.RadTbwSingle.IsChecked);

                window.EnduranceRatedBadge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.TbwEditRange.IsChecked = true;
                window.TbwEditLowerText.Text = "900";
                window.TbwEditUpperText.Text = "800";
                window.TbwEditSaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Visible, window.TbwEditOverlay.Visibility);
                Assert.Contains("greater than minimum", window.TbwEditErrorText.Text);
                Assert.Equal(800, config.Current.DiskTbwRatings["editable"]);
                Invoke(window, "TbwEditClose_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.TbwEditOverlay.Visibility);
            }
            finally { window.ForceClose(); }

            return Task.CompletedTask;
        });
    }

    private static AlertRecord Alert(DateTime time, AlertSeverity severity, string rule, double value) => new()
    {
        TimestampUtc = time, Severity = severity, RuleKey = rule, Title = rule, Message = "message", Value = value, Threshold = 1,
    };

    private static DiskInfo Disk(string id = "2") => new()
    {
        DiskId = id, InstanceName = $"{id} F:", FriendlyName = "Test HDD", Volumes = "F:", MediaType = DiskMediaType.Hdd,
    };

    private static SmartHealthScanResult Result(SmartScanGrade grade, int controllerErrors, int? temperature,
        bool telemetry, int? maximum = null) => new()
    {
        DiskId = "2", DevicePath = @"\\.\PhysicalDrive2", DisplayName = "F:", Model = "Model", SerialNumber = "Serial",
        FirmwareVersion = "FW", BusType = "USB", WindowsHealth = grade == SmartScanGrade.Critical ? "Unhealthy" : "Healthy",
        OperationalStatus = "OK", DeviceStatus = "OK", SmartAccess = telemetry ? "Direct" : "Limited", ScannedUtc = DateTime.UtcNow,
        DiskPresent = grade != SmartScanGrade.Unavailable, SmartTelemetryAvailable = telemetry, ControllerErrorCount = controllerErrors,
        TemperatureC = temperature, TemperatureMaxC = maximum, LifetimeBytesWritten = 1, LifetimeBytesRead = 2,
        Grade = grade, Headline = grade.ToString(), Summary = "summary", Findings = ["finding"],
    };

    private static void Invoke(object target, string method, params object[] args)
        => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static void UpdateUserSettings(UserSettingsStore store, Action<UserSettings> update)
    {
        var settings = store.Current;
        update(settings);
        store.Save(settings);
    }

    [Fact]
    public void AlertHistory_RendersEmptyAndSingularStates()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            repo.UpsertDisks([Disk()]);
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_userSettings));
            try
            {
                window.UpdateAlertHistory();
                Assert.Equal(Visibility.Visible, window.AlertHistoryEmpty.Visibility);
                Assert.Equal("0 alerts  ·  0 dismissed", window.AlertHistorySummary.Text);

                repo.InsertAlert(Alert(DateTime.UtcNow, AlertSeverity.Info, "single", 1));
                window.UpdateAlertHistory();
                Assert.Equal(Visibility.Collapsed, window.AlertHistoryEmpty.Visibility);
                Assert.Equal("1 alert  ·  0 dismissed", window.AlertHistorySummary.Text);
            }
            finally { window.ForceClose(); }
            return Task.CompletedTask;
        });
    }

    private static object CreateAlertRow(string diskId, bool canScan, long[]? alertIds = null)
    {
        Type type = typeof(MainWindow).GetNestedType("AlertRow", BindingFlags.NonPublic)!;
        return Activator.CreateInstance(type, "title", "message", "time", Brushes.Red,
            $"disk-controller:{diskId}", diskId, 2, canScan,
            canScan ? Visibility.Visible : Visibility.Collapsed,
            Visibility.Collapsed,
            alertIds ?? new long[] { 1 })!;
    }

    private static object CreateProcessRow(string processName)
    {
        Type type = typeof(MainWindow).GetNestedType("ProcessRow", BindingFlags.NonPublic)!;
        return Activator.CreateInstance(type, processName, "1 GB", "0 B", 100d)!;
    }

    private static object CreateFileTargetRow(bool canTail)
    {
        Type type = typeof(MainWindow).GetNestedType("FileTargetRow", BindingFlags.NonPublic)!;
        object row = Activator.CreateInstance(type,
            @"C:\data\trace.log", "trace.log", "Log", "1 KB", 100d, "Text log")!;
        type.GetProperty("CanTail")!.SetValue(row, canTail);
        return row;
    }

    private static object CreateHandleOwnerRow()
    {
        Type type = typeof(MainWindow).GetNestedType("HandleOwnerRow", BindingFlags.NonPublic)!;
        return Activator.CreateInstance(type, "writer", "42", @"File  C:\data\trace.log")!;
    }

    private static T Property<T>(object target, string name)
        => (T)target.GetType().GetProperty(name)!.GetValue(target)!;

    private static T InvokePrivateStatic<T>(string name, params object[] args)
        => (T)typeof(MainWindow).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, args)!;

    private static void AssertEnduranceAlertValidationBranches()
    {
        Assert.False(MainWindow.TryParseEnduranceAlert(
            true, "bad", EnduranceAlertTimeUnit.Years, false, "20", out _, out _));
        Assert.False(MainWindow.TryParseEnduranceAlert(
            true, "NaN", EnduranceAlertTimeUnit.Years, false, "20", out _, out _));
        Assert.False(MainWindow.TryParseEnduranceAlert(
            true, "0", EnduranceAlertTimeUnit.Years, false, "20", out _, out _));
        Assert.False(MainWindow.TryParseEnduranceAlert(
            false, "-1", EnduranceAlertTimeUnit.Years, true, "bad", out _, out _));
        Assert.False(MainWindow.TryParseEnduranceAlert(
            false, "-1", EnduranceAlertTimeUnit.Years, true, "NaN", out _, out _));
        Assert.False(MainWindow.TryParseEnduranceAlert(
            false, "-1", EnduranceAlertTimeUnit.Years, true, "-1", out _, out _));
        Assert.False(MainWindow.TryParseEnduranceAlert(
            false, "-1", EnduranceAlertTimeUnit.Years, true, "101", out _, out _));
        Assert.True(MainWindow.TryParseEnduranceAlert(
            false, "-1", EnduranceAlertTimeUnit.Years, true, "10", out EnduranceAlertThreshold percentOnly, out _));
        Assert.Equal(0, percentOnly.RemainingLifeValue);
        Assert.True(MainWindow.TryParseEnduranceAlert(
            true, "2", EnduranceAlertTimeUnit.Months, false, "-1", out EnduranceAlertThreshold lifeOnly, out _));
        Assert.Equal(0, lifeOnly.RemainingPercent);

        Assert.False(MainWindow.TryParseChartColor(null, out _));
        Assert.False(MainWindow.TryParseChartColor("#123", out _));
        Assert.False(MainWindow.TryParseChartColor("#GG0000", out _));
        Assert.False(MainWindow.TryParseChartColor("#00GG00", out _));
        Assert.False(MainWindow.TryParseChartColor("#0000GG", out _));
        Assert.True(MainWindow.TryParseChartColor("  #abcdef ", out Color parsed));
        Assert.Equal("#ABCDEF", MainWindow.FormatChartColor(parsed));
    }

    private sealed class FakePresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    [Fact]
    public void SettingsHeader_StaysPinnedOutsideTheScrollingContent()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            repo.UpsertDisks([Disk()]);
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_userSettings));
            try
            {
                Assert.Equal(Visibility.Collapsed, window.SettingsHeader.Visibility);

                Invoke(window, "Gear_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, window.SettingsPanel.Visibility);
                Assert.Equal(Visibility.Visible, window.SettingsHeader.Visibility);

                // Scrolling the settings content must never move the back button out of view.
                Assert.DoesNotContain(Ancestors(window.SettingsHeader), element => ReferenceEquals(element, window.BodyScroller));
                Assert.Contains(Ancestors(window.SettingsPanel), element => ReferenceEquals(element, window.BodyScroller));

                Invoke(window, "Gear_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.SettingsHeader.Visibility);
                Assert.Equal(Visibility.Visible, window.DashboardPanel.Visibility);
            }
            finally { window.ForceClose(); }
            return Task.CompletedTask;
        });
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for (var parent = System.Windows.Media.VisualTreeHelper.GetParent(element)
                 ?? System.Windows.LogicalTreeHelper.GetParent(element);
             parent is not null;
             parent = System.Windows.Media.VisualTreeHelper.GetParent(parent)
                 ?? System.Windows.LogicalTreeHelper.GetParent(parent))
        {
            yield return parent;
        }
    }

    [Fact]
    public void FileTargets_ExplainAnOpaqueWriterFromTheProcessCard()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            repo.UpsertDisks([Disk()]);
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_userSettings));
            try
            {
                window.ShowFileTargets("System");
                Assert.Equal(Visibility.Visible, window.FileTargetsOverlay.Visibility);
                Assert.Equal("Files written by System", window.FileTargetsTitle.Text);
                Assert.Equal(Visibility.Visible, window.FileTargetsNoteBorder.Visibility);
                Assert.Contains("kernel", window.FileTargetsNote.Text, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(Visibility.Visible, window.FileTargetsEmpty.Visibility);

                var utcNow = DateTime.UtcNow;
                var minute = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, utcNow.Minute, 0, DateTimeKind.Utc)
                    .AddMinutes(-2);
                repo.AddProcessSamples([new ProcessIoSample { TimestampUtc = minute, ProcessName = "System", WriteBytes = 4000 }]);
                repo.AddProcessFileSamples(
                [
                    new ProcessFileIoSample
                    {
                        TimestampUtc = minute, ProcessName = "System", Path = @"C:\$Mft",
                        Kind = FileTargetKind.NtfsMetadata, WriteBytes = 3000,
                    },
                    new ProcessFileIoSample
                    {
                        TimestampUtc = minute, ProcessName = "System", Path = @"C:\pagefile.sys",
                        Kind = FileTargetKind.PagingFile, WriteBytes = 1000,
                    },
                ]);

                window.ShowFileTargets("System");
                var rows = ((IEnumerable)window.FileTargetsList.ItemsSource).Cast<object>().ToList();
                Assert.Equal(2, rows.Count);
                Assert.Equal("$Mft", rows[0].GetType().GetProperty("FileName")!.GetValue(rows[0]));
                Assert.Equal("NTFS metadata", rows[0].GetType().GetProperty("KindLabel")!.GetValue(rows[0]));
                Assert.Equal(Visibility.Collapsed, window.FileTargetsEmpty.Visibility);
                Assert.Contains("100%", window.FileTargetsFooter.Text);

                // An ordinary application needs no kernel explanation.
                window.ShowFileTargets("chrome");
                Assert.Equal(Visibility.Collapsed, window.FileTargetsNoteBorder.Visibility);

                window.FileTargetsClose_Click(window, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.FileTargetsOverlay.Visibility);
            }
            finally { window.ForceClose(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void FileTargets_DescribeCoverageAndDisabledTracking()
    {
        Assert.Contains("turned off", MainWindow.FileTargetsEmptyText(trackingEnabled: false));
        Assert.Contains("ETW collector", MainWindow.FileTargetsEmptyText(trackingEnabled: true));
        Assert.Equal("Per-file history is kept for 7 day(s).", MainWindow.FileTargetsCoverage(0, 0, 7));
        Assert.Contains("50%", MainWindow.FileTargetsCoverage(1000, 500, 7));
        Assert.Contains("100%", MainWindow.FileTargetsCoverage(1000, 4000, 7));
    }

    [Fact]
    public void FileTargets_EnterClose_DoesNotReopenUntilPointerLeavesBar()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_userSettings));
            try
            {
                object processRow = CreateProcessRow("System");
                var bar = new Border { DataContext = processRow };
                var mouse = new System.Windows.Input.MouseEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount)
                { RoutedEvent = UIElement.MouseEnterEvent };

                Invoke(window, "ProcessBar_MouseEnter", bar, mouse);
                Assert.Equal(Visibility.Visible, window.FileTargetsOverlay.Visibility);

                var visibleLeave = new System.Windows.Input.MouseEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount)
                { RoutedEvent = UIElement.MouseLeaveEvent };
                Invoke(window, "ProcessBar_MouseLeave", bar, visibleLeave);
                Assert.False(window.IsFileTargetsHoverSuppressed(bar));

                var enter = new System.Windows.Input.KeyEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice,
                    new FakePresentationSource(),
                    Environment.TickCount,
                    System.Windows.Input.Key.Enter)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                Invoke(window, "FileTargetsOverlay_PreviewKeyDown", window.FileTargetsOverlay, enter);
                Assert.True(enter.Handled);
                Assert.Equal(Visibility.Collapsed, window.FileTargetsOverlay.Visibility);
                Assert.True(window.IsFileTargetsHoverSuppressed(bar));

                Invoke(window, "ProcessBar_MouseEnter", bar, mouse);
                Assert.Equal(Visibility.Collapsed, window.FileTargetsOverlay.Visibility);

                var unrelated = new Border { DataContext = processRow };
                Invoke(window, "ProcessBar_MouseLeave", unrelated, visibleLeave);
                Assert.True(window.IsFileTargetsHoverSuppressed(bar));

                var leave = new System.Windows.Input.MouseEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount)
                { RoutedEvent = UIElement.MouseLeaveEvent };
                Invoke(window, "ProcessBar_MouseLeave", bar, leave);
                Assert.False(window.IsFileTargetsHoverSuppressed(bar));

                Invoke(window, "ProcessBar_MouseEnter", bar, mouse);
                Assert.Equal(Visibility.Visible, window.FileTargetsOverlay.Visibility);

                var escape = new System.Windows.Input.KeyEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice,
                    new FakePresentationSource(),
                    Environment.TickCount,
                    System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                Invoke(window, "FileTargetsOverlay_PreviewKeyDown", window.FileTargetsOverlay, escape);
                Assert.True(escape.Handled);

                window.ShowFileTargets("System");
                var otherKey = new System.Windows.Input.KeyEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice,
                    new FakePresentationSource(),
                    Environment.TickCount,
                    System.Windows.Input.Key.Space)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                Invoke(window, "FileTargetsOverlay_PreviewKeyDown", window.FileTargetsOverlay, otherKey);
                Assert.False(otherKey.Handled);
                Assert.Equal(Visibility.Visible, window.FileTargetsOverlay.Visibility);

                var rowGrid = new Grid { DataContext = processRow };
                var click = new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount,
                    System.Windows.Input.MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
                Invoke(window, "ProcessRow_Click", rowGrid, click);
                Assert.Equal(Visibility.Visible, window.FileTargetsOverlay.Visibility);
                Invoke(window, "ProcessRow_Click", new Grid(), click);
                Invoke(window, "ProcessRow_Click", new object(), click);
                Invoke(window, "ProcessBar_MouseLeave", new object(), leave);
            }
            finally { window.ForceClose(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void MonitoringCoverage_HighCoverage_RendersProjectionAndAllThroughputRanges()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            var now = DateTime.UtcNow;
            var end = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
            var disk = new DiskInfo
            {
                DiskId = "coverage", InstanceName = "coverage C:", FriendlyName = "Coverage SSD", Volumes = "C:",
                MediaType = DiskMediaType.Ssd, LifetimeBytesWritten = 10_000_000_000,
                LifetimeBytesRead = 20_000_000_000,
            };
            repo.UpsertDisks([disk]);
            repo.AddDiskSamples([
                new DiskSample { TimestampUtc = end.AddDays(-8), DiskId = disk.DiskId, WriteBytes = 1 },
                new DiskSample { TimestampUtc = end.AddMinutes(-1), DiskId = disk.DiskId, WriteBytes = 7_000_000_000_000 },
            ]);
            for (int minute = 7 * 24 * 60; minute >= 1; minute--)
                repo.AddCollectorHeartbeat(end.AddMinutes(-minute));

            using var config = new ConfigStore(_cfg);
            config.Update(value =>
            {
                value.DiskTbwRatings[disk.DiskId] = 150;
                value.DiskTbwRatingsUpper[disk.DiskId] = 600;
                value.HighCoveragePercent = 90;
            });
            var window = new MainWindow(repo, config, new UserSettingsStore(_userSettings));
            try
            {
                Invoke(window, "UpdateEndurance", disk, end);
                Assert.NotEqual("-", window.EnduranceProjValue.Text);
                Assert.Contains("reaches 150 to 600 TBW", window.EnduranceProjSub.Text);
                Assert.Contains("18.63 GB read", window.SmartWearLifeText.Text);
                Assert.Contains("Recent monitoring coverage: 100%", window.EnduranceConsumedText.Text);
                Assert.NotEqual("-", window.EnduranceAvgHour.Text);

                foreach (System.Windows.Controls.Primitives.ToggleButton button in new[] { window.TpBtn1h, window.TpBtn24h, window.TpBtn7d, window.TpBtn30d })
                    Invoke(window, "ThroughputRange_Click", button, new RoutedEventArgs());
                Assert.Contains("Monitoring coverage:", window.ThroughputCoverageText.Text);

                window.DiskSelector.ItemsSource = Array.Empty<object>();
                window.DiskSelector.SelectedItem = null;
                Invoke(window, "ThroughputRange_Click", window.TpBtn1h, new RoutedEventArgs());
            }
            finally { window.ForceClose(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void LiveDiskActivity_ProjectsRatesDownsamplesAndRendersCurrentValues()
    {
        var now = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);
        var projected = MainWindow.BuildLiveDiskPoints(
        [
            new LiveDiskSample { TimestampUtc = now.AddSeconds(-10), DiskId = "0", ElapsedMilliseconds = 5000, ReadBytes = 5_000_000, WriteBytes = 10_000_000 },
            new LiveDiskSample { TimestampUtc = now.AddSeconds(-5), DiskId = "0", ElapsedMilliseconds = 5000, ReadBytes = 15_000_000, WriteBytes = 20_000_000 },
            new LiveDiskSample { TimestampUtc = now, DiskId = "0", ElapsedMilliseconds = 5000, ReadBytes = 25_000_000, WriteBytes = 30_000_000 },
        ], maxPoints: 2);

        Assert.Equal(2, projected.Count);
        Assert.Equal(2, projected[0].ReadMbps);
        Assert.Equal(3, projected[0].WriteMbps);
        Assert.Equal(5, projected[1].ReadMbps);
        Assert.Equal(6, projected[1].WriteMbps);
        Assert.Empty(MainWindow.BuildLiveDiskPoints([], 120));
        Assert.Empty(MainWindow.BuildLiveDiskPoints(
            [new LiveDiskSample { TimestampUtc = now, DiskId = "0", ElapsedMilliseconds = 5000 }], 0));

        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            var disk = Disk();
            repo.UpsertDisks([disk]);
            repo.AddLiveDiskSamples(
            [
                new LiveDiskSample
                {
                    TimestampUtc = DateTime.UtcNow,
                    DiskId = disk.DiskId,
                    ElapsedMilliseconds = 5000,
                    ReadBytes = 10_000_000,
                    WriteBytes = 20_000_000,
                },
            ], DateTime.UtcNow.AddMinutes(-30));
            using var config = new ConfigStore(_cfg);
            config.Update(value => value.LiveGraphRetentionMinutes = 30);
            var window = new MainWindow(repo, config, new UserSettingsStore(_userSettings));
            try
            {
                Assert.Contains("last 30 min", window.LiveDiskCaption.Text);
                Assert.Contains("Read 2 MB/s", window.LiveDiskCurrent.Text);
                Assert.Contains("Write 4 MB/s", window.LiveDiskCurrent.Text);
                Invoke(window, "RefreshLiveDiskActivity");

                repo.AddLiveDiskSamples([], DateTime.UtcNow);
                window.DiskSelector.ItemsSource = Array.Empty<object>();
                window.DiskSelector.SelectedItem = null;
                Invoke(window, "RefreshLiveDiskActivity");
            }
            finally { window.ForceClose(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void SuspendedProcesses_RenderAndResumeFromTheDashboard()    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            repo.UpsertDisks([Disk()]);
            using var config = new ConfigStore(_cfg);
            var userSettings = new UserSettingsStore(_userSettings);
            var window = new MainWindow(repo, config, userSettings);
            try
            {
                Invoke(window, "RefreshSuspended");
                Assert.Equal(Visibility.Visible, window.SuspendedProcessEmpty.Visibility);
                Assert.Equal(Visibility.Collapsed, window.ResumeAllSuspendedButton.Visibility);

                var now = DateTime.UtcNow;
                var identities = new[] { new ProcessControl.ProcessIdentity(4242, 1, @"C:\Apps\gone.exe") };
                repo.AddSuspendedProcess("auto", now, null, identities, now.AddMinutes(30), SuspendSource.AutoRule);
                repo.AddSuspendedProcess("manual", now, null, identities, null, SuspendSource.Manual);

                Invoke(window, "RefreshSuspended");
                Assert.Equal(Visibility.Collapsed, window.SuspendedProcessEmpty.Visibility);
                Assert.Equal(Visibility.Visible, window.ResumeAllSuspendedButton.Visibility);
                var rows = ((IEnumerable)window.SuspendedProcessList.ItemsSource).Cast<object>().ToList();
                Assert.Equal(2, rows.Count);
                Assert.Contains(rows, r => (string)r.GetType().GetProperty("SourceLabel")!.GetValue(r)! == "Auto-suspend rule");
                Assert.Contains(rows, r => (string)r.GetType().GetProperty("SourceLabel")!.GetValue(r)! == "Suspended by you");
                Assert.Contains(rows, r => (string)r.GetType().GetProperty("ResumeAutomationName")!.GetValue(r)! == "Resume manual");

                // The tracked identities no longer exist, so resuming clears the stale records.
                Invoke(window, "ResumeAllSuspended_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, window.SuspendedProcessStatus.Visibility);
                Assert.Contains("Could not resume", window.SuspendedProcessStatus.Text);
                Assert.Empty(((IEnumerable)window.SuspendedProcessList.ItemsSource).Cast<object>());
                Assert.Equal(Visibility.Visible, window.SuspendedProcessEmpty.Visibility);

                repo.AddSuspendedProcess("manual", now, null, identities, null, SuspendSource.Manual);
                Invoke(window, "RefreshSuspended");
                Invoke(window, "ResumeSuspendedProcess_Click",
                    new Button { DataContext = ((IEnumerable)window.SuspendedProcessList.ItemsSource).Cast<object>().First() },
                    new RoutedEventArgs());
                Assert.Contains("no longer running", window.SuspendedProcessStatus.Text);
                Assert.Empty(repo.GetSuspendedProcessNames());
            }
            finally { window.ForceClose(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void SuspendSettings_PersistTheDefaultInterval()
    {
        RunStaAsync(() =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db); repo.EnsureSchema();
            repo.UpsertDisks([Disk()]);
            using var config = new ConfigStore(_cfg);
            var userSettings = new UserSettingsStore(_userSettings);
            var window = new MainWindow(repo, config, userSettings);
            try
            {
                Invoke(window, "LoadSuspendRules");
                Assert.Equal("30", window.TxtSuspendMinutes.Text);

                window.TxtSuspendMinutes.Text = "15";
                Invoke(window, "SaveRules_Click", window, new RoutedEventArgs());
                Assert.Equal(15, userSettings.Current.DefaultSuspendMinutes);
                Assert.Equal("15", window.TxtSuspendMinutes.Text);
            }
            finally { window.ForceClose(); }
            return Task.CompletedTask;
        });
    }

    private static async Task ExerciseStaleScans(MainWindow window)
    {
        using var successGate = new ManualResetEventSlim();
        var successStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.SmartScanner = (_, _, _) => { successStarted.TrySetResult(); successGate.Wait(); return Result(SmartScanGrade.Healthy, 0, 40, true); };
        Task staleSuccess = window.StartSmartScanAsync(Disk(), 0);
        await successStarted.Task;
        window.SmartScanClose_Click(window, new RoutedEventArgs());
        successGate.Set();
        await staleSuccess;

        using var failureGate = new ManualResetEventSlim();
        var failureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.SmartScanner = (_, _, _) => { failureStarted.TrySetResult(); failureGate.Wait(); throw new InvalidOperationException("stale"); };
        Task staleFailure = window.StartSmartScanAsync(Disk(), 0);
        await failureStarted.Task;
        window.SmartScanClose_Click(window, new RoutedEventArgs());
        failureGate.Set();
        await staleFailure;
    }

    private static void EnsureApplication()
    {
        if (Application.Current is null)
        {
            var app = new DiskActivityMonitor.Tray.App();
            app.InitializeComponent();
        }
        var current = Application.Current ?? throw new InvalidOperationException("WPF application was not initialized.");
        current.Resources["TextPrimary"] = new SolidColorBrush(Colors.White);
        current.Resources["Caption"] = new Style(typeof(TextBlock));
        current.Resources["ToolButton"] = new Style(typeof(Button));
    }

    private static void RunStaAsync(Func<Task> action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                Task task = action();
                var frame = new System.Windows.Threading.DispatcherFrame();
                task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (error is not null) throw new TargetInvocationException(error);
    }
}
