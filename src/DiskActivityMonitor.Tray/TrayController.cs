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
    private readonly DispatcherTimer _timer;
    private readonly AutoSuspendManager _autoSuspend;

    private MainWindow? _window;
    private AlertSeverity? _iconSeverity;
    private Color _currentColor = TrayIconFactory.Ok;
    private long _lastBalloonAlertId;
    private Icon? _currentIcon;
    private readonly DispatcherTimer _toastTimer;
    private readonly Queue<AlertRecord> _toastQueue = new();
    internal Action TbwSetupPresenter { get; set; }

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
        TbwSetupPresenter = () =>
        {
            ShowDashboard();
            _window!.ShowTbwOnlineSetup();
        };
    }

    public void Initialize()
    {
        SetIcon(TrayIconFactory.Ok, AlertSeverity.Info);
        _notifyIcon.Visible = true;
        _notifyIcon.Text = "Disk Activity Monitor";
        _notifyIcon.DoubleClick += (_, _) => ShowDashboard();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open dashboard", null, (_, _) => ShowDashboard());
        menu.Items.Add("Open data folder", null, (_, _) => OpenDataFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => System.Windows.Application.Current.Shutdown());
        _notifyIcon.ContextMenuStrip = menu;

        // Suppress balloons for alerts that already existed when the app launched.
        _lastBalloonAlertId = _repo.GetRecentAlerts(1).FirstOrDefault()?.Id ?? 0;

        SafeUpdate();
        _timer.Start();

        PromptTbwOnlineSetupIfNeeded();
    }

    internal bool PromptTbwOnlineSetupIfNeeded()
    {
        if (!MainWindow.ShouldPromptTbwOnlineSetup(_userSettings.Current, AiSecretsStore.Load()))
            return false;
        TbwSetupPresenter();
        return true;
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
            foreach (var ev in _autoSuspend.Evaluate(DateTime.UtcNow))
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
    /// Shows a modern Windows toast for an alert. Process alerts include a snooze-duration
    /// selection box plus Snooze/Dismiss buttons; other alerts are informational only.
    /// Falls back to a legacy balloon if toasts are unavailable.
    /// </summary>
    private void ShowAlertToast(AlertRecord a)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddText(a.Title)
                .AddText(a.Message)
                .SetToastDuration(ToastDuration.Long);

            // Labelled duration picker shared by the snooze buttons below.
            builder.AddComboBox("snoozeDuration", "Snooze for", SnoozeOptions.DefaultId, SnoozeOptions.Choices);

            const string procPrefix = "proc-1h:";
            if (a.RuleKey.StartsWith(procPrefix, StringComparison.Ordinal))
            {
                var process = a.RuleKey[procPrefix.Length..];
                // Name the process so it's clear exactly what gets snoozed.
                builder.AddButton(new ToastButton()
                    .SetContent($"Snooze {process}")
                    .AddArgument("action", "snooze")
                    .AddArgument("process", process));
            }

            builder.AddButton(new ToastButton()
                    .SetContent("Snooze all alerts")
                    .AddArgument("action", "snooze-all"))
                   .AddButton(new ToastButton()
                    .SetContent("Dismiss")
                    .AddArgument("action", "dismiss")
                    .AddArgument("alertId", a.Id.ToString()));

            builder.Show();
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
            _notifyIcon.ShowBalloonTip(8000, a.Title, a.Message, icon);
        }
    }

    /// <summary>Asks the user (via a toast with a Suspend button) to confirm suspending a heavy writer.</summary>
    private void ShowSuspendConfirmToast(AutoSuspendRule rule, long written)
    {
        try
        {
            var suspendButton = new ToastButton()
                .SetContent("Suspend now")
                .AddArgument("action", "suspend")
                .AddArgument("process", rule.ProcessName);
            if (!string.IsNullOrWhiteSpace(rule.ExecutablePath))
                suspendButton.AddArgument("path", rule.ExecutablePath);

            new ToastContentBuilder()
                .AddText($"{rule.ProcessName} is requesting heavy file writes")
                .AddText($"{rule.ProcessName} requested {ByteFormat.Humanize(written)} of logical file writes in the last hour (limit {rule.ThresholdGbPerHour:0.#} GB/h). Physical disk writes may be lower. Suspend it?")
                .SetToastDuration(ToastDuration.Long)
                .AddButton(suspendButton)
                .AddButton(new ToastButton()
                    .SetContent("Ignore")
                    .AddArgument("action", "suspend-ignore")
                    .AddArgument("process", rule.ProcessName))
                .Show();
        }
        catch (Exception ex)
        {
            LogToastError($"suspend-confirm:{rule.ProcessName}", ex);
            if (!_notifyIcon.Visible) _notifyIcon.Visible = true;
            _notifyIcon.ShowBalloonTip(8000, $"{rule.ProcessName} requesting heavy file writes",
                $"Requested {ByteFormat.Humanize(written)} of logical file writes in the last hour. Open the dashboard to suspend it.", ToolTipIcon.Warning);
        }
    }

    /// <summary>Notifies the user that an auto-suspend rule fired, offering a Resume button on success.</summary>
    private void ShowAutoSuspendedToast(AutoSuspendRule rule, long written, ProcessControl.Result result)
    {
        string body = result.Affected > 0
            ? $"{rule.ProcessName} was suspended after requesting {ByteFormat.Humanize(written)} of logical file writes in the last hour (limit {rule.ThresholdGbPerHour:0.#} GB/h)."
            : result.AccessDenied
                ? $"{rule.ProcessName} exceeded its write limit but could not be suspended (access denied - it may require elevation)."
                : $"{rule.ProcessName} exceeded its write limit but is no longer running.";
        try
        {
            var b = new ToastContentBuilder()
                .AddText(result.Affected > 0 ? $"{rule.ProcessName} auto-suspended" : $"{rule.ProcessName} not suspended")
                .AddText(body)
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
            b.Show();
        }
        catch (Exception ex)
        {
            LogToastError($"auto-suspend:{rule.ProcessName}", ex);
            if (!_notifyIcon.Visible) _notifyIcon.Visible = true;
            _notifyIcon.ShowBalloonTip(8000, $"{rule.ProcessName} auto-suspended", body, ToolTipIcon.Warning);
        }
    }

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
        _window ??= new MainWindow(_repo, _config, _userSettings);
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
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
        _window?.ForceClose();
    }
}
