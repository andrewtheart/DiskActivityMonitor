using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DiskActivityMonitor.Core;
using DiskActivityMonitor.Core.Collection;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Models;
using Microsoft.Toolkit.Uwp.Notifications;

namespace DiskActivityMonitor.Tray;

public partial class App : System.Windows.Application
{
    private const string ShowEventName = "DiskActivityMonitor.Tray.ShowWindow";

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
    private const int ASFW_ANY = -1;

    private SingleInstanceGuard? _instanceGuard;
    private TrayController? _tray;
    private MonitorRepository? _repo;
    private EventWaitHandle? _showSignal;
    private DispatcherTimer? _toastActivationTimer;
    private volatile bool _stoppingShowListener;
    internal Func<bool> WasToastActivated { get; set; } = ToastNotificationManagerCompat.WasCurrentProcessToastActivated;
    internal TryAcquireGuardDelegate GuardAcquirer { get; set; } = SingleInstanceGuard.TryAcquire;
    internal Action ShowWindowSignaler { get; set; } = SignalShowWindow;
    internal Action ShutdownRequester { get; set; }
    internal Action<Action> DispatcherInvoker { get; set; }
    internal Func<MonitorRepository> RepositoryFactory { get; set; } = () => new MonitorRepository();
    internal Func<ConfigStore> ConfigFactory { get; set; } = () => new ConfigStore();
    internal Func<UserSettingsStore> UserSettingsFactory { get; set; } = () => new UserSettingsStore();
    internal Func<MonitorRepository, ConfigStore, UserSettingsStore, TrayController> TrayFactory { get; set; } =
        (repo, config, settings) => new TrayController(repo, config, settings);
    internal Action<OnActivated> ToastActivationSubscriber { get; set; } =
        handler => ToastNotificationManagerCompat.OnActivated += handler;
    internal Func<EventWaitHandle> ShowSignalFactory { get; set; } =
        () => new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
    internal Action<Thread> BackgroundThreadStarter { get; set; } = thread => thread.Start();
    internal Action<TrayController> DashboardOpener { get; set; } = tray => tray.OpenDashboard();

    internal delegate bool TryAcquireGuardDelegate(string name, out SingleInstanceGuard? guard);
    internal readonly record struct StartupInstanceDecision(bool ContinueStartup, bool ShouldSignalShowWindow, bool ShouldShutdown);

    public App()
    {
        ShutdownRequester = Shutdown;
        DispatcherInvoker = action => Dispatcher.Invoke(action);
    }

