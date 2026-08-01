using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DiskActivityMonitor.Core.Collection;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using Microsoft.Toolkit.Uwp.Notifications;

namespace DiskActivityMonitor.Tray;

public partial class App : System.Windows.Application
{
    private const string ShowEventName = "DiskActivityMonitor.Tray.ShowWindow";

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
    private const int ASFW_ANY = -1;

    private Mutex? _instanceMutex;
    private TrayController? _tray;
    private MonitorRepository? _repo;
    private EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _repo = new MonitorRepository();
        _repo.EnsureSchema(); // harmless if the service already created it; lets the UI run standalone.

        // Handle toast button clicks (snooze/dismiss). Windows may start a dedicated process to
        // service the click; that process writes to the shared DB and then exits without a tray.
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;
        if (ToastNotificationManagerCompat.WasCurrentProcessToastActivated())
        {
            // Safety net so an activation-only process never lingers if no handler runs.
            var bail = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            bail.Tick += (_, _) => Shutdown();
            bail.Start();
            return;
        }

        // Single-instance guard (normal launch only).
        _instanceMutex = new Mutex(initiallyOwned: true, "DiskActivityMonitor.Tray.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        var config = new ConfigStore();
        config.StartWatching();
        var userSettings = new UserSettingsStore();

        _tray = new TrayController(_repo, config, userSettings);
        _tray.Initialize();

        // Background listener: a toast body click (possibly handled by a short-lived process)
        // sets this named event, which brings the dashboard to the foreground here.
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            var signal = _showSignal;
            while (signal is not null)
            {
                try { signal.WaitOne(); }
                catch { break; }
                Dispatcher.Invoke(() => _tray?.OpenDashboard());
            }
        })
        { IsBackground = true, Name = "ShowWindowListener" };
        listener.Start();

        // Allow a shortcut / "open" action to launch straight into the dashboard.
        if (e.Args.Any(a => string.Equals(a, "--show", StringComparison.OrdinalIgnoreCase)))
            _tray.OpenDashboard();
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
                if (_repo is not null)
                    AutoSuspendManager.SuspendTracked(
                        _repo,
                        suspendName,
                        string.IsNullOrWhiteSpace(executablePath) ? null : executablePath);
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
            else
            {
                // Body click (no/unknown action): bring the running dashboard to the foreground.
                SignalShowWindow();
            }
        }
        catch { /* never crash on a toast callback */ }
        finally
        {
            // If this process exists solely to service the toast click, shut it down.
            if (ToastNotificationManagerCompat.WasCurrentProcessToastActivated())
                Dispatcher.Invoke(Shutdown);
        }
    }

    /// <summary>Reads the snooze duration chosen in the toast's selection box (defaults to 1 hour).</summary>
    private static TimeSpan GetSnoozeDuration(ToastNotificationActivatedEventArgsCompat e)
    {
        var durationId = SnoozeOptions.DefaultId;
        if (e.UserInput is not null && e.UserInput.TryGetValue("snoozeDuration", out var sel) && sel is not null)
            durationId = sel.ToString() ?? SnoozeOptions.DefaultId;
        return SnoozeOptions.ToTimeSpan(durationId);
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

    protected override void OnExit(ExitEventArgs e)
    {
        _showSignal?.Dispose();
        _showSignal = null;
        _tray?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
