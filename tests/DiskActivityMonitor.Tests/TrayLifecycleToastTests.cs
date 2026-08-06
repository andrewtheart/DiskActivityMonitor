using DiskActivityMonitor.Core.Collection;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Models;
using DiskActivityMonitor.Core.Updates;
using DiskActivityMonitor.Tray;
using Microsoft.Toolkit.Uwp.Notifications;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using Windows.Foundation.Collections;

namespace DiskActivityMonitor.Tests;

// Creates the WPF Application, so it must share the serialized collection; running it in
// parallel leaves Application.Current owned by a foreign thread.
[Collection("WPF")]
public sealed class TrayLifecycleToastTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dam_tray_toast_{Guid.NewGuid():N}.db");
    private readonly string _cfg = Path.Combine(Path.GetTempPath(), $"dam_tray_toast_{Guid.NewGuid():N}.json");
    private readonly string _settings = Path.Combine(Path.GetTempPath(), $"dam_tray_toast_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_db) + "*"))
            try { File.Delete(file); } catch { }
        try { File.Delete(_cfg); } catch { }
        try { File.Delete(_settings); } catch { }
    }

    [Fact]
    public void ToastDurationParsing_UsesDefaultsWhenInputMissingOrNull()
    {
        Assert.Equal(SnoozeOptions.ToTimeSpan(SnoozeOptions.DefaultId), App.GetSnoozeDuration((IDictionary<string, object>?)null));
        Assert.Equal(SuspendDurationOptions.ToTimeSpan(SuspendDurationOptions.DefaultId), App.GetSuspendDuration((IDictionary<string, object>?)null));

        var nullValues = new Dictionary<string, object?>
        {
            ["snoozeDuration"] = null,
            ["suspendDuration"] = null,
        };

        Assert.Equal(
            SnoozeOptions.ToTimeSpan(SnoozeOptions.DefaultId),
            App.GetSnoozeDuration(ToObjectDictionary(nullValues)));
        Assert.Equal(
            SuspendDurationOptions.ToTimeSpan(SuspendDurationOptions.DefaultId),
            App.GetSuspendDuration(ToObjectDictionary(nullValues)));
    }

    [Fact]
    public void ToastDurationParsing_UsesSelectedValuesAndFallsBackForUnknown()
    {
        var selected = new Dictionary<string, object>
        {
            ["snoozeDuration"] = "5m",
            ["suspendDuration"] = "manual",
        };

        Assert.Equal(TimeSpan.FromMinutes(5), App.GetSnoozeDuration(selected));
        Assert.Null(App.GetSuspendDuration(selected));

        var unknown = new Dictionary<string, object>
        {
            ["snoozeDuration"] = "definitely-unknown",
            ["suspendDuration"] = "definitely-unknown",
        };

        Assert.Equal(SnoozeOptions.ToTimeSpan(SnoozeOptions.DefaultId), App.GetSnoozeDuration(unknown));
        Assert.Equal(SuspendDurationOptions.ToTimeSpan(SuspendDurationOptions.DefaultId), App.GetSuspendDuration(unknown));
    }

    [Fact]
    public void ToastDurationEventOverloads_ReadTheToolkitActivationInput()
    {
        var activated = (ToastNotificationActivatedEventArgsCompat)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(ToastNotificationActivatedEventArgsCompat));
        typeof(ToastNotificationActivatedEventArgsCompat)
            .GetField("<UserInput>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(activated, new ValueSet
            {
                ["snoozeDuration"] = "5m",
                ["suspendDuration"] = "manual",
            });

        Assert.Equal(TimeSpan.FromMinutes(5), App.GetSnoozeDuration(activated));
        Assert.Null(App.GetSuspendDuration(activated));
    }

    [Theory]
    [InlineData(double.NaN, "an unknown time")]
    [InlineData(double.PositiveInfinity, "an unknown time")]
    [InlineData(0.0, "an unknown time")]
    [InlineData(2.25, "2.3 years")]
    [InlineData(1.0 / 12.0, "1 month")]
    [InlineData(0.5, "6 months")]
    public void FormatToastYears_CoversAllFormattingBranches(double input, string expected)
    {
        Assert.Equal(expected, TrayController.FormatToastYears(input));
    }

    [Fact]
    public void AlertToast_BuildsProcessAndOrdinaryToasts_AndFallsBackOnFailure()
    {
        using var harness = CreateController();
        var presented = new List<ToastContentBuilder>();
        var balloons = new List<(string Title, ToolTipIcon Icon)>();
        harness.Controller.ToastPresenter = presented.Add;
        harness.Controller.BalloonPresenter = (_, title, _, icon) => balloons.Add((title, icon));

        harness.Controller.ShowAlertToast(Alert("proc-1h:writer", AlertSeverity.Warning));
        harness.Controller.ShowAlertToast(Alert("misc", AlertSeverity.Warning));

        Assert.Equal(2, presented.Count);
        Assert.Empty(balloons);

        harness.Controller.ToastPresenter = _ => throw new InvalidOperationException("toast unavailable");
        harness.Controller.ShowAlertToast(Alert("misc", AlertSeverity.Critical));

        var fallback = Assert.Single(balloons);
        Assert.Equal("Alert", fallback.Title);
        Assert.Equal(ToolTipIcon.Error, fallback.Icon);
    }

    [Fact]
    public void SuspensionToasts_BuildSuccessVariants_AndUseExpectedFallbacks()
    {
        using var harness = CreateController();
        var presented = new List<ToastContentBuilder>();
        var balloons = new List<ToolTipIcon>();
        var logged = new List<string>();
        harness.Controller.ToastPresenter = presented.Add;
        harness.Controller.BalloonPresenter = (_, _, _, icon) => balloons.Add(icon);
        harness.Controller.ToastErrorLogger = (context, _) => logged.Add(context);
        var rule = new AutoSuspendRule
        {
            ProcessName = "writer",
            ThresholdGbPerHour = 1,
            ExecutablePath = @"C:\Apps\writer.exe",
        };

        harness.Controller.ShowSuspendConfirmToast(rule, 1);
        harness.Controller.ShowAutoSuspendedToast(rule, 1, new ProcessControl.Result(1, 1, false));
        harness.Controller.ShowAutoSuspendedToast(rule, 1, new ProcessControl.Result(1, 0, true));
        harness.Controller.ShowResumedToast(new ExpiredSuspension("writer", SuspendSource.AutoRule, new ProcessControl.Result(1, 1, false)));

        Assert.Equal(4, presented.Count);
        Assert.Empty(balloons);

        harness.Controller.ToastPresenter = _ => throw new InvalidOperationException("toast unavailable");
        harness.Controller.ShowSuspendConfirmToast(rule, 1);
        harness.Controller.ShowAutoSuspendedToast(rule, 1, new ProcessControl.Result(0, 0, false));
        harness.Controller.ShowResumedToast(new ExpiredSuspension("writer", SuspendSource.Manual, default));

        Assert.Equal([ToolTipIcon.Warning, ToolTipIcon.Warning, ToolTipIcon.Info], balloons);
        Assert.Equal(["suspend-confirm:writer", "auto-suspend:writer", "auto-resume:writer"], logged);
    }

    [Fact]
    public void AlertWriteWindows_RoutesProcessCombinedDiskAndUnknownRules()
    {
        using var harness = CreateController();
        DateTime end = new(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);
        harness.Repo.AddProcessSamples(
        [
            new ProcessIoSample { TimestampUtc = end.AddMinutes(-1), ProcessName = "writer", WriteBytes = 100 },
            new ProcessIoSample { TimestampUtc = end.AddMinutes(-1), ProcessName = "other", WriteBytes = 50 },
        ]);
        harness.Repo.AddDiskSamples(
        [
            new DiskSample { TimestampUtc = end.AddMinutes(-1), DiskId = "0", WriteBytes = 200 },
        ]);

        Assert.Equal(100, harness.Controller.GetAlertWriteWindows(Alert("proc-1h:writer", timestamp: end)).FiveMinutes);
        Assert.Equal(150, harness.Controller.GetAlertWriteWindows(Alert("procs-all-1h", timestamp: end)).FiveMinutes);
        Assert.Equal(200, harness.Controller.GetAlertWriteWindows(Alert("ssd-1h:0", timestamp: end)).FiveMinutes);
        Assert.Equal(200, harness.Controller.GetAlertWriteWindows(Alert("ssd-24h:0", timestamp: end)).FiveMinutes);
        Assert.Equal(default, harness.Controller.GetAlertWriteWindows(Alert("other", timestamp: end)));
    }

    [Fact]
    public async Task AutomaticUpdateCheck_ContainsFailuresAndPresentsOnlyEligibleReleases()
    {
        using var harness = CreateController(new UserSettings
        {
            AppUpdateCheckMode = AppUpdateCheckMode.Automatic,
        });
        harness.Controller.AppUpdateCheck = (_, _) => throw new InvalidOperationException("offline");

        Assert.Null(await harness.Controller.MaybeRunAutomaticAppUpdateCheckAsync());
        Assert.NotNull(harness.Settings.Current.LastAppUpdateCheckUtc);

        var presented = new List<AppUpdateCheckResult>();
        harness.Controller.AppUpdateAvailablePresenter = (check, _) => presented.Add(check);

        AppUpdateCheckResult next = new(new Version(2, 0), new Version(2, 0), CreateRelease());
        harness.Controller.AppUpdateCheck = (_, _) => Task.FromResult<AppUpdateCheckResult?>(next);
        harness.Settings.Update(value => value.LastAppUpdateCheckUtc = null);
        Assert.False((await harness.Controller.MaybeRunAutomaticAppUpdateCheckAsync())!.UpdateAvailable);

        next = new AppUpdateCheckResult(new Version(1, 0), new Version(2, 0), null);
        harness.Settings.Update(value => value.LastAppUpdateCheckUtc = null);
        Assert.Null((await harness.Controller.MaybeRunAutomaticAppUpdateCheckAsync())!.Release);

        next = new AppUpdateCheckResult(new Version(1, 0), new Version(2, 0), CreateRelease());
        harness.Settings.Update(value =>
        {
            value.LastAppUpdateCheckUtc = null;
            value.LastAppUpdateAlertedVersion = "2.0.0";
        });
        Assert.NotNull(await harness.Controller.MaybeRunAutomaticAppUpdateCheckAsync());
        Assert.Empty(presented);

        harness.Settings.Update(value =>
        {
            value.LastAppUpdateCheckUtc = null;
            value.LastAppUpdateAlertedVersion = null;
        });
        Assert.NotNull(await harness.Controller.MaybeRunAutomaticAppUpdateCheckAsync());
        Assert.Single(presented);
    }

    [Theory]
    [InlineData("proc-1h:writer", AlertSeverity.Warning, "High logical writes: writer", "1h limit")]
    [InlineData("procs-all-1h", AlertSeverity.Warning, "High combined logical writes", "1h limit")]
    [InlineData("ssd-wear:0", AlertSeverity.Warning, "Alert", "SMART endurance used")]
    [InlineData("ssd-1h:0", AlertSeverity.Warning, "Alert", "1h limit")]
    [InlineData("ssd-24h:0", AlertSeverity.Warning, "Alert", "24h limit")]
    [InlineData("ssd-24h:0", AlertSeverity.Critical, "Alert", "24h critical limit")]
    [InlineData("tbw-life:0", AlertSeverity.Warning, "Alert", "Projected life")]
    [InlineData("disk-controller:0", AlertSeverity.Warning, "Alert", "1 error")]
    [InlineData("other", AlertSeverity.Warning, "Alert", "Details")]
    public void AlertToastFormatting_CoversEveryRuleFamily(
        string ruleKey,
        AlertSeverity severity,
        string expectedTitle,
        string expectedBody)
    {
        AlertRecord alert = Alert(
            ruleKey,
            severity,
            value: ruleKey.StartsWith("disk-controller:", StringComparison.Ordinal) ? 1 : 2);

        var text = TrayController.FormatAlertToastText(alert, new TrayController.WriteWindowStats(1, 2, 3, 4));

        Assert.Equal(expectedTitle, text.Title);
        Assert.Contains(expectedBody, text.Body);
    }

    [Fact]
    public void Formatting_CoversPluralControllerErrorsAndSuspensionDurations()
    {
        var controller = TrayController.FormatAlertToastText(
            Alert("disk-controller:0", value: 2),
            default);
        var rule = new AutoSuspendRule { ProcessName = "writer", ThresholdGbPerHour = 1 };
        var affected = new ProcessControl.Result(1, 1, false);

        Assert.Contains("2 Disk event 11 errors", controller.Body);
        Assert.Contains("Resumes in 30 min.", TrayController.FormatAutoSuspendText(rule, default, affected, 30).Body);
        Assert.Contains("Resume manually.", TrayController.FormatAutoSuspendText(rule, default, affected, 0).Body);
    }

    [Fact]
    public void StartupPrompts_CoverConsentAndNonConsentPaths()
    {
        RunSta(() =>
        {
            EnsureApplication();
            using var promptHarness = CreateController(new UserSettings { AppUpdateCheckMode = AppUpdateCheckMode.Prompt });
            promptHarness.Controller.TbwSetupPresenter = () => { };
            promptHarness.Controller.RunStartupPrompts();
            promptHarness.Controller.Dispose();

            using var manualHarness = CreateController(new UserSettings { AppUpdateCheckMode = AppUpdateCheckMode.Manual });
            manualHarness.Controller.TbwSetupPresenter = () => { };
            manualHarness.Controller.RunStartupPrompts();
        });
    }

    [Theory]
    [InlineData(false, 0L)]
    [InlineData(true, 1L)]
    public void Initialize_ConfiguresTrayStateAndRoutesCommands(bool seedAlert, long expectedLastAlertId)
    {
        RunSta(() =>
        {
            EnsureApplication();
            using var harness = CreateController(new UserSettings
            {
                AppUpdateCheckMode = AppUpdateCheckMode.Manual,
                SuppressTbwOnlineSetupPrompt = true,
            });
            if (seedAlert)
                harness.Repo.InsertAlert(Alert("seed"));
            int promptRuns = 0;
            int dashboardShows = 0;
            int folderShows = 0;
            int exitRequests = 0;
            harness.Controller.StartupPromptsRunner = () => promptRuns++;
            harness.Controller.DashboardPresenter = () => dashboardShows++;
            harness.Controller.DataFolderPresenter = () => folderShows++;
            harness.Controller.ExitRequester = () => exitRequests++;

            harness.Controller.Initialize();
            harness.Controller.OnOpenDashboard(null, EventArgs.Empty);
            harness.Controller.OnOpenDataFolder(null, EventArgs.Empty);
            harness.Controller.OnExitRequested(null, EventArgs.Empty);

            Assert.Equal(1, promptRuns);
            Assert.Equal(1, dashboardShows);
            Assert.Equal(1, folderShows);
            Assert.Equal(1, exitRequests);
            Assert.Equal(
                expectedLastAlertId,
                typeof(TrayController).GetField("_lastBalloonAlertId", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(harness.Controller));
            Assert.NotNull(
                typeof(TrayController).GetField("_trayMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(harness.Controller));
            Assert.True(
                ((System.Windows.Threading.DispatcherTimer)typeof(TrayController)
                    .GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(harness.Controller)!).IsEnabled);
        });
    }

    [Fact]
    public void DefaultPresenters_CreateAndReuseDashboardWindow()
    {
        RunSta(() =>
        {
            EnsureApplication();
            using var harness = CreateController(new UserSettings { AppUpdateCheckMode = AppUpdateCheckMode.Manual });

            harness.Controller.TbwSetupPresenter();
            harness.Controller.AppUpdateAvailablePresenter(
                new AppUpdateCheckResult(new Version(1, 0, 0), new Version(2, 0, 0), null),
                CreateRelease());

            Assert.NotNull(typeof(TrayController).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(harness.Controller));
        });
    }

    private ControllerHarness CreateController(UserSettings? initialSettings = null)
    {
        var repo = new MonitorRepository(_db);
        repo.EnsureSchema();
        var config = new ConfigStore(_cfg);
        var settings = new UserSettingsStore(_settings);
        if (initialSettings is not null)
            settings.Save(initialSettings);
        return new ControllerHarness(new TrayController(repo, config, settings), repo, config, settings);
    }

    private static AlertRecord Alert(
        string ruleKey,
        AlertSeverity severity = AlertSeverity.Warning,
        DateTime? timestamp = null,
        double value = 2)
        => new()
        {
            Id = 1,
            TimestampUtc = timestamp ?? DateTime.UtcNow,
            Severity = severity,
            RuleKey = ruleKey,
            Title = "Alert",
            Message = "Details",
            Value = value,
            Threshold = 1,
        };

    private static AppReleaseInfo CreateRelease()
    {
        var asset = new AppReleaseAsset(
            "DiskActivityMonitor-Setup-2.0.0-x64.exe",
            new Uri("https://example.invalid/update.exe"),
            1,
            new string('0', 64));
        return new AppReleaseInfo(
            new Version(2, 0, 0),
            "v2.0.0",
            "Release",
            "Notes",
            new Uri("https://example.invalid/release"),
            DateTimeOffset.UtcNow,
            asset);
    }

    private static void EnsureApplication()
    {
        if (System.Windows.Application.Current is null)
        {
            var app = new App();
            app.InitializeComponent();
        }
        System.Windows.Application.Current!.Resources["TextPrimary"] = new SolidColorBrush(Colors.White);
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new TargetInvocationException(error);
    }

    private sealed class ControllerHarness(
        TrayController controller,
        MonitorRepository repo,
        ConfigStore config,
        UserSettingsStore settings) : IDisposable
    {
        public TrayController Controller { get; } = controller;
        public MonitorRepository Repo { get; } = repo;
        public UserSettingsStore Settings { get; } = settings;

        public void Dispose()
        {
            Controller.Dispose();
            config.Dispose();
        }
    }

    private static IDictionary<string, object> ToObjectDictionary(Dictionary<string, object?> source)
    {
        return source.ToDictionary(pair => pair.Key, pair => pair.Value!);
    }
}
