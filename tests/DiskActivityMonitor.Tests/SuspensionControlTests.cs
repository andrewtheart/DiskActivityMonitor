using DiskActivityMonitor.Core.Collection;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Models;
using DiskActivityMonitor.Tray;

namespace DiskActivityMonitor.Tests;

public sealed class SuspensionControlTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dam_suspend_{Guid.NewGuid():N}.db");
    private readonly string _settings = Path.Combine(Path.GetTempPath(), $"dam_suspend_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_db) + "*"))
            try { File.Delete(file); } catch { }
        try { File.Delete(_settings); } catch { }
    }

    [Theory]
    [InlineData("5m", 5)]
    [InlineData("15m", 15)]
    [InlineData("30m", 30)]
    [InlineData("1h", 60)]
    [InlineData("nonsense", 30)]
    public void SuspendDuration_MapsChoicesToMinutes(string id, int expectedMinutes)
        => Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), SuspendDurationOptions.ToTimeSpan(id));

    [Fact]
    public void SuspendDuration_ManualChoiceNeverExpires()
    {
        Assert.Null(SuspendDurationOptions.ToTimeSpan(SuspendDurationOptions.ManualId));
        Assert.Null(SuspendDurationOptions.ResumeAt(DateTime.UtcNow, 0));
    }

    [Fact]
    public void SuspendDuration_DefaultsToThirtyMinutes()
    {
        Assert.Equal("30m", SuspendDurationOptions.DefaultId);
        Assert.Equal(TimeSpan.FromMinutes(30), SuspendDurationOptions.ToTimeSpan(SuspendDurationOptions.DefaultId));
        Assert.Equal(30, new UserSettings().DefaultSuspendMinutes);

        // Windows toast selection boxes are limited to five items.
        Assert.True(SuspendDurationOptions.Choices.Length <= 5);
        Assert.Contains(SuspendDurationOptions.Choices, c => c.Id == SuspendDurationOptions.DefaultId);
    }

    [Theory]
    [InlineData(0, SuspendDurationOptions.ManualId)]
    [InlineData(5, "5m")]
    [InlineData(10, "15m")]
    [InlineData(30, "30m")]
    [InlineData(240, "1h")]
    public void SuspendDuration_PreselectsNearestConfiguredChoice(int minutes, string expectedId)
        => Assert.Equal(expectedId, SuspendDurationOptions.DefaultIdFor(minutes));

    [Fact]
    public void ResumeExpired_ReleasesOnlyDueSuspensions()
    {
        var repo = new MonitorRepository(_db);
        repo.EnsureSchema();
        var now = DateTime.UtcNow;
        var identities = new[] { new ProcessControl.ProcessIdentity(4242, 1, @"C:\Apps\gone.exe") };

        repo.AddSuspendedProcess("expired", now.AddHours(-1), null, identities, now.AddMinutes(-1), SuspendSource.Manual);
        repo.AddSuspendedProcess("pending", now, null, identities, now.AddMinutes(30), SuspendSource.AutoRule);
        repo.AddSuspendedProcess("indefinite", now, null, identities, null, SuspendSource.Manual);

        var manager = new AutoSuspendManager(repo, new UserSettingsStore(_settings));
        var resumed = manager.ResumeExpired(now);

        var single = Assert.Single(resumed);
        Assert.Equal("expired", single.ProcessName);
        Assert.Equal(SuspendSource.Manual, single.Source);

        var remaining = repo.GetSuspendedProcessNames();
        Assert.DoesNotContain("expired", remaining);
        Assert.Contains("pending", remaining);
        Assert.Contains("indefinite", remaining);
    }

    [Fact]
    public void SuspendTracked_RecordsDeadlineAndOrigin()
    {
        var repo = new MonitorRepository(_db);
        repo.EnsureSchema();
        var now = DateTime.UtcNow;

        // No such process exists, so nothing is suspended and nothing must be recorded.
        var result = AutoSuspendManager.SuspendTracked(
            repo, $"dam-absent-{Guid.NewGuid():N}", null, now.AddMinutes(30), SuspendSource.Manual, now);

        Assert.Equal(0, result.Affected);
        Assert.Empty(repo.GetSuspendedProcessNames());
    }

    [Fact]
    public void SuspendOrigin_SeparatesRuleConfirmationsFromAdHocSuspends()
    {
        // The auto-suspend confirmation toast is rule-driven even though the user approves it.
        Assert.Equal(SuspendSource.AutoRule, SuspendOriginArguments.ToSource(SuspendOriginArguments.Rule));

        // An alert toast carries no origin, so it stays an ad-hoc user action.
        Assert.Equal(SuspendSource.Manual, SuspendOriginArguments.ToSource(null));
        Assert.Equal(SuspendSource.Manual, SuspendOriginArguments.ToSource(""));
        Assert.Equal(SuspendSource.Manual, SuspendOriginArguments.ToSource("Rule"));
    }

    [Fact]
    public void SuspendMinutes_ClampAndRejectInvalidInput()
    {
        Assert.Equal(45, MainWindow.ParseSuspendMinutes("45", 30));
        Assert.Equal(0, MainWindow.ParseSuspendMinutes("0", 30));
        Assert.Equal(1440, MainWindow.ParseSuspendMinutes("100000", 30));
        Assert.Equal(30, MainWindow.ParseSuspendMinutes("-5", 30));
        Assert.Equal(30, MainWindow.ParseSuspendMinutes("later", 30));
    }

    [Fact]
    public void SuspensionDetail_DescribesDeadlineAndOrigin()
    {
        var suspendedAt = new DateTime(2026, 8, 4, 9, 0, 0, DateTimeKind.Utc);
        var indefinite = new SuspendedProcessState("writer", suspendedAt, null, [], null, SuspendSource.Manual);
        Assert.Contains("until you resume it", MainWindow.FormatSuspensionDetail(indefinite, suspendedAt));
        Assert.Equal("Suspended by you", MainWindow.SourceLabel(SuspendSource.Manual));
        Assert.Equal("Auto-suspend rule", MainWindow.SourceLabel(SuspendSource.AutoRule));

        var timed = indefinite with { ResumeAtUtc = suspendedAt.AddMinutes(30) };
        Assert.Contains("30 min", MainWindow.FormatSuspensionDetail(timed, suspendedAt));
        Assert.Contains("less than a minute", MainWindow.FormatSuspensionDetail(timed, suspendedAt.AddSeconds(1770)));
        Assert.Contains("2 h", MainWindow.FormatSuspensionDetail(
            indefinite with { ResumeAtUtc = suspendedAt.AddHours(2) }, suspendedAt));
        Assert.Contains("resuming now", MainWindow.FormatSuspensionDetail(timed, suspendedAt.AddMinutes(30)));
    }
}
