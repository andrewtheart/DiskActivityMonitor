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
        Assert.Equal(15, cfg.LiveGraphRetentionMinutes);
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
        Assert.Equal(150, cfg.DefaultSsdTbw);
        Assert.Equal(600, cfg.DefaultSsdTbwUpper);
        Assert.Equal(90, cfg.SsdWearWarnPercent);
        Assert.Equal(2, cfg.TbwProjectionWarnYears);
        Assert.True(cfg.DefaultEnduranceAlert.EnableProjectedLife);
        Assert.Equal(1, cfg.DefaultEnduranceAlert.RemainingLifeValue);
        Assert.Equal(EnduranceAlertTimeUnit.Years, cfg.DefaultEnduranceAlert.RemainingLifeUnit);
        Assert.True(cfg.DefaultEnduranceAlert.EnableRemainingPercent);
        Assert.Equal(20, cfg.DefaultEnduranceAlert.RemainingPercent);
        Assert.Equal(1, cfg.TbwProjectionCriticalYears);
        Assert.Equal(90, cfg.HighCoveragePercent);
        Assert.Equal(512, cfg.TailMaxReadKb);
        Assert.Equal(1024, cfg.TailMaxBufferKb);
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
    public void EffectiveTbwUpper_PerDiskLowerWithoutUpper_IsExplicitSingleRating()
    {
        var cfg = new AppConfig
        {
            DefaultSsdTbw = 150,
            DefaultSsdTbwUpper = 600,
            DiskTbwRatings = { ["disk0"] = 300 },
        };

        Assert.Null(cfg.EffectiveTbwUpper("disk0"));
    }

    [Fact]
    public void JsonRoundTrip_PreservesValues()
    {
        var cfg = new AppConfig
        {
            SampleIntervalSeconds = 10,
            LiveGraphRetentionMinutes = 30,
            TailMaxReadKb = 2048,
            TailMaxBufferKb = 4096,
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
            DefaultEnduranceAlert = new EnduranceAlertThreshold
            {
                RemainingLifeValue = 6,
                RemainingLifeUnit = EnduranceAlertTimeUnit.Months,
                RemainingPercent = 15,
            },
            DiskEnduranceAlertOverrides =
            {
                ["disk1"] = new EnduranceAlertThreshold
                {
                    RemainingLifeValue = 45,
                    RemainingLifeUnit = EnduranceAlertTimeUnit.Days,
                    RemainingPercent = 8,
                },
            },
        };

        var json = JsonSerializer.Serialize(cfg, AppConfig.SerializerOptions);
        var deserialized = JsonSerializer.Deserialize<AppConfig>(json, AppConfig.SerializerOptions)!;

        Assert.Equal(10, deserialized.SampleIntervalSeconds);
        Assert.Equal(30, deserialized.LiveGraphRetentionMinutes);
        Assert.Equal(2048, deserialized.TailMaxReadKb);
        Assert.Equal(4096, deserialized.TailMaxBufferKb);
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
        Assert.Equal(6, deserialized.DefaultEnduranceAlert.RemainingLifeValue);
        Assert.Equal(EnduranceAlertTimeUnit.Months, deserialized.DefaultEnduranceAlert.RemainingLifeUnit);
        Assert.Equal(15, deserialized.DefaultEnduranceAlert.RemainingPercent);
        Assert.Equal(45, deserialized.EffectiveEnduranceAlert("disk1").RemainingLifeValue);
    }

    [Fact]
    public void EffectiveEnduranceAlert_UsesDiskOverrideAndReturnsAClone()
    {
        var config = new AppConfig();
        config.DiskEnduranceAlertOverrides["1"] = new EnduranceAlertThreshold
        {
            RemainingLifeValue = 3,
            RemainingLifeUnit = EnduranceAlertTimeUnit.Months,
            RemainingPercent = 12,
        };

        EnduranceAlertThreshold threshold = config.EffectiveEnduranceAlert("1");
        threshold.RemainingPercent = 99;

        Assert.Equal(3 * (365.25 / 12.0), threshold.RemainingLifeDays, 6);
        Assert.Equal(12, config.EffectiveEnduranceAlert("1").RemainingPercent);
        Assert.Equal(365.25, config.EffectiveEnduranceAlert("missing").RemainingLifeDays, 6);

        EnduranceAlertThreshold normalized = AppConfig.CloneEnduranceAlert(new EnduranceAlertThreshold
        {
            RemainingLifeValue = double.NaN,
            RemainingLifeUnit = (EnduranceAlertTimeUnit)99,
            RemainingPercent = double.PositiveInfinity,
        });
        Assert.Equal(1, normalized.RemainingLifeValue);
        Assert.Equal(EnduranceAlertTimeUnit.Years, normalized.RemainingLifeUnit);
        Assert.Equal(20, normalized.RemainingPercent);
        Assert.Equal(365.25, normalized.RemainingLifeDays, 6);
        Assert.Equal(10, new EnduranceAlertThreshold
        {
            RemainingLifeValue = 10,
            RemainingLifeUnit = EnduranceAlertTimeUnit.Days,
        }.RemainingLifeDays);
        Assert.Equal(20, AppConfig.CloneEnduranceAlert(null).RemainingPercent);
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
        var cfg = new AppConfig { DefaultSsdTbwUpper = null };
        var json = JsonSerializer.Serialize(cfg, AppConfig.SerializerOptions);
        Assert.DoesNotContain("defaultSsdTbwUpper", json);
    }

    [Fact]
    public void MachineConfig_DoesNotExposePerUserActionSettings()
    {
        string[] propertyNames =
        [
            "AutoSuspendRules",
            "EnableNotifications",
            "EnableTbwWebLookup",
            "SuppressTbwOnlineSetupPrompt",
            "WebSearchProvider",
            "TbwLookupMethod",
            "TbwLookupModel",
        ];

        Assert.All(propertyNames, name => Assert.Null(typeof(AppConfig).GetProperty(name)));
    }
}
