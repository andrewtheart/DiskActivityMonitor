using System.Text.Json;
using DiskActivityMonitor.Core.Configuration;

namespace DiskActivityMonitor.Tests;

public class ConfigStoreTests : IDisposable
{
    private readonly string _configPath;

    public ConfigStoreTests()
    {
        _configPath = Path.Combine(Path.GetTempPath(), $"dam_cfg_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        try { File.Delete(_configPath); } catch { }
        try { File.Delete(_configPath + ".tmp"); } catch { }
        try { Directory.Delete(_configPath, recursive: true); } catch { }
    }

    [Fact]
    public void Constructor_NoFile_CreatesDefaultConfig()
    {
        using var store = new ConfigStore(_configPath);
        Assert.Equal(5, store.Current.SampleIntervalSeconds);
        Assert.True(File.Exists(_configPath));
    }

    [Fact]
    public void Save_And_Reload_PreservesValues()
    {
        using var store = new ConfigStore(_configPath);
        var cfg = store.Current;
        cfg.SsdWarnGbPerHour = 42;
        cfg.AlertCooldownMinutes = 30;
        store.Save(cfg);

        var reloaded = store.Reload();
        Assert.Equal(42, reloaded.SsdWarnGbPerHour);
        Assert.Equal(30, reloaded.AlertCooldownMinutes);
    }

    [Fact]
    public void Constructor_LegacyConfig_PreservesValuesAndAppliesNewDefaults()
    {
        File.WriteAllText(_configPath, """
            {
              "sampleIntervalSeconds": 12
            }
            """);

        using var store = new ConfigStore(_configPath);

        Assert.Equal(12, store.Current.SampleIntervalSeconds);
        Assert.True(store.Current.EnableControllerErrorAlerts);
    }

    [Fact]
    public void Constructor_PreviousUnknownTbwDefault_MigratesToEstimateRange()
    {
        File.WriteAllText(_configPath, """
            {
              "defaultSsdTbw": 750,
              "diskTbwRatings": { "known": 1200 }
            }
            """);

        using var store = new ConfigStore(_configPath);

        Assert.Equal(150, store.Current.DefaultSsdTbw);
        Assert.Equal(600, store.Current.DefaultSsdTbwUpper);
        Assert.Equal(1200, store.Current.DiskTbwRatings["known"]);
    }

    [Fact]
    public void Reload_CorruptFile_RetainsLastGoodConfig()
    {
        using var store = new ConfigStore(_configPath);
        store.Update(config => config.SampleIntervalSeconds = 12);
        File.WriteAllText(_configPath, "NOT JSON {{{{");

        var reloaded = store.Reload();
        Assert.Equal(12, reloaded.SampleIntervalSeconds);
    }

    [Fact]
    public void Reload_OversizedFile_RetainsLastGoodConfig()
    {
        using var store = new ConfigStore(_configPath);
        store.Update(config => config.SampleIntervalSeconds = 12);
        using (var stream = File.Create(_configPath))
            stream.SetLength(1024 * 1024 + 1);

        Assert.Equal(12, store.Reload().SampleIntervalSeconds);
    }

    [Fact]
    public async Task Current_IsThreadSafe()
    {
        using var store = new ConfigStore(_configPath);
        // Just verify that getting Current from multiple threads doesn't throw
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => store.Current.SampleIntervalSeconds))
            .ToArray();
        await Task.WhenAll(tasks);
        Assert.All(tasks, t => Assert.Equal(5, t.Result));
    }

    [Fact]
    public void Save_DoesNotUsePredictableTempFile()
    {
        using var store = new ConfigStore(_configPath);
        string predictableTempPath = _configPath + ".tmp";
        File.WriteAllText(predictableTempPath, "sentinel");

        store.Save(store.Current);

        Assert.Equal("sentinel", File.ReadAllText(predictableTempPath));
        Assert.True(File.Exists(_configPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_configPath)!, $".{Path.GetFileName(_configPath)}.*.tmp"));
    }

    [Fact]
    public void Save_OutputIsValidJson()
    {
        using var store = new ConfigStore(_configPath);
        var cfg = store.Current;
        cfg.DiskTbwRatings["disk0"] = 1200;
        store.Save(cfg);

        var json = File.ReadAllText(_configPath);
        var doc = JsonDocument.Parse(json);
        Assert.NotNull(doc);
        Assert.True(doc.RootElement.TryGetProperty("diskTbwRatings", out var ratings));
        Assert.Equal(1200, ratings.GetProperty("disk0").GetDouble());
    }

    [Fact]
    public void Current_ReturnsDeepSnapshot()
    {
        using var store = new ConfigStore(_configPath);
        store.Update(config => config.DiskTbwRatings["disk0"] = 1200);

        var snapshot = store.Current;
        snapshot.SampleIntervalSeconds = 30;
        snapshot.DiskTbwRatings["disk0"] = 1;

        Assert.Equal(5, store.Current.SampleIntervalSeconds);
        Assert.Equal(1200, store.Current.DiskTbwRatings["disk0"]);
    }

    [Fact]
    public void Update_SerializesChangesAndDoesNotRetainCallbackObject()
    {
        using var store = new ConfigStore(_configPath);
        AppConfig? callbackObject = null;

        Parallel.Invoke(
            () => store.Update(config => config.SampleIntervalSeconds = 12),
            () => store.Update(config => config.RetentionDays = 30));
        store.Update(config =>
        {
            callbackObject = config;
            config.DiskTbwRatings["disk0"] = 1200;
        });
        callbackObject!.DiskTbwRatings["disk0"] = 1;

        Assert.Equal(12, store.Current.SampleIntervalSeconds);
        Assert.Equal(30, store.Current.RetentionDays);
        Assert.Equal(1200, store.Current.DiskTbwRatings["disk0"]);
    }

    [Fact]
    public void Update_WriteFailureRetainsLastPersistedSnapshot()
    {
        using var store = new ConfigStore(_configPath);
        store.Update(config => config.SampleIntervalSeconds = 12);
        File.Delete(_configPath);
        Directory.CreateDirectory(_configPath);

        Assert.Throws<UnauthorizedAccessException>(() =>
            store.Update(config => config.SampleIntervalSeconds = 30));

        Assert.Equal(12, store.Current.SampleIntervalSeconds);
    }
}
