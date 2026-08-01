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
        Assert.Equal("serper", store.Current.WebSearchProvider);
        Assert.True(store.Current.EnableControllerErrorAlerts);
    }

    [Fact]
    public void Reload_CorruptFile_ReturnsDefaults()
    {
        using var store = new ConfigStore(_configPath);
        File.WriteAllText(_configPath, "NOT JSON {{{{");

        var reloaded = store.Reload();
        Assert.Equal(5, reloaded.SampleIntervalSeconds); // default
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
    public void Save_UsesAtomicWriteViaTempFile()
    {
        using var store = new ConfigStore(_configPath);
        store.Save(store.Current);

        // The .tmp file should have been cleaned up (moved over main)
        Assert.False(File.Exists(_configPath + ".tmp"));
        Assert.True(File.Exists(_configPath));
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
}
