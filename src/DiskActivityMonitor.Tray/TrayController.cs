using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Threading;
using DiskActivityMonitor.Core;
using DiskActivityMonitor.Core.Ai;
using DiskActivityMonitor.Core.Collection;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Models;
using DiskActivityMonitor.Core.Updates;
using Microsoft.Toolkit.Uwp.Notifications;

namespace DiskActivityMonitor.Tray;

/// <summary>
/// Owns the system-tray icon, its context menu, periodic tooltip/icon refresh, and balloon
/// notifications for new alerts. Lazily creates the dashboard window on demand.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly MonitorRepository _repo;
    private readonly ConfigStore _config;
    private readonly UserSettingsStore _userSettings;
    private readonly NotifyIcon _notifyIcon = new();
    private DarkTrayContextMenu? _trayMenu;
    private readonly DispatcherTimer _timer;
    private readonly AutoSuspendManager _autoSuspend;

    private MainWindow? _window;
    private AlertSeverity? _iconSeverity;
    private Color _currentColor = TrayIconFactory.Ok;
    private long _lastBalloonAlertId;
    private Icon? _currentIcon;
    private readonly DispatcherTimer _toastTimer;
    private readonly Queue<AlertRecord> _toastQueue = new();
    internal Action<ToastContentBuilder> ToastPresenter { get; set; }
    internal Action<int, string, string, ToolTipIcon> BalloonPresenter { get; set; }
    internal Action<string, Exception> ToastErrorLogger { get; set; } = LogToastError;
    internal Action TbwSetupPresenter { get; set; }
    internal Action StartupPromptsRunner { get; set; }
    internal Action DashboardPresenter { get; set; }
    internal Action DataFolderPresenter { get; set; }
    internal Action ExitRequester { get; set; }
    internal Func<string, CancellationToken, Task<AppUpdateCheckResult?>> AppUpdateCheck { get; set; } =
        (version, cancellationToken) => AppUpdateChecker.CheckLatestAsync(version, cancellationToken: cancellationToken);
    internal Action<AppUpdateCheckResult, AppReleaseInfo> AppUpdateAvailablePresenter { get; set; }

    /// <summary>Alerts are treated as timestamped events shown once; the icon color and the
    /// dashboard log reflect only alerts raised within this trailing window, then auto-clear.</summary>
    private static readonly TimeSpan RecentAlertWindow = TimeSpan.FromHours(1);

    public TrayController(MonitorRepository repo, ConfigStore config, UserSettingsStore userSettings)
    {
        _repo = repo;
        _config = config;
        _userSettings = userSettings;
        _autoSuspend = new AutoSuspendManager(repo, userSettings);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) => SafeUpdate();
        // Drains queued alert toasts one at a time so each is visible (Windows replaces an
        // on-screen balloon when a new one is shown).
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _toastTimer.Tick += (_, _) => DrainToastQueue();
        ToastPresenter = typeof(ToastContentBuilder)
            .GetMethod(nameof(ToastContentBuilder.Show), Type.EmptyTypes)!
            .CreateDelegate<Action<ToastContentBuilder>>();
        BalloonPresenter = _notifyIcon.ShowBalloonTip;
        TbwSetupPresenter = () =>
        {
            ShowDashboard();
            _window!.ShowTbwOnlineSetup();
        };
        AppUpdateAvailablePresenter = (check, release) =>
        {
            ShowDashboard();
            _window!.ShowAppUpdateRelease(check, release);
        };
        StartupPromptsRunner = RunStartupPrompts;
        DashboardPresenter = ShowDashboard;
        DataFolderPresenter = OpenDataFolder;
        ExitRequester = () => System.Windows.Application.Current.Shutdown();
    }

    public void Initialize()
    {
        SetIcon(TrayIconFactory.Ok, AlertSeverity.Info);
        _notifyIcon.Visible = true;
        _notifyIcon.Text = "Disk Activity Monitor";
        _notifyIcon.DoubleClick += OnOpenDashboard;

        _trayMenu = new DarkTrayContextMenu();
        _trayMenu.AddCommand("Open dashboard", OnOpenDashboard);
        _trayMenu.AddCommand("Open data folder", OnOpenDataFolder);
        _trayMenu.AddDivider();
        _trayMenu.AddCommand("Exit", OnExitRequested);
        _notifyIcon.ContextMenuStrip = _trayMenu;

        // Suppress balloons for alerts that already existed when the app launched.
        _lastBalloonAlertId = _repo.GetRecentAlerts(1).FirstOrDefault()?.Id ?? 0;

        SafeUpdate();
        _timer.Start();

        StartupPromptsRunner();
    }

    internal void OnOpenDashboard(object? sender, EventArgs e)
        => DashboardPresenter();

    internal void OnOpenDataFolder(object? sender, EventArgs e)
        => DataFolderPresenter();

    internal void OnExitRequested(object? sender, EventArgs e)
        => ExitRequester();

    internal void RunStartupPrompts()
    {
        PromptTbwOnlineSetupIfNeeded();
        if (MainWindow.ShouldPromptAppUpdateConsent(_userSettings.Current))
        {
            ShowDashboard();
            _window!.ShowAppUpdateConsent();
        }
        else
        {
            _ = MaybeRunAutomaticAppUpdateCheckAsync();
        }
    }

    internal bool PromptTbwOnlineSetupIfNeeded()
    {
        if (!MainWindow.ShouldPromptTbwOnlineSetup(_userSettings.Current, AiSecretsStore.Load()))
            return false;
        TbwSetupPresenter();
        return true;
    }

    internal async Task<AppUpdateCheckResult?> MaybeRunAutomaticAppUpdateCheckAsync()
    {
        UserSettings settings = _userSettings.Current;
        if (settings.AppUpdateCheckMode != AppUpdateCheckMode.Automatic
            || !AppUpdateChecker.ShouldAutoCheck(
                settings.LastAppUpdateCheckUtc,
                DateTimeOffset.UtcNow,
                AppUpdateChecker.DefaultAutoCheckInterval))
        {
            return null;
        }

        AppUpdateCheckResult? check;
        try
        {
            check = await AppUpdateCheck(MainWindow.CurrentAppVersion(), CancellationToken.None);
        }
        catch
        {
            check = null;
        }
        _userSettings.Update(value => value.LastAppUpdateCheckUtc = DateTimeOffset.UtcNow);

        if (check?.UpdateAvailable == true && check.Release is { } release
            && !string.Equals(
                release.Version.ToString(),
                _userSettings.Current.LastAppUpdateAlertedVersion,
                StringComparison.Ordinal))
        {
            AppUpdateAvailablePresenter(check, release);
        }
        return check;
    }

    private void SafeUpdate()
    {
        try { Update(); }
        catch { /* a transient DB lock should never crash the tray */ }
    }

    private void Update()
    {
        var disks = _repo.GetDisks();
        // Alerts raised within the trailing window. Dismissed records stay in history but no longer
        // keep the tray icon in a warning state.
        var recent = _repo.GetRecentAlerts(50, sinceUtc: DateTime.UtcNow - RecentAlertWindow);
        var visibleRecent = recent.Where(a => !a.Acknowledged).ToList();

        // Icon color follows the worst non-dismissed alert in the recent window, then returns to green.
        var severity = visibleRecent.Count == 0 ? AlertSeverity.Info : visibleRecent.Max(a => a.Severity);
        var color = severity switch
        {
            AlertSeverity.Critical => TrayIconFactory.Critical,
            AlertSeverity.Warning => TrayIconFactory.Warning,
            _ => TrayIconFactory.Ok,
        };
        if (_iconSeverity != severity)
            SetIcon(color, severity);

        // Tooltip: today's writes on the primary (first SSD) disk.
        var primary = disks.FirstOrDefault(d => d.IsSsd) ?? disks.FirstOrDefault();
        if (primary is not null)
        {
            var midnightUtc = DateTime.Now.Date.ToUniversalTime();
            var todayWrite = _repo.GetDiskTotals(primary.DiskId, midnightUtc, DateTime.UtcNow).Write;
            var label = string.IsNullOrWhiteSpace(primary.Volumes) ? $"Disk {primary.DiskId}" : primary.Volumes.Trim();
            SetTooltip($"Disk Activity Monitor\n{label} today: {ByteFormat.Humanize(todayWrite)} written");
        }

        // Raise a desktop toast only for the FIRST alert of each episode. The collector re-raises
        // the same rule every cooldown while a condition stays tripped; toasting every repeat
        // floods Windows (which then throttles banners) and annoys the user. So we skip a fresh
        // alert when an earlier alert for the same rule already fired inside the recent window.
        // Each toast lands in the Windows notification center (stamped with its time) and stays
        // there until the user clears it, so it is shown exactly once per episode.
        var fresh = _repo.GetRecentAlerts(50)
            .Where(a => a.Id > _lastBalloonAlertId)
            .OrderBy(a => a.Id)
            .ToList();
        if (fresh.Count > 0)
        {
            _lastBalloonAlertId = fresh[^1].Id;
            if (_userSettings.Current.EnableNotifications)
            {
                // Earliest alert id per rule within the recent window = the "episode opener".
                var firstInWindowByRule = recent
                    .GroupBy(a => a.RuleKey)
                    .ToDictionary(g => g.Key, g => g.Min(a => a.Id));

                foreach (var a in fresh)
                {
                    if (firstInWindowByRule.TryGetValue(a.RuleKey, out var firstId) && firstId != a.Id)
                        continue; // a repeat while the episode is still inside the window - don't re-toast
                    _toastQueue.Enqueue(a);
                }

                DrainToastQueue();              // show the first one immediately
                if (_toastQueue.Count > 0)
                    _toastTimer.Start();        // space out the rest
            }
        }

        // Auto-suspend rules: stop (or ask to stop) runaway writers. These toasts are always
        // shown - they are protective prompts, not routine alerts - so they ignore the
        // notification toggle that governs the alert balloons above.
        try
        {
            // One tick is one instant, so resume deadlines and rule windows cannot disagree.
            var nowUtc = DateTime.UtcNow;

            // Hand back control first: a suspension whose interval elapsed must never outlive it.
            foreach (var expired in _autoSuspend.ResumeExpired(nowUtc))
                ShowResumedToast(expired);

            foreach (var ev in _autoSuspend.Evaluate(nowUtc))
            {
                if (ev.Outcome == SuspendOutcome.ConfirmNeeded)
                    ShowSuspendConfirmToast(ev.Rule, ev.WrittenBytes);
                else
                    ShowAutoSuspendedToast(ev.Rule, ev.WrittenBytes, ev.Result);
            }
        }
        catch { /* never crash the tray on suspend evaluation */ }
    }

    private void DrainToastQueue()
    {
        if (_toastQueue.Count == 0)
        {
            _toastTimer.Stop();
            return;
        }

        ShowAlertToast(_toastQueue.Dequeue());
    }

    /// <summary>
    /// Shows a modern Windows toast for an alert. Process alerts can be snoozed or suspended -
    /// each with its own duration picker - while other alerts are informational only.
    /// Falls back to a legacy balloon if toasts are unavailable.
    /// </summary>
    internal void ShowAlertToast(AlertRecord a)
    {
        var text = FormatAlertToastText(a, GetAlertWriteWindows(a));
        try
        {
            var builder = new ToastContentBuilder()
                .AddText(text.Title)
                .AddText(text.Body)
                .SetToastDuration(ToastDuration.Long);

            // Labelled duration picker shared by the snooze buttons below.
            builder.AddComboBox("snoozeDuration", "Snooze for", SnoozeOptions.DefaultId, SnoozeOptions.Choices);

            const string procPrefix = "proc-1h:";
            if (a.RuleKey.StartsWith(procPrefix, StringComparison.Ordinal))
            {
                var process = a.RuleKey[procPrefix.Length..];

                // Suspending is the strongest action offered here, so it gets its own interval
                // picker; the app resumes the process automatically when that interval elapses.
                builder.AddComboBox(
                    "suspendDuration",
                    "Suspend for",
                    SuspendDurationOptions.DefaultIdFor(_userSettings.Current.DefaultSuspendMinutes),
                    SuspendDurationOptions.Choices);

                builder.AddButton(new ToastButton()
                    .SetContent($"Suspend {process}")
                    .AddArgument("action", "suspend")
                    .AddArgument("process", process));

                // Name the process so it's clear exactly what gets snoozed.
                builder.AddButton(new ToastButton()
                    .SetContent($"Snooze {process}")
                    .AddArgument("action", "snooze")
                    .AddArgument("process", process));
            }

                    if (a.RuleKey.StartsWith("endurance-health:", StringComparison.Ordinal))
                    {
                    builder.AddButton(new ToastButton()
                        .SetContent("Snooze this disk")
                        .AddArgument("action", "snooze-rule")
                        .AddArgument("rule", a.RuleKey));
                    }

            builder.AddButton(new ToastButton()
                    .SetContent("Snooze all alerts")
                    .AddArgument("action", "snooze-all"))
                   .AddButton(new ToastButton()
                    .SetContent("Dismiss")
                    .AddArgument("action", "dismiss")
                    .AddArgument("alertId", a.Id.ToString()));

            ToastPresenter(builder);
        }
        catch (Exception ex)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(Paths.BaseDirectory, "toast-error.log"),
                    $"{DateTime.Now:O}  {a.RuleKey}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
            }
            catch { /* ignore logging failures */ }

            var icon = a.Severity == AlertSeverity.Critical ? ToolTipIcon.Error : ToolTipIcon.Warning;
            if (!_notifyIcon.Visible) _notifyIcon.Visible = true;
            BalloonPresenter(8000, text.Title, text.Body, icon);
        }
    }

    internal readonly record struct WriteWindowStats(
        long FiveMinutes,
        long FifteenMinutes,
        long ThirtyMinutes,
        long TwentyFourHours);

    internal WriteWindowStats GetAlertWriteWindows(AlertRecord alert)
    {
        if (alert.RuleKey.StartsWith("proc-1h:", StringComparison.Ordinal))
        {
            string process = alert.RuleKey["proc-1h:".Length..];
            return GetWriteWindows(alert.TimestampUtc, (from, to) => _repo.GetProcessWrite(process, from, to));
        }

        if (alert.RuleKey == "procs-all-1h")
            return GetWriteWindows(alert.TimestampUtc, _repo.GetAllProcessesWrite);

        if (alert.RuleKey.StartsWith("ssd-1h:", StringComparison.Ordinal)
            || alert.RuleKey.StartsWith("ssd-24h:", StringComparison.Ordinal))
        {
            string diskId = alert.RuleKey[(alert.RuleKey.IndexOf(':') + 1)..];
            return GetWriteWindows(alert.TimestampUtc, (from, to) => _repo.GetDiskTotals(diskId, from, to).Write);
        }

        return default;
    }

    private WriteWindowStats GetProcessWriteWindows(string processName, DateTime nowUtc)
        => GetWriteWindows(nowUtc, (from, to) => _repo.GetProcessWrite(processName, from, to));

    internal static WriteWindowStats GetWriteWindows(
        DateTime nowUtc,
        Func<DateTime, DateTime, long> windowWrite)
    {
        var end = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, nowUtc.Minute, 0, DateTimeKind.Utc);
        return new WriteWindowStats(
            windowWrite(end.AddMinutes(-5), end),
            windowWrite(end.AddMinutes(-15), end),
            windowWrite(end.AddMinutes(-30), end),
            windowWrite(end.AddHours(-24), end));
    }

    internal static (string Title, string Body) FormatAlertToastText(AlertRecord alert, WriteWindowStats windows)
    {
        if (alert.RuleKey.StartsWith("proc-1h:", StringComparison.Ordinal))
        {
            string process = alert.RuleKey["proc-1h:".Length..];
            return ($"High logical writes: {process}",
                $"{FormatWriteWindows(windows)}. 1h limit: {ByteFormat.Humanize(alert.Threshold)}.");
        }

        if (alert.RuleKey == "procs-all-1h")
            return ("High combined logical writes",
                $"{FormatWriteWindows(windows)}. 1h limit: {ByteFormat.Humanize(alert.Threshold)}.");

        if (alert.RuleKey.StartsWith("ssd-wear:", StringComparison.Ordinal))
            return (alert.Title,
                $"SMART endurance used: {alert.Value:0.#}% (warning {alert.Threshold:0.#}%). Back up data and plan replacement.");

        if (alert.RuleKey.StartsWith("ssd-1h:", StringComparison.Ordinal))
            return (alert.Title,
                $"{FormatWriteWindows(windows)}. 1h limit: {ByteFormat.Humanize(alert.Threshold)}.");

        if (alert.RuleKey.StartsWith("ssd-24h:", StringComparison.Ordinal))
        {
            string limit = alert.Severity == AlertSeverity.Critical ? "critical limit" : "limit";
            return (alert.Title,
                $"{FormatWriteWindows(windows)}. 24h {limit}: {ByteFormat.Humanize(alert.Threshold)}.");
        }

        if (alert.RuleKey.StartsWith("tbw-life:", StringComparison.Ordinal))
            return (alert.Title,
                $"Projected life: ~{FormatToastYears(alert.Value)} at the recent write rate.");

        if (alert.RuleKey.StartsWith("disk-controller:", StringComparison.Ordinal))
        {
            string countWord = alert.Value == 1 ? "error" : "errors";
            return (alert.Title,
                $"Windows logged {alert.Value:0} Disk event 11 {countWord}. Back up data; check cable, port, power, enclosure, or controller.");
        }

        return (alert.Title, alert.Message);
    }

    private static string FormatWriteWindows(WriteWindowStats windows)
        => $"5m: {ByteFormat.Humanize(windows.FiveMinutes)}, "
            + $"15m: {ByteFormat.Humanize(windows.FifteenMinutes)}, "
            + $"30m: {ByteFormat.Humanize(windows.ThirtyMinutes)}, "
            + $"24h: {ByteFormat.Humanize(windows.TwentyFourHours)}";

    internal static string FormatToastYears(double years)
    {
        if (!double.IsFinite(years) || years <= 0)
            return "an unknown time";
        if (years >= 1)
            return $"{years:0.0} years";
        int months = Math.Max(1, (int)Math.Round(years * 12));
        return months == 1 ? "1 month" : $"{months} months";
    }

    /// <summary>Asks the user (via a toast with a Suspend button) to confirm suspending a heavy writer.</summary>
    internal void ShowSuspendConfirmToast(AutoSuspendRule rule, long written)
    {
        var text = FormatSuspendConfirmationText(rule, GetProcessWriteWindows(rule.ProcessName, DateTime.UtcNow));
        try
        {
            var suspendButton = new ToastButton()
                .SetContent("Suspend now")
                .AddArgument("action", "suspend")
                .AddArgument("process", rule.ProcessName)
                .AddArgument("source", SuspendOriginArguments.Rule);
            if (!string.IsNullOrWhiteSpace(rule.ExecutablePath))
                suspendButton.AddArgument("path", rule.ExecutablePath);

            var builder = new ToastContentBuilder()
                .AddText(text.Title)
                .AddText(text.Body)
                .SetToastDuration(ToastDuration.Long)
                .AddComboBox(
                    "suspendDuration",
                    "Suspend for",
                    SuspendDurationOptions.DefaultIdFor(_userSettings.Current.DefaultSuspendMinutes),
                    SuspendDurationOptions.Choices)
                .AddButton(suspendButton)
                .AddButton(new ToastButton()
                    .SetContent("Ignore")
                    .AddArgument("action", "suspend-ignore")
                    .AddArgument("process", rule.ProcessName));
            ToastPresenter(builder);
        }
        catch (Exception ex)
        {
            ToastErrorLogger($"suspend-confirm:{rule.ProcessName}", ex);
            if (!_notifyIcon.Visible) _notifyIcon.Visible = true;
            BalloonPresenter(8000, text.Title, text.Body, ToolTipIcon.Warning);
        }
    }

    internal static (string Title, string Body) FormatSuspendConfirmationText(
        AutoSuspendRule rule,
        WriteWindowStats windows)
        => ($"High logical writes: {rule.ProcessName}",
            $"{FormatWriteWindows(windows)}. 1h limit: {rule.ThresholdGbPerHour:0.#} GB. Suspend?");

    /// <summary>Notifies the user that an auto-suspend rule fired, offering a Resume button on success.</summary>
    internal void ShowAutoSuspendedToast(AutoSuspendRule rule, long written, ProcessControl.Result result)
    {
        int minutes = _userSettings.Current.DefaultSuspendMinutes;
        var text = FormatAutoSuspendText(
            rule,
            GetProcessWriteWindows(rule.ProcessName, DateTime.UtcNow),
            result,
            minutes);
        try
        {
            var b = new ToastContentBuilder()
                .AddText(text.Title)
                .AddText(text.Body)
                .SetToastDuration(ToastDuration.Long);
            if (result.Affected > 0)
            {
                var resumeButton = new ToastButton()
                    .SetContent("Resume")
                    .AddArgument("action", "resume")
                    .AddArgument("process", rule.ProcessName);
                if (!string.IsNullOrWhiteSpace(rule.ExecutablePath))
                    resumeButton.AddArgument("path", rule.ExecutablePath);
                b.AddButton(resumeButton);
            }
            ToastPresenter(b);
        }
        catch (Exception ex)
        {
            ToastErrorLogger($"auto-suspend:{rule.ProcessName}", ex);
            if (!_notifyIcon.Visible) _notifyIcon.Visible = true;
            BalloonPresenter(8000, text.Title, text.Body, ToolTipIcon.Warning);
        }
    }

    internal static (string Title, string Body) FormatAutoSuspendText(
        AutoSuspendRule rule,
        WriteWindowStats windows,
        ProcessControl.Result result,
        int minutes)
    {
        if (result.Affected > 0)
        {
            string resume = minutes > 0 ? $"Resumes in {minutes} min." : "Resume manually.";
            return ($"{rule.ProcessName} suspended",
                $"{FormatWriteWindows(windows)}. 1h limit: {rule.ThresholdGbPerHour:0.#} GB. {resume}");
        }

        return result.AccessDenied
            ? ($"Could not suspend {rule.ProcessName}", "Write limit exceeded; access denied. Elevation may be required.")
            : ($"{rule.ProcessName} not suspended", "Write limit exceeded, but the process had exited.");
    }

    /// <summary>Tells the user a suspension interval elapsed and the process is running again.</summary>
    internal void ShowResumedToast(ExpiredSuspension expired)
    {
        var text = FormatResumeText(expired.ProcessName, expired.Result);
        try
        {
            var builder = new ToastContentBuilder()
                .AddText(text.Title)
                .AddText(text.Body);
            ToastPresenter(builder);
        }
        catch (Exception ex)
        {
            ToastErrorLogger($"auto-resume:{expired.ProcessName}", ex);
            if (!_notifyIcon.Visible) _notifyIcon.Visible = true;
            BalloonPresenter(8000, text.Title, text.Body, ToolTipIcon.Info);
        }
    }

    internal static (string Title, string Body) FormatResumeText(string processName, ProcessControl.Result result)
        => result.Affected > 0
            ? ($"{processName} resumed", "Suspension expired; process resumed automatically.")
            : ($"{processName} no longer suspended", "Suspension expired; process exited or was already resumed.");

    private static void LogToastError(string context, Exception ex)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(Paths.BaseDirectory, "toast-error.log"),
                $"{DateTime.Now:O}  {context}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
        }
        catch { /* ignore logging failures */ }
    }

    private void SetIcon(Color color, AlertSeverity severity)
    {
        var newIcon = TrayIconFactory.Create(color);
        _notifyIcon.Icon = newIcon;
        _currentIcon?.Dispose();
        _currentIcon = newIcon;
        _iconSeverity = severity;
        _currentColor = color;
        // Keep the taskbar/window icon identical to the tray icon when the dashboard is open.
        if (_window is not null)
            _window.Icon = TrayIconFactory.CreateImageSource(color);
    }

    private void SetTooltip(string text)
    {
        // NotifyIcon.Text is capped at 63 characters.
        if (text.Length > 63) text = text[..63];
        _notifyIcon.Text = text;
    }

    private void ShowDashboard()
    {
        if (_window is null)
        {
            _window = new MainWindow(_repo, _config, _userSettings);
            _window.AutomaticUpdateCheckRequested = () => _ = MaybeRunAutomaticAppUpdateCheckAsync();
        }
        _window.Icon = TrayIconFactory.CreateImageSource(_currentColor);
        _window.ShowAndActivate();
    }

    /// <summary>Opens the dashboard window programmatically (used for the --show launch arg).</summary>
    public void OpenDashboard() => ShowDashboard();

    private void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", Paths.BaseDirectory) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        _timer.Stop();
        _toastTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip = null;
        _trayMenu?.Dispose();
        _trayMenu = null;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
        _window?.ForceClose();
    }
}
