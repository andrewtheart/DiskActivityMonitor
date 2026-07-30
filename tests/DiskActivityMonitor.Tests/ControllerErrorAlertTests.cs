using DiskActivityMonitor.Core.Alerts;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Models;

namespace DiskActivityMonitor.Tests;

public sealed class ControllerErrorAlertTests : IDisposable
{
    private readonly string _dbPath;
    private readonly MonitorRepository _repo;
    private readonly AlertEngine _engine;

    public ControllerErrorAlertTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"dam_controller_{Guid.NewGuid():N}.db");
        _repo = new MonitorRepository(databasePath: _dbPath);
        _repo.EnsureSchema();
        _engine = new AlertEngine(_repo);
    }

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_dbPath) + "*"))
            try { File.Delete(file); } catch { }
    }

    private static DiskInfo MakeDisk(string id = "2", string volumes = "F:") => new()
    {
        DiskId = id,
        InstanceName = $"{id} {volumes}",
        FriendlyName = "Test USB Disk",
        Volumes = volumes,
        MediaType = DiskMediaType.Hdd,
    };

    private static DiskControllerErrorSummary MakeErrors(DateTime nowUtc, int count, string diskId = "2") => new()
    {
        DiskId = diskId,
        DevicePath = $@"\Device\Harddisk{diskId}\DR{diskId}",
        Count = count,
        FirstUtc = nowUtc.AddDays(-7),
        LatestUtc = nowUtc.AddMinutes(-2),
    };

    [Fact]
    public void EvaluateControllerErrors_AtWarningThreshold_RaisesMappedWarning()
    {
        var now = new DateTime(2026, 7, 28, 20, 0, 0, DateTimeKind.Utc);
        var cfg = new AppConfig
        {
            ControllerErrorWindowDays = 14,
            ControllerErrorWarnCount = 3,
            ControllerErrorCriticalCount = 10,
        };

        var alerts = _engine.EvaluateControllerErrors([MakeDisk()], [MakeErrors(now, 3)], cfg, now);

        var alert = Assert.Single(alerts);
        Assert.Equal("disk-controller:2", alert.RuleKey);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Contains("F:", alert.Title);
        Assert.Contains("3 Disk event 11 controller errors", alert.Message);
        Assert.Contains(@"\Device\Harddisk2\DR2", alert.Message);
        Assert.Contains("Healthy/Online", alert.Message);
        Assert.Equal(3, alert.Value);
        Assert.Equal(3, alert.Threshold);
    }

    [Fact]
    public void EvaluateControllerErrors_AtCriticalThreshold_RaisesCritical()
    {
        var now = new DateTime(2026, 7, 28, 20, 0, 0, DateTimeKind.Utc);
        var cfg = new AppConfig { ControllerErrorWarnCount = 3, ControllerErrorCriticalCount = 10 };

        var alert = Assert.Single(_engine.EvaluateControllerErrors([MakeDisk()], [MakeErrors(now, 42)], cfg, now));

        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.StartsWith("Repeated controller errors", alert.Title);
        Assert.Equal(42, alert.Value);
        Assert.Equal(10, alert.Threshold);
    }

    [Fact]
    public void EvaluateControllerErrors_BelowWarningThreshold_ReturnsEmpty()
    {
        var now = DateTime.UtcNow;
        var cfg = new AppConfig { ControllerErrorWarnCount = 3, ControllerErrorCriticalCount = 10 };

        var alerts = _engine.EvaluateControllerErrors([MakeDisk()], [MakeErrors(now, 2)], cfg, now);

        Assert.Empty(alerts);
    }

    [Fact]
    public void EvaluateControllerErrors_Disabled_ReturnsEmpty()
    {
        var now = DateTime.UtcNow;
        var cfg = new AppConfig { EnableControllerErrorAlerts = false };

        var alerts = _engine.EvaluateControllerErrors([MakeDisk()], [MakeErrors(now, 42)], cfg, now);

        Assert.Empty(alerts);
    }

    [Fact]
    public void EvaluateControllerErrors_UnknownDisk_UsesPhysicalDiskLabel()
    {
        var now = DateTime.UtcNow;
        var cfg = new AppConfig { ControllerErrorWarnCount = 3 };

        var alert = Assert.Single(_engine.EvaluateControllerErrors([], [MakeErrors(now, 3, "3")], cfg, now));

        Assert.Contains("Harddisk3", alert.Title);
        Assert.Contains("not currently present", alert.Message);
    }

    [Fact]
    public void EvaluateControllerErrors_WithinCooldown_SuppressesRepeat()
    {
        var now = new DateTime(2026, 7, 28, 20, 0, 0, DateTimeKind.Utc);
        var cfg = new AppConfig
        {
            ControllerErrorWarnCount = 3,
            ControllerErrorCriticalCount = 10,
            AlertCooldownMinutes = 5,
        };

        Assert.Single(_engine.EvaluateControllerErrors([MakeDisk()], [MakeErrors(now, 13)], cfg, now));
        var repeat = _engine.EvaluateControllerErrors([MakeDisk()], [MakeErrors(now.AddMinutes(2), 13)], cfg, now.AddMinutes(2));

        Assert.Empty(repeat);
    }

    [Fact]
    public void EvaluateControllerErrors_ZeroWarningAndGlobalSnoozeSuppress()
    {
        var now = DateTime.UtcNow;
        Assert.Empty(_engine.EvaluateControllerErrors([MakeDisk()], [MakeErrors(now, 42)],
            new AppConfig { ControllerErrorWarnCount = 0 }, now));

        _repo.SnoozeAllAlerts(now.AddHours(1));
        Assert.Empty(_engine.EvaluateControllerErrors([MakeDisk()], [MakeErrors(now, 42)],
            new AppConfig { ControllerErrorWarnCount = 1 }, now));
    }

    [Fact]
    public void EvaluateControllerErrors_DisabledCriticalAndSingleEventUseWarningSingular()
    {
        var now = DateTime.UtcNow;
        var alert = Assert.Single(_engine.EvaluateControllerErrors([MakeDisk()], [MakeErrors(now, 1)],
            new AppConfig { ControllerErrorWarnCount = 1, ControllerErrorCriticalCount = 0 }, now));

        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Contains("1 Disk event 11 controller error for", alert.Message);
    }
}