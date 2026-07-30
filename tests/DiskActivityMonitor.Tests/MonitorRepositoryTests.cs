using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Models;

namespace DiskActivityMonitor.Tests;

/// <summary>
/// Tests for MonitorRepository using an in-memory SQLite database.
/// Each test gets a fresh database via <see cref="CreateRepo"/>.
/// </summary>
public class MonitorRepositoryTests : IDisposable
{
    private readonly string _dbPath;

    public MonitorRepositoryTests()
    {
        // Use a unique temp file per test instance to avoid cross-test interference.
        _dbPath = Path.Combine(Path.GetTempPath(), $"dam_test_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        // Clean up the temp database files.
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_dbPath) + "*"))
            try { File.Delete(f); } catch { }
    }

    private MonitorRepository CreateRepo()
    {
        var repo = new MonitorRepository(databasePath: _dbPath);
        repo.EnsureSchema();
        return repo;
    }

    // ────────────────────────────────────────── Schema / Migration

    [Fact]
    public void EnsureSchema_CanBeCalledTwice()
    {
        var repo = CreateRepo();
        repo.EnsureSchema(); // second call should be a no-op
    }

    // ────────────────────────────────────────── Disk CRUD

    [Fact]
    public void UpsertDisks_InsertsAndUpdates()
    {
        var repo = CreateRepo();

        var disk = new DiskInfo
        {
            DiskId = "0",
            InstanceName = "0 C:",
            FriendlyName = "Samsung 990 PRO",
            Volumes = "C:",
            MediaType = DiskMediaType.Ssd,
            SizeBytes = 2_000_000_000_000,
            SerialNumber = "SN123",
            WearPercent = 5,
            LifetimeBytesWritten = 1_000_000_000_000,
            LifetimeBytesRead = 2_000_000_000_000,
        };

        repo.UpsertDisks([disk]);
        var disks = repo.GetDisks();
        Assert.Single(disks);
        Assert.Equal("Samsung 990 PRO", disks[0].FriendlyName);
        Assert.Equal(5, disks[0].WearPercent);
        Assert.Equal(1_000_000_000_000, disks[0].LifetimeBytesWritten);

        // Update the disk
        disk.FriendlyName = "Samsung 990 PRO 2TB";
        disk.WearPercent = 10;
        repo.UpsertDisks([disk]);
        disks = repo.GetDisks();
        Assert.Single(disks);
        Assert.Equal("Samsung 990 PRO 2TB", disks[0].FriendlyName);
        Assert.Equal(10, disks[0].WearPercent);
    }

    [Fact]
    public void UpsertDisks_NullLifetimeBytes_PreservesExisting()
    {
        var repo = CreateRepo();

        // First insert with lifetime bytes
        repo.UpsertDisks([new DiskInfo
        {
            DiskId = "0", InstanceName = "0 C:", MediaType = DiskMediaType.Ssd,
            LifetimeBytesWritten = 500_000_000_000,
        }]);

        // Second upsert with null lifetime bytes (e.g. SMART read failed)
        repo.UpsertDisks([new DiskInfo
        {
            DiskId = "0", InstanceName = "0 C:", MediaType = DiskMediaType.Ssd,
            LifetimeBytesWritten = null,
        }]);

        var disks = repo.GetDisks();
        Assert.Equal(500_000_000_000, disks[0].LifetimeBytesWritten);
    }

    // ────────────────────────────────────────── Disk Samples

    [Fact]
    public void AddDiskSamples_And_GetDiskTotals()
    {
        var repo = CreateRepo();
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        repo.AddDiskSamples([
            new DiskSample { TimestampUtc = now, DiskId = "0", ReadBytes = 1000, WriteBytes = 2000 },
            new DiskSample { TimestampUtc = now.AddMinutes(1), DiskId = "0", ReadBytes = 500, WriteBytes = 1500 },
        ]);

        var (read, write) = repo.GetDiskTotals("0", now.AddMinutes(-1), now.AddMinutes(5));
        Assert.Equal(1500, read);
        Assert.Equal(3500, write);
    }

    [Fact]
    public void AddDiskSamples_SameMinute_Accumulates()
    {
        var repo = CreateRepo();
        var ts = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        repo.AddDiskSamples([new DiskSample { TimestampUtc = ts, DiskId = "0", ReadBytes = 100, WriteBytes = 200 }]);
        repo.AddDiskSamples([new DiskSample { TimestampUtc = ts, DiskId = "0", ReadBytes = 300, WriteBytes = 400 }]);

        var (read, write) = repo.GetDiskTotals("0", ts, ts.AddMinutes(1));
        Assert.Equal(400, read);   // 100 + 300
        Assert.Equal(600, write);  // 200 + 400
    }

    [Fact]
    public void GetDiskTotals_NoDiskData_ReturnsZero()
    {
        var repo = CreateRepo();
        var now = DateTime.UtcNow;
        var (read, write) = repo.GetDiskTotals("nonexistent", now.AddHours(-1), now);
        Assert.Equal(0, read);
        Assert.Equal(0, write);
    }

    [Fact]
    public void GetEarliestSample_ReturnsMostAncientTimestamp()
    {
        var repo = CreateRepo();
        var t1 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        repo.AddDiskSamples([
            new DiskSample { TimestampUtc = t2, DiskId = "0", WriteBytes = 1 },
            new DiskSample { TimestampUtc = t1, DiskId = "0", WriteBytes = 1 },
        ]);

        Assert.Equal(t1, repo.GetEarliestSample("0"));
    }

    [Fact]
    public void GetEarliestSample_NoDiskData_ReturnsNull()
    {
        var repo = CreateRepo();
        Assert.Null(repo.GetEarliestSample("missing"));
    }

    [Fact]
    public void GetHourlyDiskTotals_GroupsByHour()
    {
        var repo = CreateRepo();
        var h1 = new DateTime(2025, 6, 1, 10, 0, 0, DateTimeKind.Utc);

        repo.AddDiskSamples([
            new DiskSample { TimestampUtc = h1, DiskId = "0", ReadBytes = 100, WriteBytes = 200 },
            new DiskSample { TimestampUtc = h1.AddMinutes(30), DiskId = "0", ReadBytes = 50, WriteBytes = 80 },
            new DiskSample { TimestampUtc = h1.AddHours(1), DiskId = "0", ReadBytes = 10, WriteBytes = 20 },
        ]);

        var hourly = repo.GetHourlyDiskTotals("0", h1, h1.AddHours(2));
        Assert.Equal(2, hourly.Count);
        Assert.Equal(150, hourly[0].Read);
        Assert.Equal(280, hourly[0].Write);
        Assert.Equal(10, hourly[1].Read);
        Assert.Equal(20, hourly[1].Write);
    }

    // ────────────────────────────────────────── Process Samples

    [Fact]
    public void AddProcessSamples_And_GetTopProcesses()
    {
        var repo = CreateRepo();
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        repo.AddProcessSamples([
            new ProcessIoSample { TimestampUtc = now, ProcessName = "chrome", ReadBytes = 100, WriteBytes = 5000 },
            new ProcessIoSample { TimestampUtc = now, ProcessName = "vscode", ReadBytes = 200, WriteBytes = 3000 },
            new ProcessIoSample { TimestampUtc = now, ProcessName = "notepad", ReadBytes = 50, WriteBytes = 100 },
        ]);

        var top = repo.GetTopProcesses(now.AddMinutes(-1), now.AddMinutes(1), topN: 2);
        Assert.Equal(2, top.Count);
        Assert.Equal("chrome", top[0].ProcessName);
        Assert.Equal(5000, top[0].WriteBytes);
        Assert.Equal("vscode", top[1].ProcessName);
    }

    [Fact]
    public void GetProcessWrite_ReturnsSumForNamedProcess()
    {
        var repo = CreateRepo();
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        repo.AddProcessSamples([
            new ProcessIoSample { TimestampUtc = now, ProcessName = "chrome", WriteBytes = 1000 },
            new ProcessIoSample { TimestampUtc = now.AddMinutes(1), ProcessName = "chrome", WriteBytes = 2000 },
        ]);

        Assert.Equal(3000, repo.GetProcessWrite("chrome", now.AddMinutes(-1), now.AddMinutes(5)));
    }

    [Fact]
    public void GetAllProcessesWrite_SumsAcrossAllProcesses()
    {
        var repo = CreateRepo();
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        repo.AddProcessSamples([
            new ProcessIoSample { TimestampUtc = now, ProcessName = "a", WriteBytes = 100 },
            new ProcessIoSample { TimestampUtc = now, ProcessName = "b", WriteBytes = 200 },
        ]);

        Assert.Equal(300, repo.GetAllProcessesWrite(now.AddMinutes(-1), now.AddMinutes(1)));
    }

    [Fact]
    public void GetKnownProcessNames_ReturnsDistinctNames()
    {
        var repo = CreateRepo();
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        repo.AddProcessSamples([
            new ProcessIoSample { TimestampUtc = now, ProcessName = "chrome", WriteBytes = 1 },
            new ProcessIoSample { TimestampUtc = now.AddMinutes(1), ProcessName = "chrome", WriteBytes = 1 },
            new ProcessIoSample { TimestampUtc = now, ProcessName = "vscode", WriteBytes = 1 },
        ]);

        var names = repo.GetKnownProcessNames();
        Assert.Equal(2, names.Count);
        Assert.Contains("chrome", names);
        Assert.Contains("vscode", names);
    }

    // ────────────────────────────────────────── Alerts

    [Fact]
    public void InsertAlert_And_GetRecentAlerts()
    {
        var repo = CreateRepo();
        var now = DateTime.UtcNow;

        var alert = new AlertRecord
        {
            TimestampUtc = now,
            Severity = AlertSeverity.Warning,
            RuleKey = "test-rule",
            Title = "Test Alert",
            Message = "Something happened",
            Value = 1000,
            Threshold = 500,
        };

        var id = repo.InsertAlert(alert);
        Assert.True(id > 0);

        var recent = repo.GetRecentAlerts(10);
        Assert.Single(recent);
        Assert.Equal("test-rule", recent[0].RuleKey);
        Assert.Equal("Test Alert", recent[0].Title);
        Assert.Equal(AlertSeverity.Warning, recent[0].Severity);
        Assert.False(recent[0].Acknowledged);
    }

    [Fact]
    public void GetRecentAlerts_WithSinceUtc_FiltersOlderAlerts()
    {
        var repo = CreateRepo();
        var old = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var recent = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        repo.InsertAlert(new AlertRecord { TimestampUtc = old, RuleKey = "old", Title = "Old", Message = "m", Value = 0, Threshold = 0 });
        repo.InsertAlert(new AlertRecord { TimestampUtc = recent, RuleKey = "new", Title = "New", Message = "m", Value = 0, Threshold = 0 });

        var result = repo.GetRecentAlerts(10, sinceUtc: new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Single(result);
        Assert.Equal("new", result[0].RuleKey);
    }

    [Fact]
    public void GetRecentAlerts_UnacknowledgedOnly_FiltersAcknowledged()
    {
        var repo = CreateRepo();
        var now = DateTime.UtcNow;

        var id1 = repo.InsertAlert(new AlertRecord { TimestampUtc = now, RuleKey = "a", Title = "A", Message = "m", Value = 0, Threshold = 0 });
        repo.InsertAlert(new AlertRecord { TimestampUtc = now, RuleKey = "b", Title = "B", Message = "m", Value = 0, Threshold = 0 });

        repo.AcknowledgeAlerts([id1]);

        var unacked = repo.GetRecentAlerts(10, unacknowledgedOnly: true);
        Assert.Single(unacked);
        Assert.Equal("b", unacked[0].RuleKey);
    }

    [Fact]
    public void DismissAndRestoreAlerts_PreservesCompleteHistory()
    {
        var repo = CreateRepo();
        var now = DateTime.UtcNow;
        long dismissedId = repo.InsertAlert(new AlertRecord
        {
            TimestampUtc = now, RuleKey = "dismissed", Title = "Dismissed", Message = "m", Value = 0, Threshold = 0,
        });
        repo.InsertAlert(new AlertRecord
        {
            TimestampUtc = now, RuleKey = "visible", Title = "Visible", Message = "m", Value = 0, Threshold = 0,
        });

        repo.DismissAlerts([dismissedId]);

        Assert.Equal(2, repo.GetRecentAlerts(10).Count);
        Assert.True(repo.GetRecentAlerts(10).Single(a => a.Id == dismissedId).Acknowledged);
        Assert.DoesNotContain(repo.GetRecentAlerts(10, unacknowledgedOnly: true), a => a.Id == dismissedId);

        repo.RestoreAlerts([dismissedId]);

        Assert.False(repo.GetRecentAlerts(10).Single(a => a.Id == dismissedId).Acknowledged);
        Assert.Equal(2, repo.GetRecentAlerts(10, unacknowledgedOnly: true).Count);
    }

    [Fact]
    public void AcknowledgeAlerts_AllAtOnce()
    {
        var repo = CreateRepo();
        var now = DateTime.UtcNow;

        repo.InsertAlert(new AlertRecord { TimestampUtc = now, RuleKey = "a", Title = "A", Message = "m", Value = 0, Threshold = 0 });
        repo.InsertAlert(new AlertRecord { TimestampUtc = now, RuleKey = "b", Title = "B", Message = "m", Value = 0, Threshold = 0 });

        repo.AcknowledgeAlerts(); // null = all
        var unacked = repo.GetRecentAlerts(10, unacknowledgedOnly: true);
        Assert.Empty(unacked);
    }

    [Fact]
    public void AcknowledgeAlertsByRule_OnlyAffectsMatchingRule()
    {
        var repo = CreateRepo();
        var now = DateTime.UtcNow;

        repo.InsertAlert(new AlertRecord { TimestampUtc = now, RuleKey = "ssd-1h:0", Title = "A", Message = "m", Value = 0, Threshold = 0 });
        repo.InsertAlert(new AlertRecord { TimestampUtc = now, RuleKey = "proc-1h:chrome", Title = "B", Message = "m", Value = 0, Threshold = 0 });

        repo.AcknowledgeAlertsByRule("ssd-1h:0");

        var unacked = repo.GetRecentAlerts(10, unacknowledgedOnly: true);
        Assert.Single(unacked);
        Assert.Equal("proc-1h:chrome", unacked[0].RuleKey);
    }

    [Fact]
    public void GetLastAlertTime_ReturnsLatestForRule()
    {
        var repo = CreateRepo();
        var t1 = new DateTime(2025, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        repo.InsertAlert(new AlertRecord { TimestampUtc = t1, RuleKey = "r1", Title = "T", Message = "m", Value = 0, Threshold = 0 });
        repo.InsertAlert(new AlertRecord { TimestampUtc = t2, RuleKey = "r1", Title = "T", Message = "m", Value = 0, Threshold = 0 });

        Assert.Equal(t2, repo.GetLastAlertTime("r1"));
    }

    [Fact]
    public void GetLastAlertTime_NoAlerts_ReturnsNull()
    {
        var repo = CreateRepo();
        Assert.Null(repo.GetLastAlertTime("nonexistent"));
    }

    // ────────────────────────────────────────── Global Snooze

    [Fact]
    public void GlobalSnooze_ActivatesAndExpires()
    {
        var repo = CreateRepo();
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var until = now.AddHours(1);

        Assert.False(repo.IsGlobalSnoozeActive(now));

        repo.SnoozeAllAlerts(until);
        Assert.True(repo.IsGlobalSnoozeActive(now));
        Assert.False(repo.IsGlobalSnoozeActive(until.AddSeconds(1)));

        Assert.NotNull(repo.GetGlobalSnoozeUntil(now));
        Assert.Null(repo.GetGlobalSnoozeUntil(until.AddSeconds(1)));
    }

    [Fact]
    public void ClearGlobalSnooze_DeactivatesImmediately()
    {
        var repo = CreateRepo();
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        repo.SnoozeAllAlerts(now.AddHours(1));
        Assert.True(repo.IsGlobalSnoozeActive(now));

        repo.ClearGlobalSnooze();
        Assert.False(repo.IsGlobalSnoozeActive(now));
    }

    // ────────────────────────────────────────── Process Snooze

    [Fact]
    public void ProcessSnooze_TracksActiveSnoozes()
    {
        var repo = CreateRepo();
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        repo.SnoozeProcess("chrome", now.AddHours(1));

        var active = repo.GetActiveProcessSnoozes(now);
        Assert.Contains("chrome", active);

        // After expiry
        var expired = repo.GetActiveProcessSnoozes(now.AddHours(2));
        Assert.DoesNotContain("chrome", expired);
    }

    [Fact]
    public void ClearProcessSnooze_RemovesSnooze()
    {
        var repo = CreateRepo();
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        repo.SnoozeProcess("chrome", now.AddHours(1));
        repo.ClearProcessSnooze("chrome");

        Assert.DoesNotContain("chrome", repo.GetActiveProcessSnoozes(now));
    }

    [Fact]
    public void GetProcessSnoozes_ReturnsActiveWithExpiry()
    {
        var repo = CreateRepo();
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var until = now.AddHours(2);

        repo.SnoozeProcess("chrome", until);
        repo.SnoozeProcess("vscode", until.AddHours(1));

        var snoozes = repo.GetProcessSnoozes(now);
        Assert.Equal(2, snoozes.Count);
    }

    // ────────────────────────────────────────── Suspended Processes

    [Fact]
    public void SuspendedProcesses_AddGetRemove()
    {
        var repo = CreateRepo();
        var now = DateTime.UtcNow;

        repo.AddSuspendedProcess("chrome", now);
        var suspended = repo.GetSuspendedProcessNames();
        Assert.Contains("chrome", suspended);

        var withTime = repo.GetSuspendedProcesses();
        Assert.Single(withTime);
        Assert.Equal("chrome", withTime[0].Name);

        repo.RemoveSuspendedProcess("chrome");
        Assert.Empty(repo.GetSuspendedProcessNames());
    }

    [Fact]
    public void AcknowledgeProcessAlerts_OnlyAffectsMatchingProcess()
    {
        var repo = CreateRepo();
        var now = DateTime.UtcNow;

        repo.InsertAlert(new AlertRecord { TimestampUtc = now, RuleKey = "proc-1h:chrome", Title = "Chrome", Message = "m", Value = 0, Threshold = 0 });
        repo.InsertAlert(new AlertRecord { TimestampUtc = now, RuleKey = "proc-1h:vscode", Title = "VSCode", Message = "m", Value = 0, Threshold = 0 });

        repo.AcknowledgeProcessAlerts("chrome");

        var unacked = repo.GetRecentAlerts(10, unacknowledgedOnly: true);
        Assert.Single(unacked);
        Assert.Equal("proc-1h:vscode", unacked[0].RuleKey);
    }
}
