using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DiskActivityMonitor.Core;
using DiskActivityMonitor.Core.Ai;
using DiskActivityMonitor.Core.Collection;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Models;
using DiskActivityMonitor.Tray.Controls;
using Microsoft.Web.WebView2.Core;
using DispatcherTimer = System.Windows.Threading.DispatcherTimer;

namespace DiskActivityMonitor.Tray;

public partial class MainWindow : Window
{
    private readonly MonitorRepository _repo;
    private readonly ConfigStore _config;
    private readonly UserSettingsStore _userSettings;
    private readonly DispatcherTimer _refreshTimer;
    private bool _forceClose;
    private bool _helpInitialized;
    private Uri? _helpDocumentUri;

    // Rated-TBW web lookup (on-device Foundry Local model + web search).
    private TbwLookupService? _tbwLookup;
    private CancellationTokenSource? _tbwCts;
    private bool _tbwLookupForceRequested;
    internal Func<IProgress<int>?, CancellationToken, Task> TbwModelDownloader = null!;
    internal Action<string> TbwSetupUrlLauncher { get; set; } = url =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    internal Action<AiSecrets> TbwSetupSecretsSaver { get; set; } = AiSecretsStore.Save;

    private enum RangeKind { H24, D30, W12 }
    private RangeKind _range = RangeKind.H24;

    private sealed record DiskChoice(DiskInfo Disk, string Display);
    private sealed record ProcessRow(string Name, string WriteText, string ReadText, double BarWidth);
    private sealed record AlertRow(
        string Title,
        string Message,
        string TimeText,
        Brush SeverityBrush,
        string? DiskId,
        int ControllerErrorCount,
        bool CanRunSmartScan,
        Visibility SmartActionVisibility,
        long[] AlertIds);
    private sealed record AlertHistoryRow(
        long Id,
        string Title,
        string Message,
        string TimeText,
        Brush SeverityBrush,
        string StatusText,
        Brush StatusBrush,
        Visibility DismissVisibility,
        Visibility RestoreVisibility);

    private DiskInfo? _smartScanDisk;
    private int _smartScanControllerErrors;
    private int _smartScanGeneration;
    internal Func<DiskInfo, int, IProgress<string>?, SmartHealthScanResult> SmartScanner { get; set; }
        = (disk, errors, progress) => SmartHealthScanner.Scan(disk, errors, progress);

    /// <summary>Alerts are shown once and treated as a timestamped log; only those raised within
    /// this trailing window are surfaced (older ones age out automatically rather than persisting
    /// until dismissed).</summary>
    private static readonly TimeSpan RecentAlertWindow = TimeSpan.FromHours(1);

    /// <summary>A selectable rolling window for the "Top writing processes" list.</summary>
    private sealed record ProcRange(string Label, TimeSpan Span);

    private static readonly ProcRange[] ProcessRanges =
    {
        new("Last minute", TimeSpan.FromMinutes(1)),
        new("Last 5 minutes", TimeSpan.FromMinutes(5)),
        new("Last 15 minutes", TimeSpan.FromMinutes(15)),
        new("Last 30 minutes", TimeSpan.FromMinutes(30)),
        new("Last hour", TimeSpan.FromHours(1)),
        new("Last 3 hours", TimeSpan.FromHours(3)),
        new("Last 6 hours", TimeSpan.FromHours(6)),
        new("Last 12 hours", TimeSpan.FromHours(12)),
        new("Last 24 hours", TimeSpan.FromHours(24)),
        new("Past week", TimeSpan.FromDays(7)),
        new("Past 2 weeks", TimeSpan.FromDays(14)),
        new("Past month", TimeSpan.FromDays(30)),
        new("Past 6 months", TimeSpan.FromDays(182)),
        new("Past year", TimeSpan.FromDays(365)),
    };

    private TimeSpan _processWindow = TimeSpan.FromHours(24);
    private TimeSpan _throughputWindow = TimeSpan.FromHours(24);

    /// <summary>Editable view-model for one auto-suspend rule row.</summary>
    private sealed class SuspendRuleVm
    {
        public string ProcessName { get; set; } = "";
        public string ThresholdText { get; set; } = "5";
        public bool IsAuto { get; set; }
        public bool Enabled { get; set; } = true;
        public string? ExecutablePath { get; set; }
        public bool CanAuto => !string.IsNullOrWhiteSpace(ExecutablePath);
    }

    private sealed record SuspendedRow(string Name, string Display);

    private readonly ObservableCollection<SuspendRuleVm> _suspendRules = new();

    private static readonly Brush CriticalBrush = Frozen(0xE0, 0x4A, 0x4A);
    private static readonly Brush WarningBrush = Frozen(0xF0, 0xA0, 0x20);
    private static readonly Brush InfoBrush = Frozen(0x3F, 0xB9, 0x50);

    public MainWindow(MonitorRepository repo, ConfigStore config, UserSettingsStore userSettings)
    {
        _repo = repo;
        _config = config;
        _userSettings = userSettings;
        TbwModelDownloader = (progress, ct) => TbwLookup.DownloadModelAsync(progress, ct);
        InitializeComponent();

        // Taskbar/window icon uses the exact same glyph as the system-tray icon.
        Icon = TrayIconFactory.CreateImageSource(TrayIconFactory.Ok);

        Btn24h.IsChecked = true;
        TpBtn24h.IsChecked = true;
        ProcessRangeSelector.ItemsSource = ProcessRanges;
        ProcessRangeSelector.SelectedItem = ProcessRanges.First(r => r.Span == _processWindow);
        TrendTimeZoneText.Text = LocalTimeDisplay.ZoneLabel();
        SuspendRuleList.ItemsSource = _suspendRules;
        _suspendRules.CollectionChanged += (_, _) =>
            SuspendRuleEmpty.Visibility = _suspendRules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LoadSuspendRules();
        LoadDisks();
        LoadSettingsFields();
        RefreshAll();

        _refreshTimer = new DispatcherTimer();
        _refreshTimer.Tick += (_, _) => RefreshAll();
        ApplyRefreshInterval();
        _refreshTimer.Start();
    }

    /// <summary>Sets the auto-refresh timer interval from the configured dashboard refresh seconds.</summary>
    private void ApplyRefreshInterval()
    {
        int secs = Math.Clamp(_config.Current.DashboardRefreshSeconds, 1, 600);
        _refreshTimer.Interval = TimeSpan.FromSeconds(secs);
    }

    // ----------------------------------------------------------- Window lifecycle

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        RefreshAll();
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing just hides the dashboard; the tray icon keeps the app alive.
        if (!_forceClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    // ----------------------------------------------------------- Data loading

    private void LoadDisks()
    {
        var disks = _repo.GetDisks();
        var choices = disks
            .OrderByDescending(d => d.IsSsd)
            .ThenBy(d => d.DiskId)
            .Select(d => new DiskChoice(d, $"{d.DisplayName}  -  {MediaTag(d)}"))
            .ToList();

        var previous = (DiskSelector.SelectedItem as DiskChoice)?.Disk.DiskId;
        DiskSelector.ItemsSource = choices;

        if (choices.Count == 0)
        {
            SubtitleText.Text = "No disks detected yet - start the collector service.";
            return;
        }

        var keep = choices.FirstOrDefault(c => c.Disk.DiskId == previous);
        DiskSelector.SelectedItem = keep ?? choices.First();
    }

    private static string MediaTag(DiskInfo d) => d.MediaType switch
    {
        DiskMediaType.Ssd => "SSD",
        DiskMediaType.Scm => "Optane/SCM",
        DiskMediaType.Hdd => "HDD",
        _ => "unknown media",
    };

    private DiskInfo? SelectedDisk => (DiskSelector.SelectedItem as DiskChoice)?.Disk;

    private void RefreshAll()
    {
        var disk = SelectedDisk;
        if (disk is null) return;

        UpdateSummary(disk);
        UpdateChart(disk);
        UpdateThroughput(disk);
        UpdateProcesses();
        UpdateAlerts();
        RefreshSuspended();
    }

    // ----------------------------------------------------------- Compiled help

    private async void Help_Click(object sender, RoutedEventArgs e)
    {
        HelpOverlay.Visibility = Visibility.Visible;
        HelpOverlay.Focus();
        if (_helpInitialized) return;

        _helpInitialized = true;
        HelpLoadingPanel.Visibility = Visibility.Visible;
        HelpFallbackPanel.Visibility = Visibility.Collapsed;
        HelpWebView.Visibility = Visibility.Collapsed;

        string htmlPath = Path.Combine(AppContext.BaseDirectory, "HELP.html");
        string markdownPath = Path.Combine(AppContext.BaseDirectory, "HELP.md");
        try
        {
            if (!File.Exists(htmlPath))
            {
                ShowHelpFallback("The generated HELP.html file was not found. Rebuild the tray project with pandoc installed.", markdownPath);
                return;
            }

            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DiskActivityMonitor", "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await HelpWebView.EnsureCoreWebView2Async(environment);
            HelpWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            HelpWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            HelpWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            HelpWebView.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            HelpWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x15, 0x18, 0x1B);
            _helpDocumentUri = new Uri(htmlPath);
            HelpWebView.Source = _helpDocumentUri;
        }
        catch (Exception ex)
        {
            ShowHelpFallback($"WebView2 could not render compiled help: {ex.Message}", markdownPath);
        }
    }

