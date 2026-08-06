using System.Reflection;
using DiskActivityMonitor.Cli;
using DiskActivityMonitor.Core;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Service;
using DiskActivityMonitor.Tray;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Microsoft.Toolkit.Uwp.Notifications;
using System.Windows;
using Windows.Foundation.Collections;

namespace DiskActivityMonitor.Tests;

[Collection("WPF")]
public sealed class LifecycleEntryPointTests
{
    [Fact]
    public void ServiceEntry_ReturnsWithoutStartingHost_WhenDuplicateInstance()
    {
        bool ensurePathsCalled = false;
        bool buildHostCalled = false;
        bool runHostCalled = false;

        int exit = ServiceProgramEntry.Run(
            [],
            (string _, out SingleInstanceGuard? guard) =>
            {
                guard = null;
                return false;
            },
            ensurePaths: () => ensurePathsCalled = true,
            buildHost: _ =>
            {
                buildHostCalled = true;
                return Host.CreateApplicationBuilder().Build();
            },
            runHost: _ => runHostCalled = true);

        Assert.Equal(0, exit);
        Assert.False(ensurePathsCalled);
        Assert.False(buildHostCalled);
        Assert.False(runHostCalled);
    }

    [Fact]
    public void ServiceEntry_CreatesPathsBuildsAndRunsHost_WhenPrimaryInstance()
    {
        bool ensurePathsCalled = false;
        bool runHostCalled = false;
        string guardName = "Local\\DiskActivityMonitor.Tests.ServiceEntry." + Guid.NewGuid().ToString("N");

        int exit = ServiceProgramEntry.Run(
            ["--anything"],
            (string _, out SingleInstanceGuard? guard) =>
            {
                return SingleInstanceGuard.TryAcquire(guardName, out guard);
            },
            ensurePaths: () => ensurePathsCalled = true,
            buildHost: _ => Host.CreateApplicationBuilder().Build(),
            runHost: _ => runHostCalled = true);

        Assert.Equal(0, exit);
        Assert.True(ensurePathsCalled);
        Assert.True(runHostCalled);

        Assert.True(SingleInstanceGuard.TryAcquire(guardName, out var released));
        released!.Dispose();
    }

    [Fact]
    public void ServiceHostBuilder_RegistersServiceWorkerAndDependencies()
    {
        using IHost host = ServiceProgramEntry.BuildHost([]);
        var services = host.Services;

        Assert.NotNull(services.GetService<DiskActivityMonitor.Core.Configuration.ConfigStore>());
        Assert.NotNull(services.GetService<DiskActivityMonitor.Core.Data.MonitorRepository>());
        Assert.Single(services.GetServices<IHostedService>().OfType<CollectorWorker>());
        var options = new WindowsServiceLifetimeOptions();
        ServiceProgramEntry.ConfigureWindowsService(options);
        Assert.Equal("DiskActivityMonitor", options.ServiceName);
    }

    [Fact]
    public void ExecutableEntryPoints_ReturnImmediatelyWhenTheirComponentMutexIsHeld()
    {
        ServiceProgramEntry.EntryPointTryAcquire = (string _, out SingleInstanceGuard? guard) =>
        {
            guard = null;
            return false;
        };
        CliProgramEntry.EntryPointTryAcquire = (string _, out SingleInstanceGuard? guard) =>
        {
            guard = null;
            return false;
        };
        try
        {
            Assert.Equal(0, InvokeEntryPoint(typeof(ServiceProgramEntry).Assembly));
            Assert.Equal(2, InvokeEntryPoint(typeof(CliProgramEntry).Assembly));
        }
        finally
        {
            ServiceProgramEntry.EntryPointTryAcquire = SingleInstanceGuard.TryAcquire;
            CliProgramEntry.EntryPointTryAcquire = SingleInstanceGuard.TryAcquire;
        }
    }

