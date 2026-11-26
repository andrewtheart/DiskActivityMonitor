using DiskActivityMonitor.Core;
using DiskActivityMonitor.Core.Alerts;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Models;

namespace DiskActivityMonitor.Tests;

/// <summary>
/// Tests for the alert engine's threshold evaluation, cooldown, and snooze logic.
/// Uses a real in-memory SQLite database to exercise the full alert pipeline.
/// </summary>
public class AlertEngineTests : IDisposable
{
    private readonly string _dbPath;
    private readonly MonitorRepository _repo;
    private readonly AlertEngine _engine;

    public AlertEngineTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"dam_alert_{Guid.NewGuid():N}.db");
        _repo = new MonitorRepository(databasePath: _dbPath);
        _repo.EnsureSchema();
        _engine = new AlertEngine(_repo);
    }

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_dbPath) + "*"))
            try { File.Delete(f); } catch { }
    }

    private static DiskInfo MakeSsd(string id = "0", int? wearPercent = null, long? lifetimeWritten = null) => new()
    {
        DiskId = id,
        InstanceName = $"{id} C:",
        FriendlyName = $"Test SSD {id}",
        Volumes = "C:",
        MediaType = DiskMediaType.Ssd,
        WearPercent = wearPercent,
        LifetimeBytesWritten = lifetimeWritten,
    };

    private static DiskInfo MakeHdd(string id = "1") => new()
    {
        DiskId = id,
        InstanceName = $"{id} D:",
        FriendlyName = $"Test HDD {id}",
        Volumes = "D:",
        MediaType = DiskMediaType.Hdd,
    };

    // ────────────────────────────────────────── No alerts when below thresholds

    [Fact]
    public void Evaluate_BelowThresholds_ReturnsEmpty()
    {
        var cfg = new AppConfig { SsdWarnGbPerHour = 10 };
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        // Add small write activity
        _repo.AddDiskSamples([
            new DiskSample { TimestampUtc = now.AddMinutes(-30), DiskId = "0", WriteBytes = 1_000_000 },
        ]);

        var alerts = _engine.Evaluate([MakeSsd()], cfg, now);
        Assert.Empty(alerts);
    }

    // ────────────────────────────────────────── HDD disks are skipped

    [Fact]
    public void Evaluate_HddDisk_SkipsSsdRules()
    {
        var cfg = new AppConfig { SsdWarnGbPerHour = 0.001 }; // very low threshold
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        _repo.AddDiskSamples([
            new DiskSample { TimestampUtc = now.AddMinutes(-5), DiskId = "1", WriteBytes = (long)(50 * ByteFormat.GiB) },
        ]);

        var alerts = _engine.Evaluate([MakeHdd()], cfg, now);
        Assert.Empty(alerts);
    }

    // ────────────────────────────────────────── SSD 1-hour threshold

    [Fact]
    public void Evaluate_Ssd1HourExceeded_RaisesWarning()
    {
        var cfg = new AppConfig { SsdWarnGbPerHour = 10 };
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        _repo.AddDiskSamples([
            new DiskSample { TimestampUtc = now.AddMinutes(-30), DiskId = "0", WriteBytes = (long)(15 * ByteFormat.GiB) },
        ]);

        var alerts = _engine.Evaluate([MakeSsd()], cfg, now);
        Assert.Contains(alerts, a => a.RuleKey == "ssd-1h:0" && a.Severity == AlertSeverity.Warning);
    }

    [Theory]
    [InlineData(10_000_000_000, true)]
    [InlineData(9_999_999_999, false)]
    public void Evaluate_WriteThresholds_UseDecimalGigabytes(long hourWrite, bool expectedAlert)
    {
        // Issue #3: a 10 GB setting previously meant 10 GiB (10.74 decimal GB).
        var cfg = new AppConfig { SsdWarnGbPerHour = 10 };
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        _repo.AddDiskSamples([
            new DiskSample { TimestampUtc = now.AddMinutes(-30), DiskId = "0", WriteBytes = hourWrite },
        ]);

        var alerts = _engine.Evaluate([MakeSsd()], cfg, now);

        var alert = alerts.SingleOrDefault(a => a.RuleKey == "ssd-1h:0");
        Assert.Equal(expectedAlert, alert is not null);
        if (alert is not null)
        {
            Assert.Equal(10_000_000_000, alert.Threshold);
            Assert.Contains("10 GB written in the last hour (threshold 10 GB)", alert.Message);
        }
    }

    // ────────────────────────────────────────── SSD 24-hour threshold (warning vs critical)

    [Fact]
    public void Evaluate_Ssd24HourExceedsWarning_RaisesWarning()
    {
        var cfg = new AppConfig { SsdWarnGbPerDay = 100, SsdCriticalGbPerDay = 250 };
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        // Spread 120 GB over the last 24 hours
        for (int i = 0; i < 24; i++)
        {
            _repo.AddDiskSamples([new DiskSample
            {
                TimestampUtc = now.AddHours(-24 + i),
                DiskId = "0",
                WriteBytes = (long)(5 * ByteFormat.GiB),
            }]);
        }

        var alerts = _engine.Evaluate([MakeSsd()], cfg, now);
        Assert.Contains(alerts, a => a.RuleKey == "ssd-24h:0" && a.Severity == AlertSeverity.Warning);
    }

    [Fact]
    public void Evaluate_Ssd24HourExceedsCritical_RaisesCritical()
    {
        var cfg = new AppConfig { SsdWarnGbPerDay = 100, SsdCriticalGbPerDay = 250 };
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        // 300 GB in 24 hours
        for (int i = 0; i < 24; i++)
        {
            _repo.AddDiskSamples([new DiskSample
            {
                TimestampUtc = now.AddHours(-24 + i),
                DiskId = "0",
                WriteBytes = (long)(12.5 * ByteFormat.GiB),
            }]);
        }

        var alerts = _engine.Evaluate([MakeSsd()], cfg, now);
        Assert.Contains(alerts, a => a.RuleKey == "ssd-24h:0" && a.Severity == AlertSeverity.Critical);
    }

    [Fact]
    public void Evaluate_UnknownTbwProjection_ReportsRemainingEstimatedRange()
    {
        var cfg = new AppConfig
        {
            SsdWarnGbPerHour = 0,
            SsdWarnGbPerDay = 0,
            SsdCriticalGbPerDay = 0,
        };
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        for (int day = 8; day >= 1; day--)
        {
            _repo.AddDiskSamples([new DiskSample
            {
                TimestampUtc = now.AddDays(-day),
                DiskId = "0",
                WriteBytes = 100_000_000_000_000,
            }]);
        }
        for (int minute = 7 * 24 * 60; minute >= 1; minute--)
            _repo.AddCollectorHeartbeat(now.AddMinutes(-minute));

        var alerts = _engine.Evaluate([MakeSsd()], cfg, now);

        var alert = Assert.Single(alerts, candidate => candidate.RuleKey == "endurance-health:0");
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Contains("Lifetime writes have already passed the assumed 150 to 600 TBW default rating.", alert.Message);
        Assert.Contains("The projected-time warning threshold is", alert.Message);
        Assert.DoesNotContain("remaining life is", alert.Message);
        Assert.DoesNotContain("Writes made before monitoring began", alert.Message);
    }

    [Fact]
    public void Evaluate_TbwProjection_LowCoverageDoesNotRaise()
    {
        var cfg = new AppConfig
        {
            SsdWarnGbPerHour = 0,
            SsdWarnGbPerDay = 0,
            SsdCriticalGbPerDay = 0,
            DefaultEnduranceAlert = new EnduranceAlertThreshold
            {
                RemainingLifeValue = 100,
                RemainingLifeUnit = EnduranceAlertTimeUnit.Years,
                EnableRemainingPercent = false,
            },
        };
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        _repo.AddDiskSamples([
            new DiskSample { TimestampUtc = now.AddDays(-8), DiskId = "0", WriteBytes = 100_000_000_000_000 },
            new DiskSample { TimestampUtc = now.AddMinutes(-1), DiskId = "0", WriteBytes = 100_000_000_000_000 },
        ]);

        var alerts = _engine.Evaluate([MakeSsd()], cfg, now, highCoveragePercent: 90);

        Assert.DoesNotContain(alerts, candidate => candidate.RuleKey == "endurance-health:0");
    }

    [Theory]
    [InlineData(7_000_000_000_000, 1, true)]
    [InlineData(1_000_000_000_000, 1, false)]
    [InlineData(1_000_000_000_000, 4, true)]
    public void Evaluate_EnduranceProjection_UsesConfiguredRemainingLife(
        long sevenDayBytes,
        double warningYears,
        bool expectedAlert)
    {
        var cfg = new AppConfig
        {
            DefaultSsdTbw = 150,
            DefaultSsdTbwUpper = null,
            SsdWarnGbPerHour = 0,
            SsdWarnGbPerDay = 0,
            SsdCriticalGbPerDay = 0,
            DefaultEnduranceAlert = new EnduranceAlertThreshold
            {
                RemainingLifeValue = warningYears,
                RemainingLifeUnit = EnduranceAlertTimeUnit.Years,
                EnableRemainingPercent = false,
            },
        };
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        _repo.AddDiskSamples([
            new DiskSample { TimestampUtc = now.AddDays(-8), DiskId = "0", WriteBytes = 1 },
            new DiskSample { TimestampUtc = now.AddMinutes(-1), DiskId = "0", WriteBytes = sevenDayBytes },
        ]);
        for (int minute = 7 * 24 * 60; minute >= 1; minute--)
            _repo.AddCollectorHeartbeat(now.AddMinutes(-minute));

        var alerts = _engine.Evaluate([MakeSsd()], cfg, now);
        var projection = alerts.SingleOrDefault(candidate => candidate.RuleKey == "endurance-health:0");

        if (!expectedAlert)
        {
            Assert.Null(projection);
        }
        else
        {
            Assert.NotNull(projection);
            Assert.Equal(AlertSeverity.Warning, projection.Severity);
            Assert.Contains("the assumed 150 TBW default rating is reached in", projection.Message);
            Assert.Contains("Writes made before monitoring began are unknown, so the actual time may be shorter.", projection.Message);
        }
    }

    // ────────────────────────────────────────── SMART wear percentage

    [Fact]
    public void Evaluate_WearExceedsThreshold_RaisesWarning()
    {
        var cfg = new AppConfig();
        var now = DateTime.UtcNow;
        var disk = MakeSsd(wearPercent: 92);

        var alerts = _engine.Evaluate([disk], cfg, now);
        Assert.Contains(alerts, a => a.RuleKey == "endurance-health:0" && a.Severity == AlertSeverity.Warning);
    }

    [Fact]
    public void Evaluate_PerDiskRemainingPercentOverride_TakesPrecedence()
    {
        var cfg = new AppConfig();
        cfg.DiskEnduranceAlertOverrides["0"] = new EnduranceAlertThreshold
        {
            EnableProjectedLife = false,
            RemainingPercent = 5,
        };
        var now = DateTime.UtcNow;
        var disk = MakeSsd(wearPercent: 92);

        var alerts = _engine.Evaluate([disk], cfg, now);
        Assert.DoesNotContain(alerts, a => a.RuleKey == "endurance-health:0");
    }

    [Fact]
    public void Evaluate_WearBelowThreshold_NoAlert()
    {
        var cfg = new AppConfig();
        var now = DateTime.UtcNow;
        var disk = MakeSsd(wearPercent: 50);

        var alerts = _engine.Evaluate([disk], cfg, now);
        Assert.DoesNotContain(alerts, a => a.RuleKey.StartsWith("endurance-health:"));
    }

    [Fact]
    public void Evaluate_WearNull_NoAlert()
    {
        var cfg = new AppConfig();
        var now = DateTime.UtcNow;
        var disk = MakeSsd(wearPercent: null);

        var alerts = _engine.Evaluate([disk], cfg, now);
        Assert.DoesNotContain(alerts, a => a.RuleKey.StartsWith("endurance-health:"));
    }

    [Fact]
    public void Evaluate_EnduranceRuleSnooze_IsScopedToOneDisk()
    {
        var cfg = new AppConfig();
        DateTime now = DateTime.UtcNow;
        _repo.SnoozeAlertRule("endurance-health:0", now.AddHours(1));

        var alerts = _engine.Evaluate(
            [MakeSsd("0", wearPercent: 95), MakeSsd("1", wearPercent: 95)],
            cfg,
            now);

        Assert.DoesNotContain(alerts, alert => alert.RuleKey == "endurance-health:0");
        Assert.Contains(alerts, alert => alert.RuleKey == "endurance-health:1");
    }

    [Fact]
    public void Evaluate_EnduranceProjection_UsesDriveLifetimeWritesWhenAvailable()
    {
        var cfg = new AppConfig
        {
            DefaultSsdTbw = 1,
            DefaultSsdTbwUpper = null,
            DefaultEnduranceAlert = new EnduranceAlertThreshold
            {
                EnableProjectedLife = true,
                RemainingLifeValue = 10,
                RemainingLifeUnit = EnduranceAlertTimeUnit.Years,
                EnableRemainingPercent = false,
            },
            SsdWarnGbPerHour = 0,
            SsdWarnGbPerDay = 0,
            SsdCriticalGbPerDay = 0,
        };
        DateTime now = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
        _repo.AddDiskSamples([
            new DiskSample { TimestampUtc = now.AddDays(-8), DiskId = "0", WriteBytes = 1 },
            new DiskSample { TimestampUtc = now.AddMinutes(-1), DiskId = "0", WriteBytes = 700_000_000_000 },
        ]);
        for (int minute = 7 * 24 * 60; minute >= 1; minute--)
            _repo.AddCollectorHeartbeat(now.AddMinutes(-minute));

        var alerts = _engine.Evaluate([MakeSsd(lifetimeWritten: 900_000_000_000)], cfg, now);

        Assert.Contains(alerts, alert => alert.RuleKey == "endurance-health:0"
            && alert.Message.Contains("the assumed 1 TBW default rating is reached in", StringComparison.Ordinal));
    }

    // ────────────────────────────────────────── Endurance sources: SMART wear vs configured vs assumed TBW

    private void AddSevenDaysOfCoverage(DateTime now, long sevenDayBytes)
    {
        _repo.AddDiskSamples([
            new DiskSample { TimestampUtc = now.AddDays(-8), DiskId = "0", WriteBytes = 1 },
            new DiskSample { TimestampUtc = now.AddMinutes(-1), DiskId = "0", WriteBytes = sevenDayBytes },
        ]);
        for (int minute = 7 * 24 * 60; minute >= 1; minute--)
            _repo.AddCollectorHeartbeat(now.AddMinutes(-minute));
    }

    private static AppConfig EnduranceOnlyConfig() => new()
    {
        SsdWarnGbPerHour = 0,
        SsdWarnGbPerDay = 0,
        SsdCriticalGbPerDay = 0,
    };

    [Fact]
    public void Evaluate_AssumedTbwExceededButSmartReportsLowWear_DoesNotWarn()
    {
        // Issue #2: 252 TB written against the assumed 150-600 TBW default, while SMART reports 37% used.
        var disk = MakeSsd(wearPercent: 37, lifetimeWritten: 252_210_000_000_000);

        var alerts = _engine.Evaluate([disk], EnduranceOnlyConfig(), DateTime.UtcNow);

        Assert.DoesNotContain(alerts, alert => alert.RuleKey == "endurance-health:0");
    }

    [Fact]
    public void Evaluate_HighSmartWearWithFewLifetimeWrites_WarnsFromSmart()
    {
        // Only 40% of the assumed lower bound, but the drive itself reports 95% used.
        var disk = MakeSsd(wearPercent: 95, lifetimeWritten: 60_000_000_000_000);

        var alerts = _engine.Evaluate([disk], EnduranceOnlyConfig(), DateTime.UtcNow);

        var alert = Assert.Single(alerts, candidate => candidate.RuleKey == "endurance-health:0");
        Assert.Contains("SMART data reports 95% of its endurance used (5% remaining)", alert.Message);
        Assert.DoesNotContain("assumed", alert.Message);
        Assert.Contains("plan drive replacement", alert.Message);
        Assert.Equal(5, alert.Value);
        Assert.Equal(20, alert.Threshold);
    }

    [Fact]
    public void Evaluate_ConfiguredTbwConsumed_WarnsWithRatingAndSmartContext()
    {
        var cfg = EnduranceOnlyConfig();
        cfg.DiskTbwRatings["0"] = 300;
        var disk = MakeSsd(wearPercent: 10, lifetimeWritten: 250_000_000_000_000);

        var alerts = _engine.Evaluate([disk], cfg, DateTime.UtcNow);

        var alert = Assert.Single(alerts, candidate => candidate.RuleKey == "endurance-health:0");
        Assert.Contains("Lifetime writes of 250 TB are 83.33% of this drive's configured 300 TBW rating", alert.Message);
        Assert.Contains("the warning threshold is 80% of the rating", alert.Message);
        Assert.Contains("SMART data reports 10% of its endurance used.", alert.Message);
        Assert.Contains("Drives often keep working past their rated TBW", alert.Message);
        Assert.DoesNotContain("plan drive replacement", alert.Message);
    }

    [Fact]
    public void Evaluate_SmartWearAboveOneHundred_WarnsWithZeroRemaining()
    {
        // NVMe Percentage Used keeps counting past 100% once rated endurance is exceeded.
        var disk = MakeSsd(wearPercent: 115, lifetimeWritten: 900_000_000_000_000);

        var alerts = _engine.Evaluate([disk], EnduranceOnlyConfig(), DateTime.UtcNow);

        var alert = Assert.Single(alerts, candidate => candidate.RuleKey == "endurance-health:0");
        Assert.Contains("SMART data reports 115% of its endurance used (0% remaining)", alert.Message);
        Assert.Contains("plan drive replacement", alert.Message);
        Assert.Equal(0, alert.Value);
    }

    [Theory]
    [InlineData(100_000_000_000, AlertSeverity.Warning, 100_000_000_000)]
    [InlineData(99_999_999_999, null, 0)]
    [InlineData(250_000_000_000, AlertSeverity.Critical, 250_000_000_000)]
    [InlineData(249_999_999_999, AlertSeverity.Warning, 100_000_000_000)]
    public void Evaluate_DailyWriteThresholds_UseDecimalGigabytes(long dayWrite, AlertSeverity? expectedSeverity, double expectedThreshold)
    {
        var cfg = new AppConfig { SsdWarnGbPerHour = 0, SsdWarnGbPerDay = 100, SsdCriticalGbPerDay = 250 };
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        _repo.AddDiskSamples([
            new DiskSample { TimestampUtc = now.AddHours(-3), DiskId = "0", WriteBytes = dayWrite },
        ]);

        var alerts = _engine.Evaluate([MakeSsd()], cfg, now);

        var alert = alerts.SingleOrDefault(a => a.RuleKey == "ssd-24h:0");
        Assert.Equal(expectedSeverity, alert?.Severity);
        if (alert is not null)
            Assert.Equal(expectedThreshold, alert.Threshold);
    }

    [Fact]
    public void Evaluate_AssumedTbwWithoutSmart_WarnsAsLabelledEstimate()
    {
        var disk = MakeSsd(wearPercent: null, lifetimeWritten: 200_000_000_000_000);

        var alerts = _engine.Evaluate([disk], EnduranceOnlyConfig(), DateTime.UtcNow);

        var alert = Assert.Single(alerts, candidate => candidate.RuleKey == "endurance-health:0");
        Assert.Contains("are 33.33% to 133.33% of the assumed 150 to 600 TBW default rating", alert.Message);
        Assert.Contains("of the rating's lower bound", alert.Message);
        Assert.Contains("only an estimate", alert.Message);
        Assert.Contains("Look up rated TBW", alert.Message);
        Assert.DoesNotContain("plan drive replacement", alert.Message);
    }

    [Fact]
    public void Evaluate_TbwConsumedDisabled_SuppressesTbwOnlyWarning()
    {
        var cfg = EnduranceOnlyConfig();
        cfg.DefaultEnduranceAlert.EnableTbwConsumed = false;
        var disk = MakeSsd(wearPercent: null, lifetimeWritten: 200_000_000_000_000);

        var alerts = _engine.Evaluate([disk], cfg, DateTime.UtcNow);

        Assert.DoesNotContain(alerts, alert => alert.RuleKey == "endurance-health:0");
    }

    [Fact]
    public void Evaluate_ProjectionWithAssumedTbwAndSmartWear_ExtrapolatesSmartWearRate()
    {
        DateTime now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        AddSevenDaysOfCoverage(now, 7_000_000_000_000);   // about 1 TB/day
        var disk = MakeSsd(wearPercent: 50, lifetimeWritten: 100_000_000_000_000);

        var alerts = _engine.Evaluate([disk], EnduranceOnlyConfig(), now);

        // 100 TB at 50-51% used leaves about 96-100 TB, roughly 3 months at 1 TB/day.
        var alert = Assert.Single(alerts, candidate => candidate.RuleKey == "endurance-health:0");
        Assert.Contains("At 1 TB/day, the drive's SMART wear reaches 100% in 3.2 months to 3.3 months, extrapolated from 50% used after 100 TB written.", alert.Message);
        Assert.InRange(alert.Value, 95.5, 96.5);
        Assert.DoesNotContain("assumed", alert.Message);
        Assert.Equal(1, alert.Message.Split("SMART").Length - 1);
        Assert.Contains("plan drive replacement", alert.Message);
    }

    [Theory]
    [InlineData(98, true)]     // only the conservative 96-day end is inside the window
    [InlineData(95, false)]
    public void Evaluate_SmartProjection_ComparesConservativeBoundWithThreshold(double warningDays, bool expectedAlert)
    {
        DateTime now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        AddSevenDaysOfCoverage(now, 7_000_000_000_000);
        var cfg = EnduranceOnlyConfig();
        cfg.DefaultEnduranceAlert.RemainingLifeValue = warningDays;
        cfg.DefaultEnduranceAlert.RemainingLifeUnit = EnduranceAlertTimeUnit.Days;
        var disk = MakeSsd(wearPercent: 50, lifetimeWritten: 100_000_000_000_000);

        var alerts = _engine.Evaluate([disk], cfg, now);

        Assert.Equal(expectedAlert, alerts.Any(candidate => candidate.RuleKey == "endurance-health:0"));
    }

    [Fact]
    public void Evaluate_ConfiguredTbwAlreadyPassed_SaysSoInsteadOfZeroRemainingLife()
    {
        DateTime now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        AddSevenDaysOfCoverage(now, 7_000_000_000);   // about 1 GB/day
        var cfg = EnduranceOnlyConfig();
        cfg.DiskTbwRatings["0"] = 300;
        cfg.DefaultEnduranceAlert.EnableTbwConsumed = false;
        var disk = MakeSsd(wearPercent: 37, lifetimeWritten: 310_000_000_000_000);

        var alerts = _engine.Evaluate([disk], cfg, now);

        var alert = Assert.Single(alerts, candidate => candidate.RuleKey == "endurance-health:0");
        Assert.StartsWith("Lifetime writes have already passed this drive's configured 300 TBW rating.", alert.Message);
        Assert.DoesNotContain("0 days", alert.Message);
        Assert.Contains("SMART data reports 37% of its endurance used.", alert.Message);
        Assert.DoesNotContain("plan drive replacement", alert.Message);
        Assert.Equal(0, alert.Value);
    }

    [Fact]
    public void Evaluate_ConfiguredTbwRangeLowerBoundPassed_ReportsTimeToUpperBound()
    {
        DateTime now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        AddSevenDaysOfCoverage(now, 7_000_000_000_000);   // about 1 TB/day
        var cfg = EnduranceOnlyConfig();
        cfg.DiskTbwRatings["0"] = 300;
        cfg.DiskTbwRatingsUpper["0"] = 600;
        cfg.DefaultEnduranceAlert.EnableTbwConsumed = false;
        var disk = MakeSsd(wearPercent: null, lifetimeWritten: 400_000_000_000_000);

        var alerts = _engine.Evaluate([disk], cfg, now);

        var alert = Assert.Single(alerts, candidate => candidate.RuleKey == "endurance-health:0");
        Assert.StartsWith("Lifetime writes have passed the lower bound of this drive's configured 300 to 600 TBW rating; at 1 TB/day the upper bound is reached in 6.6 months.", alert.Message);
    }

    [Fact]
    public void Evaluate_ProjectionWithConfiguredTbw_PrefersRatingOverSmartWear()
    {
        DateTime now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        AddSevenDaysOfCoverage(now, 7_000_000_000_000);
        var cfg = EnduranceOnlyConfig();
        cfg.DiskTbwRatings["0"] = 150;
        cfg.DefaultEnduranceAlert.EnableTbwConsumed = false;
        var disk = MakeSsd(wearPercent: 5, lifetimeWritten: 100_000_000_000_000);

        var alerts = _engine.Evaluate([disk], cfg, now);

        var alert = Assert.Single(alerts, candidate => candidate.RuleKey == "endurance-health:0");
        Assert.Contains("At 1 TB/day, this drive's configured 150 TBW rating is reached in 50 days.", alert.Message);
        Assert.Contains("SMART data reports 5% of its endurance used.", alert.Message);
        Assert.DoesNotContain("Writes made before monitoring began", alert.Message);
        Assert.Contains("Drives often keep working past their rated TBW", alert.Message);
    }

    [Fact]
    public void Evaluate_ProjectionWithAssumedTbwAndNoSmart_LabelsAssumedRating()
    {
        DateTime now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        AddSevenDaysOfCoverage(now, 7_000_000_000_000);
        var cfg = EnduranceOnlyConfig();
        cfg.DefaultEnduranceAlert.EnableTbwConsumed = false;
        var disk = MakeSsd(wearPercent: null, lifetimeWritten: 100_000_000_000_000);

        var alerts = _engine.Evaluate([disk], cfg, now);

        var alert = Assert.Single(alerts, candidate => candidate.RuleKey == "endurance-health:0");
        Assert.Contains("At 1 TB/day, the assumed 150 to 600 TBW default rating is reached in 50 days to 16.4 months.", alert.Message);
        Assert.Contains("Look up rated TBW", alert.Message);
    }

    [Theory]
    [InlineData(double.NaN, "an unknown time")]
    [InlineData(-1, "an unknown time")]
    [InlineData(0.5, "0.5 days")]
    [InlineData(10, "10 days")]
    [InlineData(90, "3 months")]
    [InlineData(730.5, "2 years")]
    public void FormatRemainingTime_CoversEveryRange(double days, string expected)
        => Assert.Equal(expected, AlertEngine.FormatRemainingTime(days));

    // ────────────────────────────────────────── Cooldown

    [Fact]
    public void Evaluate_SameRuleWithinCooldown_Suppressed()
    {
        var cfg = new AppConfig { SsdWarnGbPerHour = 10, AlertCooldownMinutes = 5 };
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        _repo.AddDiskSamples([
            new DiskSample { TimestampUtc = now.AddMinutes(-30), DiskId = "0", WriteBytes = (long)(15 * ByteFormat.GiB) },
        ]);

        // First evaluation fires
        var alerts1 = _engine.Evaluate([MakeSsd()], cfg, now);
        Assert.NotEmpty(alerts1);

        // Second evaluation 2 minutes later (within cooldown) — suppressed
        var alerts2 = _engine.Evaluate([MakeSsd()], cfg, now.AddMinutes(2));
        Assert.DoesNotContain(alerts2, a => a.RuleKey == "ssd-1h:0");
    }

    [Fact]
    public void Evaluate_SameRuleAfterCooldown_FiresAgain()
    {
        var cfg = new AppConfig { SsdWarnGbPerHour = 10, AlertCooldownMinutes = 5 };
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        _repo.AddDiskSamples([
            new DiskSample { TimestampUtc = now.AddMinutes(-30), DiskId = "0", WriteBytes = (long)(15 * ByteFormat.GiB) },
        ]);

        var alerts1 = _engine.Evaluate([MakeSsd()], cfg, now);
        Assert.NotEmpty(alerts1);

        // Re-add activity so the threshold is still exceeded in the new window
        _repo.AddDiskSamples([
            new DiskSample { TimestampUtc = now.AddMinutes(3), DiskId = "0", WriteBytes = (long)(15 * ByteFormat.GiB) },
        ]);

        // 10 minutes later (past cooldown)
        var alerts2 = _engine.Evaluate([MakeSsd()], cfg, now.AddMinutes(10));
        Assert.Contains(alerts2, a => a.RuleKey == "ssd-1h:0");
    }

    // ────────────────────────────────────────── Global Snooze

    [Fact]
    public void Evaluate_GlobalSnoozeActive_SuppressesAll()
    {
        var cfg = new AppConfig { SsdWarnGbPerHour = 10, SsdWearWarnPercent = 90 };
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        _repo.SnoozeAllAlerts(now.AddHours(1)); // snooze until 13:00

        _repo.AddDiskSamples([
            new DiskSample { TimestampUtc = now.AddMinutes(-30), DiskId = "0", WriteBytes = (long)(50 * ByteFormat.GiB) },
        ]);

        var alerts = _engine.Evaluate([MakeSsd(wearPercent: 95)], cfg, now);
        Assert.Empty(alerts);
    }

    [Fact]
    public void Evaluate_GlobalSnoozeExpired_AlertsFire()
    {
        var cfg = new AppConfig { SsdWearWarnPercent = 90 };
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        _repo.SnoozeAllAlerts(now.AddHours(-1)); // already expired

        var alerts = _engine.Evaluate([MakeSsd(wearPercent: 95)], cfg, now);
        Assert.NotEmpty(alerts);
    }

    // ────────────────────────────────────────── Per-process snooze

    [Fact]
    public void Evaluate_SnoozedProcess_SkipsItsAlerts()
    {
        var cfg = new AppConfig { ProcessWarnGbPerHour = 1 };
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        _repo.AddProcessSamples([
            new ProcessIoSample { TimestampUtc = now.AddMinutes(-10), ProcessName = "chrome", WriteBytes = (long)(5 * ByteFormat.GiB) },
        ]);

        _repo.SnoozeProcess("chrome", now.AddHours(1));

        var alerts = _engine.Evaluate([MakeSsd()], cfg, now);
        Assert.DoesNotContain(alerts, a => a.RuleKey == "proc-1h:chrome");
    }

    // ────────────────────────────────────────── Per-process threshold

    [Fact]
    public void Evaluate_ProcessExceedsThreshold_RaisesAlert()
    {
        var cfg = new AppConfig { ProcessWarnGbPerHour = 5 };
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        _repo.AddProcessSamples([
            new ProcessIoSample { TimestampUtc = now.AddMinutes(-10), ProcessName = "chrome", WriteBytes = (long)(10 * ByteFormat.GiB) },
        ]);

        var alerts = _engine.Evaluate([MakeSsd()], cfg, now);
        Assert.Contains(alerts, a => a.RuleKey == "proc-1h:chrome");
    }

    [Fact]
    public void Evaluate_ProcessAlert_ListsPhysicalWritesByDriveInDescendingOrder()
    {
        var cfg = new AppConfig { ProcessWarnGbPerHour = 5 };
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        _repo.AddProcessSamples([
            new ProcessIoSample { TimestampUtc = now.AddMinutes(-10), ProcessName = "backup", WriteBytes = (long)(10 * ByteFormat.GB) },
        ]);
        _repo.AddDiskSamples([
            new DiskSample { TimestampUtc = now.AddMinutes(-10), DiskId = "0", WriteBytes = (long)(2 * ByteFormat.GB) },
            new DiskSample { TimestampUtc = now.AddMinutes(-10), DiskId = "1", WriteBytes = (long)(9 * ByteFormat.GB) },
        ]);

        var alerts = _engine.Evaluate([MakeSsd(), MakeHdd()], cfg, now);

        var alert = Assert.Single(alerts, candidate => candidate.RuleKey == "proc-1h:backup");
        int hddPosition = alert.Message.IndexOf("D:  (Test HDD 1): 9 GB", StringComparison.Ordinal);
        int ssdPosition = alert.Message.IndexOf("C:  (Test SSD 0): 2 GB", StringComparison.Ordinal);
        Assert.True(hddPosition >= 0 && ssdPosition > hddPosition, alert.Message);
        Assert.Contains("Physical writes by drive (all processes, last hour)", alert.Message);
        Assert.Contains("Process requests cannot be assigned to one drive exactly", alert.Message);
    }

    // ────────────────────────────────────────── All-processes combined threshold

    [Fact]
    public void Evaluate_AllProcessesCombinedExceeds_RaisesAlert()
    {
        var cfg = new AppConfig { AllProcessesWarnGbPerHour = 10 };
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        _repo.AddProcessSamples([
            new ProcessIoSample { TimestampUtc = now.AddMinutes(-10), ProcessName = "chrome", WriteBytes = (long)(6 * ByteFormat.GiB) },
            new ProcessIoSample { TimestampUtc = now.AddMinutes(-10), ProcessName = "vscode", WriteBytes = (long)(6 * ByteFormat.GiB) },
        ]);

        var alerts = _engine.Evaluate([MakeSsd()], cfg, now);
        Assert.Contains(alerts, a => a.RuleKey == "procs-all-1h");
    }

    // ────────────────────────────────────────── Alerts are persisted

    [Fact]
    public void Evaluate_PersistsAlerts()
    {
        var cfg = new AppConfig { SsdWarnGbPerHour = 10 };
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        _repo.AddDiskSamples([
            new DiskSample { TimestampUtc = now.AddMinutes(-30), DiskId = "0", WriteBytes = (long)(15 * ByteFormat.GiB) },
        ]);

        var alerts = _engine.Evaluate([MakeSsd()], cfg, now);
        Assert.NotEmpty(alerts);

        // Verify persisted
        var persisted = _repo.GetRecentAlerts(100);
        Assert.True(persisted.Count >= alerts.Count);
        Assert.Contains(persisted, a => a.RuleKey == "ssd-1h:0");
    }

    // ────────────────────────────────────────── Threshold disabled (0)

    [Fact]
    public void Evaluate_ThresholdZero_DoesNotFire()
    {
        var cfg = new AppConfig { SsdWarnGbPerHour = 0 }; // disabled
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        _repo.AddDiskSamples([
            new DiskSample { TimestampUtc = now.AddMinutes(-30), DiskId = "0", WriteBytes = (long)(100 * ByteFormat.GiB) },
        ]);

        var alerts = _engine.Evaluate([MakeSsd()], cfg, now);
        Assert.DoesNotContain(alerts, a => a.RuleKey == "ssd-1h:0");
    }

    // ────────────────────────────────────────── Empty disk list

    [Fact]
    public void Evaluate_NoDisks_ReturnsEmpty()
    {
        var cfg = new AppConfig();
        var alerts = _engine.Evaluate([], cfg, DateTime.UtcNow);
        Assert.Empty(alerts);
    }
}
