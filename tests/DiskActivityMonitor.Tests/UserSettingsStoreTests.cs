using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Updates;

namespace DiskActivityMonitor.Tests;

public sealed class UserSettingsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dam_user_{Guid.NewGuid():N}.json");
    private readonly string _legacyPath = Path.Combine(Path.GetTempPath(), $"dam_legacy_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
        try { File.Delete(_path + ".tmp"); } catch { }
        try { File.Delete(_legacyPath); } catch { }
        foreach (string file in Directory.GetFiles(Path.GetDirectoryName(_path)!, $".{Path.GetFileName(_path)}.*.tmp"))
            try { File.Delete(file); } catch { }
    }

    [Fact]
    public void Save_And_Reload_PreservesAutoSuspendRules()
    {
        var lastUpdateCheck = new DateTimeOffset(2026, 8, 5, 12, 30, 0, TimeSpan.Zero);
        var store = new UserSettingsStore(_path);
        store.Save(new UserSettings
        {
            EnableNotifications = false,
            EnableTbwWebLookup = false,
            SuppressTbwOnlineSetupPrompt = true,
            WebSearchProvider = "google",
            TbwLookupMethod = TbwLookupMethod.SerperOnly,
            TbwLookupModel = "model-id",
            AppUpdateCheckMode = AppUpdateCheckMode.Manual,
            LastAppUpdateCheckUtc = lastUpdateCheck,
            LastAppUpdateAlertedVersion = "1.4.13",
            MaxInstallerSizeMb = 384,
            AutoSuspendRules =
            [
                new AutoSuspendRule
                {
                    ProcessName = "writer",
                    ThresholdGbPerHour = 2.5,
                    Mode = SuspendMode.Auto,
                    Enabled = true,
                },
            ],
        });

        var reloaded = new UserSettingsStore(_path).Current;

        var rule = Assert.Single(reloaded.AutoSuspendRules);
        Assert.Equal("writer", rule.ProcessName);
        Assert.Equal(2.5, rule.ThresholdGbPerHour);
        Assert.Equal(SuspendMode.Auto, rule.Mode);
        Assert.False(reloaded.EnableNotifications);
        Assert.False(reloaded.EnableTbwWebLookup);
        Assert.True(reloaded.SuppressTbwOnlineSetupPrompt);
        Assert.Equal("google", reloaded.WebSearchProvider);
        Assert.Equal(TbwLookupMethod.SerperOnly, reloaded.TbwLookupMethod);
        Assert.Equal("model-id", reloaded.TbwLookupModel);
        Assert.Equal(AppUpdateCheckMode.Manual, reloaded.AppUpdateCheckMode);
        Assert.Equal(lastUpdateCheck, reloaded.LastAppUpdateCheckUtc);
        Assert.Equal("1.4.13", reloaded.LastAppUpdateAlertedVersion);
        Assert.Equal(384, reloaded.MaxInstallerSizeMb);
    }

    [Fact]
    public void Save_DoesNotUsePredictableTempFile()
    {
        File.WriteAllText(_path + ".tmp", "sentinel");

        new UserSettingsStore(_path).Save(new UserSettings());

        Assert.Equal("sentinel", File.ReadAllText(_path + ".tmp"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_path)!, $".{Path.GetFileName(_path)}.*.tmp"));
    }

    [Fact]
    public void Constructor_OversizedFile_IsIgnored()
    {
        using (var stream = File.Create(_path))
            stream.SetLength(1024 * 1024 + 1);

        Assert.Empty(new UserSettingsStore(_path).Current.AutoSuspendRules);
    }

    [Fact]
    public void Current_ReturnsDeepSnapshot()
    {
        var store = new UserSettingsStore(_path);
        store.Save(new UserSettings
        {
            EnableNotifications = false,
            AutoSuspendRules = [new AutoSuspendRule { ProcessName = "writer" }],
        });

        var snapshot = store.Current;
        snapshot.EnableNotifications = true;
        snapshot.AutoSuspendRules[0].ProcessName = "changed";

        Assert.False(store.Current.EnableNotifications);
        Assert.Equal("writer", Assert.Single(store.Current.AutoSuspendRules).ProcessName);
    }

    [Fact]
    public void InvalidInstallerSizeLimit_FallsBackToTheDocumentedDefault()
    {
        new UserSettingsStore(_path).Save(new UserSettings { MaxInstallerSizeMb = 0 });

        Assert.Equal(AppUpdateDownloader.DefaultMaxInstallerSizeMb, new UserSettingsStore(_path).Current.MaxInstallerSizeMb);
    }

    [Fact]
    public void Update_PreservesUnrelatedSettingsAndDoesNotRetainCallbackObject()
    {
        var store = new UserSettingsStore(_path);
        store.Save(new UserSettings
        {
            EnableNotifications = true,
            EnableTbwWebLookup = false,
            AutoSuspendRules = [new AutoSuspendRule { ProcessName = "writer" }],
        });
        UserSettings? callbackObject = null;

        store.Update(settings =>
        {
            callbackObject = settings;
            settings.EnableNotifications = false;
        });
        callbackObject!.EnableTbwWebLookup = true;
        callbackObject.AutoSuspendRules[0].ProcessName = "changed";

        var current = store.Current;
        Assert.False(current.EnableNotifications);
        Assert.False(current.EnableTbwWebLookup);
        Assert.Equal("writer", Assert.Single(current.AutoSuspendRules).ProcessName);
    }

    [Fact]
    public void Constructor_MigratesLegacyPreferencesButNotActionRules()
    {
        File.WriteAllText(_legacyPath, """
            {
              "enableNotifications": false,
              "enableTbwWebLookup": false,
              "suppressTbwOnlineSetupPrompt": true,
              "webSearchProvider": "google",
              "tbwLookupModel": "model-id",
              "autoSuspendRules": [
                { "processName": "explorer", "thresholdGbPerHour": 0.01, "mode": "Auto", "enabled": true }
              ]
            }
            """);

        var migrated = new UserSettingsStore(_path, _legacyPath).Current;

        Assert.False(migrated.EnableNotifications);
        Assert.False(migrated.EnableTbwWebLookup);
        Assert.True(migrated.SuppressTbwOnlineSetupPrompt);
        Assert.Equal("google", migrated.WebSearchProvider);
        Assert.Equal("model-id", migrated.TbwLookupModel);
        Assert.Empty(migrated.AutoSuspendRules);
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void Constructor_ExistingUserSettingsTakePrecedenceOverLegacyConfig()
    {
        new UserSettingsStore(_path).Save(new UserSettings { EnableTbwWebLookup = true });
        File.WriteAllText(_legacyPath, "{\"enableTbwWebLookup\":false}");

        Assert.True(new UserSettingsStore(_path, _legacyPath).Current.EnableTbwWebLookup);
    }
}