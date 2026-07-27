using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
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
using DispatcherTimer = System.Windows.Threading.DispatcherTimer;

namespace DiskActivityMonitor.Tray;

public partial class MainWindow : Window
{
    private readonly MonitorRepository _repo;
    private readonly ConfigStore _config;
    private readonly DispatcherTimer _refreshTimer;
    private bool _forceClose;

    // Rated-TBW web lookup (on-device Foundry Local model + web search).
    private static readonly HttpClient TbwHttp = new() { Timeout = TimeSpan.FromMinutes(5) };
    private TbwLookupService? _tbwLookup;
    private CancellationTokenSource? _tbwCts;

    private enum RangeKind { H24, D30, W12 }
    private RangeKind _range = RangeKind.H24;

    private sealed record DiskChoice(DiskInfo Disk, string Display);
    private sealed record ProcessRow(string Name, string WriteText, string ReadText, double BarWidth);
    private sealed record AlertRow(string Title, string Message, string TimeText, Brush SeverityBrush);

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

    /// <summary>Editable view-model for one auto-suspend rule row.</summary>
    private sealed class SuspendRuleVm
    {
        public string ProcessName { get; set; } = "";
        public string ThresholdText { get; set; } = "5";
        public bool IsAuto { get; set; }
        public bool Enabled { get; set; } = true;
        public string? ExecutablePath { get; set; }
    }

    private sealed record SuspendedRow(string Name, string Display);

    private readonly ObservableCollection<SuspendRuleVm> _suspendRules = new();

    private static readonly Brush CriticalBrush = Frozen(0xE0, 0x4A, 0x4A);
    private static readonly Brush WarningBrush = Frozen(0xF0, 0xA0, 0x20);
    private static readonly Brush InfoBrush = Frozen(0x3F, 0xB9, 0x50);

    public MainWindow(MonitorRepository repo, ConfigStore config)
    {
        _repo = repo;
        _config = config;
        InitializeComponent();

        // Taskbar/window icon uses the exact same glyph as the system-tray icon.
        Icon = TrayIconFactory.CreateImageSource(TrayIconFactory.Ok);

        Btn24h.IsChecked = true;
        ProcessRangeSelector.ItemsSource = ProcessRanges;
        ProcessRangeSelector.SelectedItem = ProcessRanges.First(r => r.Span == _processWindow);
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
        UpdateProcesses();
        UpdateAlerts();
        RefreshSuspended();
    }

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

