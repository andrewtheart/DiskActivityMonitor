using DiskActivityMonitor.Core;
using DiskActivityMonitor.Tray;

namespace DiskActivityMonitor.Tests;

public sealed class TotalWrittenTrendTests
{
    [Theory]
    [InlineData(1, 60)]
    [InlineData(24, 900)]
    [InlineData(24 * 7, 7200)]
    [InlineData(24 * 30, 43200)]
    [InlineData(24 * 365, 345600)]
    public void SelectTrendBucket_AdaptsToSelectedDuration(int hours, int expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            MainWindow.SelectTrendBucket(TimeSpan.FromHours(hours)));
    }

    [Theory]
    [InlineData(10_000L, 600L, 999L, 9_400L)]
    [InlineData(100L, 500L, 999L, 0L)]
    [InlineData(null, 600L, 999L, 999L)]
    [InlineData(null, 600L, -1L, 0L)]
    public void CalculateTrendStartTotal_UsesLifetimeAnchorOrRecordedHistory(
        long? lifetime,
        long recordedAfterStart,
        long recordedBeforeStart,
        long expected)
    {
        Assert.Equal(
            expected,
            MainWindow.CalculateTrendStartTotal(lifetime, recordedAfterStart, recordedBeforeStart));
    }

    [Fact]
    public void ResolveCustomTrendRange_IncludesEntirePastEndDate()
    {
        DateTime nowUtc = DateTime.UtcNow;
        DateTime todayLocal = nowUtc.ToLocalTime().Date;
        DateTime start = todayLocal.AddDays(-3);
        DateTime end = todayLocal.AddDays(-1);

        var result = MainWindow.ResolveCustomTrendRange(start, end, nowUtc);

        Assert.NotNull(result);
        Assert.Equal(DateTime.SpecifyKind(start, DateTimeKind.Local).ToUniversalTime(), result.Value.FromUtc);
        Assert.Equal(DateTime.SpecifyKind(end, DateTimeKind.Local).AddDays(1).ToUniversalTime(), result.Value.ToUtc);
    }

    [Fact]
    public void ResolveCustomTrendRange_TodayEndsAtCurrentInstant()
    {
        DateTime nowUtc = DateTime.UtcNow;
        DateTime todayLocal = nowUtc.ToLocalTime().Date;

        var result = MainWindow.ResolveCustomTrendRange(todayLocal, todayLocal, nowUtc);

        Assert.NotNull(result);
        Assert.Equal(nowUtc, result.Value.ToUtc);
    }

    [Fact]
    public void ResolveCustomTrendRange_RejectsMissingReversedAndFutureDates()
    {
        DateTime nowUtc = DateTime.UtcNow;
        DateTime todayLocal = nowUtc.ToLocalTime().Date;

        Assert.Null(MainWindow.ResolveCustomTrendRange(null, todayLocal, nowUtc));
        Assert.Null(MainWindow.ResolveCustomTrendRange(todayLocal, todayLocal.AddDays(-1), nowUtc));
        Assert.Null(MainWindow.ResolveCustomTrendRange(todayLocal.AddDays(1), todayLocal.AddDays(2), nowUtc));
    }

    [Theory]
    [InlineData(1, 1024, "1 KB/hour")]
    [InlineData(24 * 7, 7 * 1024, "1 KB/day")]
    [InlineData(24 * 364, 52 * 1024, "1 KB/week")]
    public void FormatTrendChange_UsesAReadableRateForTheRange(
        int hours,
        long increase,
        string expectedRate)
    {
        string result = MainWindow.FormatTrendChange(increase, TimeSpan.FromHours(hours));

        Assert.Contains($"Increase: +{ByteFormat.Humanize(increase)}", result);
        Assert.Contains(expectedRate, result);
    }
}