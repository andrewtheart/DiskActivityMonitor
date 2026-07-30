using System.Text.Json;
using DiskActivityMonitor.Core.Configuration;

namespace DiskActivityMonitor.Tests;

public class AppConfigTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var cfg = new AppConfig();

        Assert.Equal(5, cfg.SampleIntervalSeconds);
        Assert.Equal(15, cfg.DashboardRefreshSeconds);
        Assert.Equal(365, cfg.RetentionDays);
        Assert.Equal(10, cfg.SsdWarnGbPerHour);
        Assert.Equal(100, cfg.SsdWarnGbPerDay);
        Assert.Equal(250, cfg.SsdCriticalGbPerDay);
        Assert.Equal(5, cfg.ProcessWarnGbPerHour);
        Assert.Equal(20, cfg.AllProcessesWarnGbPerHour);
        Assert.Equal(5, cfg.AlertCooldownMinutes);
        Assert.True(cfg.EnableControllerErrorAlerts);
        Assert.Equal(14, cfg.ControllerErrorWindowDays);
        Assert.Equal(3, cfg.ControllerErrorWarnCount);
        Assert.Equal(10, cfg.ControllerErrorCriticalCount);
        Assert.False(cfg.SuppressTbwOnlineSetupPrompt);
        Assert.Equal("serper", cfg.WebSearchProvider);
        Assert.Equal(750, cfg.DefaultSsdTbw);
        Assert.Null(cfg.DefaultSsdTbwUpper);
        Assert.Equal(90, cfg.SsdWearWarnPercent);
        Assert.True(cfg.EnableNotifications);
        Assert.Equal(2, cfg.TbwProjectionWarnYears);
        Assert.Equal(1, cfg.TbwProjectionCriticalYears);
    }

    [Fact]
    public void EffectiveTbw_NoOverride_ReturnsDefault()
    {
        var cfg = new AppConfig { DefaultSsdTbw = 600 };
        Assert.Equal(600, cfg.EffectiveTbw("disk0"));
    }

    [Fact]
    public void EffectiveTbw_WithOverride_ReturnsOverride()
    {
        var cfg = new AppConfig
        {
            DefaultSsdTbw = 600,
            DiskTbwRatings = { ["disk0"] = 1200 },
        };
        Assert.Equal(1200, cfg.EffectiveTbw("disk0"));
    }

    [Fact]
    public void EffectiveTbw_OverrideIsZero_FallsBackToDefault()
    {
        var cfg = new AppConfig
        {
            DefaultSsdTbw = 600,
            DiskTbwRatings = { ["disk0"] = 0 },
        };
        Assert.Equal(600, cfg.EffectiveTbw("disk0"));
    }

    [Fact]
    public void EffectiveTbwUpper_NoUpperConfigured_ReturnsNull()
    {
        var cfg = new AppConfig { DefaultSsdTbw = 600 };
        Assert.Null(cfg.EffectiveTbwUpper("disk0"));
    }

    [Fact]
    public void EffectiveTbwUpper_DefaultUpperGreaterThanLower_ReturnsIt()
    {
        var cfg = new AppConfig
        {
            DefaultSsdTbw = 600,
            DefaultSsdTbwUpper = 1200,
        };
        Assert.Equal(1200, cfg.EffectiveTbwUpper("disk0"));
    }

    [Fact]
    public void EffectiveTbwUpper_DefaultUpperNotGreaterThanLower_ReturnsNull()
    {
        var cfg = new AppConfig
        {
            DefaultSsdTbw = 600,
            DefaultSsdTbwUpper = 500, // less than lower
        };
        Assert.Null(cfg.EffectiveTbwUpper("disk0"));
    }

    [Fact]
    public void EffectiveTbwUpper_PerDiskUpperOverride_TakesPrecedence()
    {
        var cfg = new AppConfig
        {
            DefaultSsdTbw = 600,
            DefaultSsdTbwUpper = 800,
            DiskTbwRatingsUpper = { ["disk0"] = 1500 },
        };
        Assert.Equal(1500, cfg.EffectiveTbwUpper("disk0"));
    }

    [Fact]
    public void EffectiveTbwUpper_PerDiskUpperNotGreater_FallsBackToDefault()
    {
        var cfg = new AppConfig
        {
            DefaultSsdTbw = 600,
            DefaultSsdTbwUpper = 900,
            DiskTbwRatingsUpper = { ["disk0"] = 500 }, // less than lower
        };
        Assert.Equal(900, cfg.EffectiveTbwUpper("disk0"));
    }

    [Fact]
    public void JsonRoundTrip_PreservesValues()
    {
        var cfg = new AppConfig
        {
            SampleIntervalSeconds = 10,
            SsdWarnGbPerHour = 20,
            DefaultSsdTbw = 1000,
            DefaultSsdTbwUpper = 1500,
            DiskTbwRatings = { ["disk1"] = 800 },
            DiskTbwRatingsUpper = { ["disk1"] = 1200 },
            AlertCooldownMinutes = 15,
            EnableControllerErrorAlerts = false,
            ControllerErrorWindowDays = 30,
            ControllerErrorWarnCount = 5,
            ControllerErrorCriticalCount = 20,
            SuppressTbwOnlineSetupPrompt = true,
            WebSearchProvider = "serper",
            EnableNotifications = false,
        };

        var json = JsonSerializer.Serialize(cfg, AppConfig.SerializerOptions);
        var deserialized = JsonSerializer.Deserialize<AppConfig>(json, AppConfig.SerializerOptions)!;

        Assert.Equal(10, deserialized.SampleIntervalSeconds);
        Assert.Equal(20, deserialized.SsdWarnGbPerHour);
        Assert.Equal(1000, deserialized.DefaultSsdTbw);
        Assert.Equal(1500, deserialized.DefaultSsdTbwUpper);
        Assert.Equal(800, deserialized.DiskTbwRatings["disk1"]);
        Assert.Equal(1200, deserialized.DiskTbwRatingsUpper["disk1"]);
        Assert.Equal(15, deserialized.AlertCooldownMinutes);
        Assert.False(deserialized.EnableControllerErrorAlerts);
        Assert.Equal(30, deserialized.ControllerErrorWindowDays);
        Assert.Equal(5, deserialized.ControllerErrorWarnCount);
        Assert.Equal(20, deserialized.ControllerErrorCriticalCount);
        Assert.True(deserialized.SuppressTbwOnlineSetupPrompt);
        Assert.Equal("serper", deserialized.WebSearchProvider);
        Assert.False(deserialized.EnableNotifications);
    }

    [Fact]
    public void JsonSerialization_UsesCamelCase()
    {
        var cfg = new AppConfig();
        var json = JsonSerializer.Serialize(cfg, AppConfig.SerializerOptions);
        Assert.Contains("\"sampleIntervalSeconds\"", json);
        Assert.Contains("\"ssdWarnGbPerHour\"", json);
        Assert.Contains("\"controllerErrorWarnCount\"", json);
        Assert.DoesNotContain("\"SampleIntervalSeconds\"", json);
    }

    [Fact]
    public void JsonSerialization_OmitsNulls()
    {
        var cfg = new AppConfig(); // DefaultSsdTbwUpper is null
        var json = JsonSerializer.Serialize(cfg, AppConfig.SerializerOptions);
        Assert.DoesNotContain("defaultSsdTbwUpper", json);
    }
}
