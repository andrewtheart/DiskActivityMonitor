using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DiskActivityMonitor.Core;
using DiskActivityMonitor.Core.Ai;
using DiskActivityMonitor.Core.Collection;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Files;
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
    private readonly DispatcherTimer _liveDiskTimer;
    private bool _forceClose;
    private bool _helpInitialized;
    private Uri? _helpDocumentUri;

    // Rated-TBW web lookup (on-device Foundry Local model + web search).
    private TbwLookupService? _tbwLookup;
    private CancellationTokenSource? _tbwCts;
    private string? _loadedTbwDiskId;
    private double? _loadedTbwLower;
    private double? _loadedTbwUpper;
    private bool _loadedTbwHadOverride;
    private bool _tbwLookupForceRequested;
    private TbwLookupDiagnostics? _tbwLookupDiagnostics;
    private DiskInfo? _tbwEditDisk;
    internal Func<IProgress<int>?, CancellationToken, Task> TbwModelDownloader = null!;
    internal Func<IProgress<string>?, CancellationToken, Task> FoundryLocalInstaller = null!;
    internal Func<DiskInfo, Task> TbwPostInstallLookup = null!;
    internal Action<string> TbwSetupUrlLauncher { get; set; } = url =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    internal Action<AiSecrets> TbwSetupSecretsSaver { get; set; } = AiSecretsStore.Save;
    internal Func<Color, Color?> ChartColorPicker { get; set; } = ShowChartColorPicker;
    internal Action<System.Windows.Controls.ContextMenu, System.Windows.Controls.Button> AlertSnoozeMenuPresenter { get; set; }
        = ShowAlertSnoozeMenu;

    private enum TrendRangeKind { H1, H24, D7, D30, Custom, Zoom }
    private TrendRangeKind _trendRange = TrendRangeKind.H1;
    private TimeSpan? _trendZoomWindow;
    private int _liveGraphWindowMinutes;

    private sealed record DiskChoice(DiskInfo? Disk, string Display)
    {
        public bool IsAll => Disk is null;
    }
    private sealed record ChartLegendItem(string Label, Brush Brush);
    private sealed record CollapsibleCard(
        FrameworkElement Body,
        System.Windows.Controls.TextBlock CollapsedTitle,
        System.Windows.Shapes.Path Chevron);
    private sealed class ChartColorRow : INotifyPropertyChanged
    {
        private string _hex;
        private Brush _previewBrush;

        public ChartColorRow(string key, string label, Color defaultColor, string? configured)
        {
            Key = key;
            Label = label;
            DefaultColor = defaultColor;
            _hex = TryParseChartColor(configured, out Color parsed)
                ? FormatChartColor(parsed)
                : FormatChartColor(defaultColor);
            _previewBrush = Preview(_hex, defaultColor);
        }

        public string Key { get; }
        public string Label { get; }
        public Color DefaultColor { get; }
        public string ChooseAutomationName => $"Choose color for {Label}";

        public string Hex
        {
            get => _hex;
            set
            {
                if (_hex == value) return;
                _hex = value;
                OnPropertyChanged(nameof(Hex));
                if (TryParseChartColor(value, out Color color))
                {
                    _previewBrush = Preview(FormatChartColor(color), DefaultColor);
                    OnPropertyChanged(nameof(PreviewBrush));
                }
            }
        }

        public Brush PreviewBrush => _previewBrush;
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Reset() => Hex = FormatChartColor(DefaultColor);

        private static Brush Preview(string value, Color fallback)
        {
            Color color = TryParseChartColor(value, out Color parsed) ? parsed : fallback;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private void OnPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    private sealed record ProcessRow(string Name, string WriteText, string ReadText, double BarWidth)
    {
        public string AutomationName => $"{Name}, {WriteText} written. Show files";
    }

    /// <summary>One file inside the per-process drill-down.</summary>
    private sealed record FileTargetRow(
        string Path, string FileName, string KindLabel, string WriteText, double BarWidth, string Explanation)
    {
        /// <summary>False for extensions on the configured binary list, which cannot be tailed as text.</summary>
        public bool CanTail { get; init; } = true;

        public string TailToolTip => CanTail
            ? "Live tail this file"
            : "This file type is on the binary extensions list, so it cannot be tailed as text";

        public string TailAutomationName => $"Live tail {FileName}";
        public string CopyAutomationName => $"Copy full path for {FileName}";
        public string TraceAutomationName => $"Find processes holding {FileName} open";
        public string DeleteAutomationName => $"Delete {FileName}";
    }

    /// <summary>One horizontal bar in the dialog's file-type breakdown.</summary>
    private sealed record FileTypeRow(string Label, string ValueText, double BarWidth, Brush Fill);

    /// <summary>A process reported by Sysinternals Handle as holding an object open.</summary>
    private sealed record HandleOwnerRow(string ProcessName, string PidText, string Detail);
    private sealed record AlertRow(
        string Title,
        string Message,
        string TimeText,
        Brush SeverityBrush,
        string RuleKey,
        string? DiskId,
        int ControllerErrorCount,
        bool CanRunSmartScan,
        Visibility SmartActionVisibility,
        Visibility SnoozeVisibility,
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

    internal static bool AlertMatchesSearch(string title, string message, string query)
        => string.IsNullOrWhiteSpace(query)
            || title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
            || message.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

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
    private IReadOnlyList<AlertRow> _alertRows = [];
    private FrameworkElement? _fileTargetsHoverSource;
    private FrameworkElement? _suppressedFileTargetsHoverSource;

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

    /// <summary>Dashboard row for a process this app suspended and can resume.</summary>
    private sealed record SuspendedProcessRow(string Name, string SourceLabel, string Detail, string ResumeAutomationName);

    private readonly ObservableCollection<SuspendRuleVm> _suspendRules = new();
    private readonly ObservableCollection<ChartColorRow> _chartColorRows = new();
    private readonly Dictionary<string, CollapsibleCard> _collapsibleCards = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Brush CriticalBrush = Frozen(0xE0, 0x4A, 0x4A);
    private static readonly Brush WarningBrush = Frozen(0xF0, 0xA0, 0x20);
    private static readonly Brush InfoBrush = Frozen(0x3F, 0xB9, 0x50);
    private static readonly Color[] SeriesPalette =
    [
        Color.FromRgb(0x4F, 0xC3, 0xF7),
        Color.FromRgb(0xFF, 0xA7, 0x26),
        Color.FromRgb(0xEF, 0x53, 0x50),
        Color.FromRgb(0x66, 0xBB, 0x6A),
        Color.FromRgb(0xFF, 0xCA, 0x28),
        Color.FromRgb(0xAB, 0x47, 0xBC),
        Color.FromRgb(0x26, 0xC6, 0xDA),
        Color.FromRgb(0xEC, 0x40, 0x7A),
        Color.FromRgb(0x7E, 0x57, 0xC2),
        Color.FromRgb(0x9C, 0xCC, 0x65),
        Color.FromRgb(0x8D, 0x6E, 0x63),
        Color.FromRgb(0x78, 0x90, 0x9C),
    ];

    public MainWindow(MonitorRepository repo, ConfigStore config, UserSettingsStore userSettings)
    {
        _repo = repo;
        _config = config;
        _userSettings = userSettings;
        TbwModelDownloader = (progress, ct) => TbwLookup.DownloadModelAsync(progress, ct);
        FoundryLocalInstaller = FoundryLocalClient.InstallAsync;
        TbwPostInstallLookup = disk => StartTbwLookupAsync(
            disk,
            force: _tbwLookupForceRequested,
            userInitiated: true);
        InitializeComponent();
        _liveGraphWindowMinutes = Math.Clamp(_config.Current.LiveGraphRetentionMinutes, 1, 120);
        InitializeCollapsiblePanels();

        // Taskbar/window icon uses the exact same glyph as the system-tray icon.
        Icon = TrayIconFactory.CreateImageSource(TrayIconFactory.Ok);

        Btn1h.IsChecked = true;
        TrendStartDate.SelectedDate = DateTime.Today.AddDays(-30);
        TrendEndDate.SelectedDate = DateTime.Today;
        TpBtn24h.IsChecked = true;
        ProcessRangeSelector.ItemsSource = ProcessRanges;
        ProcessRangeSelector.SelectedItem = ProcessRanges.First(r => r.Span == _processWindow);
        TrendTimeZoneText.Text = LocalTimeDisplay.ZoneLabel();
        AlertSearchBox.TextChanged += AlertSearch_TextChanged;
        ChartColorList.ItemsSource = _chartColorRows;
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

        _liveDiskTimer = new DispatcherTimer();
        _liveDiskTimer.Tick += (_, _) => RefreshLiveDiskActivity();
        ApplyLiveDiskRefreshInterval();
        _liveDiskTimer.Start();
    }

    /// <summary>Sets the auto-refresh timer interval from the configured dashboard refresh seconds.</summary>
    private void ApplyRefreshInterval()
    {
        int secs = Math.Clamp(_config.Current.DashboardRefreshSeconds, 1, 600);
        _refreshTimer.Interval = TimeSpan.FromSeconds(secs);
    }

    private void ApplyLiveDiskRefreshInterval()
    {
        int seconds = Math.Clamp(_config.Current.SampleIntervalSeconds, 1, 60);
        _liveDiskTimer.Interval = TimeSpan.FromSeconds(seconds);
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
        _refreshTimer.Stop();
        _liveDiskTimer.Stop();
        _forceClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        CancelAppUpdateOperations();
        // Closing just hides the dashboard; the tray icon keeps the app alive.
        if (!_forceClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void InitializeCollapsiblePanels()
    {
        HashSet<string> collapsed = _userSettings.Current.CollapsedPanels;
        List<System.Windows.Controls.Border> cards = FindLogicalChildren<System.Windows.Controls.Border>(this)
            .Where(border => border.Tag is string key && PanelTitles.ContainsKey(key))
            .ToList();
        foreach (System.Windows.Controls.Border card in cards)
            InitializeCollapsiblePanel(card, collapsed);
    }

    internal void InitializeCollapsiblePanel(
        System.Windows.Controls.Border card,
        IReadOnlySet<string> collapsed)
    {
        string key = card.Tag as string ?? "";
        if (!PanelTitles.ContainsKey(key) || card.Child is not FrameworkElement body)
            return;

        card.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        card.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        card.Child = null;
        var container = new System.Windows.Controls.Grid();
        container.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        container.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

        var header = new System.Windows.Controls.Grid { Height = 20 };
        var title = new System.Windows.Controls.TextBlock
        {
            Text = PanelTitles[key],
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        title.SetResourceReference(StyleProperty, "H2");
        var chevron = new System.Windows.Shapes.Path
        {
            Data = System.Windows.Media.Geometry.Parse("M 1 6 L 5 2 L 9 6"),
            Stroke = FindResource("TextSecondary") as Brush ?? Brushes.Gray,
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        var button = new System.Windows.Controls.Button
        {
            Width = 24,
            Height = 20,
            Padding = new Thickness(0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = chevron,
            ToolTip = $"Collapse {PanelTitles[key]}",
        };
        AutomationProperties.SetName(button, $"Collapse {PanelTitles[key]}");
        button.Click += (_, _) => SetPanelCollapsed(key, !IsPanelCollapsed(key));

        header.Children.Add(title);
        header.Children.Add(button);
        System.Windows.Controls.Grid.SetRow(header, 0);
        System.Windows.Controls.Grid.SetRow(body, 1);
        container.Children.Add(header);
        container.Children.Add(body);
        card.Child = container;

        _collapsibleCards[key] = new CollapsibleCard(body, title, chevron);
        ApplyPanelCollapsed(key, collapsed.Contains(key));
    }

    private static readonly IReadOnlyDictionary<string, string> PanelTitles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["summary-today"] = "Written today",
            ["summary-24h"] = "Last 24 hours",
            ["summary-7d"] = "Last 7 days",
            ["summary-endurance"] = "SSD endurance",
            ["endurance"] = "SSD endurance",
            ["live-activity"] = "Live disk activity",
            ["total-written"] = "Total written over time",
            ["throughput"] = "Disk throughput",
            ["processes"] = "Top application write requests",
            ["alerts"] = "Alert center",
            ["suspended"] = "Suspended processes",
            ["auto-suspend"] = "Auto-suspend rules",
            ["settings"] = "Settings & thresholds",
        };

    private static IEnumerable<T> FindLogicalChildren<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (object childObject in LogicalTreeHelper.GetChildren(root))
        {
            if (childObject is not DependencyObject child)
                continue;
            if (child is T match)
                yield return match;
            foreach (T descendant in FindLogicalChildren<T>(child))
                yield return descendant;
        }
    }

    internal bool IsPanelCollapsed(string key)
        => _collapsibleCards.TryGetValue(key, out CollapsibleCard? card)
            && card.Body.Visibility == Visibility.Collapsed;

    internal void SetPanelCollapsed(string key, bool collapsed)
    {
        if (!_collapsibleCards.ContainsKey(key)) return;
        ApplyPanelCollapsed(key, collapsed);
        _userSettings.Update(settings =>
        {
            if (collapsed) settings.CollapsedPanels.Add(key);
            else settings.CollapsedPanels.Remove(key);
        });
    }

    private void ApplyPanelCollapsed(string key, bool collapsed)
    {
        if (!_collapsibleCards.TryGetValue(key, out CollapsibleCard? card)) return;
        card.Body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        card.CollapsedTitle.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        card.Chevron.Data = System.Windows.Media.Geometry.Parse(
            collapsed ? "M 1 2 L 5 6 L 9 2" : "M 1 6 L 5 2 L 9 6");
        if (card.Chevron.Parent is System.Windows.Controls.Button button)
        {
            string action = collapsed ? "Expand" : "Collapse";
            button.ToolTip = $"{action} {PanelTitles[key]}";
            AutomationProperties.SetName(button, $"{action} {PanelTitles[key]}");
        }
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

        var previousChoice = DiskSelector.SelectedItem as DiskChoice;
        string? previous = previousChoice?.Disk?.DiskId;
        bool keepAll = previousChoice?.IsAll == true;
        choices.Insert(0, new DiskChoice(null, "All disks"));
        DiskSelector.ItemsSource = choices;

        if (disks.Count == 0)
        {
            SubtitleText.Text = "No disks detected yet - start the collector service.";
            return;
        }

        var keep = keepAll
            ? choices[0]
            : previous is null
                ? null
                : choices.FirstOrDefault(c => c.Disk?.DiskId == previous);
        DiskSelector.SelectedItem = keep ?? choices[1];
    }

    private static string MediaTag(DiskInfo d) => d.MediaType switch
    {
        DiskMediaType.Ssd => "SSD",
        DiskMediaType.Scm => "Optane/SCM",
        DiskMediaType.Hdd => "HDD",
        _ => "unknown media",
    };

    private DiskInfo? SelectedDisk => (DiskSelector.SelectedItem as DiskChoice)?.Disk;

    private IReadOnlyList<DiskInfo> SelectedDisks
    {
        get
        {
            if (DiskSelector.SelectedItem is not DiskChoice choice)
                return [];
            return choice.Disk is DiskInfo disk ? [disk] : _repo.GetDisks();
        }
    }

    private void RefreshAll()
    {
        IReadOnlyList<DiskInfo> disks = SelectedDisks;
        if (disks.Count == 0) return;

        UpdateSummary(disks);
        UpdateLiveDiskActivity(disks);
        UpdateChart(disks);
        UpdateThroughput(disks);
        UpdateProcesses();
        UpdateAlerts();
        RefreshSuspended();
        CheckDatabaseSize();
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
        IReadOnlyList<DiskInfo> disks = SelectedDisks;
        if (disks.Count > 0) UpdateThroughput(disks);
    }

    /// <summary>Computes and renders average / median / peak I/O throughput (MB/s) for the selected window.</summary>
    private void UpdateThroughput(IReadOnlyList<DiskInfo> disks)
    {
        var nowUtc = DateTime.UtcNow;
        var fromUtc = nowUtc - _throughputWindow;
        var perMinute = _repo.GetDiskMinuteTotals(disks.Select(disk => disk.DiskId).ToArray(), fromUtc, nowUtc);
        MonitoringCoverage coverage = _repo.GetMonitoringCoverage(fromUtc, nowUtc);
        var stats = ThroughputStats.Compute(perMinute, coverage.MonitoredMinutes);
        double totalBytes = perMinute.Sum(value => (double)value);
        var rates = MonitoringRateStats.Compute(
            totalBytes,
            coverage.MonitoredMinutes,
            coverage.RequestedMinutes,
            _config.Current.HighCoveragePercent);

        ThroughputAvg.Text = FormatMbps(stats.AverageMbps);
        ThroughputMedian.Text = FormatMbps(stats.MedianMbps);
        ThroughputPeak.Text = FormatMbps(stats.PeakMbps);

        var bars = new List<ChartBar>
        {
            new("Average", stats.AverageMbps, Brush: ChartBrush("throughput:average", SeriesPalette[0])),
            new("Median", stats.MedianMbps, Brush: ChartBrush("throughput:median", SeriesPalette[6])),
            new("Peak", stats.PeakMbps, Highlight: true, Brush: ChartBrush("throughput:peak", SeriesPalette[1])),
        };
        ThroughputChart.SetData(bars, v => $"{FormatMbps(v)} MB/s");
        ThroughputCoverageText.Text = FormatCoverageSummary(rates);
    }

    internal static string FormatCoverageSummary(MonitoringRateStats rates)
    {
        string coverage = $"Monitoring coverage: {rates.CoveragePercent:0.#}% ({rates.MonitoredMinutes:N0} of {rates.RequestedMinutes:N0} min).";
        if (rates.MonitoredMinutes == 0)
            return coverage + " No monitored throughput is available yet.";
        if (!rates.HasHighCoverage)
            return coverage + " Average and median are per monitored minute; calendar average is withheld.";
        double calendarMbps = rates.CalendarBytesPerHour / 3600.0 / 1_000_000.0;
        return coverage + $" Calendar average: {FormatMbps(calendarMbps)} MB/s.";
    }

    private static string FormatMbps(double mbps) => mbps switch
    {
        >= 100 => mbps.ToString("0"),
        >= 10 => mbps.ToString("0.#"),
        > 0 => mbps.ToString("0.##"),
        _ => "0",
    };

    private void RefreshLiveDiskActivity()
    {
        IReadOnlyList<DiskInfo> disks = SelectedDisks;
        if (disks.Count > 0) UpdateLiveDiskActivity(disks);
    }

    private void UpdateLiveDiskActivity(IReadOnlyList<DiskInfo> disks)
    {
        int retentionMinutes = Math.Clamp(_config.Current.LiveGraphRetentionMinutes, 1, 120);
        _liveGraphWindowMinutes = Math.Clamp(_liveGraphWindowMinutes, 1, retentionMinutes);
        DateTime fromUtc = DateTime.UtcNow.AddMinutes(-_liveGraphWindowMinutes);
        var series = new List<ChartSeries>();
        var latest = new List<string>();
        LiveDiskPoint? singleCurrent = null;
        int diskIndex = 0;
        foreach (DiskInfo disk in disks.OrderBy(item => item.DiskId, StringComparer.Ordinal))
        {
            IReadOnlyList<LiveDiskPoint> points = BuildLiveDiskPoints(
                _repo.GetLiveDiskSamples(disk.DiskId, fromUtc),
                maxPoints: 120);
            string label = DiskChartLabel(disk);
            Brush readBrush = ChartBrush(LiveColorKey(disk.DiskId, "read"), SeriesPalette[(diskIndex * 2) % SeriesPalette.Length]);
            Brush writeBrush = ChartBrush(LiveColorKey(disk.DiskId, "write"), SeriesPalette[((diskIndex * 2) + 1) % SeriesPalette.Length]);
            series.Add(new ChartSeries(
                LiveColorKey(disk.DiskId, "read"),
                $"{label} read",
                readBrush,
                points.Select(point => new TimeValuePoint(point.TimestampUtc, point.ReadMbps)).ToList()));
            series.Add(new ChartSeries(
                LiveColorKey(disk.DiskId, "write"),
                $"{label} write",
                writeBrush,
                points.Select(point => new TimeValuePoint(point.TimestampUtc, point.WriteMbps)).ToList()));
            if (points.Count > 0)
            {
                LiveDiskPoint current = points[^1];
                if (disks.Count == 1)
                    singleCurrent = current;
                latest.Add($"{label}  R {FormatMbps(current.ReadMbps)}  W {FormatMbps(current.WriteMbps)} MB/s");
            }
            diskIndex++;
        }
        LiveDiskActivityChart.SetSeries(series);
        LiveDiskLegend.ItemsSource = series.Select(item => new ChartLegendItem(item.Label, item.Brush)).ToList();
        LiveDiskCaption.Text = $"Physical read/write throughput from the last {_liveGraphWindowMinutes:N0} min of granular collector samples";

        if (latest.Count == 0)
        {
            LiveDiskCurrent.Text = "Waiting for the collector's next granular sample.";
            return;
        }

        LiveDiskCurrent.Text = singleCurrent is LiveDiskPoint one
            ? $"Now  Read {FormatMbps(one.ReadMbps)} MB/s   Write {FormatMbps(one.WriteMbps)} MB/s"
            : "Now  " + string.Join("   |   ", latest);
    }

    internal static IReadOnlyList<LiveDiskPoint> BuildLiveDiskPoints(
        IReadOnlyList<LiveDiskSample> samples,
        int maxPoints)
    {
        if (samples.Count == 0 || maxPoints <= 0) return [];

        int stride = Math.Max(1, (int)Math.Ceiling(samples.Count / (double)maxPoints));
        var points = new List<LiveDiskPoint>(Math.Min(samples.Count, maxPoints));
        for (int start = 0; start < samples.Count; start += stride)
        {
            IReadOnlyList<LiveDiskSample> bucket = samples.Skip(start).Take(stride).ToList();
            double elapsedSeconds = bucket.Sum(sample => Math.Max(1, sample.ElapsedMilliseconds)) / 1000.0;
            points.Add(new LiveDiskPoint(
                bucket[^1].TimestampUtc,
                bucket.Sum(sample => Math.Max(0, sample.ReadBytes)) / elapsedSeconds / 1_000_000.0,
                bucket.Sum(sample => Math.Max(0, sample.WriteBytes)) / elapsedSeconds / 1_000_000.0));
        }
        return points;
    }

    private void UpdateSummary(IReadOnlyList<DiskInfo> disks)
    {
        var nowUtc = DateTime.UtcNow;
        var midnightUtc = DateTime.Today.ToUniversalTime();

        var today = SumDiskTotals(disks, midnightUtc, nowUtc);
        var day24 = SumDiskTotals(disks, nowUtc.AddHours(-24), nowUtc);
        var week7 = SumDiskTotals(disks, nowUtc.AddDays(-7), nowUtc);

        TodayMetric.Text = ByteFormat.Humanize(today.Write);
        TodayReadSub.Text = $"read {ByteFormat.Humanize(today.Read)}";
        Day24Metric.Text = ByteFormat.Humanize(day24.Write);
        Day24ReadSub.Text = $"read {ByteFormat.Humanize(day24.Read)}";
        Week7Metric.Text = ByteFormat.Humanize(week7.Write);
        Week7AvgSub.Text = $"avg {ByteFormat.Humanize(week7.Write / 7.0)}/day";

        if (disks.Count == 1)
        {
            UpdateEndurance(disks[0], nowUtc);
        }
        else
        {
            UpdateAggregateEndurance(disks, nowUtc);
        }
    }

    private void UpdateAggregateEndurance(IReadOnlyList<DiskInfo> disks, DateTime nowUtc)
    {
        DateTime fromUtc = nowUtc.AddDays(-7);
        List<DateTime> earliestSamples = disks.Select(disk => _repo.GetEarliestSample(disk.DiskId))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();
        if (earliestSamples.Count > 0 && earliestSamples.Min() > fromUtc)
        {
            DateTime first = earliestSamples.Min();
            fromUtc = first;
        }

        var recent = SumDiskTotals(disks, fromUtc, nowUtc);
        MonitoringCoverage coverage = _repo.GetMonitoringCoverage(fromUtc, nowUtc);
        MonitoringRateStats rates = MonitoringRateStats.Compute(
            recent.Write,
            coverage.MonitoredMinutes,
            coverage.RequestedMinutes,
            _config.Current.HighCoveragePercent);
        double perHour = rates.MonitoredBytesPerHour;
        double perDay = perHour * 24;

        WearMetric.Text = $"{disks.Count:N0} disks";
        WearSub.Text = perDay > 0
            ? $"combined {ByteFormat.Humanize(perDay)}/day; lifespan remains drive-specific"
            : "Combined physical activity; lifespan remains drive-specific.";
        EnduranceDiskText.Text = "All disks selected";
        EnduranceRatedText.Text = "Per-disk ratings";
        EnduranceRatedBadge.IsEnabled = false;
        SmartWearValue.Text = "Per drive";
        SmartWearFillCol.Width = new GridLength(0, GridUnitType.Star);
        SmartWearRestCol.Width = new GridLength(100, GridUnitType.Star);
        SmartWearText.Text = "SMART wear and TBW cannot be safely combined; select a disk for its endurance percentage.";

        long lifetimeWrite = 0;
        long lifetimeRead = 0;
        int lifetimeWriteCount = 0;
        int lifetimeReadCount = 0;
        foreach (DiskInfo disk in disks)
        {
            if (disk.LifetimeBytesWritten is long write)
            {
                lifetimeWrite = SaturatingAdd(lifetimeWrite, write);
                lifetimeWriteCount++;
            }
            if (disk.LifetimeBytesRead is long read)
            {
                lifetimeRead = SaturatingAdd(lifetimeRead, read);
                lifetimeReadCount++;
            }
        }
        SmartWearLifeText.Text = FormatAggregateLifetime(
            lifetimeWrite,
            lifetimeWriteCount,
            lifetimeRead,
            lifetimeReadCount);
        SmartWearLifeText.Visibility = Visibility.Visible;
        EnduranceProjValue.Text = "Per drive";
        EnduranceProjSub.Text = "select a disk for its projected lifespan";
        EnduranceAvgHour.Text = perHour > 0 ? ByteFormat.Humanize(perHour) : "-";
        EnduranceAvgDay.Text = perDay > 0 ? ByteFormat.Humanize(perDay) : "-";
        EnduranceAvgWeek.Text = perDay > 0 ? ByteFormat.Humanize(perDay * 7) : "-";
        EnduranceConsumedText.Text = $"Combined recent monitoring coverage: {rates.CoveragePercent:0.#}%. "
            + "Lifetime totals include only disks that expose them; per-drive wear remains available by selecting that disk.";
    }

    private (long Read, long Write) SumDiskTotals(
        IEnumerable<DiskInfo> disks,
        DateTime fromUtc,
        DateTime toUtc)
    {
        long read = 0;
        long write = 0;
        foreach (DiskInfo disk in disks)
        {
            var totals = _repo.GetDiskTotals(disk.DiskId, fromUtc, toUtc);
            read = SaturatingAdd(read, totals.Read);
            write = SaturatingAdd(write, totals.Write);
        }
        return (read, write);
    }

    internal static long SaturatingAdd(long left, long right)
        => right > 0 && left > long.MaxValue - right ? long.MaxValue
            : right < 0 && left < long.MinValue - right ? long.MinValue
            : left + right;

        internal static string FormatAggregateLifetime(
                long lifetimeWrite,
                int lifetimeWriteCount,
                long lifetimeRead,
                int lifetimeReadCount)
                => lifetimeWriteCount == 0
                        ? "No drives expose lifetime-write totals."
                        : $"{ByteFormat.Humanize(lifetimeWrite)} written across {lifetimeWriteCount:N0} reporting disk(s)"
                            + (lifetimeReadCount > 0 ? $" · {ByteFormat.Humanize(lifetimeRead)} read across {lifetimeReadCount:N0}" : "");

    private void UpdateEndurance(DiskInfo disk, DateTime nowUtc)
    {
        EnduranceRatedBadge.IsEnabled = true;
        var cfg = _config.Current;
        var earliest = _repo.GetEarliestSample(disk.DiskId);
        double observedBytes = 0;
        if (earliest is not null)
            observedBytes = _repo.GetDiskTotals(disk.DiskId, earliest.Value, nowUtc).Write;

        MonitoringRateStats recentRate = _repo.GetRecentDiskWriteRate(
            disk.DiskId,
            nowUtc,
            cfg.HighCoveragePercent);
        double avgPerDay = recentRate.MonitoredBytesPerHour * 24.0;
        double avgPerHour = avgPerDay / 24.0;
        double avgPerWeek = avgPerDay * 7.0;
        bool canProject = recentRate.HasHighCoverage && avgPerDay > 0;

        double tbwLow = cfg.EffectiveTbw(disk.DiskId);
        double? tbwHigh = cfg.EffectiveTbwUpper(disk.DiskId);
        bool ranged = tbwHigh.HasValue;
        bool estimatedTbw = !cfg.DiskTbwRatings.ContainsKey(disk.DiskId);
        double tbwLowBytes = tbwLow * 1_000_000_000_000d;            // TBW specs use decimal terabytes.
        double tbwHighBytes = (tbwHigh ?? tbwLow) * 1_000_000_000_000d;
        string tbwLabel = ranged ? $"{tbwLow:0.#} to {tbwHigh:0.#} TBW" : $"{tbwLow:0.#} TBW";
        string tbwBasisLabel = estimatedTbw ? $"{tbwLabel} default estimate" : tbwLabel;

        // Prefer the drive's own lifetime-written total (from SMART) over what we've observed.
        long? lifeWritten = disk.LifetimeBytesWritten;
        double consumedBytes = lifeWritten ?? observedBytes;

        // % of TBW consumed: a lower rating yields a higher %, so the range is [low%@highTBW .. high%@lowTBW].
        double pctHigh = tbwLowBytes > 0 ? consumedBytes / tbwLowBytes * 100.0 : 0;
        double pctLow = tbwHighBytes > 0 ? consumedBytes / tbwHighBytes * 100.0 : pctHigh;

        // Years to reach TBW at the recent rate: a higher rating yields more years.
        double yearsLow = canProject ? Math.Max(tbwLowBytes - (lifeWritten ?? 0), tbwLowBytes * 0.001) / (avgPerDay * 365.0) : double.NaN;
        double yearsHigh = canProject ? Math.Max(tbwHighBytes - (lifeWritten ?? 0), tbwHighBytes * 0.001) / (avgPerDay * 365.0) : double.NaN;
        string yearsText;
        if (double.IsNaN(yearsLow)) yearsText = "-";
        else if (!ranged) yearsText = $"{FormatYearsShort(yearsLow)} yrs";
        else if (yearsLow >= 100 && yearsHigh >= 100) yearsText = "100+ yrs";
        else yearsText = $"{FormatYearsShort(yearsLow)} to {FormatYearsShort(yearsHigh)} yrs";

        // Summary card (glanceable projection).
        if (avgPerDay > 0 && !double.IsNaN(yearsLow))
        {
            WearMetric.Text = yearsText;
            WearSub.Text = $"using {tbwBasisLabel} at {ByteFormat.Humanize(avgPerDay)}/day";
        }
        else
        {
            WearMetric.Text = "-";
            WearSub.Text = recentRate.MonitoredMinutes == 0
                ? $"Using {tbwBasisLabel}. Collecting data to project lifespan."
                : $"Projection withheld at {recentRate.CoveragePercent:0.#}% monitoring coverage (requires {cfg.HighCoveragePercent:0.#}%).";
        }

        // Endurance panel.
        EnduranceDiskText.Text = disk.DisplayName;
        EnduranceRatedText.Text = estimatedTbw ? $"{tbwLabel} estimated" : $"{tbwLabel} rated";

        // Headline 1: precise lifetime writes / configured TBW when available. Drive SMART wear is
        // typically reported only as a whole percentage, so retain it as supporting context rather
        // than rounding away the more precise lifetime-write calculation.
        if (lifeWritten is not null && tbwLowBytes > 0)
        {
            double fill = Math.Clamp(pctHigh, 0, 100);
            SmartWearValue.Text = ranged
                ? $"~{FormatPercent(pctLow)} to {FormatPercent(pctHigh)}"
                : $"~{FormatPercent(pctHigh)}";
            SmartWearFillCol.Width = new GridLength(fill, GridUnitType.Star);
            SmartWearRestCol.Width = new GridLength(100 - fill, GridUnitType.Star);
            string smartContext = disk.WearPercent is int wear
                ? $"; drive SMART reports {wear}% used (whole-percent precision)"
                : "; this drive reports no SMART wear attribute";
            SmartWearText.Text = $"estimated from lifetime writes \u00f7 {tbwBasisLabel}{smartContext}";
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
                ? $"reaches {tbwBasisLabel} at {ByteFormat.Humanize(avgPerDay)}/day"
                : $"reaches {tbwLabel} about {DateTime.Now.AddDays(Math.Min(yearsLow, 5000) * 365.0):MMM yyyy}, at {ByteFormat.Humanize(avgPerDay)}/day";
        }
        else
        {
            EnduranceProjValue.Text = "-";
            EnduranceProjSub.Text = recentRate.MonitoredMinutes == 0
                ? "collecting data to project a lifespan"
                : $"withheld at {recentRate.CoveragePercent:0.#}% coverage; requires {cfg.HighCoveragePercent:0.#}%";
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
            lifeLine = $"Lifetime (from the drive): {ByteFormat.Humanize(lw)} written{readPart} \u2014 {pctText} of {tbwBasisLabel}. ";
        }
        string sinceLine = earliest is null
            ? "No history recorded yet."
            : $"Recorded by this app since {LocalTimeDisplay.FormatUtcWithZone(earliest.Value, "MMM d")}: {ByteFormat.Humanize(observedBytes)} written. Recent monitoring coverage: {recentRate.CoveragePercent:0.#}%; averages are per monitored time.";
        EnduranceConsumedText.Text = lifeLine + sinceLine;
    }

    /// <summary>Formats a years value without a unit: "100+", whole numbers above 10, else one decimal.</summary>
    private static string FormatYearsShort(double y)
        => double.IsNaN(y) ? "-" : y >= 100 ? "100+" : y >= 10 ? $"{y:0}" : $"{y:0.0}";

    internal static string FormatPercent(double value) => $"{value:0.##}%";

    private void UpdateChart(IReadOnlyList<DiskInfo> disks)
    {
        var nowUtc = DateTime.UtcNow;
        if (!TryGetTrendRange(nowUtc, out DateTime fromUtc, out DateTime toUtc))
        {
            TotalWrittenTrendChart.SetData([], ByteFormat.Humanize);
            TrendRangeText.Text = "Choose a valid date range.";
            TrendChangeText.Text = "";
            return;
        }

        TimeSpan bucketSize = SelectTrendBucket(toUtc - fromUtc);
        var writesByBucket = new SortedDictionary<DateTime, long>();
        long totalAtStart = 0;
        bool hasRecordedHistory = false;
        bool allLifetimeAnchored = true;
        foreach (DiskInfo disk in disks)
        {
            foreach (var (bucketEndUtc, writeBytes) in _repo.GetDiskWriteBuckets(disk.DiskId, fromUtc, toUtc, bucketSize))
            {
                writesByBucket.TryGetValue(bucketEndUtc, out long existing);
                writesByBucket[bucketEndUtc] = SaturatingAdd(existing, writeBytes);
            }

            DateTime? earliest = _repo.GetEarliestSample(disk.DiskId);
            long recordedAfterStart = disk.LifetimeBytesWritten is null
                ? 0
                : _repo.GetDiskTotals(disk.DiskId, fromUtc, nowUtc).Write;
            long recordedBeforeStart = disk.LifetimeBytesWritten is not null || earliest is not DateTime first || first >= fromUtc
                ? 0
                : _repo.GetDiskTotals(disk.DiskId, first, fromUtc).Write;
            totalAtStart = SaturatingAdd(totalAtStart, CalculateTrendStartTotal(
                disk.LifetimeBytesWritten,
                recordedAfterStart,
                recordedBeforeStart));
            hasRecordedHistory |= earliest is DateTime recordedAt && recordedAt < toUtc;
            allLifetimeAnchored &= disk.LifetimeBytesWritten is not null;
        }

        if (!hasRecordedHistory && disks.All(disk => disk.LifetimeBytesWritten is null))
        {
            TotalWrittenTrendChart.SetData([], ByteFormat.Humanize);
            TrendCaption.Text = "Cumulative physical writes recorded by this app; no samples exist in this range.";
            TrendRangeText.Text = FormatTrendRange(fromUtc, toUtc);
            TrendChangeText.Text = "No samples";
            return;
        }

        IReadOnlyList<Trends.TotalWrittenPoint> points = Trends.BuildCumulative(
            writesByBucket.Select(item => (item.Key, item.Value)),
            fromUtc,
            toUtc,
            totalAtStart);
        TotalWrittenTrendChart.LineBrush = ChartBrush(
            disks.Count == 1 ? $"total:{disks[0].DiskId}" : "total:all",
            SeriesPalette[1]);
        TotalWrittenTrendChart.SetData(points, ByteFormat.Humanize);

        long increase = Math.Max(0, points[^1].TotalBytes - points[0].TotalBytes);
        TrendTitle.Text = disks.Count == 1 ? "Total written over time" : "Total written over time · All disks";
        TrendCaption.Text = disks.Count > 1
            ? allLifetimeAnchored
                ? "Combined drive lifetime totals anchored to SMART; changes use recorded physical writes."
                : "Combined cumulative physical writes; SMART lifetime anchors are used where available."
            : allLifetimeAnchored
                ? "Drive lifetime total anchored to SMART; changes use recorded physical writes."
                : "Cumulative physical writes recorded since monitoring began; lifetime SMART total is unavailable.";
        TrendRangeText.Text = FormatTrendRange(fromUtc, toUtc);
        TrendChangeText.Text = FormatTrendChange(increase, toUtc - fromUtc);
    }

    internal static TimeSpan SelectTrendBucket(TimeSpan duration)
    {
        if (duration <= TimeSpan.FromHours(2)) return TimeSpan.FromMinutes(1);
        if (duration <= TimeSpan.FromDays(2)) return TimeSpan.FromMinutes(15);
        if (duration <= TimeSpan.FromDays(14)) return TimeSpan.FromHours(2);
        if (duration <= TimeSpan.FromDays(62)) return TimeSpan.FromHours(12);
        return TimeSpan.FromDays(Math.Max(1, Math.Ceiling(duration.TotalDays / 96.0)));
    }

    internal static long CalculateTrendStartTotal(
        long? lifetimeWritten,
        long recordedAfterStart,
        long recordedBeforeStart)
        => lifetimeWritten is long lifetime
            ? Math.Max(0, lifetime - Math.Max(0, recordedAfterStart))
            : Math.Max(0, recordedBeforeStart);

    internal static (DateTime FromUtc, DateTime ToUtc)? ResolveCustomTrendRange(
        DateTime? startDate,
        DateTime? endDate,
        DateTime nowUtc)
    {
        if (startDate is null || endDate is null || endDate.Value.Date < startDate.Value.Date)
            return null;

        DateTime todayLocal = nowUtc.ToLocalTime().Date;
        DateTime startLocal = DateTime.SpecifyKind(startDate.Value.Date, DateTimeKind.Local);
        if (startLocal.Date > todayLocal)
            return null;

        DateTime endLocal = DateTime.SpecifyKind(endDate.Value.Date, DateTimeKind.Local);
        DateTime fromUtc = startLocal.ToUniversalTime();
        DateTime toUtc = endLocal.Date >= todayLocal
            ? nowUtc
            : endLocal.AddDays(1).ToUniversalTime();
        return (fromUtc, toUtc);
    }

    private bool TryGetTrendRange(DateTime nowUtc, out DateTime fromUtc, out DateTime toUtc)
    {
        toUtc = nowUtc;
        fromUtc = _trendRange switch
        {
            TrendRangeKind.H1 => nowUtc.AddHours(-1),
            TrendRangeKind.H24 => nowUtc.AddHours(-24),
            TrendRangeKind.D7 => nowUtc.AddDays(-7),
            TrendRangeKind.D30 => nowUtc.AddDays(-30),
            _ when _trendZoomWindow is TimeSpan zoomWindow => nowUtc - zoomWindow,
            _ => nowUtc,
        };

        if (_trendRange == TrendRangeKind.Custom)
        {
            var custom = ResolveCustomTrendRange(
                TrendStartDate.SelectedDate,
                TrendEndDate.SelectedDate,
                nowUtc);
            if (custom is null)
            {
                TrendCustomError.Text = "Start must be on or before the end date and cannot be in the future.";
                TrendCustomError.Visibility = Visibility.Visible;
                return false;
            }

            (fromUtc, toUtc) = custom.Value;
        }

        TrendCustomError.Visibility = Visibility.Collapsed;
        return true;
    }

    private static string DiskChartLabel(DiskInfo disk)
        => !string.IsNullOrWhiteSpace(disk.Volumes) ? disk.Volumes.Trim()
            : !string.IsNullOrWhiteSpace(disk.FriendlyName) ? disk.FriendlyName.Trim()
            : $"Disk {disk.DiskId}";

    private static string LiveColorKey(string diskId, string metric) => $"live:{diskId}:{metric}";

    private Brush ChartBrush(string key, Color fallback)
    {
        string? configured = _userSettings.Current.ChartColors.GetValueOrDefault(key);
        Color color = TryParseChartColor(configured, out Color parsed) ? parsed : fallback;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    internal static bool TryParseChartColor(string? value, out Color color)
    {
        color = default;
        string hex = value?.Trim().TrimStart('#') ?? "";
        if (hex.Length != 6
            || !byte.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte red)
            || !byte.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte green)
            || !byte.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte blue))
            return false;
        color = Color.FromRgb(red, green, blue);
        return true;
    }

    internal static string FormatChartColor(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private void ConfigureChart_Click(object sender, RoutedEventArgs e)
    {
        string chart = (sender as FrameworkElement)?.Tag?.ToString() ?? "";
        IReadOnlyList<DiskInfo> disks = SelectedDisks;
        if (disks.Count == 0) return;

        _chartColorRows.Clear();
        UserSettings settings = _userSettings.Current;
        switch (chart)
        {
            case "live":
                int diskIndex = 0;
                foreach (DiskInfo disk in disks.OrderBy(item => item.DiskId, StringComparer.Ordinal))
                {
                    string label = DiskChartLabel(disk);
                    AddChartColorRow(
                        LiveColorKey(disk.DiskId, "read"),
                        $"{label} read",
                        SeriesPalette[(diskIndex * 2) % SeriesPalette.Length],
                        settings);
                    AddChartColorRow(
                        LiveColorKey(disk.DiskId, "write"),
                        $"{label} write",
                        SeriesPalette[((diskIndex * 2) + 1) % SeriesPalette.Length],
                        settings);
                    diskIndex++;
                }
                ChartConfigTitle.Text = "Configure live disk activity";
                break;
            case "total":
                string totalKey = disks.Count == 1 ? $"total:{disks[0].DiskId}" : "total:all";
                string totalLabel = disks.Count == 1
                    ? $"{DiskChartLabel(disks[0])} total written"
                    : "All disks total written";
                AddChartColorRow(totalKey, totalLabel, SeriesPalette[1], settings);
                ChartConfigTitle.Text = "Configure total written";
                break;
            case "throughput":
                AddChartColorRow("throughput:average", "Average", SeriesPalette[0], settings);
                AddChartColorRow("throughput:median", "Median", SeriesPalette[6], settings);
                AddChartColorRow("throughput:peak", "Peak", SeriesPalette[1], settings);
                ChartConfigTitle.Text = "Configure disk throughput";
                break;
            default:
                return;
        }

        ChartConfigError.Visibility = Visibility.Collapsed;
        ChartConfigOverlay.Visibility = Visibility.Visible;
        ChartConfigOverlay.Focus();
    }

    private void AddChartColorRow(string key, string label, Color fallback, UserSettings settings)
        => _chartColorRows.Add(new ChartColorRow(
            key,
            label,
            fallback,
            settings.ChartColors.GetValueOrDefault(key)));

    private void ChartColorChoose_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChartColorRow row)
            return;
        TryParseChartColor(row.Hex, out Color current);
        Color? selected = ChartColorPicker(current);
        if (selected is Color color)
            row.Hex = FormatChartColor(color);
    }

    [ExcludeFromCodeCoverage]
    private static Color? ShowChartColorPicker(Color current)
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            AllowFullOpen = true,
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B)
            : null;
    }

    private void ChartConfigReset_Click(object sender, RoutedEventArgs e)
    {
        foreach (ChartColorRow row in _chartColorRows)
            row.Reset();
        ChartConfigError.Visibility = Visibility.Collapsed;
    }

    private void ChartConfigSave_Click(object sender, RoutedEventArgs e)
    {
        ChartColorRow? invalid = _chartColorRows.FirstOrDefault(row => !TryParseChartColor(row.Hex, out _));
        if (invalid is not null)
        {
            ChartConfigError.Text = $"Enter a six-digit hex color for {invalid.Label}.";
            ChartConfigError.Visibility = Visibility.Visible;
            return;
        }

        _userSettings.Update(settings =>
        {
            foreach (ChartColorRow row in _chartColorRows)
                settings.ChartColors[row.Key] = row.Hex.ToUpperInvariant();
        });
        ChartConfigOverlay.Visibility = Visibility.Collapsed;
        RefreshAll();
    }

    private void ChartConfigClose_Click(object sender, RoutedEventArgs e)
        => ChartConfigOverlay.Visibility = Visibility.Collapsed;

    private void ChartConfigOverlay_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        e.Handled = true;
        ChartConfigOverlay.Visibility = Visibility.Collapsed;
    }

    private static string FormatTrendRange(DateTime fromUtc, DateTime toUtc)
    {
        string format = toUtc - fromUtc <= TimeSpan.FromDays(2) ? "MMM d, h:mm tt" : "MMM d, yyyy";
        return $"{LocalTimeDisplay.FormatUtc(fromUtc, format)} to {LocalTimeDisplay.FormatUtc(toUtc, format)}";
    }

    internal static string FormatTrendChange(long increase, TimeSpan duration)
    {
        double units;
        string unit;
        if (duration <= TimeSpan.FromDays(2))
        {
            units = Math.Max(duration.TotalHours, 1.0 / 60);
            unit = "hour";
        }
        else if (duration <= TimeSpan.FromDays(90))
        {
            units = Math.Max(duration.TotalDays, 1.0 / 24);
            unit = "day";
        }
        else
        {
            units = Math.Max(duration.TotalDays / 7.0, 1.0 / (24 * 7));
            unit = "week";
        }

        return $"Increase: +{ByteFormat.Humanize(increase)}  |  Avg {ByteFormat.HumanizeRate(increase / units, unit)}";
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

    private void ProcessRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProcessRow row)
            ShowFileTargets(row.Name);
    }

    private void ProcessBar_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not FrameworkElement element
            || ReferenceEquals(element, _suppressedFileTargetsHoverSource)
            || element.DataContext is not ProcessRow row)
        {
            return;
        }

        _fileTargetsHoverSource = element;
        ShowFileTargets(row.Name);
    }

    private void ProcessBar_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not FrameworkElement element || FileTargetsOverlay.Visibility == Visibility.Visible)
            return;

        if (ReferenceEquals(element, _suppressedFileTargetsHoverSource))
            _suppressedFileTargetsHoverSource = null;
        if (ReferenceEquals(element, _fileTargetsHoverSource))
            _fileTargetsHoverSource = null;
    }

    /// <summary>
    /// Opens the per-file breakdown for one process. This is what makes an opaque writer such as
    /// the kernel System process actionable: the files identify the work, not the process name.
    /// </summary>
    internal void ShowFileTargets(string processName)
    {
        var nowUtc = DateTime.UtcNow;
        var endUtc = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, nowUtc.Minute, 0, DateTimeKind.Utc);
        var startUtc = endUtc - _processWindow;

        _fileTargetsProcess = processName;
        _fileTargetsWindowStartUtc = startUtc;
        _fileTargetsWindowEndUtc = endUtc;

        var targets = _repo.GetTopFileTargets(processName, startUtc, endUtc, topN: 20);
        long processWrite = _repo.GetProcessWrite(processName, startUtc, endUtc);
        long attributed = _repo.GetFileTargetWriteTotal(processName, startUtc, endUtc);

        FileTargetsTitle.Text = $"Files written by {processName}";
        FileTargetsSubtitle.Text = $"{ProcessRangeLabel()}  \u00b7  {ByteFormat.Humanize(processWrite)} of logical write requests";

        string? note = FileTargetNormalizer.ExplainProcess(processName);
        FileTargetsNote.Text = note ?? "";
        FileTargetsNoteBorder.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;

        var binaryPolicy = new BinaryExtensionPolicy(_config.Current.BinaryExtensions);
        const double barArea = 660;
        double max = targets.Count > 0 ? Math.Max(1, targets.Max(t => t.WriteBytes)) : 1;
        FileTargetsList.ItemsSource = targets
            .Select(t => new FileTargetRow(
                t.Path,
                FileNameOf(t.Path),
                FileTargetNormalizer.Label(t.Kind),
                ByteFormat.Humanize(t.WriteBytes),
                Math.Max(2, t.WriteBytes / max * barArea),
                FileTargetNormalizer.ExplainTarget(t.Path, t.Kind))
            {
                CanTail = !binaryPolicy.IsBinary(t.Path),
            })
            .ToList();

        FileTargetsEmpty.Text = FileTargetsEmptyText(_config.Current.TrackFileTargets);
        FileTargetsEmpty.Visibility = targets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FileTargetsFooter.Text = FileTargetsCoverage(processWrite, attributed, _config.Current.FileTargetRetentionDays);

        UpdateFileTargetsAnalytics(processName, startUtc, endUtc, processWrite);

        FileTargetsOverlay.Visibility = Visibility.Visible;
        FileTargetsOverlay.Focus();
    }

    private string ProcessRangeLabel()
        => ProcessRangeSelector.SelectedItem is ProcRange range ? range.Label : _processWindow.ToString();

    internal static string FileTargetsEmptyText(bool trackingEnabled)
        => trackingEnabled
            ? "No per-file records for this process in the selected window. Per-file attribution needs the ETW collector (the installed LocalSystem service) and only stores files above the configured size floor, so brief or tiny writes may not appear. Widen the window or wait a minute for new data."
            : "Per-file attribution is turned off. Enable \"Record which files each process writes\" in Settings; new data appears within a minute.";

    /// <summary>States how much of the process total the listed files account for.</summary>
    internal static string FileTargetsCoverage(long processWriteBytes, long attributedBytes, int retentionDays)
    {
        string retention = $"Per-file history is kept for {retentionDays} day(s).";
        if (processWriteBytes <= 0 || attributedBytes <= 0)
            return retention;

        double share = Math.Min(1, (double)attributedBytes / processWriteBytes);
        return $"Listed files account for {share:P0} of this process's logical writes. {retention}";
    }

    private static string FileNameOf(string path)
    {
        int slash = path.LastIndexOf('\\');
        return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
    }

    internal void FileTargetsClose_Click(object sender, RoutedEventArgs e)
        => CloseFileTargets();

    private void FileTargetsOverlay_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (System.Windows.Input.Key.Escape or System.Windows.Input.Key.Enter)) return;
        e.Handled = true;
        CloseFileTargets();
    }

    internal void CloseFileTargets()
    {
        _suppressedFileTargetsHoverSource = _fileTargetsHoverSource;
        FileTargetsOverlay.Visibility = Visibility.Collapsed;
    }

    internal bool IsFileTargetsHoverSuppressed(FrameworkElement element)
        => ReferenceEquals(element, _suppressedFileTargetsHoverSource);

    internal void UpdateAlerts()
    {
        // The main Alert center shows only non-dismissed alerts from the trailing window. Dismissed
        // records stay in the database and remain available through the complete alert history.
        var alerts = _repo.GetRecentAlerts(200, unacknowledgedOnly: true, sinceUtc: DateTime.UtcNow - RecentAlertWindow);

        // The alert engine re-raises the same rule every cooldown period while a condition stays
        // tripped, so collapse repeats: show one row per rule (its latest occurrence) with a count.
        _alertRows = alerts
            .GroupBy(a => a.RuleKey)
            .OrderByDescending(g => g.Max(a => a.Id))
            .Select(g =>
            {
                var latest = g.OrderByDescending(a => a.Id).First();
                int count = g.Count();
                var time = LocalTimeDisplay.FormatUtc(latest.TimestampUtc, "MMM d, h:mm tt");
                const string controllerPrefix = "disk-controller:";
                bool canScan = latest.RuleKey.StartsWith(controllerPrefix, StringComparison.OrdinalIgnoreCase);
                bool canSnoozeRule = latest.RuleKey.StartsWith("endurance-health:", StringComparison.OrdinalIgnoreCase);
                string? diskId = canScan ? latest.RuleKey[controllerPrefix.Length..] : null;
                int controllerErrors = canScan
                    ? (int)Math.Clamp(Math.Round(latest.Value), 0, int.MaxValue)
                    : 0;
                return new AlertRow(
                    latest.Title,
                    latest.Message,
                    $"{(count > 1 ? $"{time}  \u00b7  \u00d7{count} since {LocalTimeDisplay.FormatUtc(g.Min(a => a.TimestampUtc), "h:mm tt")}" : time)} ({LocalTimeDisplay.ZoneId()})",
                    latest.Severity switch
                    {
                        AlertSeverity.Critical => CriticalBrush,
                        AlertSeverity.Warning => WarningBrush,
                        _ => InfoBrush,
                    },
                    latest.RuleKey,
                    diskId,
                    controllerErrors,
                    canScan,
                        canScan ? Visibility.Visible : Visibility.Collapsed,
                        canSnoozeRule ? Visibility.Visible : Visibility.Collapsed,
                        g.Select(a => a.Id).ToArray());
            })
            .ToList();

        ApplyAlertFilter();
    }

    private void AlertSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => ApplyAlertFilter();

    private void AlertSearchClear_Click(object sender, RoutedEventArgs e)
    {
        AlertSearchBox.Clear();
        AlertSearchBox.Focus();
    }

    private void SnoozeEnduranceAlert_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button
            || button.CommandParameter is not AlertRow row
            || !row.RuleKey.StartsWith("endurance-health:", StringComparison.OrdinalIgnoreCase))
            return;

        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var (id, label) in SnoozeOptions.Choices)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = label,
                Tag = id,
            };
            item.Click += (_, _) =>
            {
                _repo.SnoozeAlertRule(row.RuleKey, DateTime.UtcNow + SnoozeOptions.ToTimeSpan(id));
                _repo.AcknowledgeAlertsByRule(row.RuleKey);
                UpdateAlerts();
            };
            menu.Items.Add(item);
        }
        AlertSnoozeMenuPresenter(menu, button);
    }

    [ExcludeFromCodeCoverage]
    private static void ShowAlertSnoozeMenu(
        System.Windows.Controls.ContextMenu menu,
        System.Windows.Controls.Button button)
    {
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ApplyAlertFilter()
    {
        string query = AlertSearchBox.Text;
        var visibleRows = _alertRows
            .Where(row => AlertMatchesSearch(row.Title, row.Message, query))
            .ToList();
        bool hasQuery = !string.IsNullOrWhiteSpace(query);

        AlertList.ItemsSource = visibleRows;
        AlertSearchClearButton.Visibility = hasQuery ? Visibility.Visible : Visibility.Collapsed;
        AlertEmpty.Text = hasQuery
            ? "No alerts match your search."
            : "No visible alerts in the last hour. Dismissed alerts remain available under All alerts.";
        AlertEmpty.Visibility = visibleRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void UpdateAlertHistory()
    {
        var alerts = _repo.GetRecentAlerts(int.MaxValue);
        var rows = alerts.Select(a => new AlertHistoryRow(
            a.Id,
            a.Title,
            a.Message,
            LocalTimeDisplay.FormatUtcWithZone(a.TimestampUtc, "MMM d, yyyy  h:mm tt"),
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
        SmartScanTimeValue.Text = LocalTimeDisplay.FormatUtcWithZone(result.ScannedUtc, "h:mm:ss tt");
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
        TxtLiveGraphRetention.Text = cfg.LiveGraphRetentionMinutes.ToString(CultureInfo.InvariantCulture);
        TxtHighCoveragePercent.Text = cfg.HighCoveragePercent.ToString(CultureInfo.InvariantCulture);
        ChkControllerErrors.IsChecked = cfg.EnableControllerErrorAlerts;
        ChkNotify.IsChecked = userSettings.EnableNotifications;
        TxtDatabaseSizeWarnGb.Text = cfg.DatabaseSizeWarnGb.ToString(CultureInfo.InvariantCulture);
        TxtDatabaseSizeCooldown.Text = cfg.DatabaseSizeAlertCooldownHours.ToString(CultureInfo.InvariantCulture);
        TxtBinaryExtensions.Text = cfg.BinaryExtensions;
        TxtTailInitialLines.Text = cfg.TailInitialLines.ToString(CultureInfo.InvariantCulture);
        TxtTailMaxLines.Text = cfg.TailMaxLines.ToString(CultureInfo.InvariantCulture);
        TxtTailMaxReadKb.Text = cfg.TailMaxReadKb.ToString(CultureInfo.InvariantCulture);
        TxtTailMaxBufferKb.Text = cfg.TailMaxBufferKb.ToString(CultureInfo.InvariantCulture);
        UpdateDatabaseSizeCaption();

        ChkTrackFileTargets.IsChecked = cfg.TrackFileTargets;
        TxtFileTargetsPerProcess.Text = cfg.FileTargetsPerProcessPerMinute.ToString(CultureInfo.InvariantCulture);
        TxtFileTargetMinKb.Text = cfg.FileTargetMinKbPerMinute.ToString(CultureInfo.InvariantCulture);
        TxtFileTargetRetention.Text = cfg.FileTargetRetentionDays.ToString(CultureInfo.InvariantCulture);
        TxtFileTargetTrackingLimit.Text = cfg.FileTargetTrackingLimit.ToString(CultureInfo.InvariantCulture);

        // Web TBW lookup settings.
        ChkTbwLookup.IsChecked = userSettings.EnableTbwWebLookup;
        SelectProviderItem(userSettings.WebSearchProvider);
        RadTbwLookupFoundry.IsChecked = userSettings.TbwLookupMethod == TbwLookupMethod.FoundryLocal;
        RadTbwLookupSerperOnly.IsChecked = userSettings.TbwLookupMethod == TbwLookupMethod.SerperOnly;
        var secrets = AiSecretsStore.Load();
        TxtGoogleKey.Text = secrets.GoogleApiKey ?? "";
        TxtGoogleCx.Text = secrets.GoogleCseId ?? "";
        TxtSerperKey.Password = secrets.SerperApiKey ?? "";
        LoadAppUpdateSettings(userSettings);

        LoadTbwField();
        LoadEnduranceAlertFields();
    }

    private void LoadTbwField()
    {
        var disk = SelectedDisk;
        if (disk is null)
        {
            TbwSettingsSection.Visibility = Visibility.Collapsed;
            TxtTbw.Text = "";
            TxtTbwUpper.Text = "";
            _loadedTbwDiskId = null;
            _loadedTbwLower = null;
            _loadedTbwUpper = null;
            _loadedTbwHadOverride = false;
            RadTbwRange.IsChecked = true;
            return;
        }
        TbwSettingsSection.Visibility = Visibility.Visible;
        var cfg = _config.Current;
        var label = string.IsNullOrWhiteSpace(disk.Volumes) ? $"Disk {disk.DiskId}" : disk.Volumes.Trim();
        TbwLabel.Text = $"TBW setting for {label}";
        double lower = cfg.EffectiveTbw(disk.DiskId);
        var upper = cfg.EffectiveTbwUpper(disk.DiskId);
        TxtTbw.Text = lower.ToString(CultureInfo.InvariantCulture);
        TxtTbwUpper.Text = upper.HasValue ? upper.Value.ToString(CultureInfo.InvariantCulture) : "";
        _loadedTbwDiskId = disk.DiskId;
        _loadedTbwLower = lower;
        _loadedTbwUpper = upper;
        _loadedTbwHadOverride = cfg.DiskTbwRatings.ContainsKey(disk.DiskId);
        RadTbwRange.IsChecked = upper.HasValue;
        RadTbwSingle.IsChecked = !upper.HasValue;
    }

    private void LoadEnduranceAlertFields()
    {
        AppConfig config = _config.Current;
        DiskInfo? disk = SelectedDisk;
        bool hasOverride = disk is not null
            && config.DiskEnduranceAlertOverrides.ContainsKey(disk.DiskId);
        EnduranceAlertThreshold threshold = disk is null
            ? AppConfig.CloneEnduranceAlert(config.DefaultEnduranceAlert)
            : config.EffectiveEnduranceAlert(disk.DiskId);

        EnduranceAlertScopeText.Text = disk is null
            ? "Default for every SSD unless that disk has an override."
            : $"{disk.DisplayName}. Uses the all-disks default unless overridden here.";
        ChkEnduranceAlertOverride.Visibility = disk is null ? Visibility.Collapsed : Visibility.Visible;
        ChkEnduranceAlertOverride.IsChecked = hasOverride;
        ChkEnduranceLife.IsChecked = threshold.EnableProjectedLife;
        TxtEnduranceLifeValue.Text = threshold.RemainingLifeValue.ToString(CultureInfo.InvariantCulture);
        ChkEndurancePercent.IsChecked = threshold.EnableRemainingPercent;
        TxtEnduranceRemainingPercent.Text = threshold.RemainingPercent.ToString(CultureInfo.InvariantCulture);
        SelectEnduranceUnit(threshold.RemainingLifeUnit);

        bool editable = disk is null || hasOverride;
        SetEnduranceAlertEditorEnabled(editable);
        EnduranceAlertInheritanceText.Text = disk is null
            ? "Default: warn below 1 year projected remaining life or at/below 20% endurance remaining (above 80% used)."
            : hasOverride
                ? "This disk uses the values above instead of the all-disks default."
                : "Inherited from the all-disks default. Enable the override to set different thresholds for this disk.";
    }

    private void SelectEnduranceUnit(EnduranceAlertTimeUnit unit)
    {
        foreach (object itemObject in EnduranceLifeUnitSelector.Items)
        {
            if (itemObject is System.Windows.Controls.ComboBoxItem item
                && string.Equals(item.Tag?.ToString(), unit.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                EnduranceLifeUnitSelector.SelectedItem = item;
                return;
            }
        }
        EnduranceLifeUnitSelector.SelectedIndex = 2;
    }

    private void SetEnduranceAlertEditorEnabled(bool enabled)
    {
        ChkEnduranceLife.IsEnabled = enabled;
        TxtEnduranceLifeValue.IsEnabled = enabled;
        EnduranceLifeUnitSelector.IsEnabled = enabled;
        ChkEndurancePercent.IsEnabled = enabled;
        TxtEnduranceRemainingPercent.IsEnabled = enabled;
    }

    private void EnduranceAlertOverride_Changed(object sender, RoutedEventArgs e)
    {
        if (SelectedDisk is null)
            return;
        bool enabled = ChkEnduranceAlertOverride.IsChecked == true;
        SetEnduranceAlertEditorEnabled(enabled);
        EnduranceAlertInheritanceText.Text = enabled
            ? "This disk will use the values above instead of the all-disks default after you save."
            : "Inherited from the all-disks default. Save to remove this disk's override.";
    }

    internal static bool TryParseEnduranceAlert(
        bool enableLife,
        string lifeValueText,
        EnduranceAlertTimeUnit unit,
        bool enablePercent,
        string percentText,
        out EnduranceAlertThreshold threshold,
        out string error)
    {
        threshold = new EnduranceAlertThreshold
        {
            EnableProjectedLife = enableLife,
            RemainingLifeUnit = unit,
            EnableRemainingPercent = enablePercent,
        };
        if (!enableLife && !enablePercent)
        {
            error = "Enable at least one endurance alert condition.";
            return false;
        }
        if (!double.TryParse(lifeValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out double lifeValue)
            || !double.IsFinite(lifeValue)
            || (enableLife && lifeValue <= 0))
        {
            error = "Projected remaining life must be greater than 0.";
            return false;
        }
        if (!double.TryParse(percentText, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent)
            || !double.IsFinite(percent)
            || (enablePercent && percent is < 0 or > 100))
        {
            error = "Endurance remaining must be between 0 and 100%.";
            return false;
        }
        threshold.RemainingLifeValue = Math.Max(0, lifeValue);
        threshold.RemainingPercent = Math.Clamp(percent, 0, 100);
        error = "";
        return true;
    }

    private EnduranceAlertTimeUnit SelectedEnduranceUnit()
        => ParseEnduranceUnit(EnduranceLifeUnitSelector.SelectedItem);

    internal static EnduranceAlertTimeUnit ParseEnduranceUnit(object? selectedItem)
        => Enum.TryParse(
            (selectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString(),
            ignoreCase: true,
            out EnduranceAlertTimeUnit unit)
            ? unit
            : EnduranceAlertTimeUnit.Years;

    internal void TbwMode_Checked(object sender, RoutedEventArgs e)
    {
        if (TbwUpperPanel is null || TbwLowerLabel is null)
            return;

        bool range = RadTbwRange.IsChecked == true;
        TbwUpperPanel.Visibility = range ? Visibility.Visible : Visibility.Collapsed;
        TbwLowerLabel.Text = range ? "Minimum TBW (TB)" : "TBW rating (TB)";
    }

    internal void TbwLookupMethod_Checked(object sender, RoutedEventArgs e)
    {
        if (TbwProviderSelector is null || TbwLookupMethodHint is null)
            return;

        bool serperOnly = RadTbwLookupSerperOnly.IsChecked == true;
        if (serperOnly)
            SelectProviderItem("serper");
        TbwProviderSelector.IsEnabled = !serperOnly;
        TbwLookupMethodHint.Text = serperOnly
            ? "Uses only explicit, capacity-matched values in Serper evidence. Foundry Local is not installed or started."
            : "Foundry verifies search evidence with an on-device model before candidates are shown.";
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

    internal static bool TryParseTbwRating(
        string lowerText,
        string upperText,
        bool useRange,
        out double lower,
        out double? upper,
        out string error)
    {
        upper = null;
        if (!double.TryParse(lowerText, NumberStyles.Float, CultureInfo.InvariantCulture, out lower)
            || !double.IsFinite(lower)
            || lower <= 0)
        {
            error = useRange ? "Enter a minimum TBW greater than 0." : "Enter a TBW rating greater than 0.";
            return false;
        }

        if (useRange)
        {
            if (!double.TryParse(upperText, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedUpper)
                || !double.IsFinite(parsedUpper)
                || parsedUpper <= lower)
            {
                error = "Maximum TBW must be greater than minimum TBW.";
                return false;
            }
            upper = parsedUpper;
        }

        error = "";
        return true;
    }

    internal void Save_Click(object sender, RoutedEventArgs e)
    {
        bool enableNotifications = ChkNotify.IsChecked == true;
        var disk = SelectedDisk;
        bool enableTbwWebLookup = ChkTbwLookup.IsChecked == true;
        var lookupMethod = RadTbwLookupSerperOnly.IsChecked == true
            ? TbwLookupMethod.SerperOnly
            : TbwLookupMethod.FoundryLocal;
        string? webSearchProvider = null;
        if ((TbwProviderSelector.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() is string prov && prov.Length > 0)
            webSearchProvider = prov;
        if (lookupMethod == TbwLookupMethod.SerperOnly)
            webSearchProvider = "serper";

        if (!TryParseHighCoveragePercent(TxtHighCoveragePercent.Text, out double highCoveragePercent))
        {
            SaveStatus.Text = "High monitoring coverage must be between 1 and 100%.";
            return;
        }

        var appUpdateMode = SelectedAppUpdateMode();
        if (!TryParseMaximumInstallerSize(TxtMaxInstallerSizeMb.Text, out int maxInstallerSizeMb))
        {
            SaveStatus.Text = "Maximum installer size must be greater than 0 MB.";
            return;
        }
        bool requestAutomaticUpdateCheck = appUpdateMode == Core.Updates.AppUpdateCheckMode.Automatic
            && _userSettings.Current.AppUpdateCheckMode != Core.Updates.AppUpdateCheckMode.Automatic;

        double? tbwLower = null;
        double? tbwUpper = null;
        bool useTbwRange = RadTbwRange.IsChecked == true;
        if (disk is not null)
        {
            if (!TryParseTbwRating(
                    TxtTbw.Text,
                    TxtTbwUpper.Text,
                    useTbwRange,
                    out double lower,
                    out double? upper,
                    out string error))
            {
                SaveStatus.Text = error;
                return;
            }
            tbwLower = lower;
            tbwUpper = upper;
        }

        EnduranceAlertThreshold? enduranceAlert = null;
        bool writeEnduranceAlert = disk is null || ChkEnduranceAlertOverride.IsChecked == true;
        if (writeEnduranceAlert
            && !TryParseEnduranceAlert(
                ChkEnduranceLife.IsChecked == true,
                TxtEnduranceLifeValue.Text,
                SelectedEnduranceUnit(),
                ChkEndurancePercent.IsChecked == true,
                TxtEnduranceRemainingPercent.Text,
                out enduranceAlert,
                out string enduranceError))
        {
            SaveStatus.Text = enduranceError;
            return;
        }

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
            cfg.LiveGraphRetentionMinutes = (int)Math.Clamp(ParseOr(TxtLiveGraphRetention.Text, cfg.LiveGraphRetentionMinutes), 1, 120);
            cfg.HighCoveragePercent = highCoveragePercent;
            cfg.EnableControllerErrorAlerts = ChkControllerErrors.IsChecked == true;

            cfg.TrackFileTargets = ChkTrackFileTargets.IsChecked == true;
            cfg.FileTargetsPerProcessPerMinute = (int)Math.Clamp(ParseOr(TxtFileTargetsPerProcess.Text, cfg.FileTargetsPerProcessPerMinute), 1, 200);
            cfg.FileTargetMinKbPerMinute = ParseOr(TxtFileTargetMinKb.Text, cfg.FileTargetMinKbPerMinute);
            cfg.FileTargetRetentionDays = (int)Math.Clamp(ParseOr(TxtFileTargetRetention.Text, cfg.FileTargetRetentionDays), 1, 365);
            cfg.FileTargetTrackingLimit = (int)Math.Clamp(ParseOr(TxtFileTargetTrackingLimit.Text, cfg.FileTargetTrackingLimit), 100, 500000);

            // 0 GB disables the size warning; the upper bound keeps the threshold meaningful.
            cfg.DatabaseSizeWarnGb = Math.Clamp(ParseOr(TxtDatabaseSizeWarnGb.Text, cfg.DatabaseSizeWarnGb), 0, 4096);
            cfg.DatabaseSizeAlertCooldownHours = (int)Math.Clamp(ParseOr(TxtDatabaseSizeCooldown.Text, cfg.DatabaseSizeAlertCooldownHours), 1, 720);
            cfg.BinaryExtensions = NormalizeExtensionList(TxtBinaryExtensions.Text, cfg.BinaryExtensions);
            cfg.TailInitialLines = (int)Math.Clamp(ParseOr(TxtTailInitialLines.Text, cfg.TailInitialLines), 10, 20000);
            cfg.TailMaxLines = (int)Math.Clamp(ParseOr(TxtTailMaxLines.Text, cfg.TailMaxLines), 100, 200000);
            cfg.TailMaxReadKb = (int)Math.Clamp(ParseOr(TxtTailMaxReadKb.Text, cfg.TailMaxReadKb), 64, 16384);
            cfg.TailMaxBufferKb = (int)Math.Clamp(ParseOr(TxtTailMaxBufferKb.Text, cfg.TailMaxBufferKb), 128, 32768);

            if (disk is null)
            {
                cfg.DefaultEnduranceAlert = enduranceAlert!;
                return;
            }

            if (ChkEnduranceAlertOverride.IsChecked == true)
                cfg.DiskEnduranceAlertOverrides[disk.DiskId] = enduranceAlert!;
            else
                cfg.DiskEnduranceAlertOverrides.Remove(disk.DiskId);

            bool unchangedInheritedEstimate = !_loadedTbwHadOverride
                && _loadedTbwDiskId == disk.DiskId
                && _loadedTbwLower == tbwLower
                && _loadedTbwUpper == tbwUpper;
            if (unchangedInheritedEstimate)
            {
                cfg.DiskTbwRatings.Remove(disk.DiskId);
                cfg.DiskTbwRatingsUpper.Remove(disk.DiskId);
            }
            else
            {
                cfg.DiskTbwRatings[disk.DiskId] = tbwLower!.Value;
                if (tbwUpper.HasValue)
                    cfg.DiskTbwRatingsUpper[disk.DiskId] = tbwUpper.Value;
                else
                    cfg.DiskTbwRatingsUpper.Remove(disk.DiskId);
            }
        });
        _userSettings.Update(settings =>
        {
            settings.EnableNotifications = enableNotifications;
            settings.EnableTbwWebLookup = enableTbwWebLookup;
            settings.TbwLookupMethod = lookupMethod;
            if (webSearchProvider is not null)
                settings.WebSearchProvider = webSearchProvider;
            settings.AppUpdateCheckMode = appUpdateMode;
            settings.MaxInstallerSizeMb = maxInstallerSizeMb;
        });
        SaveStatus.Text = "Saved \u2713";
        if (requestAutomaticUpdateCheck)
            AutomaticUpdateCheckRequested();
        ApplyRefreshInterval();
        ApplyLiveDiskRefreshInterval();
        LoadSettingsFields();
        RefreshAll();
    }

    private static double ParseOr(string text, double fallback)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= 0 ? v : fallback;

    internal static bool TryParseHighCoveragePercent(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value)
            && value is >= 1 and <= 100;

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
        TxtSuspendMinutes.Text = _userSettings.Current.DefaultSuspendMinutes.ToString(CultureInfo.InvariantCulture);
        ProcessPickList.ItemsSource = _repo.GetKnownProcessNames();
    }

    private void RefreshSuspended()
    {
        var states = _repo.GetSuspendedProcessStates();
        var nowUtc = DateTime.UtcNow;

        SuspendedList.ItemsSource = states
            .Select(s => new SuspendedRow(s.Name, $"{s.Name}  \u00b7  suspended {LocalTimeDisplay.FormatUtcWithZone(s.SuspendedUtc, "h:mm tt")}"))
            .ToList();
        SuspendedHeader.Visibility = states.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        SuspendedProcessList.ItemsSource = states
            .Select(s => new SuspendedProcessRow(
                s.Name,
                SourceLabel(s.Source),
                FormatSuspensionDetail(s, nowUtc),
                $"Resume {s.Name}"))
            .ToList();
        SuspendedProcessEmpty.Visibility = states.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResumeAllSuspendedButton.Visibility = states.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    internal static string SourceLabel(SuspendSource source)
        => source == SuspendSource.AutoRule ? "Auto-suspend rule" : "Suspended by you";

    /// <summary>Describes when a process was suspended and when (or whether) it comes back.</summary>
    internal static string FormatSuspensionDetail(SuspendedProcessState state, DateTime nowUtc)
    {
        string suspended = $"Suspended {LocalTimeDisplay.FormatUtcWithZone(state.SuspendedUtc, "h:mm tt")}";
        if (state.ResumeAtUtc is not DateTime resumeAt)
            return $"{suspended}. Stays suspended until you resume it.";

        string at = LocalTimeDisplay.FormatUtcWithZone(resumeAt, "h:mm tt");
        double minutesLeft = (resumeAt - nowUtc).TotalMinutes;
        if (minutesLeft <= 0)
            return $"{suspended}. Interval elapsed - resuming now.";

        string remaining = minutesLeft < 1
            ? "less than a minute"
            : minutesLeft < 60
                ? $"{Math.Round(minutesLeft)} min"
                : $"{minutesLeft / 60:0.#} h";
        return $"{suspended}. Resumes automatically at {at} (in {remaining}).";
    }

    private void ResumeSuspendedProcess_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SuspendedProcessRow row)
            return;
        ReportResume(row.Name, AutoSuspendManager.ResumeTracked(_repo, row.Name));
        RefreshSuspended();
    }

    private void ResumeAllSuspended_Click(object sender, RoutedEventArgs e)
    {
        var names = _repo.GetSuspendedProcessStates().Select(s => s.Name).ToList();
        if (names.Count == 0) return;

        int resumed = 0;
        var failed = new List<string>();
        foreach (var name in names)
        {
            var result = AutoSuspendManager.ResumeTracked(_repo, name);
            if (result.Affected > 0) resumed++;
            else failed.Add(name);
        }

        SetSuspendedStatus(failed.Count == 0
            ? $"Resumed {resumed} process(es)."
            : $"Resumed {resumed} process(es). Could not resume: {string.Join(", ", failed)}.");
        RefreshSuspended();
    }

    private void ReportResume(string name, ProcessControl.Result result)
        => SetSuspendedStatus(result.IdentityUnavailable
            ? $"Could not safely resume '{name}' because its exact process identity is unavailable."
            : result.AccessDenied
                ? $"Could not resume '{name}' (access denied - it may require elevation)."
                : result.Affected > 0
                    ? $"Resumed '{name}'."
                    : $"'{name}' is no longer running.");

    private void SetSuspendedStatus(string message)
    {
        SuspendedProcessStatus.Text = message;
        SuspendedProcessStatus.Visibility = Visibility.Visible;
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
        _userSettings.Update(settings =>
        {
            settings.AutoSuspendRules = rules;
            settings.DefaultSuspendMinutes = ParseSuspendMinutes(TxtSuspendMinutes.Text, settings.DefaultSuspendMinutes);
        });
        SuspendStatus.Text = "Saved \u2713";
        LoadSuspendRules();
    }

    /// <summary>Clamped to a day so a mistyped value cannot strand a process suspended for weeks.</summary>
    internal static int ParseSuspendMinutes(string? text, int fallback)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes >= 0
            ? Math.Min(minutes, 24 * 60)
            : fallback;

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
        SettingsHeader.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
        DashboardPanel.Visibility = showSettings ? Visibility.Collapsed : Visibility.Visible;
        GearButton.ToolTip = showSettings ? "Back to dashboard" : "Settings";
        BodyScroller.ScrollToTop();
    }

    // ----------------------------------------------------------- Rated-TBW web lookup

    internal static bool ShouldPromptTbwOnlineSetup(UserSettings settings, AiSecrets secrets)
        => !settings.SuppressTbwOnlineSetupPrompt && string.IsNullOrWhiteSpace(secrets.SerperApiKey);

    internal void ShowTbwOnlineSetup()
    {
        ClearTbwSetupSecretEntry();
        TbwSetupIntroPanel.Visibility = Visibility.Visible;
        TbwSetupKeyPanel.Visibility = Visibility.Collapsed;
        TbwSetupIntroButtons.Visibility = Visibility.Visible;
        TbwSetupKeyButtons.Visibility = Visibility.Collapsed;
        TbwSetupDontShowAgain.IsChecked = false;
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
        HideTbwSetupSecretEntry();
        TbwSetupIntroPanel.Visibility = Visibility.Visible;
        TbwSetupKeyPanel.Visibility = Visibility.Collapsed;
        TbwSetupIntroButtons.Visibility = Visibility.Visible;
        TbwSetupKeyButtons.Visibility = Visibility.Collapsed;
        TbwSetupErrorText.Visibility = Visibility.Collapsed;
    }

    private void TbwSetupNotNow_Click(object sender, RoutedEventArgs e)
    {
        SaveTbwSetupSuppressionIfRequested();
        ClearTbwSetupSecretEntry();
        TbwSetupOverlay.Visibility = Visibility.Collapsed;
    }

    private void TbwSetupRevealKey_Checked(object sender, RoutedEventArgs e)
    {
        TbwSetupSerperKeyReveal.Text = TbwSetupSerperKey.Password;
        TbwSetupSerperKey.Visibility = Visibility.Collapsed;
        TbwSetupSerperKeyReveal.Visibility = Visibility.Visible;
        TbwSetupSerperKeyRevealGlyph.Text = "\uE8F5";
        TbwSetupSerperKeyRevealButton.ToolTip = "Hide API key";
        AutomationProperties.SetName(TbwSetupSerperKeyRevealButton, "Hide API key");
        TbwSetupSerperKeyReveal.Focus();
        TbwSetupSerperKeyReveal.CaretIndex = TbwSetupSerperKeyReveal.Text.Length;
    }

    private void TbwSetupRevealKey_Unchecked(object sender, RoutedEventArgs e)
    {
        HideTbwSetupSecretEntry();
        TbwSetupSerperKey.Focus();
    }

    private void HideTbwSetupSecretEntry()
    {
        if (TbwSetupSerperKeyReveal.Visibility == Visibility.Visible)
            TbwSetupSerperKey.Password = TbwSetupSerperKeyReveal.Text;
        TbwSetupSerperKeyReveal.Clear();
        TbwSetupSerperKeyReveal.Visibility = Visibility.Collapsed;
        TbwSetupSerperKey.Visibility = Visibility.Visible;
        TbwSetupSerperKeyRevealButton.IsChecked = false;
        TbwSetupSerperKeyRevealGlyph.Text = "\uE890";
        TbwSetupSerperKeyRevealButton.ToolTip = "Show API key";
        AutomationProperties.SetName(TbwSetupSerperKeyRevealButton, "Show API key");
    }

    private void ClearTbwSetupSecretEntry()
    {
        HideTbwSetupSecretEntry();
        TbwSetupSerperKey.Password = "";
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
        string key = (TbwSetupSerperKeyReveal.Visibility == Visibility.Visible
            ? TbwSetupSerperKeyReveal.Text
            : TbwSetupSerperKey.Password).Trim();
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
            ClearTbwSetupSecretEntry();
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

    private void EditRatedTbw_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDisk is { } disk)
            ShowTbwEditor(disk);
    }

    internal void ShowTbwEditor(DiskInfo disk)
    {
        var cfg = _config.Current;
        double lower = cfg.EffectiveTbw(disk.DiskId);
        double? upper = cfg.EffectiveTbwUpper(disk.DiskId);
        bool savedForDrive = cfg.DiskTbwRatings.ContainsKey(disk.DiskId);

        _tbwEditDisk = disk;
        TbwEditTargetText.Text = disk.DisplayName;
        TbwEditCurrentText.Text = upper.HasValue
            ? $"{lower:0.#} to {upper:0.#} TBW{(savedForDrive ? " - saved for this drive" : " - default estimate")}"
            : $"{lower:0.#} TBW{(savedForDrive ? " - saved for this drive" : " - default estimate")}";
        TbwEditLowerText.Text = lower.ToString(CultureInfo.InvariantCulture);
        TbwEditUpperText.Text = upper?.ToString(CultureInfo.InvariantCulture) ?? "";
        TbwEditRange.IsChecked = upper.HasValue;
        TbwEditSingle.IsChecked = !upper.HasValue;
        TbwEditErrorText.Visibility = Visibility.Collapsed;
        UpdateTbwEditMode();
        TbwEditOverlay.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TbwEditLowerText.Focus();
            TbwEditLowerText.SelectAll();
        }));
    }

    private void TbwEditMode_Checked(object sender, RoutedEventArgs e) => UpdateTbwEditMode();

    private void UpdateTbwEditMode()
    {
        if (TbwEditUpperPanel is null || TbwEditModeHint is null || TbwEditLowerLabel is null)
            return;

        bool useRange = TbwEditRange.IsChecked == true;
        TbwEditUpperPanel.Visibility = useRange ? Visibility.Visible : Visibility.Collapsed;
        TbwEditLowerLabel.Text = useRange ? "Minimum TBW (TB)" : "Rated TBW (TB)";
        TbwEditModeHint.Text = useRange
            ? "Use a range when credible sources publish different ratings. Wear and lifespan will be shown as ranges."
            : "Use a single value when the manufacturer publishes one rating for this exact drive capacity.";
        UpdateTbwEditPreview();
    }

    private void TbwEditValues_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (TbwEditPreviewText is null || TbwEditErrorText is null)
            return;
        TbwEditErrorText.Visibility = Visibility.Collapsed;
        UpdateTbwEditPreview();
    }

    private void UpdateTbwEditPreview()
    {
        if (TbwEditPreviewText is null || TbwEditLowerText is null || TbwEditUpperText is null)
            return;

        bool useRange = TbwEditRange.IsChecked == true;
        if (!TryParseTbwRating(
                TbwEditLowerText.Text,
                TbwEditUpperText.Text,
                useRange,
                out double lower,
                out double? upper,
                out string error))
        {
            TbwEditPreviewText.Text = error;
            TbwEditPreviewText.Foreground = (Brush)FindResource("TextSecondary");
            return;
        }

        TbwEditPreviewText.Text = upper.HasValue
            ? $"The dashboard will use {lower:0.#} to {upper:0.#} TBW and immediately recalculate wear and lifespan as ranges."
            : $"The dashboard will use {lower:0.#} TBW and immediately recalculate wear and projected lifespan.";
        TbwEditPreviewText.Foreground = (Brush)FindResource("TextPrimary");
    }

    private void TbwEditSave_Click(object sender, RoutedEventArgs e)
    {
        var disk = _tbwEditDisk;
        if (disk is null)
            return;

        bool useRange = TbwEditRange.IsChecked == true;
        if (!TryParseTbwRating(
                TbwEditLowerText.Text,
                TbwEditUpperText.Text,
                useRange,
                out double lower,
                out double? upper,
                out string error))
        {
            TbwEditErrorText.Text = error;
            TbwEditErrorText.Visibility = Visibility.Visible;
            return;
        }

        _config.Update(cfg =>
        {
            cfg.DiskTbwRatings[disk.DiskId] = lower;
            if (upper.HasValue)
                cfg.DiskTbwRatingsUpper[disk.DiskId] = upper.Value;
            else
                cfg.DiskTbwRatingsUpper.Remove(disk.DiskId);
        });

        CloseTbwEditor();
        LoadTbwField();
        RefreshAll();
    }

    private void TbwEditClose_Click(object sender, RoutedEventArgs e) => CloseTbwEditor();

    private void CloseTbwEditor()
    {
        TbwEditOverlay.Visibility = Visibility.Collapsed;
        _tbwEditDisk = null;
        TbwEditErrorText.Visibility = Visibility.Collapsed;
    }

    private void TbwEditOverlay_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape)
            return;
        CloseTbwEditor();
        e.Handled = true;
    }

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
        SetTbwLookupDiagnostics(null);
        _tbwLookupForceRequested = force;
        if (disk is null)
        {
            TbwLookupPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var userSettings = _userSettings.Current;
        UpdateTbwLookupMethodUi(userSettings.TbwLookupMethod);

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
        if (!force && (!userSettings.EnableTbwWebLookup || cfg.DiskTbwRatings.ContainsKey(disk.DiskId)))
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

        if (!force && TbwLookupCache.TryGet(model, out var cachedResult)
            && cachedResult is not null
            && cachedResult.LookupMethod == userSettings.TbwLookupMethod)
        { RenderTbwResult(disk, cachedResult); return; }

        _tbwCts = new CancellationTokenSource();
        var ct = _tbwCts.Token;
        bool serperOnly = userSettings.TbwLookupMethod == TbwLookupMethod.SerperOnly;
        SetTbwLookupProgress(
            serperOnly ? "Preparing Serper evidence search" : "Preparing local verification",
            serperOnly
                ? $"Preparing deterministic evidence parsing for \u201C{model}\u201D\u2026"
                : $"Preparing the on-device model to look up \u201C{model}\u201D endurance\u2026");

        try
        {
            var svc = TbwLookup;
            var readiness = await svc.GetReadinessAsync(ct);
            if (ct.IsCancellationRequested) return;
            if (!HandleTbwReadiness(readiness))
                return;

            var progress = new Progress<TbwLookupProgress>(p =>
                DispatchTbwLookupProgress(p, serperOnly, model));

            var result = await svc.LookupAsync(model, force, progress, ct);
            if (ct.IsCancellationRequested) return;
            RenderTbwResult(disk, result);
        }
        catch (OperationCanceledException) { /* superseded by a newer selection */ }
    }

    internal void DispatchTbwLookupProgress(TbwLookupProgress progress, bool serperOnly, string model)
    {
        void UpdateProgress() => UpdateTbwLookupProgress(progress.Stage, serperOnly, model);

        if (Dispatcher.CheckAccess())
            UpdateProgress();
        else
            Dispatcher.BeginInvoke(UpdateProgress);
    }

    internal string UpdateTbwLookupProgress(TbwLookupStage stage, bool serperOnly, string model)
    {
        TbwLookupStatus.Text = stage switch
        {
            TbwLookupStage.Searching => SetTbwLookupProgress(
                "Searching web evidence",
                $"Searching Serper for \u201C{model}\u201D endurance specifications\u2026"),
            TbwLookupStage.Analyzing => SetTbwLookupProgress(
                serperOnly ? "Parsing explicit TBW evidence" : "Verifying with the local model",
                serperOnly
                    ? "Accepting only capacity-matched TBW values explicitly present in Serper titles and snippets\u2026"
                    : "Reading the search evidence with the on-device model and rejecting unsupported values\u2026"),
            _ => TbwLookupStatus.Text,
        };
        return TbwLookupStatus.Text;
    }

    internal bool HandleTbwReadiness(TbwReadiness readiness)
    {
        if (readiness.CanRun)
            return true;

        if (readiness.NeedsFoundryInstall)
        {
            SetTbwLookupOutcome(
                "Foundry Local required",
                readiness.Reason ?? "Install Foundry Local to enable on-device verification.",
                TbwLookupOutcome.Unavailable);
            TbwLookupAction.Content = "Install Foundry Local";
            TbwLookupAction.Tag = "install-foundry";
            TbwLookupAction.Visibility = Visibility.Visible;
            TbwLookupAgainButton.Visibility = Visibility.Collapsed;
        }
        else if (readiness.NeedsModelDownload)
        {
            string reason = readiness.HasUsableGpu
                ? $"A GPU was detected. Download the on-device AI model ({readiness.DownloadAlias}) to search the web for this drive's TBW rating."
                : $"Download the on-device AI model ({readiness.DownloadAlias}) to enable the web TBW lookup (CPU-only \u2014 may be slow).";
            SetTbwLookupOutcome("On-device model required", reason, TbwLookupOutcome.Unavailable);
            TbwLookupAction.Content = "Download model";
            TbwLookupAction.Tag = "download";
            TbwLookupAction.Visibility = Visibility.Visible;
            TbwLookupAgainButton.Visibility = Visibility.Collapsed;
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

    private void UpdateTbwLookupMethodUi(TbwLookupMethod method)
    {
        bool serperOnly = method == TbwLookupMethod.SerperOnly;
        TbwLookupAnalysisHeading.Text = serperOnly ? "DETERMINISTIC PARSING" : "LOCAL VERIFICATION";
        TbwLookupAnalysisTitle.Text = serperOnly ? "Serper evidence only" : "On-device model";
        TbwLookupAnalysisText.Text = serperOnly
            ? "Accepts only explicit, capacity-matched TBW values; no local AI is used."
            : "Extracts candidates; unsupported values are rejected.";
        TbwLookupFooterText.Text = "Nothing changes until you explicitly apply a single value or range.";
    }

    /// <summary>Renders the candidate TBW values with confidence scores and per-value Apply buttons.</summary>
    internal void RenderTbwResult(DiskInfo disk, TbwLookupResult result)
    {
        TbwCandidateList.Children.Clear();
        TbwLookupAction.Visibility = Visibility.Collapsed;
        SetTbwLookupDiagnostics(result.Diagnostics);
        UpdateTbwLookupMethodUi(result.LookupMethod);
        bool serperOnly = result.LookupMethod == TbwLookupMethod.SerperOnly;
        if (!result.HasCandidates)
        {
            SetTbwLookupOutcome(
                serperOnly ? "No explicit TBW evidence found" : "No verified TBW rating found",
                result.Note ?? "No TBW rating was found on the web for this drive.",
                TbwLookupOutcome.Empty);
            return;
        }

        SetTbwLookupOutcome(
            serperOnly
                ? result.Candidates.Count == 1 ? "Evidence candidate found" : "Evidence candidates found"
                : result.Candidates.Count == 1 ? "Verified candidate found" : "Verified candidates found",
            serperOnly
                ? $"Serper-only mode found {result.Candidates.Count} explicit, capacity-matched value{(result.Candidates.Count == 1 ? "" : "s")}. No local AI verification was used."
                : result.Candidates.Count == 1
                    ? "One source-backed TBW candidate passed evidence validation. Review it before applying."
                    : $"{result.Candidates.Count} source-backed candidates passed validation. Higher confidence means more independent sources agree.",
            TbwLookupOutcome.Success);

        var textPrimary = (Brush)FindResource("TextPrimary");
        var captionStyle = (Style)FindResource("Caption");
        var toolButton = (Style)FindResource("ToolButton");

        void AddCandidateRow(string title, string detail, string buttonText, Action applyValue)
        {
            var row = new System.Windows.Controls.Grid();
            row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

            var info = new System.Windows.Controls.StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = title,
                Foreground = textPrimary,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
            });
            info.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = detail,
                Style = captionStyle,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0),
            });
            System.Windows.Controls.Grid.SetColumn(info, 0);
            row.Children.Add(info);

            var apply = new System.Windows.Controls.Button
            {
                Content = buttonText,
                Style = toolButton,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };
            apply.Click += (_, _) => applyValue();
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

        var orderedCandidates = result.Candidates.OrderBy(candidate => candidate.TbwTerabytes).ToList();
        if (orderedCandidates.Count > 1)
        {
            double lower = orderedCandidates[0].TbwTerabytes;
            double upper = orderedCandidates[^1].TbwTerabytes;
            AddCandidateRow(
                $"{lower:0.#} to {upper:0.#} TBW range",
                $"Preserves all {orderedCandidates.Count} distinct source-backed values instead of choosing one.",
                "Apply range",
                () => ApplyTbwRange(disk, lower, upper));
        }

        foreach (var candidate in result.Candidates)
        {
            int pct = (int)Math.Round(candidate.Confidence * 100);
            string sources = string.Join(", ", candidate.Sources.Take(3))
                + (candidate.Sources.Count > 3 ? $" +{candidate.Sources.Count - 3}" : "");
            AddCandidateRow(
                $"{candidate.TbwTerabytes:0.#} TBW",
                $"~{pct}% source agreement \u00B7 {candidate.SourceCount} source{(candidate.SourceCount == 1 ? "" : "s")}: {sources}",
                "Apply single",
                () => ApplyTbwCandidate(disk, candidate.TbwTerabytes));
        }
    }

    /// <summary>Applies a chosen TBW value as the drive's per-disk endurance rating.</summary>
    private void ApplyTbwCandidate(DiskInfo disk, double tbw)
    {
        _config.Update(cfg =>
        {
            cfg.DiskTbwRatings[disk.DiskId] = tbw;
            cfg.DiskTbwRatingsUpper.Remove(disk.DiskId);
        });
        TbwCandidateList.Children.Clear();
        TbwLookupAction.Visibility = Visibility.Collapsed;
        SetTbwLookupOutcome(
            $"Applied {tbw:0.#} TBW",
            "The selected endurance rating is now used for lifespan and wear projections. You can change it anytime in Settings.",
            TbwLookupOutcome.Success);
        LoadTbwField();
        RefreshAll();
    }

    private void ApplyTbwRange(DiskInfo disk, double lowerTbw, double upperTbw)
    {
        _config.Update(cfg =>
        {
            cfg.DiskTbwRatings[disk.DiskId] = lowerTbw;
            cfg.DiskTbwRatingsUpper[disk.DiskId] = upperTbw;
        });
        TbwCandidateList.Children.Clear();
        TbwLookupAction.Visibility = Visibility.Collapsed;
        SetTbwLookupOutcome(
            $"Applied {lowerTbw:0.#} to {upperTbw:0.#} TBW",
            "The selected endurance range is now used for lifespan and wear projections. You can change it anytime in Settings.",
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
        CloseTbwRawResults();
        TbwLookupPanel.Visibility = Visibility.Collapsed;
    }

    private void TbwLookupRawResults_Click(object sender, RoutedEventArgs e)
    {
        var diagnostics = _tbwLookupDiagnostics;
        if (diagnostics is null)
            return;

        TbwRawSearchMeta.Text = diagnostics.HasSearchResponse
            ? $"Provider: {diagnostics.SearchProvider}"
            : $"Provider: {diagnostics.SearchProvider} - no response body was returned.";
        TbwRawSearchText.Text = FormatTbwRawResponse(
            diagnostics.SearchResponseJson,
            "No raw search response was returned. The request may have failed before the provider sent a body.");

        TbwRawModelMeta.Text = diagnostics.HasModelResponse
            ? $"Model: {diagnostics.ModelName ?? "Foundry Local model"} - exact completion response"
            : "No model response was requested or returned for this lookup.";
        TbwRawModelText.Text = FormatTbwRawResponse(
            diagnostics.ModelResponseJson,
            "No raw model response is available. Serper-only lookups do not call a model, and failed requests may return no body.");

        TbwRawResultsTabs.SelectedItem = diagnostics.HasSearchResponse ? TbwRawSearchTab : TbwRawModelTab;
        TbwRawResultsOverlay.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var textBox = TbwRawResultsTabs.SelectedItem == TbwRawSearchTab
                ? TbwRawSearchText
                : TbwRawModelText;
            textBox.Focus();
        }));
    }

    private void TbwRawResultsClose_Click(object sender, RoutedEventArgs e) => CloseTbwRawResults();

    private void SetTbwLookupDiagnostics(TbwLookupDiagnostics? diagnostics)
    {
        _tbwLookupDiagnostics = diagnostics;
        TbwLookupRawResultsButton.Visibility = diagnostics is not null
            && (diagnostics.HasSearchResponse || diagnostics.HasModelResponse)
                ? Visibility.Visible
                : Visibility.Collapsed;
        CloseTbwRawResults();
    }

    private void CloseTbwRawResults()
    {
        TbwRawResultsOverlay.Visibility = Visibility.Collapsed;
        TbwRawSearchText.Clear();
        TbwRawModelText.Clear();
    }

    internal static string FormatTbwRawResponse(string? response, string unavailableMessage)
    {
        if (string.IsNullOrWhiteSpace(response))
            return unavailableMessage;

        try
        {
            using var document = JsonDocument.Parse(response);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch (JsonException)
        {
            return response;
        }
    }

    private async void TbwLookupAgain_Click(object sender, RoutedEventArgs e)
    {
        await StartTbwLookupAsync(SelectedDisk, force: true, userInitiated: true);
    }

    /// <summary>Handles dependency actions offered by the rated-TBW lookup modal.</summary>
    private async void TbwLookupAction_Click(object sender, RoutedEventArgs e)
    {
        string? action = (sender as System.Windows.Controls.Button)?.Tag as string;
        var disk = SelectedDisk;
        if (disk is null) return;

        if (action == "install-foundry")
        {
            await InstallFoundryLocalAndRetryAsync(disk);
            return;
        }
        if (action != "download") return;

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

    internal async Task InstallFoundryLocalAndRetryAsync(DiskInfo disk)
    {
        TbwLookupAction.Visibility = Visibility.Collapsed;
        _tbwCts = new CancellationTokenSource();
        var ct = _tbwCts.Token;
        try
        {
            var progress = new Progress<string>(message => TbwLookupStatus.Text = message);
            SetTbwLookupProgress(
                "Installing Foundry Local",
                "Windows Package Manager is preparing the official Microsoft package...");
            await FoundryLocalInstaller(progress, ct);
            if (ct.IsCancellationRequested) return;

            _tbwLookup = null;
            SetTbwLookupProgress(
                "Foundry Local installed",
                "Checking for an on-device verification model...");
            await TbwPostInstallLookup(disk);
        }
        catch (OperationCanceledException) { /* cancelled */ }
        catch (Exception ex)
        {
            SetTbwLookupOutcome("Foundry Local installation failed", ex.Message, TbwLookupOutcome.Error);
            TbwLookupAction.Content = "Try install again";
            TbwLookupAction.Tag = "install-foundry";
            TbwLookupAction.Visibility = Visibility.Visible;
            TbwLookupAgainButton.Visibility = Visibility.Collapsed;
        }
    }

    private void DiskSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        LoadTbwField();
        LoadEnduranceAlertFields();
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
        _trendZoomWindow = null;
        _trendRange = clicked == Btn24h ? TrendRangeKind.H24
            : clicked == Btn7d ? TrendRangeKind.D7
            : clicked == Btn30d ? TrendRangeKind.D30
            : clicked == BtnCustom ? TrendRangeKind.Custom
            : TrendRangeKind.H1;

        Btn1h.IsChecked = _trendRange == TrendRangeKind.H1;
        Btn24h.IsChecked = _trendRange == TrendRangeKind.H24;
        Btn7d.IsChecked = _trendRange == TrendRangeKind.D7;
        Btn30d.IsChecked = _trendRange == TrendRangeKind.D30;
        BtnCustom.IsChecked = _trendRange == TrendRangeKind.Custom;
        TrendCustomRangePanel.Visibility = _trendRange == TrendRangeKind.Custom
            ? Visibility.Visible
            : Visibility.Collapsed;

        IReadOnlyList<DiskInfo> disks = SelectedDisks;
        if (disks.Count > 0) UpdateChart(disks);
    }

    private void CustomTrendApply_Click(object sender, RoutedEventArgs e)
    {
        _trendZoomWindow = null;
        IReadOnlyList<DiskInfo> disks = SelectedDisks;
        if (disks.Count > 0) UpdateChart(disks);
    }

    private void LiveDiskChart_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        int maximum = Math.Clamp(_config.Current.LiveGraphRetentionMinutes, 1, 120);
        int[] steps = [1, 2, 5, 10, 15, 30, 60, 120];
        int[] available = steps.Where(value => value <= maximum).Append(maximum).Distinct().Order().ToArray();
        int current = Array.FindIndex(available, value => value >= _liveGraphWindowMinutes);
        if (current < 0) current = available.Length - 1;
        int next = Math.Clamp(current + (e.Delta > 0 ? -1 : 1), 0, available.Length - 1);
        if (available[next] == _liveGraphWindowMinutes) return;
        _liveGraphWindowMinutes = available[next];
        RefreshLiveDiskActivity();
        e.Handled = true;
    }

    private void TotalWrittenChart_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        TimeSpan[] steps =
        [
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(24),
            TimeSpan.FromDays(3),
            TimeSpan.FromDays(7),
            TimeSpan.FromDays(14),
            TimeSpan.FromDays(30),
            TimeSpan.FromDays(90),
            TimeSpan.FromDays(180),
            TimeSpan.FromDays(365),
        ];
        if (!TryGetTrendRange(DateTime.UtcNow, out DateTime fromUtc, out DateTime toUtc))
            return;
        TimeSpan currentWindow = toUtc - fromUtc;
        int current = Array.FindIndex(steps, value => value >= currentWindow);
        if (current < 0) current = steps.Length - 1;
        int next = Math.Clamp(current + (e.Delta > 0 ? -1 : 1), 0, steps.Length - 1);
        if (steps[next] == currentWindow) return;

        _trendRange = TrendRangeKind.Zoom;
        _trendZoomWindow = steps[next];
        Btn1h.IsChecked = steps[next] == TimeSpan.FromHours(1);
        Btn24h.IsChecked = steps[next] == TimeSpan.FromHours(24);
        Btn7d.IsChecked = steps[next] == TimeSpan.FromDays(7);
        Btn30d.IsChecked = steps[next] == TimeSpan.FromDays(30);
        BtnCustom.IsChecked = false;
        TrendCustomRangePanel.Visibility = Visibility.Collapsed;
        IReadOnlyList<DiskInfo> disks = SelectedDisks;
        if (disks.Count > 0) UpdateChart(disks);
        e.Handled = true;
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
