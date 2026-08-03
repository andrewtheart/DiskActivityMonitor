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
    public void Evaluate_UnknownTbwProjection_ReportsEstimatedRange()
    {
        var cfg = new AppConfig
        {
            SsdWarnGbPerHour = 0,
            SsdWarnGbPerDay = 0,
            SsdCriticalGbPerDay = 0,
            TbwProjectionWarnYears = 2,
            TbwProjectionCriticalYears = 1,
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

        var alerts = _engine.Evaluate([MakeSsd()], cfg, now);

        var alert = Assert.Single(alerts, candidate => candidate.RuleKey == "tbw-life:0");
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Contains("150 to 600 TBW estimate", alert.Message);
        Assert.Contains("to", alert.Message);
    }

    // ────────────────────────────────────────── SMART wear percentage

    [Fact]
    public void Evaluate_WearExceedsThreshold_RaisesWarning()
    {
        var cfg = new AppConfig { SsdWearWarnPercent = 90 };
        var now = DateTime.UtcNow;
        var disk = MakeSsd(wearPercent: 92);

        var alerts = _engine.Evaluate([disk], cfg, now);
        Assert.Contains(alerts, a => a.RuleKey == "ssd-wear:0" && a.Severity == AlertSeverity.Warning);
    }

    [Fact]
    public void Evaluate_WearAbove95_RaisesCritical()
    {
        var cfg = new AppConfig { SsdWearWarnPercent = 90 };
        var now = DateTime.UtcNow;
        var disk = MakeSsd(wearPercent: 97);

        var alerts = _engine.Evaluate([disk], cfg, now);
        Assert.Contains(alerts, a => a.RuleKey == "ssd-wear:0" && a.Severity == AlertSeverity.Critical);
    }

    [Fact]
    public void Evaluate_WearBelowThreshold_NoAlert()
    {
        var cfg = new AppConfig { SsdWearWarnPercent = 90 };
        var now = DateTime.UtcNow;
        var disk = MakeSsd(wearPercent: 50);

        var alerts = _engine.Evaluate([disk], cfg, now);
        Assert.DoesNotContain(alerts, a => a.RuleKey.StartsWith("ssd-wear:"));
    }

    [Fact]
    public void Evaluate_WearNull_NoAlert()
    {
        var cfg = new AppConfig { SsdWearWarnPercent = 90 };
        var now = DateTime.UtcNow;
        var disk = MakeSsd(wearPercent: null);

        var alerts = _engine.Evaluate([disk], cfg, now);
        Assert.DoesNotContain(alerts, a => a.RuleKey.StartsWith("ssd-wear:"));
    }

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
            new ProcessIoSample { TimestampUtc = now.AddMinutes(-10), ProcessName = "backup", WriteBytes = (long)(10 * ByteFormat.GiB) },
        ]);
        _repo.AddDiskSamples([
            new DiskSample { TimestampUtc = now.AddMinutes(-10), DiskId = "0", WriteBytes = (long)(2 * ByteFormat.GiB) },
            new DiskSample { TimestampUtc = now.AddMinutes(-10), DiskId = "1", WriteBytes = (long)(9 * ByteFormat.GiB) },
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
