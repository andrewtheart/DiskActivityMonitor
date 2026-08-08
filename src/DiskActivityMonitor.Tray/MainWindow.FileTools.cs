using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using DiskActivityMonitor.Core;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Files;
using DiskActivityMonitor.Core.Tools;
using DiskActivityMonitor.Tray.Controls;
using Microsoft.Toolkit.Uwp.Notifications;
using DispatcherTimer = System.Windows.Threading.DispatcherTimer;

namespace DiskActivityMonitor.Tray;

/// <summary>
/// File-level tooling for the per-process drill-down: analytics charts, live tailing, deletion
/// with lock diagnosis, Sysinternals Handle tracing, and database growth management.
/// </summary>
public partial class MainWindow
{
    // Context of the currently open drill-down, reused by every row action.
    private string _fileTargetsProcess = "";
    private DateTime _fileTargetsWindowStartUtc;
    private DateTime _fileTargetsWindowEndUtc;

    // Live tail state.
    private DispatcherTimer? _tailTimer;
    private string? _tailPath;
    private long _tailOffset;
    private bool _tailPaused;
    private readonly List<string> _tailLines = new();

    // Pending delete target and the diagnosis offer attached to its failure.
    private string? _pendingDeletePath;
    private bool _pendingDeleteConfirmed;

    // Database growth warning bookkeeping.
    private DateTime? _lastDatabaseWarnUtc;
    private bool _databaseCompactionRunning;

    /// <summary>Overridable download hook so tests do not reach the network.</summary>
    internal Func<CancellationToken, Task<string>> HandleInstaller { get; set; }
        = ct => HandleTool.InstallAsync(cancellationToken: ct);

    internal Func<string, int, int, TailBatch> TailInitialReader { get; set; }
        = FileTailReader.ReadTail;

    internal Func<string, long, int, int, TailBatch> TailIncrementalReader { get; set; }
        = FileTailReader.ReadFrom;

    internal Func<string, FileDeleteOutcome> FileDeleter { get; set; }
        = FileDeletionService.Delete;

    internal Func<string?> HandleLocator { get; set; }
        = () => HandleTool.Locate();

    internal Func<string, string, Task<HandleRunResult>> ProcessHandleRunner { get; set; }
        = (tool, target) => HandleTool.ListProcessHandlesAsync(tool, target);

    internal Func<string, string, Task<HandleRunResult>> PathHandleRunner { get; set; }
        = (tool, target) => HandleTool.FindPathHandlesAsync(tool, target);

    internal Action<string> ClipboardWriter { get; set; }
        = text => System.Windows.Clipboard.SetText(text);

    internal Func<DatabaseSize> DatabaseMeasurer { get; set; }
        = () => DatabaseMaintenance.Measure();

    internal Func<MonitorRepository, Task<CompactionResult>> DatabaseCompactor { get; set; }
        = repository => Task.Run(() => DatabaseMaintenance.Compact(repository));

    internal Func<DateTime> UtcNowProvider { get; set; }
        = () => DateTime.UtcNow;

    internal Action<ToastContentBuilder> DatabaseToastShower { get; set; }
        = builder => builder.Show();

    internal Func<bool>? DashboardForegroundOverride { get; set; }

    // ---------------------------------------------------------------- analytics

    /// <summary>
    /// Fills the dialog's analytics band: this process's share of all writes, its write volume
    /// over time, and which file types absorbed the bytes.
    /// </summary>
    private void UpdateFileTargetsAnalytics(string processName, DateTime startUtc, DateTime endUtc, long processWrite)
    {
        UpdateWritesByProcessPie(processName, startUtc, endUtc, processWrite);
        UpdateWriteTimeline(processName, startUtc, endUtc);
        UpdateFileTypeBreakdown(processName, startUtc, endUtc);
    }

    /// <summary>Shows how much of all disk writing this process is responsible for.</summary>
    private void UpdateWritesByProcessPie(string processName, DateTime startUtc, DateTime endUtc, long processWrite)
    {
        var top = _repo.GetTopProcesses(startUtc, endUtc, topN: 7);
        long total = _repo.GetAllProcessesWrite(startUtc, endUtc);

        var slices = top
            .Select(p => new PieSlice(p.ProcessName, p.WriteBytes, IsSameProcess(p.ProcessName, processName)))
            .ToList();

        // Keep the focused process visible even when it is not in the top ranks.
        if (!slices.Any(s => s.Highlight) && processWrite > 0)
            slices.Add(new PieSlice(processName, processWrite, true));

        long accounted = slices.Sum(s => (long)s.Value);
        if (total > accounted) slices.Add(new PieSlice("Other processes", total - accounted));

        FileTargetsPie.SetData(slices, v => ByteFormat.Humanize((long)v), ByteFormat.Humanize(total));
        FileTargetsPieCaption.Text = DescribeProcessShare(processName, processWrite, total);
    }