    [Fact]
    public void CliEntry_ReturnsDuplicateExitAndMessage_WhenInstanceIsHeld()
    {
        var messages = new List<string>();

        int exit = CliProgramEntry.Run(
            ["status"],
            (string _, out SingleInstanceGuard? guard) =>
            {
                guard = null;
                return false;
            },
            runCli: _ => throw new InvalidOperationException("Should not run"),
            writeErrorLine: messages.Add);

        Assert.Equal(2, exit);
        Assert.Single(messages);
        Assert.Equal("Another Disk Activity Monitor CLI command is already running.", messages[0]);
    }

    [Fact]
    public void CliEntry_RunsCliAndReturnsItsExitCode_WhenPrimaryInstance()
    {
        bool runCalled = false;
        string[]? seenArgs = null;
        string guardName = "Local\\DiskActivityMonitor.Tests.CliEntry." + Guid.NewGuid().ToString("N");

        int exit = CliProgramEntry.Run(
            ["alerts", "--json"],
            (string _, out SingleInstanceGuard? guard) =>
            {
                return SingleInstanceGuard.TryAcquire(guardName, out guard);
            },
            runCli: args =>
            {
                runCalled = true;
                seenArgs = args;
                return 7;
            },
            writeErrorLine: _ => throw new InvalidOperationException("No error expected"));

        Assert.Equal(7, exit);
        Assert.True(runCalled);
        Assert.NotNull(seenArgs);
        Assert.Equal(["alerts", "--json"], seenArgs);

    Assert.True(SingleInstanceGuard.TryAcquire(guardName, out var released));
    released!.Dispose();
    }