    [ExcludeFromCodeCoverage]
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RunStartup(e.Args);
    }

    internal void RunStartup(IReadOnlyList<string> args)
    {
        bool toastActivated = WasToastActivated();
        if (!HandleStartupInstanceDecision(toastActivated))
            return;

        _repo = RepositoryFactory();
        _repo.EnsureSchema(); // harmless if the service already created it; lets the UI run standalone.

        // Handle toast button clicks (snooze/dismiss). Windows may start a dedicated process to
        // service the click; that process writes to the shared DB and then exits without a tray.
        ToastActivationSubscriber(OnToastActivated);
        if (toastActivated)
        {
            // Safety net so an activation-only process never lingers if no handler runs.
            StartToastActivationTimeout();
            return;
        }

        var config = ConfigFactory();
        config.StartWatching();
        var userSettings = UserSettingsFactory();

        _tray = TrayFactory(_repo, config, userSettings);
        _tray.Initialize();

        StartShowWindowListener();

        // Allow a shortcut / "open" action to launch straight into the dashboard.
        if (args.Any(a => string.Equals(a, "--show", StringComparison.OrdinalIgnoreCase)))
            DashboardOpener(_tray);
    }

    internal void StartToastActivationTimeout()
    {
        _toastActivationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _toastActivationTimer.Tick += OnToastActivationTimeout;
        _toastActivationTimer.Start();
    }

    internal void OnToastActivationTimeout(object? sender, EventArgs e)
        => ShutdownRequester();

    internal void StartShowWindowListener()
    {
        EventWaitHandle signal = ShowSignalFactory();
        _stoppingShowListener = false;
        _showSignal = signal;
        var listener = new Thread(() =>
        {
            while (true)
            {
                try { signal.WaitOne(); }
                catch { break; }
                if (_stoppingShowListener)
                    break;
                DispatcherInvoker(() => DashboardOpener(_tray!));
            }
        })
        { IsBackground = true, Name = "ShowWindowListener" };
        BackgroundThreadStarter(listener);
    }

    internal bool HandleStartupInstanceDecision(bool toastActivated)
    {
        var decision = DecideStartupInstanceBehavior(toastActivated, GuardAcquirer);
        _instanceGuard = decision.AcquiredGuard;
        if (!decision.InstanceDecision.ContinueStartup)
        {
            if (decision.InstanceDecision.ShouldSignalShowWindow)
                ShowWindowSignaler();
            ShutdownRequester();
            return false;
        }
        return true;
    }

    internal static (StartupInstanceDecision InstanceDecision, SingleInstanceGuard? AcquiredGuard) DecideStartupInstanceBehavior(
        bool toastActivated,
        TryAcquireGuardDelegate tryAcquire)
    {
        string mutexName = toastActivated
            ? SingleInstanceGuard.ToastActivationMutexName
            : SingleInstanceGuard.TrayMutexName;

        bool acquired = tryAcquire(mutexName, out var guard);
        if (acquired)
            return (new StartupInstanceDecision(ContinueStartup: true, ShouldSignalShowWindow: false, ShouldShutdown: false), guard);

        return (
            new StartupInstanceDecision(
                ContinueStartup: false,
                ShouldSignalShowWindow: !toastActivated,
                ShouldShutdown: true),
            null);
    }

    /// <summary>
    /// Applies a snooze (or dismiss) chosen from an alert toast. Runs in the running tray process
    /// or in a short-lived process Windows starts just to service the click.
    /// </summary>
    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            var args = ToastArguments.Parse(e.Argument);
            args.TryGetValue("action", out var action);

            if (action == "snooze" && args.TryGetValue("process", out var process) && !string.IsNullOrEmpty(process))
            {
                _repo?.SnoozeProcess(process, DateTime.UtcNow + GetSnoozeDuration(e));
                _repo?.AcknowledgeProcessAlerts(process);
            }
            else if (action == "snooze-all")
            {
                _repo?.SnoozeAllAlerts(DateTime.UtcNow + GetSnoozeDuration(e));
                _repo?.AcknowledgeAlerts(); // clear the outstanding list/tray icon
            }
            else if (action == "dismiss" && args.TryGetValue("alertId", out var idText) && long.TryParse(idText, out var alertId))
            {
                _repo?.AcknowledgeAlerts(new[] { alertId });
            }
            else if (action == "suspend" && args.TryGetValue("process", out var suspendName) && !string.IsNullOrEmpty(suspendName))
            {
                args.TryGetValue("path", out var executablePath);
                args.TryGetValue("source", out var origin);
                if (_repo is not null)
                {
                    var now = DateTime.UtcNow;
                    var chosen = GetSuspendDuration(e);
                    AutoSuspendManager.SuspendTracked(
                        _repo,
                        suspendName,
                        string.IsNullOrWhiteSpace(executablePath) ? null : executablePath,
                        chosen is TimeSpan span ? now + span : null,
                        SuspendOriginArguments.ToSource(origin),
                        now);
                }
            }
            else if (action == "resume" && args.TryGetValue("process", out var resumeName) && !string.IsNullOrEmpty(resumeName))
            {
                if (_repo is not null)
                    AutoSuspendManager.ResumeTracked(_repo, resumeName);
            }
            else if (action == "suspend-ignore")
            {
                // User declined the suspend prompt; nothing to persist.
            }
            else if (action == "compact-database")
            {
                // Compaction rebuilds the file, so run it in the dashboard rather than in a
                // short-lived toast-activation process that may exit mid-rebuild.
                ShowWindowSignaler();
            }
            else if (action == "dismiss-database-size")
            {
                // The next size check re-raises this after the configured cooldown.
            }
            else
            {
                // Body click (no/unknown action): bring the running dashboard to the foreground.
                ShowWindowSignaler();
            }
        }
        catch { /* never crash on a toast callback */ }
        finally
        {
            // If this process exists solely to service the toast click, shut it down.
            if (WasToastActivated())
                DispatcherInvoker(ShutdownRequester);
        }
    }

    /// <summary>Reads the snooze duration chosen in the toast's selection box (defaults to 1 hour).</summary>
    internal static TimeSpan GetSnoozeDuration(ToastNotificationActivatedEventArgsCompat e)
    {
        return GetSnoozeDuration(e.UserInput);
    }

    internal static TimeSpan GetSnoozeDuration(IDictionary<string, object>? userInput)
    {
        var durationId = SnoozeOptions.DefaultId;
        if (userInput is not null && userInput.TryGetValue("snoozeDuration", out var sel) && sel is not null)
            durationId = sel.ToString() ?? SnoozeOptions.DefaultId;
        return SnoozeOptions.ToTimeSpan(durationId);
    }

    /// <summary>
    /// Reads the suspension interval chosen in the toast's selection box. Returns null when the
    /// user asked to keep the process suspended until they resume it themselves.
    /// </summary>
    internal static TimeSpan? GetSuspendDuration(ToastNotificationActivatedEventArgsCompat e)
    {
        return GetSuspendDuration(e.UserInput);
    }

    internal static TimeSpan? GetSuspendDuration(IDictionary<string, object>? userInput)
    {
        var durationId = SuspendDurationOptions.DefaultId;
        if (userInput is not null && userInput.TryGetValue("suspendDuration", out var sel) && sel is not null)
            durationId = sel.ToString() ?? SuspendDurationOptions.DefaultId;
        return SuspendDurationOptions.ToTimeSpan(durationId);
    }

    /// <summary>Signals the running tray instance to show and focus the dashboard window.</summary>
    private static void SignalShowWindow()
    {
        try
        {
            AllowSetForegroundWindow(ASFW_ANY); // let the (possibly other) tray process take focus
            using var ev = EventWaitHandle.OpenExisting(ShowEventName);
            ev.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { /* tray not running */ }
        catch { /* best-effort */ }
    }

    [ExcludeFromCodeCoverage]
    protected override void OnExit(ExitEventArgs e)
    {
        DisposeLifecycleResources();
        base.OnExit(e);
    }

    internal void DisposeLifecycleResources()
    {
        _toastActivationTimer?.Stop();
        _toastActivationTimer = null;
        _stoppingShowListener = true;
        _showSignal?.Set();
        _showSignal?.Dispose();
        _showSignal = null;
        _tray?.Dispose();
        _tray = null;
        _repo = null;
        _instanceGuard?.Dispose();
        _instanceGuard = null;
    }
}