    /// <summary>Wording for the pie caption, kept separate so it can be asserted directly.</summary>
    internal static string DescribeProcessShare(string processName, long processWrite, long totalWrite)
    {
        if (totalWrite <= 0 || processWrite <= 0)
            return "No write activity was recorded for any process in this window.";

        double share = Math.Min(1, (double)processWrite / totalWrite);
        return $"{processName} accounts for {share:P0} of all recorded writes in this window.";
    }

    /// <summary>Plots per-minute writes, bucketed so a long window stays legible.</summary>
    private void UpdateWriteTimeline(string processName, DateTime startUtc, DateTime endUtc)
    {
        var minutes = _repo.GetProcessMinuteWrites(processName, startUtc, endUtc);
        var bars = BuildTimelineBars(minutes, startUtc, endUtc);

        FileTargetsTimeline.SetData(bars, v => ByteFormat.Humanize((long)v));
        FileTargetsTimelineCaption.Text = DescribeTimeline(minutes.Count, bars.Count);
    }

    internal static string DescribeTimeline(int recordedMinutes, int bucketCount)
        => recordedMinutes == 0
            ? "No per-minute samples for this process in the selected window."
            : $"{recordedMinutes:N0} recorded minute(s) grouped into {bucketCount} interval(s); tall bars are write bursts.";

    /// <summary>
    /// Groups a sparse per-minute series into at most <paramref name="maxBuckets"/> equal
    /// intervals. Minutes with no sample contribute zero rather than being skipped, so a quiet
    /// period reads as a gap instead of being compressed away.
    /// </summary>
    internal static IReadOnlyList<ChartBar> BuildTimelineBars(
        IReadOnlyList<(DateTime MinuteUtc, long WriteBytes)> minutes,
        DateTime startUtc,
        DateTime endUtc,
        int maxBuckets = 48)
    {
        double totalMinutes = Math.Max(1, (endUtc - startUtc).TotalMinutes);
        int buckets = (int)Math.Clamp(Math.Ceiling(totalMinutes), 1, maxBuckets);
        double minutesPerBucket = totalMinutes / buckets;

        var totals = new long[buckets];
        foreach (var (minuteUtc, writeBytes) in minutes)
        {
            double offset = (minuteUtc - startUtc).TotalMinutes;
            if (offset < 0) continue;

            int index = (int)Math.Min(buckets - 1, offset / minutesPerBucket);
            totals[index] += writeBytes;
        }

        // Long windows need a date; short ones only need the time of day.
        string format = totalMinutes > 60 * 36 ? "MMM d" : "h:mm tt";
        long peak = totals.Max();

        var bars = new List<ChartBar>(buckets);
        for (int i = 0; i < buckets; i++)
        {
            var bucketStart = startUtc.AddMinutes(i * minutesPerBucket);
            bars.Add(new ChartBar(
                LocalTimeDisplay.FormatUtc(bucketStart, format),
                totals[i],
                Highlight: peak > 0 && totals[i] == peak));
        }

        return bars;
    }

    /// <summary>Aggregates the process's tracked files by extension.</summary>
    private void UpdateFileTypeBreakdown(string processName, DateTime startUtc, DateTime endUtc)
    {
        // Pull a deeper slice than the visible list so the breakdown reflects more than 20 files.
        var targets = _repo.GetTopFileTargets(processName, startUtc, endUtc, topN: 200);

        var groups = targets
            .GroupBy(t => ExtensionLabel(t.Path))
            .Select(g => (Label: g.Key, Bytes: g.Sum(t => t.WriteBytes)))
            .OrderByDescending(g => g.Bytes)
            .ToList();

        const int maxRows = 8;
        if (groups.Count > maxRows)
        {
            long rest = groups.Skip(maxRows).Sum(g => g.Bytes);
            groups = groups.Take(maxRows).ToList();
            if (rest > 0) groups.Add(("Other", rest));
        }

        double max = groups.Count > 0 ? Math.Max(1, groups.Max(g => g.Bytes)) : 1;
        const double barArea = 520;

        FileTargetsTypesList.ItemsSource = groups
            .Select((g, i) => new FileTypeRow(
                g.Label,
                ByteFormat.Humanize(g.Bytes),
                Math.Max(3, g.Bytes / max * barArea),
                PaletteBrush(i)))
            .ToList();

        FileTargetsTypesEmpty.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FileTargetsTypesCaption.Text = groups.Count == 0
            ? ""
            : $"Grouped from the {targets.Count:N0} busiest tracked file(s) for {processName}.";
    }

