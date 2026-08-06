using DiskActivityMonitor.Core;
using DiskActivityMonitor.Tray;

namespace DiskActivityMonitor.Tests;

public sealed class MonitoringRateStatsTests
{
    [Fact]
    public void Compute_HighCoverage_ReportsMonitoredAndCalendarRates()
    {
        var stats = MonitoringRateStats.Compute(900, 90, 100, 90);

        Assert.Equal(600, stats.MonitoredBytesPerHour);
        Assert.Equal(540, stats.CalendarBytesPerHour);
        Assert.Equal(90, stats.CoveragePercent);
        Assert.True(stats.HasHighCoverage);
    }

    [Fact]
    public void Compute_PartialCoverage_PreservesMonitoredRateButRejectsCalendarClaim()
    {
        var stats = MonitoringRateStats.Compute(300, 30, 100, 90);

        Assert.Equal(600, stats.MonitoredBytesPerHour);
        Assert.Equal(180, stats.CalendarBytesPerHour);
        Assert.Equal(30, stats.CoveragePercent);
        Assert.False(stats.HasHighCoverage);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(-5, 10, 0, 0, 0)]
    [InlineData(20, 10, 10, 100, 600)]
    public void Compute_ClampsInvalidMinuteCounts(
        int monitored,
        int requested,
        int expectedMonitored,
        double expectedCoverage,
        double expectedRate)
    {
        var stats = MonitoringRateStats.Compute(100, monitored, requested, 90);

        Assert.Equal(expectedMonitored, stats.MonitoredMinutes);
        Assert.Equal(Math.Max(0, requested), stats.RequestedMinutes);
        Assert.Equal(expectedCoverage, stats.CoveragePercent);
        Assert.Equal(expectedRate, stats.MonitoredBytesPerHour);
        Assert.False(stats.HasHighCoverage && requested <= 0);
    }

    [Theory]
    [InlineData(-20, true)]
    [InlineData(101, false)]
    public void Compute_ClampsCoverageThreshold(double threshold, bool expectedHighCoverage)
    {
        var stats = MonitoringRateStats.Compute(100, 1, 2, threshold);
        Assert.Equal(expectedHighCoverage, stats.HasHighCoverage);
    }

    [Fact]
    public void CoverageSummary_ExplainsNoDataPartialAndHighCoverage()
    {
        string none = MainWindow.FormatCoverageSummary(MonitoringRateStats.Compute(0, 0, 60, 90));
        string partial = MainWindow.FormatCoverageSummary(MonitoringRateStats.Compute(600_000_000, 30, 60, 90));
        string high = MainWindow.FormatCoverageSummary(MonitoringRateStats.Compute(600_000_000, 54, 60, 90));

        Assert.Contains("0%", none);
        Assert.Contains("No monitored throughput", none);
        Assert.Contains("50%", partial);
        Assert.Contains("calendar average is withheld", partial);
        Assert.Contains("90%", high);
        Assert.Contains("Calendar average:", high);
    }

    [Theory]
    [InlineData("1", true, 1)]
    [InlineData("100", true, 100)]
    [InlineData("92.5", true, 92.5)]
    [InlineData("0", false, 0)]
    [InlineData("101", false, 101)]
    [InlineData("NaN", false, 0)]
    [InlineData("bad", false, 0)]
    public void HighCoverageParser_RequiresFiniteOneToOneHundred(
        string text,
        bool expectedValid,
        double expectedValue)
    {
        Assert.Equal(expectedValid, MainWindow.TryParseHighCoveragePercent(text, out double value));
        if (expectedValid || double.TryParse(text, out _))
            Assert.Equal(expectedValue, double.IsNaN(value) ? 0 : value);
    }
}