        // Headline 1: lifetime wear from SMART (authoritative when the drive reports it).
        if (disk.WearPercent is int wear)
        {
            double wc = Math.Clamp(wear, 0, 100);
            SmartWearValue.Text = $"{wear}%";
            SmartWearFillCol.Width = new GridLength(wc, GridUnitType.Star);
            SmartWearRestCol.Width = new GridLength(100 - wc, GridUnitType.Star);
            SmartWearText.Text = $"{100 - wear}% endurance remaining, from the drive's SMART data";
        }
        else if (lifeWritten is not null && tbwLowBytes > 0)
        {
            // No wear attribute: estimate from lifetime writes / TBW (a range when an upper TBW is set).
            double fill = Math.Clamp(pctHigh, 0, 100);
            SmartWearValue.Text = ranged ? $"~{pctLow:0.#}\u2013{pctHigh:0.#}%" : $"~{pctHigh:0.#}%";
            SmartWearFillCol.Width = new GridLength(fill, GridUnitType.Star);
            SmartWearRestCol.Width = new GridLength(100 - fill, GridUnitType.Star);
            SmartWearText.Text = $"estimated from lifetime writes \u00f7 {tbwLabel} (this drive reports no wear attribute)";
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
            string pctText = ranged ? $"{pctLow:0.###}% to {pctHigh:0.###}%" : $"{pctHigh:0.###}%";
            lifeLine = $"Lifetime (from the drive): {ByteFormat.Humanize(lw)} written{readPart} \u2014 {pctText} of {tbwLabel}. ";
        }
        string sinceLine = earliest is null
            ? "No history recorded yet."
            : $"Recorded by this app since {earliest.Value.ToLocalTime():MMM d}: {ByteFormat.Humanize(observedBytes)} written.";
        EnduranceConsumedText.Text = lifeLine + sinceLine;
    }

    /// <summary>Formats a years value without a unit: "100+", whole numbers above 10, else one decimal.</summary>
    private static string FormatYearsShort(double y)
        => double.IsNaN(y) ? "-" : y >= 100 ? "100+" : y >= 10 ? $"{y:0}" : $"{y:0.0}";

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

    private void UpdateAlerts()
    {
        // A timestamped log of alerts raised in the trailing window. Each condition is shown once
        // here (with the time it fired) instead of staying "active" until acknowledged; older
        // alerts drop off as they leave the window.
        var alerts = _repo.GetRecentAlerts(200, sinceUtc: DateTime.UtcNow - RecentAlertWindow);

        // The alert engine re-raises the same rule every cooldown period while a condition stays
        // tripped, so collapse repeats: show one row per rule (its latest occurrence) with a count.
        var rows = alerts
            .GroupBy(a => a.RuleKey)
            .OrderByDescending(g => g.Max(a => a.Id))
            .Select(g =>
            {
                var latest = g.OrderByDescending(a => a.Id).First();
                int count = g.Count();
                var time = latest.TimestampUtc.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.CurrentCulture);
                return new AlertRow(
                    latest.Title,
                    latest.Message,
                    count > 1 ? $"{time}  \u00b7  \u00d7{count} since {g.Min(a => a.TimestampUtc).ToLocalTime():HH:mm}" : time,
                    latest.Severity switch
                    {
                        AlertSeverity.Critical => CriticalBrush,
                        AlertSeverity.Warning => WarningBrush,
                        _ => InfoBrush,
                    });
            })
            .ToList();

        AlertList.ItemsSource = rows;
        AlertEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----------------------------------------------------------- Settings

    private void LoadSettingsFields()
    {
        var cfg = _config.Current;
        TxtWarnHour.Text = cfg.SsdWarnGbPerHour.ToString(CultureInfo.InvariantCulture);
        TxtWarnDay.Text = cfg.SsdWarnGbPerDay.ToString(CultureInfo.InvariantCulture);
        TxtCritDay.Text = cfg.SsdCriticalGbPerDay.ToString(CultureInfo.InvariantCulture);
        TxtProcHour.Text = cfg.ProcessWarnGbPerHour.ToString(CultureInfo.InvariantCulture);
        TxtAllProcHour.Text = cfg.AllProcessesWarnGbPerHour.ToString(CultureInfo.InvariantCulture);
        TxtCooldown.Text = cfg.AlertCooldownMinutes.ToString(CultureInfo.InvariantCulture);
        TxtInterval.Text = cfg.SampleIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        TxtRefresh.Text = cfg.DashboardRefreshSeconds.ToString(CultureInfo.InvariantCulture);
        TxtEnduranceWarnYears.Text = cfg.TbwProjectionWarnYears.ToString(CultureInfo.InvariantCulture);
        ChkNotify.IsChecked = cfg.EnableNotifications;

        // Web TBW lookup settings.
        ChkTbwLookup.IsChecked = cfg.EnableTbwWebLookup;
        SelectProviderItem(cfg.WebSearchProvider);
        var secrets = AiSecretsStore.Load();
        TxtGoogleKey.Text = secrets.GoogleApiKey ?? "";
        TxtGoogleCx.Text = secrets.GoogleCseId ?? "";
        TxtSerperKey.Text = secrets.SerperApiKey ?? "";

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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _config.Current;
        cfg.SsdWarnGbPerHour = ParseOr(TxtWarnHour.Text, cfg.SsdWarnGbPerHour);
        cfg.SsdWarnGbPerDay = ParseOr(TxtWarnDay.Text, cfg.SsdWarnGbPerDay);
        cfg.SsdCriticalGbPerDay = ParseOr(TxtCritDay.Text, cfg.SsdCriticalGbPerDay);
        cfg.ProcessWarnGbPerHour = ParseOr(TxtProcHour.Text, cfg.ProcessWarnGbPerHour);
        cfg.AllProcessesWarnGbPerHour = ParseOr(TxtAllProcHour.Text, cfg.AllProcessesWarnGbPerHour);
        cfg.AlertCooldownMinutes = (int)Math.Clamp(ParseOr(TxtCooldown.Text, cfg.AlertCooldownMinutes), 1, 1440);
        cfg.SampleIntervalSeconds = (int)Math.Clamp(ParseOr(TxtInterval.Text, cfg.SampleIntervalSeconds), 1, 60);
        cfg.DashboardRefreshSeconds = (int)Math.Clamp(ParseOr(TxtRefresh.Text, cfg.DashboardRefreshSeconds), 1, 600);
        cfg.TbwProjectionWarnYears = ParseOr(TxtEnduranceWarnYears.Text, cfg.TbwProjectionWarnYears);
        cfg.EnableNotifications = ChkNotify.IsChecked == true;

        var disk = SelectedDisk;
        if (disk is not null)
        {
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

            // Upper bound is optional; only kept when it parses and exceeds the lower rating.
            if (double.TryParse(TxtTbwUpper.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double upper) && upper > lower)
                cfg.DiskTbwRatingsUpper[disk.DiskId] = upper;
            else
                cfg.DiskTbwRatingsUpper.Remove(disk.DiskId);
        }

        // Web TBW lookup: feature toggle + backend to config, API keys to the per-user secrets store.
        cfg.EnableTbwWebLookup = ChkTbwLookup.IsChecked == true;
        if ((TbwProviderSelector.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() is string prov && prov.Length > 0)
            cfg.WebSearchProvider = prov;
        AiSecretsStore.Save(new AiSecrets
        {
            GoogleApiKey = NullIfBlank(TxtGoogleKey.Text),
            GoogleCseId = NullIfBlank(TxtGoogleCx.Text),
            SerperApiKey = NullIfBlank(TxtSerperKey.Text),
        });
        _tbwLookup = null; // recreate with the new backend/keys on the next lookup

        _config.Save(cfg);
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
        foreach (var r in _config.Current.AutoSuspendRules)
            _suspendRules.Add(new SuspendRuleVm
            {
                ProcessName = r.ProcessName,
                ThresholdText = r.ThresholdGbPerHour.ToString(CultureInfo.InvariantCulture),
                IsAuto = r.Mode == SuspendMode.Auto,
                Enabled = r.Enabled,
                ExecutablePath = r.ExecutablePath,
            });
        SuspendRuleEmpty.Visibility = _suspendRules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProcessPickList.ItemsSource = _repo.GetKnownProcessNames();
    }

    private void RefreshSuspended()
    {
        var rows = _repo.GetSuspendedProcesses()
            .Select(s => new SuspendedRow(s.Name, $"{s.Name}  \u00b7  suspended {s.SuspendedUtc.ToLocalTime():HH:mm}"))
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
        var cfg = _config.Current;
        var rules = new List<AutoSuspendRule>();
        foreach (var vm in _suspendRules)
        {
            if (string.IsNullOrWhiteSpace(vm.ProcessName)) continue;
            double thr = double.TryParse(vm.ThresholdText, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : 5;
            rules.Add(new AutoSuspendRule
            {
                ProcessName = vm.ProcessName.Trim(),
                ThresholdGbPerHour = thr,
                Mode = vm.IsAuto ? SuspendMode.Auto : SuspendMode.Confirm,
                Enabled = vm.Enabled,
                ExecutablePath = vm.ExecutablePath,
            });
        }
        cfg.AutoSuspendRules = rules;
        _config.Save(cfg);
        SuspendStatus.Text = "Saved \u2713";
        LoadSuspendRules();
    }

    private void ResumeProc_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SuspendedRow row)
        {
            ProcessControl.Resume(row.Name);
            _repo.RemoveSuspendedProcess(row.Name);
            RefreshSuspended();
            SuspendStatus.Text = $"Resumed '{row.Name}'.";
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

    private TbwLookupService TbwLookup => _tbwLookup ??= new TbwLookupService(_config.Current, TbwHttp);

    /// <summary>
    /// Kicks off (or cancels) a web lookup of the selected SSD's rated TBW. Runs only for SSDs with no
    /// confirmed per-disk TBW yet; cached results render instantly (once per drive model, then cached).
    /// </summary>
    private async void MaybeStartTbwLookup(DiskInfo? disk)
    {
        _tbwCts?.Cancel();
        if (disk is null || !disk.IsSsd) { TbwLookupPanel.Visibility = Visibility.Collapsed; return; }

        var cfg = _config.Current;
        if (!cfg.EnableTbwWebLookup || cfg.DiskTbwRatings.ContainsKey(disk.DiskId))
        { TbwLookupPanel.Visibility = Visibility.Collapsed; return; }

        string model = (disk.FriendlyName ?? "").Trim();
        if (model.Length == 0 || model.Contains("virtual", StringComparison.OrdinalIgnoreCase))
        { TbwLookupPanel.Visibility = Visibility.Collapsed; return; }

        TbwLookupPanel.Visibility = Visibility.Visible;
        TbwCandidateList.Children.Clear();
        TbwLookupAction.Visibility = Visibility.Collapsed;

        if (TbwLookupCache.TryGet(model, out var cachedResult) && cachedResult is not null)
        { RenderTbwResult(disk, cachedResult); return; }

        _tbwCts = new CancellationTokenSource();
        var ct = _tbwCts.Token;
        TbwLookupStatus.Text = $"Preparing on-device model to look up \u201C{model}\u201D endurance\u2026";

        try
        {
            var svc = TbwLookup;
            var readiness = await svc.GetReadinessAsync(ct);
            if (ct.IsCancellationRequested) return;

            if (!readiness.CanRun)
            {
                if (readiness.NeedsModelDownload)
                {
                    TbwLookupStatus.Text = readiness.HasUsableGpu
                        ? $"A GPU was detected. Download the on-device AI model ({readiness.DownloadAlias}) to search the web for this drive's TBW rating."
                        : $"Download the on-device AI model ({readiness.DownloadAlias}) to enable the web TBW lookup (CPU-only \u2014 may be slow).";
                    TbwLookupAction.Content = "Download model";
                    TbwLookupAction.Tag = "download";
                    TbwLookupAction.Visibility = Visibility.Visible;
                }
                else
                {
                    TbwLookupStatus.Text = readiness.Reason ?? "Web TBW lookup is unavailable.";
                }
                return;
            }

            var progress = new Progress<TbwLookupProgress>(p =>
            {
                TbwLookupStatus.Text = p.Stage switch
                {
                    TbwLookupStage.Searching => $"Searching the web for \u201C{model}\u201D endurance (TBW) rating\u2026",
                    TbwLookupStage.Analyzing => "Reading the results with the on-device model\u2026",
                    _ => TbwLookupStatus.Text,
                };
            });

            var result = await svc.LookupAsync(model, force: false, progress, ct);
            if (ct.IsCancellationRequested) return;
            RenderTbwResult(disk, result);
        }
        catch (OperationCanceledException) { /* superseded by a newer selection */ }
        catch (Exception ex) { TbwLookupStatus.Text = $"Lookup failed: {ex.Message}"; }
    }

    /// <summary>Renders the candidate TBW values with confidence scores and per-value Apply buttons.</summary>
    private void RenderTbwResult(DiskInfo disk, TbwLookupResult result)
    {
        TbwCandidateList.Children.Clear();
        TbwLookupAction.Visibility = Visibility.Collapsed;
        if (!result.HasCandidates)
        {
            TbwLookupStatus.Text = result.Note ?? "No TBW rating was found on the web for this drive.";
            return;
        }

        TbwLookupStatus.Text = result.Candidates.Count == 1
            ? "Found a rated TBW value on the web \u2014 click Apply to use it:"
            : $"Found {result.Candidates.Count} candidate TBW values (sources may conflict) \u2014 higher confidence means more sources agree:";

        var textPrimary = (Brush)FindResource("TextPrimary");
        var captionStyle = (Style)FindResource("Caption");
        var toolButton = (Style)FindResource("ToolButton");

        foreach (var c in result.Candidates)
        {
            var row = new System.Windows.Controls.Grid { Margin = new Thickness(0, 6, 0, 0) };
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

            TbwCandidateList.Children.Add(row);
        }
    }

    /// <summary>Applies a chosen TBW value as the drive's per-disk endurance rating.</summary>
    private void ApplyTbwCandidate(DiskInfo disk, double tbw)
    {
        var cfg = _config.Current;
        cfg.DiskTbwRatings[disk.DiskId] = tbw;
        _config.Save(cfg);
        TbwCandidateList.Children.Clear();
        TbwLookupAction.Visibility = Visibility.Collapsed;
        TbwLookupStatus.Text = $"Applied {tbw:0.#} TBW to this drive. You can change it anytime in Settings.";
        LoadTbwField();
        RefreshAll();
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
            TbwLookupStatus.Text = "Downloading on-device model\u2026";
            await svc.DownloadModelAsync(progress, ct);
            if (ct.IsCancellationRequested) return;
            TbwLookupStatus.Text = "Model installed. Searching\u2026";
            MaybeStartTbwLookup(disk);
        }
        catch (OperationCanceledException) { /* cancelled */ }
        catch (Exception ex) { TbwLookupStatus.Text = $"Model download failed: {ex.Message}"; }
    }

    private void DiskSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        LoadTbwField();
        RefreshAll();
        MaybeStartTbwLookup(SelectedDisk);
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