    /// <summary>Extension label for grouping; files without one are reported as "(no extension)".</summary>
    internal static string ExtensionLabel(string path)
    {
        string extension = System.IO.Path.GetExtension(path);
        return extension.Length > 1 ? extension.ToLowerInvariant() : "(no extension)";
    }

    private static Brush PaletteBrush(int index)
    {
        var brush = new SolidColorBrush(PieChart.Palette[index % PieChart.Palette.Count]);
        brush.Freeze();
        return brush;
    }

    private static bool IsSameProcess(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void FileTargetsAnalyticsToggle_Click(object sender, RoutedEventArgs e)
        => FileTargetsAnalytics.Visibility = BtnFileTargetsAnalytics.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;

    // ---------------------------------------------------------------- row actions

    private void FileTargetTail_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileTargetRow row) StartFileTail(row);
    }

    private async void FileTargetTrace_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileTargetRow row)
            await TraceFileHandlesAsync(row.Path).ConfigureAwait(true);
    }

    private void FileTargetDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileTargetRow row) PromptFileDelete(row);
    }

    private void FileTargetCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileTargetRow row) return;

        try
        {
            ClipboardWriter(row.Path);
            FileTargetsFooter.Text = $"Copied full path for {row.FileName} to the clipboard.";
        }
        catch
        {
            FileTargetsFooter.Text = "The clipboard is currently unavailable.";
        }
    }

    // ---------------------------------------------------------------- live tail

    /// <summary>Opens the live tail viewer for a text file and starts polling it.</summary>
    private void StartFileTail(FileTargetRow row)
    {
        if (!row.CanTail)
        {
            ShowFileTailUnavailable(row);
            return;
        }

        StopFileTail();

        _tailPath = row.Path;
        _tailPaused = false;
        _tailLines.Clear();

        FileTailTitle.Text = row.FileName;
        FileTailPath.Text = row.Path;
        BtnFileTailPause.Content = "Pause";
        FileTailOutput.Text = "";

        var seed = TailInitialReader(
            row.Path,
            Math.Max(1, _config.Current.TailInitialLines),
            TailReadBytes());
        if (!seed.Success)
        {
            FileTailStatus.Text = seed.Error;
            FileTailOverlay.Visibility = Visibility.Visible;
            FileTailOverlay.Focus();
            return;
        }

        _tailOffset = seed.NextOffset;
        bool displayTrimmed = AppendTailLines(seed.Lines);
        FileTailStatus.Text = seed.Lines.Count == 0
            ? "The file is currently empty; new lines appear here as they are written."
            : seed.SkippedBytes > 0 || displayTrimmed
                ? $"Showing the newest bounded tail ({_tailLines.Count:N0} line(s)); earlier content was skipped. Watching for new output."
                : $"Showing the last {seed.Lines.Count:N0} line(s). Watching for new output.";

        _tailTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tailTimer.Tick += TailTimer_Tick;
        _tailTimer.Start();

        FileTailOverlay.Visibility = Visibility.Visible;
        FileTailOverlay.Focus();
    }

    /// <summary>Explains why a binary file is not offered as a tail target.</summary>
    private void ShowFileTailUnavailable(FileTargetRow row)
    {
        StopFileTail();
        _tailPath = null;

        FileTailTitle.Text = row.FileName;
        FileTailPath.Text = row.Path;
        FileTailOutput.Text = "";
        FileTailStatus.Text =
            $"\"{ExtensionLabel(row.Path)}\" is on the binary extensions list, so this file is not tailed as text. "
            + "Adjust the list in Settings if it should be treated as text.";

        FileTailOverlay.Visibility = Visibility.Visible;
        FileTailOverlay.Focus();
    }

    private void TailTimer_Tick(object? sender, EventArgs e)
    {
        if (_tailPaused || _tailPath is null) return;

        var batch = TailIncrementalReader(
            _tailPath,
            _tailOffset,
            Math.Max(1, _config.Current.TailMaxLines),
            TailReadBytes());
        if (!batch.Success)
        {
            FileTailStatus.Text = batch.Error;
            StopFileTail();
            return;
        }

        if (batch.Truncated)
        {
            _tailLines.Clear();
            FileTailStatus.Text = "The file was truncated or replaced; the view restarted from the beginning.";
        }

        _tailOffset = batch.NextOffset;
        if (batch.Lines.Count > 0)
        {
            bool displayTrimmed = AppendTailLines(batch.Lines);
            FileTailStatus.Text = batch.SkippedBytes > 0 || displayTrimmed
                ? $"Live. A large burst or display limit clipped older text; {_tailLines.Count:N0} line(s) buffered."
                : $"Live. {_tailLines.Count:N0} line(s) buffered.";
        }
    }

    private int TailReadBytes()
        => Math.Clamp(_config.Current.TailMaxReadKb, 64, 16384) * 1024;

    /// <summary>Appends lines, trimming to the configured ceiling so long runs stay bounded.</summary>
    private bool AppendTailLines(IReadOnlyList<string> lines)
    {
        _tailLines.AddRange(lines);

        int limit = Math.Max(1, _config.Current.TailMaxLines);
        bool trimmed = false;
        if (_tailLines.Count > limit)
        {
            _tailLines.RemoveRange(0, _tailLines.Count - limit);
            trimmed = true;
        }

        int maxChars = Math.Clamp(_config.Current.TailMaxBufferKb, 128, 32768) * 1024 / sizeof(char);
        trimmed |= TrimTailLines(_tailLines, maxChars);

        FileTailOutput.Text = string.Join(Environment.NewLine, _tailLines);
        if (ChkFileTailFollow.IsChecked == true) FileTailOutput.ScrollToEnd();
        return trimmed;
    }

    internal static bool TrimTailLines(List<string> lines, int maxChars)
    {
        maxChars = Math.Max(1, maxChars);
        long chars = lines.Sum(line => (long)line.Length);
        bool trimmed = false;
        while (lines.Count > 1 && chars > maxChars)
        {
            chars -= lines[0].Length;
            lines.RemoveAt(0);
            trimmed = true;
        }

        if (lines.Count == 1 && lines[0].Length > maxChars)
        {
            lines[0] = lines[0][^maxChars..];
            trimmed = true;
        }
        return trimmed;
    }

    private void FileTailPause_Click(object sender, RoutedEventArgs e)
    {
        if (_tailPath is null) return;

        _tailPaused = !_tailPaused;
        BtnFileTailPause.Content = _tailPaused ? "Resume" : "Pause";
        FileTailStatus.Text = _tailPaused
            ? "Paused. New output is still being written but is not shown."
            : "Live.";
    }

    internal void FileTailClose_Click(object sender, RoutedEventArgs e) => CloseFileTail();

    private void FileTailOverlay_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        e.Handled = true;
        CloseFileTail();
    }

    private void CloseFileTail()
    {
        StopFileTail();
        _tailPath = null;
        FileTailOverlay.Visibility = Visibility.Collapsed;
        FileTargetsOverlay.Focus();
    }

    private void StopFileTail()
    {
        if (_tailTimer is null) return;

        _tailTimer.Stop();
        _tailTimer.Tick -= TailTimer_Tick;
        _tailTimer = null;
    }

    // ---------------------------------------------------------------- delete

    /// <summary>Asks for confirmation before removing a file listed in the drill-down.</summary>
    private void PromptFileDelete(FileTargetRow row)
    {
        _pendingDeletePath = row.Path;
        _pendingDeleteConfirmed = false;

        FileDeleteIcon.Text = "\uE74D";
        FileDeleteTitle.Text = $"Delete {row.FileName}?";
        FileDeleteMessage.Text =
            "This permanently removes the file from disk. It is not sent to the Recycle Bin and cannot be undone from here.";
        FileDeletePath.Text = row.Path;

        FileDeleteLockBorder.Visibility = Visibility.Collapsed;
        FileDeleteLockList.ItemsSource = null;
        BtnFileDeleteDiagnose.Visibility = Visibility.Collapsed;
        BtnFileDeleteConfirm.Visibility = Visibility.Visible;
        BtnFileDeleteConfirm.Content = "Delete file";
        BtnFileDeleteCancel.Content = "Cancel";

        FileDeleteOverlay.Visibility = Visibility.Visible;
        FileDeleteOverlay.Focus();
    }

    private void FileDeleteConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingDeletePath is not string path) return;

        var outcome = FileDeleter(path);
        _pendingDeleteConfirmed = true;

        FileDeleteTitle.Text = outcome.Removed ? "File deleted" : "The file was not deleted";
        FileDeleteIcon.Text = outcome.Removed ? "\uE73E" : "\uE7BA";
        FileDeleteMessage.Text = outcome.Message;
        BtnFileDeleteConfirm.Visibility = Visibility.Collapsed;
        BtnFileDeleteCancel.Content = "Close";

        // Only a lock or permission failure has a process worth naming.
        BtnFileDeleteDiagnose.Visibility = outcome.NeedsLockAnalysis ? Visibility.Visible : Visibility.Collapsed;

        if (outcome.Removed) ShowFileTargets(_fileTargetsProcess);
    }

    /// <summary>Runs Handle against the file that could not be deleted and names the holders.</summary>
    private async void FileDeleteDiagnose_Click(object sender, RoutedEventArgs e)
        => await DiagnosePendingDeleteAsync().ConfigureAwait(true);

    internal async Task DiagnosePendingDeleteAsync()
    {
        if (_pendingDeletePath is not string path) return;

        BtnFileDeleteDiagnose.IsEnabled = false;
        FileDeleteLockBorder.Visibility = Visibility.Visible;
        FileDeleteLockTitle.Text = "Checking which processes have this file open...";
        FileDeleteLockList.ItemsSource = null;

        try
        {
            string? tool = HandleLocator();
            if (tool is null)
            {
                // Handle is missing: hand off to the trace dialog, which offers the download.
                FileDeleteLockTitle.Text = "Sysinternals Handle is not installed.";
                FileDeleteOverlay.Visibility = Visibility.Collapsed;
                await TraceFileHandlesAsync(path).ConfigureAwait(true);
                return;
            }

            var result = await PathHandleRunner(tool, path).ConfigureAwait(true);
            if (!result.Success)
            {
                FileDeleteLockTitle.Text = result.Error ?? "Handle did not return any output.";
                return;
            }

            var lockers = HandleOutputParser.FindLockers(result.Output, path);
            FileDeleteLockList.ItemsSource = lockers.Select(ToOwnerRow).ToList();
            FileDeleteLockTitle.Text = DescribeLockers(lockers.Count, result.Elevated);
        }
        finally
        {
            BtnFileDeleteDiagnose.IsEnabled = true;
        }
    }

    /// <summary>Summary line for a lock analysis, including the elevation caveat.</summary>
    internal static string DescribeLockers(int lockerCount, bool elevated)
    {
        if (lockerCount > 0)
        {
            return lockerCount == 1
                ? "1 process currently has this file open:"
                : $"{lockerCount} processes currently have this file open:";
        }

        return elevated
            ? "No process currently holds an open handle to this file, so the failure was not a lock. "
              + "It is most likely a permission or attribute problem."
            : "No open handle was found, but this app is not running as administrator, so handles held by "
              + "other accounts are not visible. Run Disk Activity Monitor as administrator for a complete answer.";
    }

    private static HandleOwnerRow ToOwnerRow(HandleEntry entry)
        => new(
            entry.ProcessName,
            $"PID {entry.ProcessId}",
            string.IsNullOrWhiteSpace(entry.User) ? entry.Name : $"{entry.User}  \u00b7  {entry.Name}");

    private void FileDeleteCancel_Click(object sender, RoutedEventArgs e) => CloseFileDelete();

    private void FileDeleteOverlay_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        e.Handled = true;
        CloseFileDelete();
    }

    private void CloseFileDelete()
    {
        FileDeleteOverlay.Visibility = Visibility.Collapsed;
        if (!_pendingDeleteConfirmed) _pendingDeletePath = null;
        FileTargetsOverlay.Focus();
    }

    // ---------------------------------------------------------------- handle tracing

    /// <summary>Lists every object the drill-down's process currently has open.</summary>
    private async void TraceProcessHandles_Click(object sender, RoutedEventArgs e)
        => await TraceProcessHandlesAsync().ConfigureAwait(true);

    internal async Task TraceProcessHandlesAsync()
    {
        if (string.IsNullOrWhiteSpace(_fileTargetsProcess)) return;

        PrepareHandleOverlay(
            $"Open handles for {_fileTargetsProcess}",
            "Every file, key and object this process currently holds open.",
            HandleTraceMode.Process,
            _fileTargetsProcess);

        await RunHandleTraceAsync().ConfigureAwait(true);
    }

    /// <summary>Finds which processes hold a specific file open.</summary>
    private async Task TraceFileHandlesAsync(string path)
    {
        PrepareHandleOverlay(
            $"Who has {System.IO.Path.GetFileName(path)} open?",
            path,
            HandleTraceMode.File,
            path);

        await RunHandleTraceAsync().ConfigureAwait(true);
    }

    private enum HandleTraceMode { Process, File }

    private HandleTraceMode _handleMode = HandleTraceMode.Process;
    private string _handleTarget = "";

    private void PrepareHandleOverlay(string title, string subtitle, HandleTraceMode mode, string target)
    {
        _handleMode = mode;
        _handleTarget = target;

        HandleTraceTitle.Text = title;
        HandleTraceSubtitle.Text = subtitle;
        HandleTraceOutput.Text = "";
        HandleLockerList.ItemsSource = null;
        HandleInstallPrompt.Visibility = Visibility.Collapsed;
        HandleTraceStatusBorder.Visibility = Visibility.Collapsed;
        HandleTraceFooter.Text = "";

        HandleTraceOverlay.Visibility = Visibility.Visible;
        HandleTraceOverlay.Focus();
    }

    /// <summary>
    /// Runs Handle for the current target, first offering to install it when it is not present on
    /// PATH or beside the database.
    /// </summary>
    private async Task RunHandleTraceAsync()
    {
        string? tool = HandleLocator();
        if (tool is null)
        {
            ShowHandleInstallPrompt();
            return;
        }

        SetHandleStatus("Running Handle...");
        BtnHandleCopy.IsEnabled = false;

        try
        {
            var result = _handleMode == HandleTraceMode.Process
                ? await ProcessHandleRunner(tool, _handleTarget).ConfigureAwait(true)
                : await PathHandleRunner(tool, _handleTarget).ConfigureAwait(true);

            if (!result.Success)
            {
                SetHandleStatus(result.Error ?? "Handle did not return any output.");
                return;
            }

            HandleTraceOutput.Text = result.Output.Trim().Length == 0
                ? "Handle returned no output for this target."
                : result.Output;

            if (_handleMode == HandleTraceMode.File)
            {
                var lockers = HandleOutputParser.FindLockers(result.Output, _handleTarget);
                HandleLockerList.ItemsSource = lockers.Select(ToOwnerRow).ToList();
                SetHandleStatus(DescribeLockers(lockers.Count, result.Elevated));
            }
            else
            {
                var entries = HandleOutputParser.Parse(result.Output);
                int files = entries.Count(x => x.Type.Equals("File", StringComparison.OrdinalIgnoreCase));
                SetHandleStatus($"{entries.Count:N0} open handle(s), {files:N0} of them files."
                    + (result.Elevated ? "" : " Run as administrator to include handles owned by other accounts."));
            }

            HandleTraceFooter.Text = $"Source: {tool}";
        }
        finally
        {
            BtnHandleCopy.IsEnabled = true;
        }
    }

    private void SetHandleStatus(string text)
    {
        HandleTraceStatus.Text = text;
        HandleTraceStatusBorder.Visibility = Visibility.Visible;
    }

    private void ShowHandleInstallPrompt()
    {
        HandleInstallText.Text =
            "Handle is a free Microsoft Sysinternals utility that lists open file handles. It was not found on "
            + $"PATH or in {Paths.BaseDirectory}. Downloading it fetches {HandleTool.DownloadUrl} and stores the "
            + "executable next to the monitoring database. Nothing is installed system-wide.";
        HandleInstallPrompt.Visibility = Visibility.Visible;
    }

    private async void HandleDownload_Click(object sender, RoutedEventArgs e)
        => await DownloadHandleAsync().ConfigureAwait(true);

    internal async Task DownloadHandleAsync()
    {
        BtnHandleDownload.IsEnabled = false;
        SetHandleStatus("Downloading Handle from Sysinternals...");

        try
        {
            await HandleInstaller(CancellationToken.None).ConfigureAwait(true);
            HandleInstallPrompt.Visibility = Visibility.Collapsed;
            await RunHandleTraceAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
                                       or UnauthorizedAccessException or TaskCanceledException)
        {
            SetHandleStatus($"Handle could not be downloaded: {ex.Message}");
        }
        finally
        {
            BtnHandleDownload.IsEnabled = true;
        }
    }

    private void HandleTraceCopy_Click(object sender, RoutedEventArgs e)
    {
        if (HandleTraceOutput.Text.Length == 0) return;

        try
        {
            ClipboardWriter(HandleTraceOutput.Text);
            HandleTraceFooter.Text = "Output copied to the clipboard.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process owns the clipboard; nothing useful to do.
            HandleTraceFooter.Text = "The clipboard is currently unavailable.";
        }
    }

    internal void HandleTraceClose_Click(object sender, RoutedEventArgs e)
    {
        HandleTraceOverlay.Visibility = Visibility.Collapsed;
        FileTargetsOverlay.Focus();
    }

    private void HandleTraceOverlay_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        e.Handled = true;
        HandleTraceClose_Click(sender, e);
    }

    // ---------------------------------------------------------------- database growth

    /// <summary>
    /// Warns once per cooldown when the database exceeds the configured size, notifying through
    /// Windows when the dashboard is not in front of the user.
    /// </summary>
    internal void CheckDatabaseSize()
    {
        if (_databaseCompactionRunning || DatabaseSizeOverlay.Visibility == Visibility.Visible) return;

        var cfg = _config.Current;
        var size = DatabaseMeasurer();
        DateTime now = UtcNowProvider();
        if (!DatabaseMaintenance.ShouldWarn(
            size.TotalBytes, cfg.DatabaseSizeWarnGb, _lastDatabaseWarnUtc, now, cfg.DatabaseSizeAlertCooldownHours))
        {
            return;
        }

        _lastDatabaseWarnUtc = now;
        ShowDatabaseSizeWarning(size, cfg.DatabaseSizeWarnGb);

        if (!(DashboardForegroundOverride?.Invoke() ?? IsDashboardInForeground()))
            ShowDatabaseSizeToast(size, cfg.DatabaseSizeWarnGb);
    }

    /// <summary>True when the dashboard is visible, restored and focused.</summary>
    private bool IsDashboardInForeground()
        => IsDashboardInForeground(IsVisible, WindowState, IsActive);

    internal static bool IsDashboardInForeground(bool isVisible, WindowState windowState, bool isActive)
        => isVisible && windowState != WindowState.Minimized && isActive;

    private void ShowDatabaseSizeWarning(DatabaseSize size, double warnGb)
    {
        DatabaseSizeMessage.Text = FormatDatabaseWarning(size.TotalBytes, warnGb);
        DatabaseSizeDetail.Text = FormatDatabaseDetail(size);
        BtnDatabaseCompact.IsEnabled = true;
        BtnDatabaseCompact.Content = "Compact now";

        DatabaseSizeOverlay.Visibility = Visibility.Visible;
        DatabaseSizeOverlay.Focus();
    }

    internal static string FormatDatabaseWarning(long totalBytes, double warnGb)
        => $"The monitoring database is {ByteFormat.Humanize(totalBytes)}, above your "
           + $"{warnGb.ToString("0.##", CultureInfo.CurrentCulture)} GB threshold. Compacting rebuilds the file and "
           + "returns space freed by expired history.";

    internal static string FormatDatabaseDetail(DatabaseSize size)
        => $"Database {ByteFormat.Humanize(size.MainBytes)}  \u00b7  write-ahead log {ByteFormat.Humanize(size.WalBytes)}"
           + $"  \u00b7  index {ByteFormat.Humanize(size.ShmBytes)}. Compaction can take a while on a large file and "
           + "briefly blocks the collector; no history is lost.";

    /// <summary>Raises a Windows notification when the dashboard is minimised or in the tray.</summary>
    private void ShowDatabaseSizeToast(DatabaseSize size, double warnGb)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddText("Disk Activity Monitor database is large")
                .AddText(FormatDatabaseWarning(size.TotalBytes, warnGb))
                .AddButton(new ToastButton()
                    .SetContent("Compact now")
                    .AddArgument("action", "compact-database"))
                .AddButton(new ToastButton()
                    .SetContent("Dismiss")
                    .AddArgument("action", "dismiss-database-size"));
            DatabaseToastShower(builder);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException
                                       or System.Runtime.InteropServices.COMException)
        {
            // Toast infrastructure is unavailable; the in-app overlay already carries the message.
        }
    }

    private async void DatabaseCompact_Click(object sender, RoutedEventArgs e)
        => await CompactDatabaseAsync().ConfigureAwait(true);

    internal async Task CompactDatabaseAsync()
    {
        if (_databaseCompactionRunning) return;

        _databaseCompactionRunning = true;
        BtnDatabaseCompact.IsEnabled = false;
        BtnDatabaseCompact.Content = "Compacting...";
        DatabaseSizeDetail.Text = "Rebuilding the database. This can take several minutes on a large file.";

        try
        {
            var result = await DatabaseCompactor(_repo).ConfigureAwait(true);

            DatabaseSizeTitle.Text = result.Success ? "Compaction complete" : "Compaction failed";
            DatabaseSizeMessage.Text = FormatCompactionResult(result);
            DatabaseSizeDetail.Text = FormatDatabaseDetail(DatabaseMeasurer());
            BtnDatabaseCompact.Visibility = Visibility.Collapsed;
            BtnDatabaseSizeLater.Content = "Close";
        }
        finally
        {
            _databaseCompactionRunning = false;
            BtnDatabaseCompact.IsEnabled = true;
            BtnDatabaseCompact.Content = "Compact now";
        }
    }

    internal static string FormatCompactionResult(CompactionResult result)
        => result.Success
            ? $"Reclaimed {ByteFormat.Humanize(result.ReclaimedBytes)}. The database is now "
              + $"{ByteFormat.Humanize(result.AfterBytes)}."
            : $"The database could not be compacted: {result.Error}";

    private void DatabaseSizeSettings_Click(object sender, RoutedEventArgs e)
    {
        DatabaseSizeClose_Click(sender, e);
        CloseFileTargets();
        if (SettingsPanel.Visibility != Visibility.Visible) Gear_Click(sender, e);
    }

    private void DatabaseSizeClose_Click(object sender, RoutedEventArgs e)
    {
        DatabaseSizeOverlay.Visibility = Visibility.Collapsed;
        DatabaseSizeTitle.Text = "The monitoring database is large";
        BtnDatabaseCompact.Visibility = Visibility.Visible;
        BtnDatabaseSizeLater.Content = "Not now";
    }

    private void DatabaseSizeOverlay_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        e.Handled = true;
        DatabaseSizeClose_Click(sender, e);
    }

    // ---------------------------------------------------------------- settings helpers

    /// <summary>Shows the live database size beside the threshold field.</summary>
    private void UpdateDatabaseSizeCaption()
    {
        var size = DatabaseMeasurer();
        TxtDatabaseSizeCurrent.Text = $"Currently {ByteFormat.Humanize(size.TotalBytes)} on disk.";
    }

    /// <summary>
    /// Cleans a user-typed extension list into the canonical semicolon-separated form. Returns
    /// <paramref name="fallback"/> when the field was cleared, so an empty box cannot silently
    /// make every file tailable.
    /// </summary>
    internal static string NormalizeExtensionList(string? text, string fallback)
    {
        var parsed = BinaryExtensionPolicy.Parse(text);
        if (parsed.Count == 0) return fallback;

        return string.Join(';', parsed.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    private void RestoreBinaryExtensions_Click(object sender, RoutedEventArgs e)
        => TxtBinaryExtensions.Text = Core.Configuration.AppConfig.DefaultBinaryExtensions;

    /// <summary>Runs compaction on demand from the settings page.</summary>
    private void DatabaseCompactFromSettings_Click(object sender, RoutedEventArgs e)
    {
        var size = DatabaseMeasurer();
        DatabaseSizeTitle.Text = "Compact the monitoring database";
        DatabaseSizeMessage.Text =
            $"The database is {ByteFormat.Humanize(size.TotalBytes)}. Compacting rebuilds the file and returns "
            + "space freed by expired history.";
        DatabaseSizeDetail.Text = FormatDatabaseDetail(size);
        BtnDatabaseCompact.Visibility = Visibility.Visible;
        BtnDatabaseCompact.IsEnabled = true;
        BtnDatabaseCompact.Content = "Compact now";
        BtnDatabaseSizeLater.Content = "Not now";

        DatabaseSizeOverlay.Visibility = Visibility.Visible;
        DatabaseSizeOverlay.Focus();
    }
}