    [Theory]
    [InlineData(false, false, false, true, true)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, false, false, false, true)]
    [InlineData(true, true, true, false, false)]
    public void TrayStartupDecision_CoversDuplicateAndToastActivationBranches(
        bool toastActivated,
        bool acquired,
        bool expectContinue,
        bool expectSignalShowWindow,
        bool expectShutdown)
    {
        string? requestedName = null;
        var expectedName = toastActivated
            ? SingleInstanceGuard.ToastActivationMutexName
            : SingleInstanceGuard.TrayMutexName;

        var result = App.DecideStartupInstanceBehavior(
            toastActivated,
            (string name, out SingleInstanceGuard? guard) =>
            {
                requestedName = name;
                if (!acquired)
                {
                    guard = null;
                    return false;
                }

                string guardName = "Local\\DiskActivityMonitor.Tests.TrayStartup." + Guid.NewGuid().ToString("N");
                return SingleInstanceGuard.TryAcquire(guardName, out guard);
            });

        Assert.Equal(expectedName, requestedName);
        Assert.Equal(expectContinue, result.InstanceDecision.ContinueStartup);
        Assert.Equal(expectSignalShowWindow, result.InstanceDecision.ShouldSignalShowWindow);
        Assert.Equal(expectShutdown, result.InstanceDecision.ShouldShutdown);
        Assert.Equal(acquired, result.AcquiredGuard is not null);
        result.AcquiredGuard?.Dispose();
    }

    [Fact]
    public void TrayApp_DuplicateStartupSignalsAndRequestsShutdownWithoutInitializingTray()
    {
        RunSta(() =>
        {
            App app = CreateIsolatedApp();
            int signals = 0;
            int shutdowns = 0;
            app.WasToastActivated = () => false;
            app.GuardAcquirer = (string _, out SingleInstanceGuard? guard) =>
            {
                guard = null;
                return false;
            };
            app.ShowWindowSignaler = () => signals++;
            app.ShutdownRequester = () => shutdowns++;

            Assert.False(app.HandleStartupInstanceDecision(toastActivated: false));

            Assert.Equal(1, signals);
            Assert.Equal(1, shutdowns);

            app.GuardAcquirer = (string _, out SingleInstanceGuard? guard) =>
            {
                guard = null;
                return true;
            };

            Assert.True(app.HandleStartupInstanceDecision(toastActivated: false));
            Assert.Equal(1, signals);
            Assert.Equal(1, shutdowns);
        });
    }

    [Fact]
    public void TrayApp_RunStartupStopsBeforeCreatingStateForDuplicateInstance()
    {
        RunSta(() =>
        {
            App app = CreateIsolatedApp();
            int shutdowns = 0;
            app.WasToastActivated = () => false;
            app.GuardAcquirer = (string _, out SingleInstanceGuard? guard) =>
            {
                guard = null;
                return false;
            };
            app.ShowWindowSignaler = () => { };
            app.ShutdownRequester = () => shutdowns++;
            app.RepositoryFactory = () => throw new InvalidOperationException("Repository must not be created");

            app.RunStartup([]);

            Assert.Equal(1, shutdowns);
        });
    }

    [Fact]
    public void TrayApp_RunStartupHandlesToastActivationAndTimeoutWithoutTrayInitialization()
    {
        RunSta(() =>
        {
            string db = Path.Combine(Path.GetTempPath(), $"dam_app_toast_{Guid.NewGuid():N}.db");
            try
            {
                App app = CreateIsolatedApp();
                int subscriptions = 0;
                int shutdowns = 0;
                app.WasToastActivated = () => true;
                app.GuardAcquirer = (string _, out SingleInstanceGuard? guard) =>
                {
                    guard = null;
                    return true;
                };
                app.RepositoryFactory = () => new DiskActivityMonitor.Core.Data.MonitorRepository(db);
                app.ToastActivationSubscriber = _ => subscriptions++;
                app.ShutdownRequester = () => shutdowns++;
                app.TrayFactory = (_, _, _) => throw new InvalidOperationException("Tray must not be created");

                app.RunStartup([]);
                app.OnToastActivationTimeout(null, EventArgs.Empty);

                Assert.Equal(1, subscriptions);
                Assert.Equal(1, shutdowns);
                Assert.True(
                    ((System.Windows.Threading.DispatcherTimer)typeof(App)
                        .GetField("_toastActivationTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(app)!).IsEnabled);

                app.DisposeLifecycleResources();
                app.DisposeLifecycleResources();
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (string file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(db) + "*"))
                    try { File.Delete(file); } catch { }
            }
        });
    }

    [Fact]
    public void TrayApp_RunStartupInitializesTrayRoutesShowSignalAndDisposesResources()
    {
        RunSta(() =>
        {
            string db = Path.Combine(Path.GetTempPath(), $"dam_app_start_{Guid.NewGuid():N}.db");
            string configPath = Path.Combine(Path.GetTempPath(), $"dam_app_start_{Guid.NewGuid():N}.json");
            string settingsPath = Path.Combine(Path.GetTempPath(), $"dam_app_start_{Guid.NewGuid():N}.settings.json");
            ConfigStore? config = null;
            EventWaitHandle? signal = null;
            Thread? listener = null;
            try
            {
                App app = CreateIsolatedApp();
                int subscriptions = 0;
                int dashboardShows = 0;
                app.WasToastActivated = () => false;
                app.GuardAcquirer = (string _, out SingleInstanceGuard? guard) =>
                {
                    guard = null;
                    return true;
                };
                app.RepositoryFactory = () => new DiskActivityMonitor.Core.Data.MonitorRepository(db);
                app.ConfigFactory = () => config = new ConfigStore(configPath);
                app.UserSettingsFactory = () => new UserSettingsStore(settingsPath);
                app.TrayFactory = (repo, store, settings) =>
                {
                    var tray = new TrayController(repo, store, settings);
                    tray.StartupPromptsRunner = () => { };
                    return tray;
                };
                app.ToastActivationSubscriber = _ => subscriptions++;
                app.ShowSignalFactory = () => signal = new EventWaitHandle(false, EventResetMode.AutoReset);
                app.BackgroundThreadStarter = thread =>
                {
                    listener = thread;
                    thread.Start();
                };
                app.DispatcherInvoker = action => action();
                using var shown = new ManualResetEventSlim(false);
                app.DashboardOpener = _ =>
                {
                    dashboardShows++;
                    shown.Set();
                };

                app.RunStartup(["--show"]);
                Assert.Equal(1, subscriptions);
                Assert.Equal(1, dashboardShows);

                shown.Reset();
                signal!.Set();
                Assert.True(shown.Wait(TimeSpan.FromSeconds(5)));
                Assert.Equal(2, dashboardShows);

                app.DisposeLifecycleResources();
                Assert.True(listener!.Join(TimeSpan.FromSeconds(5)));
                app.DisposeLifecycleResources();
            }
            finally
            {
                config?.Dispose();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (string path in new[] { db, configPath, configPath + ".tmp", settingsPath, settingsPath + ".tmp" })
                    try { File.Delete(path); } catch { }
            }
        });
    }

    [Fact]
    public void TrayApp_DatabaseToastActionsAndUnknownActionRouteWithoutOsSignals()
    {
        RunSta(() =>
        {
            App app = CreateIsolatedApp();
            int signals = 0;
            int shutdowns = 0;
            app.WasToastActivated = () => false;
            app.ShowWindowSignaler = () => signals++;
            app.ShutdownRequester = () => shutdowns++;
            app.DispatcherInvoker = action => action();

            InvokeToast(app, "action=compact-database");
            InvokeToast(app, "action=dismiss-database-size");
            InvokeToast(app, "action=unknown");

            Assert.Equal(2, signals);
            Assert.Equal(0, shutdowns);

            app.WasToastActivated = () => true;
            InvokeToast(app, "action=dismiss-database-size");

            Assert.Equal(2, signals);
            Assert.Equal(1, shutdowns);
        });
    }

    [Fact]
    public void TrayApp_ExitDisposesItsOwnedGuard()
    {
        RunSta(() =>
        {
            App app = CreateIsolatedApp();
            string name = "Local\\DiskActivityMonitor.Tests.AppExit." + Guid.NewGuid().ToString("N");
            Assert.True(SingleInstanceGuard.TryAcquire(name, out var guard));
            typeof(App).GetField("_instanceGuard", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, guard);

            app.DisposeLifecycleResources();

            Assert.True(SingleInstanceGuard.TryAcquire(name, out var reacquired));
            reacquired!.Dispose();
        });
    }

    private static int InvokeEntryPoint(Assembly assembly)
        => (int)assembly.EntryPoint!.Invoke(null, [Array.Empty<string>()])!;

    private static App EnsureApplication()
    {
        if (Application.Current is null)
        {
            var created = new App();
            created.InitializeComponent();
        }
        return Assert.IsType<App>(Application.Current);
    }

    private static App CreateIsolatedApp()
    {
        var app = CreateFrameworkEventArgs<App>();
        app.WasToastActivated = () => false;
        app.GuardAcquirer = (string _, out SingleInstanceGuard? guard) =>
        {
            guard = null;
            return true;
        };
        app.ShowWindowSignaler = () => { };
        app.ShutdownRequester = () => { };
        app.DispatcherInvoker = action => action();
        app.ToastActivationSubscriber = _ => { };
        app.BackgroundThreadStarter = thread => thread.Start();
        app.DashboardOpener = _ => { };
        return app;
    }

    private static void InvokeToast(App app, string argument)
    {
        var activated = CreateToastEventArgs(argument, new Dictionary<string, object>());
        InvokeNonPublic(app, "OnToastActivated", activated);
    }

    private static ToastNotificationActivatedEventArgsCompat CreateToastEventArgs(
        string argument,
        IDictionary<string, object> userInput)
    {
        var activated = CreateFrameworkEventArgs<ToastNotificationActivatedEventArgsCompat>();
        var valueSet = new ValueSet();
        foreach (var item in userInput)
            valueSet[item.Key] = item.Value;
        SetBackingField(activated, "<Argument>k__BackingField", argument);
        SetBackingField(activated, "<UserInput>k__BackingField", valueSet);
        return activated;
    }

    private static T CreateFrameworkEventArgs<T>() where T : class
        => (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static void SetBackingField(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void InvokeNonPublic(App app, string methodName, object argument)
        => typeof(App).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, [argument]);

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
}
