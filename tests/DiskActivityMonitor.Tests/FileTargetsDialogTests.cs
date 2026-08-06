using System.Collections;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Files;
using DiskActivityMonitor.Core.Models;
using DiskActivityMonitor.Core.Tools;
using DiskActivityMonitor.Tray;

namespace DiskActivityMonitor.Tests;

/// <summary>
/// Drives the real per-file drill-down against a seeded repository and asserts the rendered WPF
/// state: analytics charts, row actions, and the binary-extension gate on live tailing.
/// </summary>
[Collection("WPF")]
public sealed class FileTargetsDialogTests : IDisposable
{
    private static readonly Lazy<StaHarness> Sta = new(() => new StaHarness());
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dam_dlg_{Guid.NewGuid():N}.db");
    private readonly string _cfg = Path.Combine(Path.GetTempPath(), $"dam_dlg_{Guid.NewGuid():N}.json");
    private readonly string _user = Path.Combine(Path.GetTempPath(), $"dam_dlg_user_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_db) + "*"))
            try { File.Delete(file); } catch { }
        try { File.Delete(_cfg); } catch { }
        try { File.Delete(_user); } catch { }
    }

    [Fact]
    public void Drilldown_ShrinksAnalyticsInsideNarrowViewport()
    {
        RunSta(() =>
        {
            EnsureApplication();

            var repo = new MonitorRepository(_db);
            repo.EnsureSchema();
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_user));
            try
            {
                const double viewportWidth = 900;
                const double viewportHeight = 700;
                window.FileTargetsOverlay.Visibility = Visibility.Visible;

                var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                content.Measure(new Size(viewportWidth, viewportHeight));
                content.Arrange(new Rect(0, 0, viewportWidth, viewportHeight));
                content.UpdateLayout();

                Point dialogRight = window.FileTargetsDialogBorder.TranslatePoint(
                    new Point(window.FileTargetsDialogBorder.ActualWidth, 0),
                    content);
                Point timelineRight = window.FileTargetsTimeline.TranslatePoint(
                    new Point(window.FileTargetsTimeline.ActualWidth, 0),
                    content);

                Assert.InRange(window.FileTargetsDialogBorder.ActualWidth, 1, viewportWidth - 56);
                Assert.InRange(dialogRight.X, 1, viewportWidth);
                Assert.True(window.FileTargetsTimeline.ActualWidth > 0);
                Assert.InRange(timelineRight.X, 1, viewportWidth);
            }
            finally
            {
                CleanupWindow(window);
            }
        });
    }

    [Fact]
    public void Drilldown_PopulatesAnalyticsAndRowActionsFromRealData()
    {
        RunSta(() =>
        {
            EnsureApplication();

            var repo = new MonitorRepository(_db);
            repo.EnsureSchema();
            repo.UpsertDisks([new DiskInfo
            {
                DiskId = "0", InstanceName = "0 C:", FriendlyName = "Test SSD", Volumes = "C:", MediaType = DiskMediaType.Ssd,
            }]);

            var end = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day,
                DateTime.UtcNow.Hour, DateTime.UtcNow.Minute, 0, DateTimeKind.Utc);
            SeedActivity(repo, end);

            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_user));
            try
            {
                window.ShowFileTargets("System");

                Assert.Equal(Visibility.Visible, window.FileTargetsOverlay.Visibility);
                Assert.Equal("Files written by System", window.FileTargetsTitle.Text);

                // Analytics band is on by default and every chart has content.
                Assert.Equal(Visibility.Visible, window.FileTargetsAnalytics.Visibility);
                Assert.Contains("System accounts for", window.FileTargetsPieCaption.Text);
                Assert.Contains("recorded minute", window.FileTargetsTimelineCaption.Text);

                var types = ((IEnumerable)window.FileTargetsTypesList.ItemsSource!).Cast<object>().ToList();
                Assert.NotEmpty(types);
                Assert.Equal(Visibility.Collapsed, window.FileTargetsTypesEmpty.Visibility);

                // The file rows expose the tail gate: .log is text, .vhdx is on the binary list.
                var rows = ((IEnumerable)window.FileTargetsList.ItemsSource!).Cast<object>().ToList();
                Assert.Equal(2, rows.Count);
                Assert.True(CanTail(rows.Single(r => Name(r) == "app.log")));
                Assert.False(CanTail(rows.Single(r => Name(r) == "disk.vhdx")));

                // Retention wording reflects the configured value, not a hard-coded 7 days.
                Assert.Contains($"kept for {config.Current.FileTargetRetentionDays} day", window.FileTargetsFooter.Text);

                // The analytics toggle actually collapses the band.
                window.BtnFileTargetsAnalytics.IsChecked = false;
                Invoke(window, "FileTargetsAnalyticsToggle_Click", window.BtnFileTargetsAnalytics, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.FileTargetsAnalytics.Visibility);

                RenderSnapshotIfRequested(window);
            }
            finally
            {
                CleanupWindow(window);
            }
        });
    }

    [Fact]
    public void Drilldown_ExplainsAnEmptyWindowWithoutCrashing()
    {
        RunSta(() =>
        {
            EnsureApplication();

            var repo = new MonitorRepository(_db);
            repo.EnsureSchema();
            repo.UpsertDisks([new DiskInfo
            {
                DiskId = "0", InstanceName = "0 C:", FriendlyName = "Test SSD", Volumes = "C:", MediaType = DiskMediaType.Ssd,
            }]);

            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_user));
            try
            {
                window.ShowFileTargets("ghost.exe");

                Assert.Equal(Visibility.Visible, window.FileTargetsEmpty.Visibility);
                Assert.Contains("No write activity", window.FileTargetsPieCaption.Text);
                Assert.Contains("No per-minute samples", window.FileTargetsTimelineCaption.Text);
                Assert.Equal(Visibility.Visible, window.FileTargetsTypesEmpty.Visibility);
            }
            finally
            {
                CleanupWindow(window);
            }
        });
    }

    [Fact]
    public void Drilldown_AggregatesOverflowTypesAndKeepsAnUnrankedFocusVisible()
    {
        RunSta(() =>
        {
            EnsureApplication();
            var repo = new MonitorRepository(_db);
            repo.EnsureSchema();
            var end = FloorToMinute(DateTime.UtcNow);
            var minute = end.AddMinutes(-1);

            repo.AddProcessSamples(Enumerable.Range(0, 9).Select(index => new ProcessIoSample
            {
                TimestampUtc = minute,
                ProcessName = index == 8 ? "focus.exe" : $"writer{index}.exe",
                WriteBytes = index == 8 ? 10 : 10_000 - index,
            }).ToList());
            repo.AddProcessFileSamples(Enumerable.Range(0, 10).Select(index => new ProcessFileIoSample
            {
                TimestampUtc = minute,
                ProcessName = "focus.exe",
                Path = $@"C:\data\item{index}.ext{index}",
                Kind = FileTargetKind.Other,
                WriteBytes = 1000 - index,
            }).ToList());

            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_user));
            try
            {
                window.ShowFileTargets("focus.exe");

                Assert.Contains("focus.exe accounts for", window.FileTargetsPieCaption.Text);
                var groups = ((IEnumerable)window.FileTargetsTypesList.ItemsSource!).Cast<object>().ToList();
                Assert.Equal(9, groups.Count);
                Assert.Equal("Other", Property<string>(groups[^1], "Label"));

                window.BtnFileTargetsAnalytics.IsChecked = true;
                Invoke(window, "FileTargetsAnalyticsToggle_Click", window.BtnFileTargetsAnalytics, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, window.FileTargetsAnalytics.Visibility);
            }
            finally
            {
                CleanupWindow(window);
            }
        });
    }

    [Fact]
    public void RowActions_DispatchOnlyForFileTargetRows()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = CreateSeededRepository();
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_user));
            try
            {
                window.ShowFileTargets("System");
                object textRow = FileRows(window).Single(row => Name(row) == "app.log");

                Invoke(window, "FileTargetTail_Click", new object(), new RoutedEventArgs());
                Invoke(window, "FileTargetCopyPath_Click", new object(), new RoutedEventArgs());
                Invoke(window, "FileTargetDelete_Click", new object(), new RoutedEventArgs());
                Invoke(window, "FileTargetTrace_Click", new object(), new RoutedEventArgs());
                Invoke(window, "FileTargetDelete_Click", new Border(), new RoutedEventArgs());
                Invoke(window, "FileTargetTrace_Click", new Border(), new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.FileTailOverlay.Visibility);
                Assert.Equal(Visibility.Collapsed, window.FileDeleteOverlay.Visibility);
                Assert.Equal(Visibility.Collapsed, window.HandleTraceOverlay.Visibility);

                string? copiedPath = null;
                window.ClipboardWriter = path => copiedPath = path;
                Invoke(window, "FileTargetCopyPath_Click", new Border { DataContext = textRow }, new RoutedEventArgs());
                Assert.Equal(Property<string>(textRow, "Path"), copiedPath);
                Assert.Contains("Copied full path", window.FileTargetsFooter.Text);

                window.ClipboardWriter = _ => throw new InvalidOperationException("busy");
                Invoke(window, "FileTargetCopyPath_Click", new Border { DataContext = textRow }, new RoutedEventArgs());
                Assert.Contains("clipboard is currently unavailable", window.FileTargetsFooter.Text);

                window.TailInitialReader = (_, _, _) => new TailBatch(["seed"], 4, false, null);
                Invoke(window, "FileTargetTail_Click", new Border { DataContext = textRow }, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, window.FileTailOverlay.Visibility);

                Invoke(window, "FileTargetDelete_Click", new Border { DataContext = textRow }, new RoutedEventArgs());
                Assert.Equal(Visibility.Visible, window.FileDeleteOverlay.Visibility);

                window.HandleLocator = () => null;
                Invoke(window, "FileTargetTrace_Click", new Border { DataContext = textRow }, new RoutedEventArgs());
                await DrainDispatcherAsync();
                Assert.Equal(Visibility.Visible, window.HandleTraceOverlay.Visibility);
                Assert.Equal(Visibility.Visible, window.HandleInstallPrompt.Visibility);
            }
            finally
            {
                CleanupWindow(window);
            }
        });
    }

    [Fact]
    public void LiveTail_CoversUnavailableFailurePollingPauseTruncationAndCloseStates()
    {
        RunSta(() =>
        {
            EnsureApplication();
            var repo = CreateSeededRepository();
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_user));
            try
            {
                window.ShowFileTargets("System");
                object textRow = FileRows(window).Single(row => Name(row) == "app.log");
                object binaryRow = FileRows(window).Single(row => Name(row) == "disk.vhdx");

                Invoke(window, "FileTailPause_Click", window, new RoutedEventArgs());
                Invoke(window, "TailTimer_Tick", null!, EventArgs.Empty);

                Invoke(window, "FileTargetTail_Click", new Border { DataContext = binaryRow }, new RoutedEventArgs());
                Assert.Contains("binary extensions list", window.FileTailStatus.Text);

                window.TailInitialReader = (_, lines, maxReadBytes) =>
                {
                    Assert.Equal(200, lines);
                    Assert.Equal(512 * 1024, maxReadBytes);
                    return new TailBatch([], 0, false, "read failed");
                };
                Invoke(window, "FileTargetTail_Click", new Border { DataContext = textRow }, new RoutedEventArgs());
                Assert.Equal("read failed", window.FileTailStatus.Text);

                window.TailInitialReader = (_, _, _) => new TailBatch([], 5, false, null);
                Invoke(window, "FileTargetTail_Click", new Border { DataContext = textRow }, new RoutedEventArgs());
                Assert.Contains("currently empty", window.FileTailStatus.Text);

                window.TailInitialReader = (_, _, _) => new TailBatch([new string('x', 600_000)], 600_000, false, null, 100);
                Invoke(window, "FileTargetTail_Click", new Border { DataContext = textRow }, new RoutedEventArgs());
                Assert.Contains("bounded tail", window.FileTailStatus.Text);
                Assert.True(window.FileTailOutput.Text.Length <= 512 * 1024);

                var overLimit = Enumerable.Range(0, 5001).Select(index => $"line{index}").ToArray();
                window.TailInitialReader = (_, _, _) => new TailBatch(overLimit, 10, false, null);
                Invoke(window, "FileTargetTail_Click", new Border { DataContext = textRow }, new RoutedEventArgs());
                Assert.StartsWith($"line1{Environment.NewLine}", window.FileTailOutput.Text);
                Assert.EndsWith("line5000", window.FileTailOutput.Text);

                Invoke(window, "FileTailPause_Click", window, new RoutedEventArgs());
                Assert.Equal("Resume", window.BtnFileTailPause.Content);
                Invoke(window, "TailTimer_Tick", null!, EventArgs.Empty);
                Invoke(window, "FileTailPause_Click", window, new RoutedEventArgs());
                Assert.Equal("Pause", window.BtnFileTailPause.Content);

                window.TailIncrementalReader = (_, offset, lines, maxReadBytes) =>
                {
                    Assert.Equal(10, offset);
                    Assert.Equal(5000, lines);
                    Assert.Equal(512 * 1024, maxReadBytes);
                    return new TailBatch([], 10, false, null);
                };
                Invoke(window, "TailTimer_Tick", null!, EventArgs.Empty);

                window.TailIncrementalReader = (_, _, _, _) => new TailBatch(["reset", "latest"], 20, true, null);
                Invoke(window, "TailTimer_Tick", null!, EventArgs.Empty);
                Assert.Equal($"reset{Environment.NewLine}latest", window.FileTailOutput.Text);
                Assert.Contains("Live", window.FileTailStatus.Text);

                window.TailIncrementalReader = (_, _, _, _) => new TailBatch(["burst"], 30, false, null, 1024);
                Invoke(window, "TailTimer_Tick", null!, EventArgs.Empty);
                Assert.Contains("clipped older text", window.FileTailStatus.Text);

                window.TailIncrementalReader = (_, _, _, _) => new TailBatch([], 20, false, "poll failed");
                Invoke(window, "TailTimer_Tick", null!, EventArgs.Empty);
                Assert.Equal("poll failed", window.FileTailStatus.Text);

                var other = KeyArgs(Key.Space);
                Invoke(window, "FileTailOverlay_PreviewKeyDown", window.FileTailOverlay, other);
                Assert.False(other.Handled);
                var escape = KeyArgs(Key.Escape);
                Invoke(window, "FileTailOverlay_PreviewKeyDown", window.FileTailOverlay, escape);
                Assert.True(escape.Handled);
                Assert.Equal(Visibility.Collapsed, window.FileTailOverlay.Visibility);
                window.FileTailClose_Click(window, new RoutedEventArgs());
            }
            finally
            {
                CleanupWindow(window);
            }
        });
    }

    [Fact]
    public void DeleteFlow_CoversPromptOutcomesRefreshAndCloseSemantics()
    {
        RunSta(() =>
        {
            EnsureApplication();
            var repo = CreateSeededRepository();
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_user));
            try
            {
                Invoke(window, "FileDeleteConfirm_Click", window, new RoutedEventArgs());
                window.ShowFileTargets("System");
                object row = FileRows(window).Single(item => Name(item) == "app.log");
                var sender = new Border { DataContext = row };

                Invoke(window, "FileTargetDelete_Click", sender, new RoutedEventArgs());
                Assert.Contains("Delete app.log", window.FileDeleteTitle.Text);
                Assert.Equal(Visibility.Visible, window.BtnFileDeleteConfirm.Visibility);

                window.FileDeleter = _ => new FileDeleteOutcome(FileDeleteStatus.Locked, "locked");
                Invoke(window, "FileDeleteConfirm_Click", window, new RoutedEventArgs());
                Assert.Equal("The file was not deleted", window.FileDeleteTitle.Text);
                Assert.Equal(Visibility.Visible, window.BtnFileDeleteDiagnose.Visibility);

                Invoke(window, "FileTargetDelete_Click", sender, new RoutedEventArgs());
                window.FileDeleter = _ => new FileDeleteOutcome(FileDeleteStatus.ReadOnly, "read only");
                Invoke(window, "FileDeleteConfirm_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.BtnFileDeleteDiagnose.Visibility);

                Invoke(window, "FileTargetDelete_Click", sender, new RoutedEventArgs());
                Invoke(window, "FileDeleteCancel_Click", window, new RoutedEventArgs());
                Assert.Null(Field<string?>(window, "_pendingDeletePath"));

                Invoke(window, "FileTargetDelete_Click", sender, new RoutedEventArgs());
                window.FileDeleter = _ => new FileDeleteOutcome(FileDeleteStatus.Deleted, "gone");
                Invoke(window, "FileDeleteConfirm_Click", window, new RoutedEventArgs());
                Assert.Equal("File deleted", window.FileDeleteTitle.Text);
                Assert.Equal("Files written by System", window.FileTargetsTitle.Text);
                Invoke(window, "FileDeleteCancel_Click", window, new RoutedEventArgs());
                Assert.NotNull(Field<string?>(window, "_pendingDeletePath"));

                Invoke(window, "FileTargetDelete_Click", sender, new RoutedEventArgs());
                var other = KeyArgs(Key.Space);
                Invoke(window, "FileDeleteOverlay_PreviewKeyDown", window.FileDeleteOverlay, other);
                Assert.False(other.Handled);
                var escape = KeyArgs(Key.Escape);
                Invoke(window, "FileDeleteOverlay_PreviewKeyDown", window.FileDeleteOverlay, escape);
                Assert.True(escape.Handled);
                Assert.Equal(Visibility.Collapsed, window.FileDeleteOverlay.Visibility);
            }
            finally
            {
                CleanupWindow(window);
            }
        });
    }

    [Fact]
    public void DeleteDiagnosis_CoversMissingToolFailureAndLockerResults()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = CreateSeededRepository();
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_user));
            try
            {
                await window.DiagnosePendingDeleteAsync();
                window.ShowFileTargets("System");
                object row = FileRows(window).Single(item => Name(item) == "app.log");
                var sender = new Border { DataContext = row };

                Invoke(window, "FileTargetDelete_Click", sender, new RoutedEventArgs());
                window.HandleLocator = () => null;
                await window.DiagnosePendingDeleteAsync();
                Assert.Equal(Visibility.Collapsed, window.FileDeleteOverlay.Visibility);
                Assert.Equal(Visibility.Visible, window.HandleInstallPrompt.Visibility);
                Assert.True(window.BtnFileDeleteDiagnose.IsEnabled);

                Invoke(window, "FileTargetDelete_Click", sender, new RoutedEventArgs());
                window.HandleLocator = () => "handle.exe";
                window.PathHandleRunner = (_, _) => Task.FromResult(
                    new HandleRunResult(false, "", null, false));
                Invoke(window, "FileDeleteDiagnose_Click", window, new RoutedEventArgs());
                await DrainDispatcherAsync();
                Assert.Equal("Handle did not return any output.", window.FileDeleteLockTitle.Text);

                const string output = """
                    writer.exe pid: 41
                       C4: File  (RW-)   C:\logs\app.log
                    helper.exe pid: 42 USER\name
                       C8: File  (RW-)   C:\logs\app.log
                    """;
                window.PathHandleRunner = (_, _) => Task.FromResult(
                    new HandleRunResult(true, output, null, true));
                await window.DiagnosePendingDeleteAsync();
                var lockers = ((IEnumerable)window.FileDeleteLockList.ItemsSource!).Cast<object>().ToList();
                Assert.Equal(2, lockers.Count);
                Assert.Contains(lockers, item => Property<string>(item, "Detail") == @"C:\logs\app.log");
                Assert.Contains(lockers, item => Property<string>(item, "Detail").Contains(@"USER\name"));
                Assert.Contains("2 processes", window.FileDeleteLockTitle.Text);
            }
            finally
            {
                CleanupWindow(window);
            }
        });
    }

    [Fact]
    public void HandleTracing_CoversProcessAndFileModesAcrossEveryResultShape()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = CreateSeededRepository();
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_user));
            try
            {
                await window.TraceProcessHandlesAsync();
                window.ShowFileTargets("System");

                window.HandleLocator = () => null;
                Invoke(window, "TraceProcessHandles_Click", window, new RoutedEventArgs());
                await DrainDispatcherAsync();
                Assert.Equal(Visibility.Visible, window.HandleInstallPrompt.Visibility);

                window.HandleLocator = () => "handle.exe";
                window.ProcessHandleRunner = (_, target) =>
                {
                    Assert.Equal("System", target);
                    return Task.FromResult(new HandleRunResult(false, "", "run failed", false));
                };
                await window.TraceProcessHandlesAsync();
                Assert.Equal("run failed", window.HandleTraceStatus.Text);
                Assert.True(window.BtnHandleCopy.IsEnabled);

                window.ProcessHandleRunner = (_, _) => Task.FromResult(
                    new HandleRunResult(false, "", null, false));
                await window.TraceProcessHandlesAsync();
                Assert.Equal("Handle did not return any output.", window.HandleTraceStatus.Text);

                window.ProcessHandleRunner = (_, _) => Task.FromResult(
                    new HandleRunResult(true, "", null, true));
                await window.TraceProcessHandlesAsync();
                Assert.Equal("Handle returned no output for this target.", window.HandleTraceOutput.Text);
                Assert.Contains("0 open handle", window.HandleTraceStatus.Text);

                const string processOutput = """
                    System pid: 4 NT AUTHORITY\SYSTEM
                       C4: File  (RW-)   C:\logs\app.log
                       D0: Key           HKLM\SOFTWARE\Test
                    """;
                window.ProcessHandleRunner = (_, _) => Task.FromResult(
                    new HandleRunResult(true, processOutput, null, false));
                await window.TraceProcessHandlesAsync();
                Assert.Contains("2 open handle", window.HandleTraceStatus.Text);
                Assert.Contains("administrator", window.HandleTraceStatus.Text);
                Assert.Equal("Source: handle.exe", window.HandleTraceFooter.Text);

                object row = FileRows(window).Single(item => Name(item) == "app.log");
                window.PathHandleRunner = (_, _) => Task.FromResult(
                    new HandleRunResult(true, "writer.exe pid: 41 USER\\name  C4: C:\\logs\\app.log", null, true));
                Invoke(window, "FileTargetTrace_Click", new Border { DataContext = row }, new RoutedEventArgs());
                await DrainDispatcherAsync();
                Assert.Contains("Who has app.log open", window.HandleTraceTitle.Text);
                Assert.Single(((IEnumerable)window.HandleLockerList.ItemsSource!).Cast<object>());

                var other = KeyArgs(Key.Space);
                Invoke(window, "HandleTraceOverlay_PreviewKeyDown", window.HandleTraceOverlay, other);
                Assert.False(other.Handled);
                var escape = KeyArgs(Key.Escape);
                Invoke(window, "HandleTraceOverlay_PreviewKeyDown", window.HandleTraceOverlay, escape);
                Assert.True(escape.Handled);
                Assert.Equal(Visibility.Collapsed, window.HandleTraceOverlay.Visibility);
                window.HandleTraceClose_Click(window, new RoutedEventArgs());
            }
            finally
            {
                CleanupWindow(window);
            }
        });
    }

    [Fact]
    public void HandleDownloadAndCopy_CoverSuccessHandledFailureAndClipboardUnavailable()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = CreateSeededRepository();
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_user));
            try
            {
                window.ShowFileTargets("System");
                window.HandleLocator = () => "handle.exe";
                window.ProcessHandleRunner = (_, _) => Task.FromResult(
                    new HandleRunResult(true, "trace output", null, true));
                window.HandleInstaller = _ => Task.FromResult("handle.exe");

                Invoke(window, "TraceProcessHandles_Click", window, new RoutedEventArgs());
                await DrainDispatcherAsync();
                Invoke(window, "HandleDownload_Click", window, new RoutedEventArgs());
                await DrainDispatcherAsync();
                Assert.True(window.BtnHandleDownload.IsEnabled);
                Assert.Equal(Visibility.Collapsed, window.HandleInstallPrompt.Visibility);
                Assert.Equal("trace output", window.HandleTraceOutput.Text);

                window.HandleInstaller = _ => throw new HttpRequestException("offline");
                await window.DownloadHandleAsync();
                Assert.Contains("offline", window.HandleTraceStatus.Text);
                Assert.True(window.BtnHandleDownload.IsEnabled);

                window.HandleTraceOutput.Text = "";
                Invoke(window, "HandleTraceCopy_Click", window, new RoutedEventArgs());
                Assert.NotEqual("Output copied to the clipboard.", window.HandleTraceFooter.Text);

                string? copied = null;
                window.HandleTraceOutput.Text = "copy me";
                window.ClipboardWriter = text => copied = text;
                Invoke(window, "HandleTraceCopy_Click", window, new RoutedEventArgs());
                Assert.Equal("copy me", copied);
                Assert.Equal("Output copied to the clipboard.", window.HandleTraceFooter.Text);

                window.ClipboardWriter = _ => throw new System.Runtime.InteropServices.ExternalException();
                Invoke(window, "HandleTraceCopy_Click", window, new RoutedEventArgs());
                Assert.Equal("The clipboard is currently unavailable.", window.HandleTraceFooter.Text);
            }
            finally
            {
                CleanupWindow(window);
            }
        });
    }

    [Fact]
    public void DatabaseWarning_CoversThresholdCooldownForegroundToastSettingsAndCloseStates()
    {
        RunSta(() =>
        {
            EnsureApplication();
            var repo = CreateSeededRepository();
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_user));
            try
            {
                var now = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);
                window.UtcNowProvider = () => now;
                window.DatabaseMeasurer = () => new DatabaseSize(100, 20, 5);
                window.CheckDatabaseSize();
                Assert.Equal(Visibility.Collapsed, window.DatabaseSizeOverlay.Visibility);

                int toasts = 0;
                window.DatabaseMeasurer = () => new DatabaseSize(2L * 1024 * 1024 * 1024, 20, 5);
                window.DashboardForegroundOverride = () => true;
                window.DatabaseToastShower = _ => toasts++;
                window.CheckDatabaseSize();
                Assert.Equal(Visibility.Visible, window.DatabaseSizeOverlay.Visibility);
                Assert.Contains("above your", window.DatabaseSizeMessage.Text);
                Assert.Equal(0, toasts);

                window.CheckDatabaseSize();
                Invoke(window, "DatabaseSizeClose_Click", window, new RoutedEventArgs());
                Assert.Equal("The monitoring database is large", window.DatabaseSizeTitle.Text);
                Assert.Equal(Visibility.Visible, window.BtnDatabaseCompact.Visibility);
                Assert.Equal("Not now", window.BtnDatabaseSizeLater.Content);

                window.CheckDatabaseSize();
                Assert.Equal(Visibility.Collapsed, window.DatabaseSizeOverlay.Visibility);

                now = now.AddHours(config.Current.DatabaseSizeAlertCooldownHours + 1);
                window.DashboardForegroundOverride = () => false;
                window.CheckDatabaseSize();
                Assert.Equal(1, toasts);
                Invoke(window, "DatabaseSizeClose_Click", window, new RoutedEventArgs());

                now = now.AddHours(config.Current.DatabaseSizeAlertCooldownHours + 1);
                window.DatabaseToastShower = _ => throw new InvalidOperationException("toast unavailable");
                window.CheckDatabaseSize();
                Assert.Equal(Visibility.Visible, window.DatabaseSizeOverlay.Visibility);
                Invoke(window, "DatabaseSizeClose_Click", window, new RoutedEventArgs());

                window.DashboardForegroundOverride = null;
                Assert.False((bool)InvokeResult(window, "IsDashboardInForeground")!);

                now = now.AddHours(config.Current.DatabaseSizeAlertCooldownHours + 1);
                window.DatabaseToastShower = _ => toasts++;
                window.CheckDatabaseSize();
                Assert.Equal(2, toasts);
                Invoke(window, "DatabaseSizeClose_Click", window, new RoutedEventArgs());

                Invoke(window, "UpdateDatabaseSizeCaption");
                Assert.Contains("Currently", window.TxtDatabaseSizeCurrent.Text);
                Invoke(window, "RestoreBinaryExtensions_Click", window, new RoutedEventArgs());
                Assert.Equal(AppConfig.DefaultBinaryExtensions, window.TxtBinaryExtensions.Text);

                Invoke(window, "DatabaseCompactFromSettings_Click", window, new RoutedEventArgs());
                Assert.Equal("Compact the monitoring database", window.DatabaseSizeTitle.Text);
                Assert.Equal(Visibility.Visible, window.DatabaseSizeOverlay.Visibility);

                var other = KeyArgs(Key.Space);
                Invoke(window, "DatabaseSizeOverlay_PreviewKeyDown", window.DatabaseSizeOverlay, other);
                Assert.False(other.Handled);
                var escape = KeyArgs(Key.Escape);
                Invoke(window, "DatabaseSizeOverlay_PreviewKeyDown", window.DatabaseSizeOverlay, escape);
                Assert.True(escape.Handled);
                Assert.Equal(Visibility.Collapsed, window.DatabaseSizeOverlay.Visibility);

                window.ShowFileTargets("System");
                Invoke(window, "DatabaseSizeSettings_Click", window, new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, window.FileTargetsOverlay.Visibility);
                Assert.Equal(Visibility.Visible, window.SettingsPanel.Visibility);

                SetField(window, "_databaseCompactionRunning", true);
                int measurements = 0;
                window.DatabaseMeasurer = () =>
                {
                    measurements++;
                    return new DatabaseSize(1, 1, 1);
                };
                window.CheckDatabaseSize();
                Assert.Equal(0, measurements);
            }
            finally
            {
                SetField(window, "_databaseCompactionRunning", false);
                CleanupWindow(window);
            }
        });
    }

    [Fact]
    public void DatabaseCompaction_CoversGuardSuccessFailureAndFinalReset()
    {
        RunStaAsync(async () =>
        {
            EnsureApplication();
            var repo = CreateSeededRepository();
            using var config = new ConfigStore(_cfg);
            var window = new MainWindow(repo, config, new UserSettingsStore(_user));
            try
            {
                SetField(window, "_databaseCompactionRunning", true);
                Invoke(window, "DatabaseCompact_Click", window, new RoutedEventArgs());
                await window.CompactDatabaseAsync();
                Assert.True(Field<bool>(window, "_databaseCompactionRunning"));

                SetField(window, "_databaseCompactionRunning", false);
                window.DatabaseMeasurer = () => new DatabaseSize(40, 5, 5);
                window.DatabaseCompactor = _ => Task.FromResult(new CompactionResult(true, 1000, 500, null));
                await window.CompactDatabaseAsync();
                Assert.Equal("Compaction complete", window.DatabaseSizeTitle.Text);
                Assert.Contains("Reclaimed", window.DatabaseSizeMessage.Text);
                Assert.Equal(Visibility.Collapsed, window.BtnDatabaseCompact.Visibility);
                Assert.Equal("Close", window.BtnDatabaseSizeLater.Content);
                Assert.False(Field<bool>(window, "_databaseCompactionRunning"));
                Assert.True(window.BtnDatabaseCompact.IsEnabled);
                Assert.Equal("Compact now", window.BtnDatabaseCompact.Content);

                window.BtnDatabaseCompact.Visibility = Visibility.Visible;
                window.DatabaseCompactor = _ => Task.FromResult(new CompactionResult(false, 1000, 1000, "locked"));
                await window.CompactDatabaseAsync();
                Assert.Equal("Compaction failed", window.DatabaseSizeTitle.Text);
                Assert.Contains("locked", window.DatabaseSizeMessage.Text);
            }
            finally
            {
                CleanupWindow(window);
            }
        });
    }

    /// <summary>Writes two processes of per-minute write activity plus tracked files for System.</summary>
    private static void SeedActivity(MonitorRepository repo, DateTime end)
    {
        var processSamples = new List<ProcessIoSample>();
        for (int i = 1; i <= 30; i++)
        {
            var minute = end.AddMinutes(-i);
            processSamples.Add(new ProcessIoSample
            {
                TimestampUtc = minute,
                ProcessName = "System",
                WriteBytes = 4L * 1024 * 1024 * i,
                ReadBytes = 1024,
            });
            processSamples.Add(new ProcessIoSample
            {
                TimestampUtc = minute,
                ProcessName = "chrome.exe",
                WriteBytes = 1L * 1024 * 1024,
                ReadBytes = 512,
            });
        }
        repo.AddProcessSamples(processSamples);

        var fileSamples = new List<ProcessFileIoSample>();
        for (int i = 1; i <= 30; i++)
        {
            var minute = end.AddMinutes(-i);
            fileSamples.Add(new ProcessFileIoSample
            {
                TimestampUtc = minute,
                ProcessName = "System",
                Path = @"C:\logs\app.log",
                Kind = FileTargetKind.Other,
                WriteBytes = 2L * 1024 * 1024 * i,
            });
            fileSamples.Add(new ProcessFileIoSample
            {
                TimestampUtc = minute,
                ProcessName = "System",
                Path = @"C:\vm\disk.vhdx",
                Kind = FileTargetKind.VirtualDisk,
                WriteBytes = 1L * 1024 * 1024 * i,
            });
        }
        repo.AddProcessFileSamples(fileSamples);
    }

    private static string Name(object row)
        => (string)row.GetType().GetProperty("FileName")!.GetValue(row)!;

    private static bool CanTail(object row)
        => (bool)row.GetType().GetProperty("CanTail")!.GetValue(row)!;

    private MonitorRepository CreateSeededRepository()
    {
        var repo = new MonitorRepository(_db);
        repo.EnsureSchema();
        repo.UpsertDisks([new DiskInfo
        {
            DiskId = "0", InstanceName = "0 C:", FriendlyName = "Test SSD", Volumes = "C:", MediaType = DiskMediaType.Ssd,
        }]);
        SeedActivity(repo, FloorToMinute(DateTime.UtcNow));
        return repo;
    }

    private static DateTime FloorToMinute(DateTime value)
        => new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, DateTimeKind.Utc);

    private static IReadOnlyList<object> FileRows(MainWindow window)
        => ((IEnumerable)window.FileTargetsList.ItemsSource!).Cast<object>().ToList();

    private static T Property<T>(object target, string name)
        => (T)target.GetType().GetProperty(name)!.GetValue(target)!;

    private static T Field<T>(MainWindow window, string name)
        => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static void SetField<T>(MainWindow window, string name, T value)
        => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    private static void CleanupWindow(MainWindow window)
    {
        Field<System.Windows.Threading.DispatcherTimer>(window, "_refreshTimer").Stop();
        Field<System.Windows.Threading.DispatcherTimer?>(window, "_tailTimer")?.Stop();
        window.Hide();
    }

    private static object? InvokeResult(MainWindow window, string method, params object[] args)
        => typeof(MainWindow)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, args);

    private static KeyEventArgs KeyArgs(Key key)
        => new(Keyboard.PrimaryDevice, new FakePresentationSource(), Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };

    private sealed class FakePresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    private static void Invoke(MainWindow window, string method, params object[] args)
        => typeof(MainWindow)
            .GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, args);

    /// <summary>
    /// Optional PNG capture of the rendered dialog, enabled by setting <c>DAM_UI_SNAPSHOT</c> to an
    /// output path. Kept opt-in so ordinary runs stay side-effect free.
    /// </summary>
    private static void RenderSnapshotIfRequested(MainWindow window)
    {
        string? target = Environment.GetEnvironmentVariable("DAM_UI_SNAPSHOT");
        if (string.IsNullOrWhiteSpace(target)) return;

        window.BtnFileTargetsAnalytics.IsChecked = true;
        Invoke(window, "FileTargetsAnalyticsToggle_Click", window.BtnFileTargetsAnalytics, new RoutedEventArgs());

        var element = window.FileTargetsOverlay;
        element.Measure(new Size(1180, 900));
        element.Arrange(new Rect(0, 0, 1180, 900));
        element.UpdateLayout();

        var bitmap = new RenderTargetBitmap(1180, 900, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(target);
        encoder.Save(stream);
    }

    private static void EnsureApplication()
    {
        Assert.NotNull(Application.Current);
    }

    private static void RunSta(Action action)
        => RunStaAsync(() =>
        {
            action();
            return Task.CompletedTask;
        });

    private static void RunStaAsync(Func<Task> action)
        => Sta.Value.Run(action);

    private sealed class StaHarness
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _work = new();
        private App? _application;

        public StaHarness()
        {
            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new System.Windows.Threading.DispatcherSynchronizationContext());

                if (Application.Current is null)
                {
                    _application = new App();
                    _application.InitializeComponent();
                    _application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    _application.Resources["TextPrimary"] = new SolidColorBrush(Colors.White);
                    _application.Resources["Caption"] = new Style(typeof(TextBlock));
                    _application.Resources["ToolButton"] = new Style(typeof(Button));
                }

                ready.Set();
                foreach (Action item in _work.GetConsumingEnumerable())
                    item();
            })
            {
                IsBackground = true,
                Name = "FileToolsTests.STA",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
        }

        public void Run(Func<Task> action)
        {
            Exception? error = null;
            using var completed = new ManualResetEventSlim();
            _work.Add(() =>
            {
                try
                {
                    Task task = action();
                    if (!task.IsCompleted)
                    {
                        var frame = new System.Windows.Threading.DispatcherFrame();
                        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                        System.Windows.Threading.Dispatcher.PushFrame(frame);
                    }
                    task.GetAwaiter().GetResult();
                }
                catch (Exception ex) { error = ex; }
                finally { completed.Set(); }
            });
            completed.Wait();
            if (error is not null) throw new TargetInvocationException(error);
        }
    }

    private static async Task DrainDispatcherAsync()
        => await System.Windows.Threading.Dispatcher.Yield();
}