    private void HelpClose_Click(object sender, RoutedEventArgs e)
        => HelpOverlay.Visibility = Visibility.Collapsed;

    private void HelpOverlay_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        e.Handled = true;
        HelpOverlay.Visibility = Visibility.Collapsed;
    }

    private void HelpWebView_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_helpDocumentUri is not null && Uri.TryCreate(e.Uri, UriKind.Absolute, out var target)
            && target.IsFile
            && string.Equals(target.LocalPath, _helpDocumentUri.LocalPath, StringComparison.OrdinalIgnoreCase))
            return;
        e.Cancel = true;
        if (!IsAllowedExternalHelpUri(e.Uri)) return;
        try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); }
        catch { /* an external-link failure should not close help */ }
    }

    internal static bool IsAllowedExternalHelpUri(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private void HelpWebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        HelpLoadingPanel.Visibility = Visibility.Collapsed;
        if (e.IsSuccess)
        {
            HelpFallbackPanel.Visibility = Visibility.Collapsed;
            HelpWebView.Visibility = Visibility.Visible;
        }
        else
        {
            ShowHelpFallback($"Compiled help navigation failed ({e.WebErrorStatus}).",
                Path.Combine(AppContext.BaseDirectory, "HELP.md"));
        }
    }

    internal void ShowHelpFallback(string message, string markdownPath)
    {
        HelpLoadingPanel.Visibility = Visibility.Collapsed;
        HelpWebView.Visibility = Visibility.Collapsed;
        HelpFallbackMessage.Text = message;
        HelpFallbackText.Text = File.Exists(markdownPath)
            ? MarkdownToPlainText(File.ReadAllText(markdownPath))
            : "The fallback HELP.md file was also not found.";
        HelpFallbackPanel.Visibility = Visibility.Visible;
    }

    internal static string MarkdownToPlainText(string markdown)
    {
        string text = Regex.Replace(markdown ?? string.Empty, @"```[a-zA-Z0-9_-]*\r?\n([\s\S]*?)```", "$1");
        text = Regex.Replace(text, @"!\[([^\]]*)\]\([^)]*\)", "$1");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");
        text = Regex.Replace(text, @"^\s{0,3}#{1,6}\s*", string.Empty, RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s*>\s?", string.Empty, RegexOptions.Multiline);
        text = Regex.Replace(text, @"\|?\s*:?-{3,}:?\s*(?=\||$)", string.Empty, RegexOptions.Multiline);
        text = Regex.Replace(text, @"[*_~`]", string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\r?\n{3,}", Environment.NewLine + Environment.NewLine);
        return text.Trim();
    }

    // ----------------------------------------------------------- Throughput (MB/s) stats

    private void ThroughputRange_Click(object sender, RoutedEventArgs e)
    {
        _throughputWindow =
            sender == TpBtn1h ? TimeSpan.FromHours(1) :
            sender == TpBtn7d ? TimeSpan.FromDays(7) :
            sender == TpBtn30d ? TimeSpan.FromDays(30) :
            TimeSpan.FromHours(24);
        TpBtn1h.IsChecked = sender == TpBtn1h;
        TpBtn24h.IsChecked = sender == TpBtn24h;
        TpBtn7d.IsChecked = sender == TpBtn7d;
        TpBtn30d.IsChecked = sender == TpBtn30d;
        var disk = SelectedDisk;
        if (disk is not null) UpdateThroughput(disk);
    }

    /// <summary>Computes and renders average / median / peak I/O throughput (MB/s) for the selected window.</summary>
    private void UpdateThroughput(DiskInfo disk)
    {
        var nowUtc = DateTime.UtcNow;
        var perMinute = _repo.GetDiskMinuteTotals(disk.DiskId, nowUtc - _throughputWindow, nowUtc);
        int totalMinutes = (int)Math.Round(_throughputWindow.TotalMinutes);
        var stats = ThroughputStats.Compute(perMinute, totalMinutes);

        ThroughputAvg.Text = FormatMbps(stats.AverageMbps);
        ThroughputMedian.Text = FormatMbps(stats.MedianMbps);
        ThroughputPeak.Text = FormatMbps(stats.PeakMbps);

        var bars = new List<ChartBar>
        {
            new("Average", stats.AverageMbps),
            new("Median", stats.MedianMbps),
            new("Peak", stats.PeakMbps, Highlight: true),
        };
        ThroughputChart.SetData(bars, v => $"{FormatMbps(v)} MB/s");
    }

    private static string FormatMbps(double mbps) => mbps switch
    {
        >= 100 => mbps.ToString("0"),
        >= 10 => mbps.ToString("0.#"),
        > 0 => mbps.ToString("0.##"),
        _ => "0",
    };

    private void UpdateSummary(DiskInfo disk)
    {
        var nowUtc = DateTime.UtcNow;
        var midnightUtc = DateTime.Today.ToUniversalTime();

        var today = _repo.GetDiskTotals(disk.DiskId, midnightUtc, nowUtc);
        var day24 = _repo.GetDiskTotals(disk.DiskId, nowUtc.AddHours(-24), nowUtc);
        var week7 = _repo.GetDiskTotals(disk.DiskId, nowUtc.AddDays(-7), nowUtc);

        TodayMetric.Text = ByteFormat.Humanize(today.Write);
        TodayReadSub.Text = $"read {ByteFormat.Humanize(today.Read)}";
        Day24Metric.Text = ByteFormat.Humanize(day24.Write);
        Day24ReadSub.Text = $"read {ByteFormat.Humanize(day24.Read)}";
        Week7Metric.Text = ByteFormat.Humanize(week7.Write);
        Week7AvgSub.Text = $"avg {ByteFormat.Humanize(week7.Write / 7.0)}/day";

        UpdateEndurance(disk, nowUtc, week7.Write);
    }

    private void UpdateEndurance(DiskInfo disk, DateTime nowUtc, long week7Write)
    {
        var cfg = _config.Current;
        var earliest = _repo.GetEarliestSample(disk.DiskId);
        double observedBytes = 0;
        double daysMonitored = 0;
        if (earliest is not null)
        {
            observedBytes = _repo.GetDiskTotals(disk.DiskId, earliest.Value, nowUtc).Write;
            daysMonitored = Math.Max((nowUtc - earliest.Value).TotalDays, 1.0 / 24);
        }

        // Recent average write rate (trailing 7 days once we have them, else the whole history).
        double avgPerDay = daysMonitored >= 7
            ? week7Write / 7.0
            : (daysMonitored > 0 ? observedBytes / daysMonitored : 0);
        double avgPerHour = avgPerDay / 24.0;
        double avgPerWeek = avgPerDay * 7.0;

        double tbwLow = cfg.EffectiveTbw(disk.DiskId);
        double? tbwHigh = cfg.EffectiveTbwUpper(disk.DiskId);
        bool ranged = tbwHigh.HasValue;
        double tbwLowBytes = tbwLow * 1_000_000_000_000d;            // TBW specs use decimal terabytes.
        double tbwHighBytes = (tbwHigh ?? tbwLow) * 1_000_000_000_000d;
        string tbwLabel = ranged ? $"{tbwLow:0.#}\u2013{tbwHigh:0.#} TBW" : $"{tbwLow:0.#} TBW";

        // Prefer the drive's own lifetime-written total (from SMART) over what we've observed.
        long? lifeWritten = disk.LifetimeBytesWritten;
        double consumedBytes = lifeWritten ?? observedBytes;

        // % of TBW consumed: a lower rating yields a higher %, so the range is [low%@highTBW .. high%@lowTBW].
        double pctHigh = tbwLowBytes > 0 ? consumedBytes / tbwLowBytes * 100.0 : 0;
        double pctLow = tbwHighBytes > 0 ? consumedBytes / tbwHighBytes * 100.0 : pctHigh;

        // Years to reach TBW at the recent rate: a higher rating yields more years.
        double yearsLow = avgPerDay > 0 ? Math.Max(tbwLowBytes - (lifeWritten ?? 0), tbwLowBytes * 0.001) / (avgPerDay * 365.0) : double.NaN;
        double yearsHigh = avgPerDay > 0 ? Math.Max(tbwHighBytes - (lifeWritten ?? 0), tbwHighBytes * 0.001) / (avgPerDay * 365.0) : double.NaN;
        string yearsText;
        if (double.IsNaN(yearsLow)) yearsText = "-";
        else if (!ranged) yearsText = $"{FormatYearsShort(yearsLow)} yrs";
        else if (yearsLow >= 100 && yearsHigh >= 100) yearsText = "100+ yrs";
        else yearsText = $"{FormatYearsShort(yearsLow)} to {FormatYearsShort(yearsHigh)} yrs";

        // Summary card (glanceable projection).
        if (avgPerDay > 0 && !double.IsNaN(yearsLow))
        {
            WearMetric.Text = yearsText;
            WearSub.Text = $"to {tbwLabel} at {ByteFormat.Humanize(avgPerDay)}/day";
        }
        else
        {
            WearMetric.Text = "-";
            WearSub.Text = $"Rated {tbwLabel}. Collecting data to project lifespan.";
        }

        // Endurance panel.
        EnduranceDiskText.Text = disk.DisplayName;
        EnduranceRatedText.Text = $"{tbwLabel} rated";

        // Headline 1: precise lifetime writes / configured TBW when available. Drive SMART wear is
        // typically reported only as a whole percentage, so retain it as supporting context rather
        // than rounding away the more precise lifetime-write calculation.
        if (lifeWritten is not null && tbwLowBytes > 0)
        {
            double fill = Math.Clamp(pctHigh, 0, 100);
            SmartWearValue.Text = ranged
                ? $"~{FormatPercent(pctLow)}\u2013{FormatPercent(pctHigh)}"
                : $"~{FormatPercent(pctHigh)}";
            SmartWearFillCol.Width = new GridLength(fill, GridUnitType.Star);
            SmartWearRestCol.Width = new GridLength(100 - fill, GridUnitType.Star);
            string smartContext = disk.WearPercent is int wear
                ? $"; drive SMART reports {wear}% used (whole-percent precision)"
                : "; this drive reports no SMART wear attribute";
            SmartWearText.Text = $"estimated from lifetime writes \u00f7 {tbwLabel}{smartContext}";
        }
        else if (disk.WearPercent is int wear)
        {
            double wc = Math.Clamp(wear, 0, 100);
            SmartWearValue.Text = FormatPercent(wear);
            SmartWearFillCol.Width = new GridLength(wc, GridUnitType.Star);
            SmartWearRestCol.Width = new GridLength(100 - wc, GridUnitType.Star);
            SmartWearText.Text = $"{100 - wear}% endurance remaining, from the drive's SMART data (whole-percent precision)";
        }
        else
        {
            SmartWearValue.Text = "N/A";
            SmartWearFillCol.Width = new GridLength(0, GridUnitType.Star);
            SmartWearRestCol.Width = new GridLength(100, GridUnitType.Star);
            SmartWearText.Text = "This drive reports no SMART endurance data (common on USB/RAID/virtual disks), or the collector isn't elevated.";
        }

        // Total lifetime data written, straight from SMART (shown beneath the wear bar).
        if (lifeWritten is long lifeWrittenBytes)
        {
            string readPart = disk.LifetimeBytesRead is long lr ? $" \u00b7 {ByteFormat.Humanize(lr)} read" : "";
            SmartWearLifeText.Text = $"{ByteFormat.Humanize(lifeWrittenBytes)} written total{readPart}";
            SmartWearLifeText.Visibility = Visibility.Visible;
        }
        else
        {
            SmartWearLifeText.Text = "";
            SmartWearLifeText.Visibility = Visibility.Collapsed;
        }

        // Headline 2: projected lifespan at the recent average write rate.
        if (avgPerDay > 0 && !double.IsNaN(yearsLow))
        {
            EnduranceProjValue.Text = yearsText;
            EnduranceProjSub.Text = ranged
                ? $"reaches {tbwLabel} at {ByteFormat.Humanize(avgPerDay)}/day"
                : $"reaches {tbwLabel} about {DateTime.Now.AddDays(Math.Min(yearsLow, 5000) * 365.0):MMM yyyy}, at {ByteFormat.Humanize(avgPerDay)}/day";
        }
        else
        {
            EnduranceProjValue.Text = "-";
            EnduranceProjSub.Text = "collecting data to project a lifespan";
        }

        // Averages.
        EnduranceAvgHour.Text = avgPerDay > 0 ? ByteFormat.Humanize(avgPerHour) : "-";
        EnduranceAvgDay.Text = avgPerDay > 0 ? ByteFormat.Humanize(avgPerDay) : "-";
        EnduranceAvgWeek.Text = avgPerDay > 0 ? ByteFormat.Humanize(avgPerWeek) : "-";

        // Footnote: the drive's true lifetime writes (when available) plus our observed history.
        string lifeLine = "";
        if (lifeWritten is long lw)
        {
            string readPart = disk.LifetimeBytesRead is long lr ? $", {ByteFormat.Humanize(lr)} read" : "";
            string pctText = ranged ? $"{FormatPercent(pctLow)} to {FormatPercent(pctHigh)}" : FormatPercent(pctHigh);
            lifeLine = $"Lifetime (from the drive): {ByteFormat.Humanize(lw)} written{readPart} \u2014 {pctText} of {tbwLabel}. ";
        }
        string sinceLine = earliest is null
            ? "No history recorded yet."
            : $"Recorded by this app since {LocalTimeDisplay.FormatUtcWithZone(earliest.Value, "MMM d")}: {ByteFormat.Humanize(observedBytes)} written.";
        EnduranceConsumedText.Text = lifeLine + sinceLine;
    }

    /// <summary>Formats a years value without a unit: "100+", whole numbers above 10, else one decimal.</summary>
    private static string FormatYearsShort(double y)
        => double.IsNaN(y) ? "-" : y >= 100 ? "100+" : y >= 10 ? $"{y:0}" : $"{y:0.0}";

    internal static string FormatPercent(double value) => $"{value:0.##}%";

    private void UpdateChart(DiskInfo disk)
    {
        var nowUtc = DateTime.UtcNow;
        Trends.Bucket bucket;
        int count;
        DateTime fromUtc;
        switch (_range)
        {
            case RangeKind.D30:
                bucket = Trends.Bucket.Day; count = 30; fromUtc = nowUtc.AddDays(-31);
                TrendTitle.Text = "Write volume per day";
                break;
            case RangeKind.W12:
                bucket = Trends.Bucket.Week; count = 12; fromUtc = nowUtc.AddDays(-7 * 13);
                TrendTitle.Text = "Write volume per week";
                break;
            default:
                bucket = Trends.Bucket.Hour; count = 24; fromUtc = nowUtc.AddHours(-25);
                TrendTitle.Text = "Write volume per hour";
                break;
        }

        var hourly = _repo.GetHourlyDiskTotals(disk.DiskId, fromUtc, nowUtc);
        var buckets = Trends.Build(hourly, bucket, count, DateTime.Now);
        var bars = new List<ChartBar>(buckets.Count);
        for (int i = 0; i < buckets.Count; i++)
            bars.Add(new ChartBar(Trends.Label(buckets[i].BucketStartLocal, bucket), buckets[i].WriteBytes, i == buckets.Count - 1));

        Chart.SetData(bars, ByteFormat.Humanize);
    }

    private void UpdateProcesses()
    {
        var nowUtc = DateTime.UtcNow;
        // Per-process data is bucketed per minute; align to the last completed minute so short
        // windows (e.g. "Last minute") read the most recent full bucket instead of the partial one.
        var endUtc = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, nowUtc.Minute, 0, DateTimeKind.Utc);
        var top = _repo.GetTopProcesses(endUtc - _processWindow, endUtc, topN: 8);
        const double barArea = 200;
        double max = top.Count > 0 ? Math.Max(1, top.Max(p => p.WriteBytes)) : 1;

        ProcessList.ItemsSource = top
            .Select(p => new ProcessRow(
                p.ProcessName,
                ByteFormat.Humanize(p.WriteBytes),
                ByteFormat.Humanize(p.ReadBytes),
                Math.Max(2, p.WriteBytes / max * barArea)))
            .ToList();

        ProcessEmpty.Visibility = top.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void UpdateAlerts()
    {
        // The main Alert center shows only non-dismissed alerts from the trailing window. Dismissed
        // records stay in the database and remain available through the complete alert history.
        var alerts = _repo.GetRecentAlerts(200, unacknowledgedOnly: true, sinceUtc: DateTime.UtcNow - RecentAlertWindow);

        // The alert engine re-raises the same rule every cooldown period while a condition stays
        // tripped, so collapse repeats: show one row per rule (its latest occurrence) with a count.
        var rows = alerts
            .GroupBy(a => a.RuleKey)
            .OrderByDescending(g => g.Max(a => a.Id))
            .Select(g =>
            {
                var latest = g.OrderByDescending(a => a.Id).First();
                int count = g.Count();
                var time = LocalTimeDisplay.FormatUtc(latest.TimestampUtc, "MMM d, HH:mm");
                const string controllerPrefix = "disk-controller:";
                bool canScan = latest.RuleKey.StartsWith(controllerPrefix, StringComparison.OrdinalIgnoreCase);
                string? diskId = canScan ? latest.RuleKey[controllerPrefix.Length..] : null;
                int controllerErrors = canScan
                    ? (int)Math.Clamp(Math.Round(latest.Value), 0, int.MaxValue)
                    : 0;
                return new AlertRow(
                    latest.Title,
                    latest.Message,
                    $"{(count > 1 ? $"{time}  \u00b7  \u00d7{count} since {LocalTimeDisplay.FormatUtc(g.Min(a => a.TimestampUtc), "HH:mm")}" : time)} ({LocalTimeDisplay.ZoneId()})",
                    latest.Severity switch
                    {
                        AlertSeverity.Critical => CriticalBrush,
                        AlertSeverity.Warning => WarningBrush,
                        _ => InfoBrush,
                    },
                    diskId,
                    controllerErrors,
                    canScan,
                        canScan ? Visibility.Visible : Visibility.Collapsed,
                        g.Select(a => a.Id).ToArray());
            })
            .ToList();

        AlertList.ItemsSource = rows;
        AlertEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void UpdateAlertHistory()
    {
        var alerts = _repo.GetRecentAlerts(int.MaxValue);
        var rows = alerts.Select(a => new AlertHistoryRow(
            a.Id,
            a.Title,
            a.Message,
            LocalTimeDisplay.FormatUtcWithZone(a.TimestampUtc, "MMM d, yyyy  HH:mm"),
            a.Severity switch
            {
                AlertSeverity.Critical => CriticalBrush,
                AlertSeverity.Warning => WarningBrush,
                _ => InfoBrush,
            },
            a.Acknowledged ? "Dismissed" : "Not dismissed",
            a.Acknowledged ? Brushes.Gray : InfoBrush,
            a.Acknowledged ? Visibility.Collapsed : Visibility.Visible,
            a.Acknowledged ? Visibility.Visible : Visibility.Collapsed))
            .ToList();

        AlertHistoryList.ItemsSource = rows;
        int dismissed = alerts.Count(a => a.Acknowledged);
        AlertHistorySummary.Text = $"{alerts.Count:N0} alert{(alerts.Count == 1 ? "" : "s")}  \u00b7  {dismissed:N0} dismissed";
        AlertHistoryEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowAllAlerts_Click(object sender, RoutedEventArgs e)
    {
        UpdateAlertHistory();
        AlertHistoryOverlay.Visibility = Visibility.Visible;
    }

    private void AlertHistoryClose_Click(object sender, RoutedEventArgs e)
        => AlertHistoryOverlay.Visibility = Visibility.Collapsed;

    private void DismissAlert_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as System.Windows.Controls.Button;
        var menuItem = sender as System.Windows.Controls.MenuItem;
        var row = button?.CommandParameter as AlertRow ?? button?.DataContext as AlertRow
            ?? menuItem?.CommandParameter as AlertRow ?? menuItem?.DataContext as AlertRow;
        if (row is null || row.AlertIds.Length == 0) return;

        _repo.DismissAlerts(row.AlertIds);
        UpdateAlerts();
        if (AlertHistoryOverlay.Visibility == Visibility.Visible) UpdateAlertHistory();
    }

    private void DismissHistoryAlert_Click(object sender, RoutedEventArgs e)
    {
        var row = (sender as System.Windows.Controls.Button)?.CommandParameter as AlertHistoryRow;
        if (row is null) return;
        _repo.DismissAlerts([row.Id]);
        UpdateAlerts();
        UpdateAlertHistory();
    }

    private void RestoreHistoryAlert_Click(object sender, RoutedEventArgs e)
    {
        var row = (sender as System.Windows.Controls.Button)?.CommandParameter as AlertHistoryRow;
        if (row is null) return;
        _repo.RestoreAlerts([row.Id]);
        UpdateAlerts();
        UpdateAlertHistory();
    }

    // ----------------------------------------------------------- Live SMART scan

    private async void RunSmartScan_Click(object sender, RoutedEventArgs e)
    {
        var menuItem = sender as System.Windows.Controls.MenuItem;
        var row = menuItem?.CommandParameter as AlertRow ?? menuItem?.DataContext as AlertRow;
        if (row is not { CanRunSmartScan: true } || string.IsNullOrWhiteSpace(row.DiskId))
            return;

        var disk = _repo.GetDisks().FirstOrDefault(d =>
            string.Equals(d.DiskId, row.DiskId, StringComparison.OrdinalIgnoreCase)) ?? new DiskInfo
        {
            DiskId = row.DiskId,
            InstanceName = row.DiskId,
            FriendlyName = $"Physical disk {row.DiskId}",
        };

        await StartSmartScanAsync(disk, row.ControllerErrorCount);
    }

    internal async Task StartSmartScanAsync(DiskInfo disk, int controllerErrorCount)
    {
        _smartScanDisk = disk;
        _smartScanControllerErrors = Math.Max(0, controllerErrorCount);
        int generation = ++_smartScanGeneration;

        SmartScanTargetText.Text = $"{disk.DisplayName}  \u00b7  PhysicalDrive{disk.DiskId}";
        SmartScanProgressText.Text = "Preparing scan...";
        SmartScanProgressPanel.Visibility = Visibility.Visible;
        SmartScanResultPanel.Visibility = Visibility.Collapsed;
        SmartScanAgainButton.Visibility = Visibility.Collapsed;
        SmartScanOverlay.Visibility = Visibility.Visible;

        var progress = new Progress<string>(step =>
        {
            if (generation == _smartScanGeneration)
                SmartScanProgressText.Text = step;
        });

        try
        {
            var result = await Task.Run(() => SmartScanner(disk, controllerErrorCount, progress));
            if (generation != _smartScanGeneration) return;
            RenderSmartScanResult(result);
        }
        catch (Exception ex)
        {
            if (generation != _smartScanGeneration) return;
            RenderSmartScanFailure(disk, ex.Message);
        }
    }

    internal void RenderSmartScanResult(SmartHealthScanResult result)
    {
        var (background, foreground, glyph) = result.Grade switch
        {
            SmartScanGrade.Critical => (Frozen(0x49, 0x22, 0x27), CriticalBrush, "\u2715"),
            SmartScanGrade.Attention => (Frozen(0x4A, 0x36, 0x1C), WarningBrush, "!"),
            SmartScanGrade.Healthy => (Frozen(0x1D, 0x3D, 0x2A), InfoBrush, "\u2713"),
            SmartScanGrade.Limited => (Frozen(0x23, 0x31, 0x43), Frozen(0x64, 0xA7, 0xFF), "i"),
            _ => (Frozen(0x31, 0x34, 0x39), Frozen(0x9A, 0xA0, 0xA8), "?"),
        };

        SmartScanStatusBorder.Background = background;
        SmartScanStatusGlyph.Foreground = foreground;
        SmartScanStatusGlyph.Text = glyph;
        SmartScanStatusTitle.Text = result.Headline;
        SmartScanStatusText.Text = result.Summary;

        SmartScanWindowsValue.Text = result.WindowsHealth;
        SmartScanWindowsValue.Foreground = result.Grade == SmartScanGrade.Critical ? CriticalBrush : InfoBrush;
        SmartScanWindowsSub.Text = result.OperationalStatus;

        SmartScanTelemetryValue.Text = result.SmartTelemetryAvailable ? "Available" : "Limited";
        SmartScanTelemetryValue.Foreground = result.SmartTelemetryAvailable ? InfoBrush : WarningBrush;
        SmartScanTelemetrySub.Text = result.SmartAccess;

        SmartScanConnectionValue.Text = result.ControllerErrorCount > 0
            ? $"{result.ControllerErrorCount:N0} errors"
            : "No alert";
        SmartScanConnectionValue.Foreground = result.ControllerErrorCount > 0 ? WarningBrush : InfoBrush;
        SmartScanConnectionSub.Text = result.ControllerErrorCount > 0 ? "Disk event 11 window" : "No controller-error context";

        SmartScanTempValue.Text = result.TemperatureC is int temperature ? $"{temperature}\u00B0C" : "Not exposed";
        SmartScanTempValue.Foreground = result.TemperatureC is >= 60 ? CriticalBrush
            : result.TemperatureC is >= 50 ? WarningBrush
            : result.TemperatureC is not null ? InfoBrush
            : Frozen(0x9A, 0xA0, 0xA8);
        SmartScanTempSub.Text = result.TemperatureMaxC is int maximum ? $"maximum {maximum}\u00B0C" : result.BusType;

        SmartScanModelValue.Text = BlankAsDash(result.Model);
        SmartScanFirmwareValue.Text = BlankAsDash(result.FirmwareVersion);
        SmartScanSerialValue.Text = BlankAsDash(result.SerialNumber);
        SmartScanPathValue.Text = result.DevicePath;
        SmartScanLifetimeValue.Text = FormatLifetime(result.LifetimeBytesWritten, result.LifetimeBytesRead);
        SmartScanTimeValue.Text = LocalTimeDisplay.FormatUtcWithZone(result.ScannedUtc, "HH:mm:ss");
        SmartScanFindings.ItemsSource = result.Findings;

        SmartScanProgressPanel.Visibility = Visibility.Collapsed;
        SmartScanResultPanel.Visibility = Visibility.Visible;
        SmartScanResultPanel.ScrollToTop();
        SmartScanAgainButton.Visibility = Visibility.Visible;
    }

    internal void RenderSmartScanFailure(DiskInfo disk, string message)
    {
        RenderSmartScanResult(new SmartHealthScanResult
        {
            DiskId = disk.DiskId,
            DevicePath = $@"\\.\PhysicalDrive{disk.DiskId}",
            DisplayName = disk.DisplayName,
            Model = disk.FriendlyName,
            SerialNumber = disk.SerialNumber,
            ScannedUtc = DateTime.UtcNow,
            ControllerErrorCount = _smartScanControllerErrors,
            Grade = SmartScanGrade.Unavailable,
            Headline = "SMART scan could not complete",
            Summary = message,
            Findings = ["The scan is read-only. Reconnect the drive or reopen the dashboard, then try again."],
        });
    }

    private async void SmartScanAgain_Click(object sender, RoutedEventArgs e)
    {
        if (_smartScanDisk is not null)
            await StartSmartScanAsync(_smartScanDisk, _smartScanControllerErrors);
    }

    internal void SmartScanClose_Click(object sender, RoutedEventArgs e)
    {
        _smartScanGeneration++; // Ignore any in-flight scan result.
        SmartScanOverlay.Visibility = Visibility.Collapsed;
    }

    internal static string BlankAsDash(string value) => string.IsNullOrWhiteSpace(value) ? "\u2014" : value;

    internal static string FormatLifetime(long? written, long? read)
    {
        if (written is null && read is null) return "Not exposed";
        var parts = new List<string>();
        if (written is long w) parts.Add($"{ByteFormat.Humanize(w)} written");
        if (read is long r) parts.Add($"{ByteFormat.Humanize(r)} read");
        return string.Join(" \u00b7 ", parts);
    }

    // ----------------------------------------------------------- Settings

    internal void LoadSettingsFields()
    {
        var cfg = _config.Current;
        var userSettings = _userSettings.Current;
        TxtWarnHour.Text = cfg.SsdWarnGbPerHour.ToString(CultureInfo.InvariantCulture);
        TxtWarnDay.Text = cfg.SsdWarnGbPerDay.ToString(CultureInfo.InvariantCulture);
        TxtCritDay.Text = cfg.SsdCriticalGbPerDay.ToString(CultureInfo.InvariantCulture);
        TxtProcHour.Text = cfg.ProcessWarnGbPerHour.ToString(CultureInfo.InvariantCulture);
        TxtAllProcHour.Text = cfg.AllProcessesWarnGbPerHour.ToString(CultureInfo.InvariantCulture);
        TxtCooldown.Text = cfg.AlertCooldownMinutes.ToString(CultureInfo.InvariantCulture);
        TxtControllerWindow.Text = cfg.ControllerErrorWindowDays.ToString(CultureInfo.InvariantCulture);
        TxtControllerWarn.Text = cfg.ControllerErrorWarnCount.ToString(CultureInfo.InvariantCulture);
        TxtControllerCritical.Text = cfg.ControllerErrorCriticalCount.ToString(CultureInfo.InvariantCulture);
        TxtInterval.Text = cfg.SampleIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        TxtRefresh.Text = cfg.DashboardRefreshSeconds.ToString(CultureInfo.InvariantCulture);
        TxtEnduranceWarnYears.Text = cfg.TbwProjectionWarnYears.ToString(CultureInfo.InvariantCulture);
        ChkControllerErrors.IsChecked = cfg.EnableControllerErrorAlerts;
        ChkNotify.IsChecked = userSettings.EnableNotifications;

        // Web TBW lookup settings.
        ChkTbwLookup.IsChecked = userSettings.EnableTbwWebLookup;
        SelectProviderItem(userSettings.WebSearchProvider);
        var secrets = AiSecretsStore.Load();
        TxtGoogleKey.Text = secrets.GoogleApiKey ?? "";
        TxtGoogleCx.Text = secrets.GoogleCseId ?? "";
        TxtSerperKey.Password = secrets.SerperApiKey ?? "";

        LoadTbwField();
    }

    private void LoadTbwField()
    {
        var disk = SelectedDisk;
        if (disk is null) { TxtTbw.Text = ""; TxtTbwUpper.Text = ""; return; }
        var label = string.IsNullOrWhiteSpace(disk.Volumes) ? $"Disk {disk.DiskId}" : disk.Volumes.Trim();
        TbwLabel.Text = $"TBW rating for {label} (TB)";
        TxtTbw.Text = _config.Current.EffectiveTbw(disk.DiskId).ToString(CultureInfo.InvariantCulture);
        var upper = _config.Current.EffectiveTbwUpper(disk.DiskId);
        TxtTbwUpper.Text = upper.HasValue ? upper.Value.ToString(CultureInfo.InvariantCulture) : "";
    }

    private void SelectProviderItem(string provider)
    {
        foreach (var obj in TbwProviderSelector.Items)
            if (obj is System.Windows.Controls.ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), provider, StringComparison.OrdinalIgnoreCase))
            { TbwProviderSelector.SelectedItem = item; return; }
        TbwProviderSelector.SelectedIndex = 0;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal void Save_Click(object sender, RoutedEventArgs e)
    {
        bool enableNotifications = ChkNotify.IsChecked == true;
        var disk = SelectedDisk;
        bool enableTbwWebLookup = ChkTbwLookup.IsChecked == true;
        string? webSearchProvider = null;
        if ((TbwProviderSelector.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() is string prov && prov.Length > 0)
            webSearchProvider = prov;
        AiSecretsStore.Save(new AiSecrets
        {
            GoogleApiKey = NullIfBlank(TxtGoogleKey.Text),
            GoogleCseId = NullIfBlank(TxtGoogleCx.Text),
            SerperApiKey = NullIfBlank(TxtSerperKey.Password),
        });
        _tbwLookup = null; // recreate with the new backend/keys on the next lookup

        _config.Update(cfg =>
        {
            cfg.SsdWarnGbPerHour = ParseOr(TxtWarnHour.Text, cfg.SsdWarnGbPerHour);
            cfg.SsdWarnGbPerDay = ParseOr(TxtWarnDay.Text, cfg.SsdWarnGbPerDay);
            cfg.SsdCriticalGbPerDay = ParseOr(TxtCritDay.Text, cfg.SsdCriticalGbPerDay);
            cfg.ProcessWarnGbPerHour = ParseOr(TxtProcHour.Text, cfg.ProcessWarnGbPerHour);
            cfg.AllProcessesWarnGbPerHour = ParseOr(TxtAllProcHour.Text, cfg.AllProcessesWarnGbPerHour);
            cfg.AlertCooldownMinutes = (int)Math.Clamp(ParseOr(TxtCooldown.Text, cfg.AlertCooldownMinutes), 1, 1440);
            cfg.ControllerErrorWindowDays = (int)Math.Clamp(ParseOr(TxtControllerWindow.Text, cfg.ControllerErrorWindowDays), 1, 365);
            cfg.ControllerErrorWarnCount = (int)Math.Clamp(ParseOr(TxtControllerWarn.Text, cfg.ControllerErrorWarnCount), 0, 100000);
            cfg.ControllerErrorCriticalCount = (int)Math.Clamp(ParseOr(TxtControllerCritical.Text, cfg.ControllerErrorCriticalCount), 0, 100000);
            if (cfg.ControllerErrorWarnCount > 0 && cfg.ControllerErrorCriticalCount > 0)
                cfg.ControllerErrorCriticalCount = Math.Max(cfg.ControllerErrorWarnCount, cfg.ControllerErrorCriticalCount);
            cfg.SampleIntervalSeconds = (int)Math.Clamp(ParseOr(TxtInterval.Text, cfg.SampleIntervalSeconds), 1, 60);
            cfg.DashboardRefreshSeconds = (int)Math.Clamp(ParseOr(TxtRefresh.Text, cfg.DashboardRefreshSeconds), 1, 600);
            cfg.TbwProjectionWarnYears = ParseOr(TxtEnduranceWarnYears.Text, cfg.TbwProjectionWarnYears);
            cfg.EnableControllerErrorAlerts = ChkControllerErrors.IsChecked == true;

            if (disk is null)
                return;

            double lower;
            if (double.TryParse(TxtTbw.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double tbw) && tbw > 0)
            {
                cfg.DiskTbwRatings[disk.DiskId] = tbw;
                lower = tbw;
            }
            else
            {
                cfg.DiskTbwRatings.Remove(disk.DiskId);
                lower = cfg.DefaultSsdTbw;
            }

            if (double.TryParse(TxtTbwUpper.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double upper) && upper > lower)
                cfg.DiskTbwRatingsUpper[disk.DiskId] = upper;
            else
                cfg.DiskTbwRatingsUpper.Remove(disk.DiskId);
        });
        _userSettings.Update(settings =>
        {
            settings.EnableNotifications = enableNotifications;
            settings.EnableTbwWebLookup = enableTbwWebLookup;
            if (webSearchProvider is not null)
                settings.WebSearchProvider = webSearchProvider;
        });
        SaveStatus.Text = "Saved \u2713";
        ApplyRefreshInterval();
        LoadSettingsFields();
        RefreshAll();
    }

    private static double ParseOr(string text, double fallback)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= 0 ? v : fallback;

    // ----------------------------------------------------------- Auto-suspend rules

    private void LoadSuspendRules()
    {
        _suspendRules.Clear();
        foreach (var r in _userSettings.Current.AutoSuspendRules)
            _suspendRules.Add(new SuspendRuleVm
            {
                ProcessName = r.ProcessName,
                ThresholdText = r.ThresholdGbPerHour.ToString(CultureInfo.InvariantCulture),
                IsAuto = r.Mode == SuspendMode.Auto && !string.IsNullOrWhiteSpace(r.ExecutablePath),
                Enabled = r.Enabled,
                ExecutablePath = r.ExecutablePath,
            });
        SuspendRuleEmpty.Visibility = _suspendRules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProcessPickList.ItemsSource = _repo.GetKnownProcessNames();
    }

    private void RefreshSuspended()
    {
        var rows = _repo.GetSuspendedProcessStates()
            .Select(s => new SuspendedRow(s.Name, $"{s.Name}  \u00b7  suspended {LocalTimeDisplay.FormatUtcWithZone(s.SuspendedUtc, "HH:mm")}"))
            .ToList();
        SuspendedList.ItemsSource = rows;
        SuspendedHeader.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AddSeenRule_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessPickList.SelectedItem is string name && !string.IsNullOrWhiteSpace(name))
            AddRule(name, null);
        else
            SuspendStatus.Text = "Pick a seen process first.";
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select an executable to auto-suspend",
            Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true)
            AddRule(System.IO.Path.GetFileNameWithoutExtension(dlg.FileName), dlg.FileName);
    }

    private void AddRule(string name, string? path)
    {
        if (_suspendRules.Any(r => string.Equals(r.ProcessName, name, StringComparison.OrdinalIgnoreCase)))
        {
            SuspendStatus.Text = $"'{name}' already has a rule.";
            return;
        }
        double dflt = _config.Current.ProcessWarnGbPerHour > 0 ? _config.Current.ProcessWarnGbPerHour : 5;
        _suspendRules.Add(new SuspendRuleVm
        {
            ProcessName = name,
            ThresholdText = dflt.ToString(CultureInfo.InvariantCulture),
            IsAuto = false,
            Enabled = true,
            ExecutablePath = path,
        });
        SuspendStatus.Text = $"Added '{name}'. Click Save rules.";
    }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SuspendRuleVm vm)
        {
            _suspendRules.Remove(vm);
            SuspendStatus.Text = "Removed. Click Save rules.";
        }
    }

    private void SaveRules_Click(object sender, RoutedEventArgs e)
    {
        var rules = new List<AutoSuspendRule>();
        foreach (var vm in _suspendRules)
        {
            if (string.IsNullOrWhiteSpace(vm.ProcessName)) continue;
            double thr = double.TryParse(vm.ThresholdText, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : 5;
            rules.Add(new AutoSuspendRule
            {
                ProcessName = vm.ProcessName.Trim(),
                ThresholdGbPerHour = thr,
                Mode = vm.IsAuto && vm.CanAuto ? SuspendMode.Auto : SuspendMode.Confirm,
                Enabled = vm.Enabled,
                ExecutablePath = vm.ExecutablePath,
            });
        }
        _userSettings.Update(settings => settings.AutoSuspendRules = rules);
        SuspendStatus.Text = "Saved \u2713";
        LoadSuspendRules();
    }

    private void ResumeProc_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SuspendedRow row)
        {
            var result = AutoSuspendManager.ResumeTracked(_repo, row.Name);
            RefreshSuspended();
            SuspendStatus.Text = result.IdentityUnavailable
                ? $"Could not safely resume '{row.Name}' because its exact process identity is unavailable."
                : result.AccessDenied
                ? $"Could not resume '{row.Name}' (access denied)."
                : $"Resumed '{row.Name}'.";
        }
    }

    // ----------------------------------------------------------- Events

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadDisks();
        RefreshAll();
    }

    /// <summary>Toggles between the dashboard and the settings page (gear icon / Back button).</summary>
    private void Gear_Click(object sender, RoutedEventArgs e)
    {
        bool showSettings = SettingsPanel.Visibility != Visibility.Visible;
        if (showSettings)
        {
            // Refresh editable fields from the latest config when opening settings.
            LoadSettingsFields();
            LoadSuspendRules();
        }
        SettingsPanel.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
        DashboardPanel.Visibility = showSettings ? Visibility.Collapsed : Visibility.Visible;
        GearButton.ToolTip = showSettings ? "Back to dashboard" : "Settings";
        BodyScroller.ScrollToTop();
    }

    // ----------------------------------------------------------- Rated-TBW web lookup

    internal static bool ShouldPromptTbwOnlineSetup(UserSettings settings, AiSecrets secrets)
        => !settings.SuppressTbwOnlineSetupPrompt && string.IsNullOrWhiteSpace(secrets.SerperApiKey);

    internal void ShowTbwOnlineSetup()
    {
        TbwSetupIntroPanel.Visibility = Visibility.Visible;
        TbwSetupKeyPanel.Visibility = Visibility.Collapsed;
        TbwSetupIntroButtons.Visibility = Visibility.Visible;
        TbwSetupKeyButtons.Visibility = Visibility.Collapsed;
        TbwSetupDontShowAgain.IsChecked = false;
        TbwSetupSerperKey.Password = "";
        TbwSetupErrorText.Visibility = Visibility.Collapsed;
        TbwSetupOverlay.Visibility = Visibility.Visible;
        TbwSetupOverlay.Focus();
    }

    private void TbwSetupShowFromSettings_Click(object sender, RoutedEventArgs e)
        => ShowTbwOnlineSetup();

    private void TbwSetupConfigure_Click(object sender, RoutedEventArgs e)
    {
        TbwSetupIntroPanel.Visibility = Visibility.Collapsed;
        TbwSetupKeyPanel.Visibility = Visibility.Visible;
        TbwSetupIntroButtons.Visibility = Visibility.Collapsed;
        TbwSetupKeyButtons.Visibility = Visibility.Visible;
        TbwSetupSerperKey.Focus();
    }

    private void TbwSetupBack_Click(object sender, RoutedEventArgs e)
    {
        TbwSetupIntroPanel.Visibility = Visibility.Visible;
        TbwSetupKeyPanel.Visibility = Visibility.Collapsed;
        TbwSetupIntroButtons.Visibility = Visibility.Visible;
        TbwSetupKeyButtons.Visibility = Visibility.Collapsed;
        TbwSetupErrorText.Visibility = Visibility.Collapsed;
    }

    private void TbwSetupNotNow_Click(object sender, RoutedEventArgs e)
    {
        SaveTbwSetupSuppressionIfRequested();
        TbwSetupOverlay.Visibility = Visibility.Collapsed;
    }

    private void TbwSetupOpenSerper_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TbwSetupUrlLauncher("https://serper.dev/signup");
            TbwSetupErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            TbwSetupErrorText.Text = $"Could not open Serper: {ex.Message}";
            TbwSetupErrorText.Visibility = Visibility.Visible;
        }
    }

    private async void TbwSetupSave_Click(object sender, RoutedEventArgs e)
    {
        string key = TbwSetupSerperKey.Password.Trim();
        if (key.Length < 20)
        {
            TbwSetupErrorText.Text = "Paste the API key from Serper's API Keys page.";
            TbwSetupErrorText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var secrets = AiSecretsStore.Load();
            secrets.SerperApiKey = key;
            TbwSetupSecretsSaver(secrets);

            bool suppressPrompt = TbwSetupDontShowAgain.IsChecked == true;
            _userSettings.Update(settings =>
            {
                settings.EnableTbwWebLookup = true;
                settings.WebSearchProvider = "serper";
                settings.SuppressTbwOnlineSetupPrompt = suppressPrompt;
            });

            _tbwLookup = null;
            TbwSetupSerperKey.Password = "";
            TbwSetupOverlay.Visibility = Visibility.Collapsed;
            LoadSettingsFields();

            if (SelectedDisk is { IsSsd: true } disk)
                await StartTbwLookupAsync(disk, force: true, userInitiated: true);
        }
        catch (Exception ex)
        {
            TbwSetupErrorText.Text = $"Could not save the Serper key: {ex.Message}";
            TbwSetupErrorText.Visibility = Visibility.Visible;
        }
    }

    private void SaveTbwSetupSuppressionIfRequested()
    {
        if (TbwSetupDontShowAgain.IsChecked != true)
            return;
        _userSettings.Update(settings => settings.SuppressTbwOnlineSetupPrompt = true);
    }

    private TbwLookupService TbwLookup => _tbwLookup ??= new TbwLookupService(_userSettings.Current);

    private void LookupRatedTbw_Click(object sender, RoutedEventArgs e)
    {
        _ = StartTbwLookupAsync(SelectedDisk, force: true, userInitiated: true);
    }

    /// <summary>
    /// Kicks off (or cancels) a web lookup of the selected SSD's rated TBW. Automatic calls run only
    /// when no per-disk rating exists; a user-initiated pill action forces a fresh lookup even when a
    /// rating or cached result already exists.
    /// </summary>
    internal async Task StartTbwLookupAsync(DiskInfo? disk, bool force = false, bool userInitiated = false)
    {
        _tbwCts?.Cancel();
        _tbwLookupForceRequested = force;
        if (disk is null)
        {
            TbwLookupPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (!disk.IsSsd)
        {
            if (userInitiated)
            {
                TbwLookupTargetText.Text = disk.DisplayName;
                ShowTbwLookupPanel();
                TbwCandidateList.Children.Clear();
                TbwLookupAction.Visibility = Visibility.Collapsed;
                SetTbwLookupOutcome(
                    "TBW lookup is not available for this drive",
                    "Rated TBW lookup is available for SSDs and storage-class memory drives.",
                    TbwLookupOutcome.Unavailable);
            }
            else
            {
                TbwLookupPanel.Visibility = Visibility.Collapsed;
            }
            return;
        }

        var cfg = _config.Current;
        if (!force && (!_userSettings.Current.EnableTbwWebLookup || cfg.DiskTbwRatings.ContainsKey(disk.DiskId)))
        { TbwLookupPanel.Visibility = Visibility.Collapsed; return; }

        string model = (disk.FriendlyName ?? "").Trim();
        if (model.Length == 0 || model.Contains("virtual", StringComparison.OrdinalIgnoreCase))
        {
            if (userInitiated)
            {
                TbwLookupTargetText.Text = disk.DisplayName;
                ShowTbwLookupPanel();
                TbwCandidateList.Children.Clear();
                TbwLookupAction.Visibility = Visibility.Collapsed;
                SetTbwLookupOutcome(
                    "Drive model unavailable",
                    "This drive does not expose a usable model name for a rated TBW lookup.",
                    TbwLookupOutcome.Unavailable);
            }
            else
            {
                TbwLookupPanel.Visibility = Visibility.Collapsed;
            }
            return;
        }

        TbwLookupTargetText.Text = $"{disk.DisplayName}  \u00b7  {model}";
        if (userInitiated)
            ShowTbwLookupPanel();
        TbwCandidateList.Children.Clear();
        TbwLookupAction.Visibility = Visibility.Collapsed;

        if (!force && TbwLookupCache.TryGet(model, out var cachedResult) && cachedResult is not null)
        { RenderTbwResult(disk, cachedResult); return; }

        _tbwCts = new CancellationTokenSource();
        var ct = _tbwCts.Token;
        SetTbwLookupProgress(
            "Preparing local verification",
            $"Preparing the on-device model to look up \u201C{model}\u201D endurance\u2026");

        try
        {
            var svc = TbwLookup;
            var readiness = await svc.GetReadinessAsync(ct);
            if (ct.IsCancellationRequested) return;
            if (!HandleTbwReadiness(readiness))
                return;

            var progress = new Progress<TbwLookupProgress>(p =>
            {
                TbwLookupStatus.Text = p.Stage switch
                {
                    TbwLookupStage.Searching => SetTbwLookupProgress(
                        "Searching web evidence",
                        $"Searching Serper for \u201C{model}\u201D endurance specifications\u2026"),
                    TbwLookupStage.Analyzing => SetTbwLookupProgress(
                        "Verifying with the local model",
                        "Reading the search evidence with the on-device model and rejecting unsupported values\u2026"),
                    _ => TbwLookupStatus.Text,
                };
            });

            var result = await svc.LookupAsync(model, force, progress, ct);
            if (ct.IsCancellationRequested) return;
            RenderTbwResult(disk, result);
        }
        catch (OperationCanceledException) { /* superseded by a newer selection */ }
    }

    internal bool HandleTbwReadiness(TbwReadiness readiness)
    {
        if (readiness.CanRun)
            return true;

        if (readiness.NeedsModelDownload)
        {
            string reason = readiness.HasUsableGpu
                ? $"A GPU was detected. Download the on-device AI model ({readiness.DownloadAlias}) to search the web for this drive's TBW rating."
                : $"Download the on-device AI model ({readiness.DownloadAlias}) to enable the web TBW lookup (CPU-only \u2014 may be slow).";
            SetTbwLookupOutcome("On-device model required", reason, TbwLookupOutcome.Unavailable);
            TbwLookupAction.Content = "Download model";
            TbwLookupAction.Tag = "download";
            TbwLookupAction.Visibility = Visibility.Visible;
        }
        else
        {
            SetTbwLookupOutcome(
                "Lookup unavailable",
                readiness.Reason ?? "Web TBW lookup is unavailable.",
                TbwLookupOutcome.Unavailable);
        }
        return false;
    }

    internal void ShowTbwLookupPanel(bool bringIntoView = true)
    {
        TbwLookupPanel.Visibility = Visibility.Visible;
        if (bringIntoView)
            Dispatcher.BeginInvoke(new Action(() => TbwLookupPanel.Focus()));
    }

    /// <summary>Renders the candidate TBW values with confidence scores and per-value Apply buttons.</summary>
    internal void RenderTbwResult(DiskInfo disk, TbwLookupResult result)
    {
        TbwCandidateList.Children.Clear();
        TbwLookupAction.Visibility = Visibility.Collapsed;
        if (!result.HasCandidates)
        {
            SetTbwLookupOutcome(
                "No verified TBW rating found",
                result.Note ?? "No TBW rating was found on the web for this drive.",
                TbwLookupOutcome.Empty);
            return;
        }

        SetTbwLookupOutcome(
            result.Candidates.Count == 1 ? "Verified candidate found" : "Verified candidates found",
            result.Candidates.Count == 1
                ? "One source-backed TBW candidate passed evidence validation. Review it before applying."
                : $"{result.Candidates.Count} source-backed candidates passed validation. Higher confidence means more independent sources agree.",
            TbwLookupOutcome.Success);

        var textPrimary = (Brush)FindResource("TextPrimary");
        var captionStyle = (Style)FindResource("Caption");
        var toolButton = (Style)FindResource("ToolButton");

        foreach (var c in result.Candidates)
        {
            var row = new System.Windows.Controls.Grid();
            row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

            var info = new System.Windows.Controls.StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"{c.TbwTerabytes:0.#} TBW",
                Foreground = textPrimary,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
            });
            int pct = (int)Math.Round(c.Confidence * 100);
            string sources = string.Join(", ", c.Sources.Take(3)) + (c.Sources.Count > 3 ? $" +{c.Sources.Count - 3}" : "");
            info.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"~{pct}% confidence \u00B7 {c.SourceCount} source{(c.SourceCount == 1 ? "" : "s")}: {sources}",
                Style = captionStyle,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0),
            });
            System.Windows.Controls.Grid.SetColumn(info, 0);
            row.Children.Add(info);

            var apply = new System.Windows.Controls.Button
            {
                Content = "Apply",
                Style = toolButton,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };
            double value = c.TbwTerabytes;
            apply.Click += (_, _) => ApplyTbwCandidate(disk, value);
            System.Windows.Controls.Grid.SetColumn(apply, 1);
            row.Children.Add(apply);

            TbwCandidateList.Children.Add(new System.Windows.Controls.Border
            {
                Background = Frozen(0x17, 0x1A, 0x1F),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(13, 10, 13, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = row,
            });
        }
    }

    /// <summary>Applies a chosen TBW value as the drive's per-disk endurance rating.</summary>
    private void ApplyTbwCandidate(DiskInfo disk, double tbw)
    {
        _config.Update(cfg => cfg.DiskTbwRatings[disk.DiskId] = tbw);
        TbwCandidateList.Children.Clear();
        TbwLookupAction.Visibility = Visibility.Collapsed;
        SetTbwLookupOutcome(
            $"Applied {tbw:0.#} TBW",
            "The selected endurance rating is now used for lifespan and wear projections. You can change it anytime in Settings.",
            TbwLookupOutcome.Success);
        LoadTbwField();
        RefreshAll();
    }

    private enum TbwLookupOutcome { Success, Empty, Unavailable, Error }

    private string SetTbwLookupProgress(string headline, string status)
    {
        TbwLookupStateBorder.Background = Frozen(0x24, 0x32, 0x47);
        TbwLookupStateGlyph.FontFamily = new FontFamily("Segoe MDL2 Assets");
        TbwLookupStateGlyph.Text = "\uE895";
        TbwLookupStateGlyph.Foreground = Frozen(0x64, 0xA7, 0xFF);
        TbwLookupHeadline.Text = headline;
        TbwLookupStatus.Text = status;
        TbwLookupProgressBar.Visibility = Visibility.Visible;
        TbwLookupAgainButton.Visibility = Visibility.Collapsed;
        return status;
    }

    private void SetTbwLookupOutcome(string headline, string status, TbwLookupOutcome outcome)
    {
        var (background, foreground, glyph) = outcome switch
        {
            TbwLookupOutcome.Success => (Frozen(0x1D, 0x3D, 0x2A), InfoBrush, "\u2713"),
            TbwLookupOutcome.Empty => (Frozen(0x3C, 0x33, 0x20), WarningBrush, "?"),
            TbwLookupOutcome.Error => (Frozen(0x49, 0x22, 0x27), CriticalBrush, "\u2715"),
            _ => (Frozen(0x23, 0x31, 0x43), Frozen(0x64, 0xA7, 0xFF), "i"),
        };
        TbwLookupStateBorder.Background = background;
        TbwLookupStateGlyph.FontFamily = new FontFamily("Segoe UI");
        TbwLookupStateGlyph.Text = glyph;
        TbwLookupStateGlyph.Foreground = foreground;
        TbwLookupHeadline.Text = headline;
        TbwLookupStatus.Text = status;
        TbwLookupProgressBar.Visibility = Visibility.Collapsed;
        TbwLookupAgainButton.Visibility = Visibility.Visible;
    }

    private void TbwLookupClose_Click(object sender, RoutedEventArgs e)
    {
        _tbwCts?.Cancel();
        TbwLookupPanel.Visibility = Visibility.Collapsed;
    }

    private async void TbwLookupAgain_Click(object sender, RoutedEventArgs e)
    {
        await StartTbwLookupAsync(SelectedDisk, force: true, userInitiated: true);
    }

    /// <summary>Handles the panel's action button (currently: download the on-device model).</summary>
    private async void TbwLookupAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag as string != "download") return;
        var disk = SelectedDisk;
        if (disk is null) return;

        TbwLookupAction.Visibility = Visibility.Collapsed;
        _tbwCts = new CancellationTokenSource();
        var ct = _tbwCts.Token;
        try
        {
            var svc = TbwLookup;
            var progress = new Progress<int>(p => TbwLookupStatus.Text = $"Downloading on-device model\u2026 {p}%");
            SetTbwLookupProgress("Downloading the local model", "Preparing the on-device verification model\u2026");
            await TbwModelDownloader(progress, ct);
            if (ct.IsCancellationRequested) return;
            SetTbwLookupProgress("Model installed", "Starting the rated TBW search\u2026");
            await StartTbwLookupAsync(disk, force: _tbwLookupForceRequested, userInitiated: true);
        }
        catch (OperationCanceledException) { /* cancelled */ }
        catch (Exception ex)
        {
            SetTbwLookupOutcome("Model download failed", ex.Message, TbwLookupOutcome.Error);
        }
    }

    private void DiskSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        LoadTbwField();
        RefreshAll();
        _ = StartTbwLookupAsync(SelectedDisk);
    }

    private void ProcessRange_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProcessRangeSelector.SelectedItem is ProcRange r)
        {
            _processWindow = r.Span;
            UpdateProcesses();
        }
    }

    private void Range_Click(object sender, RoutedEventArgs e)
    {
        var clicked = (ToggleButton)sender;
        _range = clicked == Btn30d ? RangeKind.D30 : clicked == Btn12w ? RangeKind.W12 : RangeKind.H24;

        Btn24h.IsChecked = _range == RangeKind.H24;
        Btn30d.IsChecked = _range == RangeKind.D30;
        Btn12w.IsChecked = _range == RangeKind.W12;

        var disk = SelectedDisk;
        if (disk is not null) UpdateChart(disk);
    }

    private System.Windows.Rect? _restoreBounds;

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;

        // Double-click toggles expand / restore; a single press starts a drag-move.
        if (e.ClickCount == 2)
            ToggleExpand();
        else
            DragMove();
    }

    /// <summary>Expands the window to fill the monitor's work area, or restores its previous size.</summary>
    private void ToggleExpand()
    {
        if (_restoreBounds is { } rb)
        {
            Left = rb.Left;
            Top = rb.Top;
            Width = rb.Width;
            Height = rb.Height;
            _restoreBounds = null;
            return;
        }

        _restoreBounds = new System.Windows.Rect(Left, Top, Width, Height);

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var work = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea; // device pixels
        var src = PresentationSource.FromVisual(this);
        double sx = src?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
        double sy = src?.CompositionTarget.TransformToDevice.M22 ?? 1.0;

        Left = work.Left / sx;
        Top = work.Top / sy;
        Width = work.Width / sx;
        Height = work.Height / sy;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
