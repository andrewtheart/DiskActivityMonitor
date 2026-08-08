using DiskActivityMonitor.Cli;
using DiskActivityMonitor.Core;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Models;

namespace DiskActivityMonitor.Tests;

public sealed class CliCoverageTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dam_cli_coverage_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        CliRunner.RepositoryFactory = () => new MonitorRepository();
        CliRunner.ConfigFactory = () => new ConfigStore().Current;
        CliRunner.DatabaseStatusProvider = () => default;
        CliRunner.WatchIterationRequested = () => true;
        CliRunner.WatchDelay = Thread.Sleep;
        CliRunner.WatchClear = Console.Clear;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_db) + "*"))
            try { File.Delete(file); } catch { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Endurance_ReportsCoverageAndGatesCalendarProjection(bool highCoverage)
    {
        var repo = new MonitorRepository(_db);
        repo.EnsureSchema();
        var now = DateTime.UtcNow;
        var end = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
        repo.UpsertDisks([new DiskInfo
        {
            DiskId = "0", InstanceName = "0 C:", FriendlyName = "CLI SSD", Volumes = "C:",
            MediaType = DiskMediaType.Ssd, LifetimeBytesWritten = 10_000,
        }]);
        repo.AddDiskSamples([
            new DiskSample { TimestampUtc = end.AddDays(-8), DiskId = "0", WriteBytes = 1 },
            new DiskSample { TimestampUtc = end.AddMinutes(-1), DiskId = "0", WriteBytes = 7_000_000_000_000 },
        ]);
        int heartbeatMinutes = highCoverage ? 7 * 24 * 60 : 1;
        for (int minute = heartbeatMinutes; minute >= 1; minute--)
            repo.AddCollectorHeartbeat(end.AddMinutes(-minute));

        CliRunner.RepositoryFactory = () => new MonitorRepository(_db);
        CliRunner.ConfigFactory = () => new AppConfig
        {
            DiskTbwRatings = new() { ["0"] = 150 },
            HighCoveragePercent = 90,
        };
        var output = new StringWriter();
        TextWriter original = Console.Out;
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, CliRunner.Run(["endurance", "--disk", "0"]));
        }
        finally
        {
            Console.SetOut(original);
        }

        string text = output.ToString();
        Assert.Contains("Monitored average", text);
        Assert.Contains("Coverage", text);
        if (highCoverage)
        {
            Assert.Contains("Calendar average", text);
            Assert.Contains("at the monitored rate", text);
        }
        else
        {
            Assert.DoesNotContain("Calendar average", text);
            Assert.Contains("withheld below 90% coverage", text);
        }
    }

    [Fact]
    public void StatusAlertsAndOneWatchIteration_RenderStandardTimes()
    {
        var repo = new MonitorRepository(_db);
        repo.EnsureSchema();
        DateTime now = DateTime.UtcNow;
        repo.UpsertDisks([new DiskInfo
        {
            DiskId = "0", InstanceName = "0 C:", FriendlyName = "CLI SSD", Volumes = "C:",
            MediaType = DiskMediaType.Ssd,
        }]);
        repo.AddDiskSamples([new DiskSample
        {
            TimestampUtc = now.AddMinutes(-1), DiskId = "0", WriteBytes = 123,
        }]);
        repo.InsertAlert(new AlertRecord
        {
            TimestampUtc = now,
            Severity = AlertSeverity.Warning,
            RuleKey = "test",
            Title = "CLI alert",
            Message = "details",
            Value = 1,
            Threshold = 1,
        });

        CliRunner.RepositoryFactory = () => new MonitorRepository(_db);
        CliRunner.ConfigFactory = () => new AppConfig { DashboardRefreshSeconds = 2 };
        CliRunner.DatabaseStatusProvider = () => (true, 1024 * 1024, now);
        int watchChecks = 0;
        CliRunner.WatchIterationRequested = () => watchChecks++ == 0;
        CliRunner.WatchDelay = _ => { };
        CliRunner.WatchClear = () => { };

        var output = new StringWriter();
        TextWriter original = Console.Out;
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, CliRunner.Run(["status"]));
            CliRunner.DatabaseStatusProvider = () => default;
            Assert.Equal(0, CliRunner.Run(["status"]));
            Assert.Equal(0, CliRunner.Run(["alerts", "--full"]));
            Assert.Equal(0, CliRunner.Run(["watch", "--interval", "2"]));
        }
        finally
        {
            Console.SetOut(original);
        }

        string text = output.ToString();
        Assert.Contains("Database          : 1.0 MB, updated", text);
        Assert.Contains("CLI alert", text);
        Assert.Contains("Disk Activity Monitor — live", text);
        Assert.Matches(@"\b(?:AM|PM)\b", text);
        Assert.True(CliRunner.ShouldContinueWatch(false, () => true));
        Assert.False(CliRunner.ShouldContinueWatch(false, () => false));
        bool requested = false;
        Assert.False(CliRunner.ShouldContinueWatch(true, () => { requested = true; return true; }));
        Assert.False(requested);

        string metadataPath = Path.Combine(Path.GetTempPath(), $"dam_cli_metadata_{Guid.NewGuid():N}.db");
        try
        {
            Assert.False(CliRunner.ReadDatabaseStatus(metadataPath).Exists);
            File.WriteAllBytes(metadataPath, new byte[] { 1, 2, 3 });
            var metadata = CliRunner.ReadDatabaseStatus(metadataPath);
            Assert.True(metadata.Exists);
            Assert.Equal(3, metadata.Length);
            _ = CliRunner.ReadDatabaseStatus();
        }
        finally
        {
            try { File.Delete(metadataPath); } catch { }
        }
    }
}
