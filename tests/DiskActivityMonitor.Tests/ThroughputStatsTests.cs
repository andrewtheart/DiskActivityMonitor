using DiskActivityMonitor.Core;
using Xunit;

namespace DiskActivityMonitor.Tests;

public class ThroughputStatsTests
{
    [Fact]
    public void Compute_SingleBusyMinute_RateIsBytesPerMinuteOverSixtyMillion()
    {
        // 120 MB written in one minute = 2 MB/s.
        var s = ThroughputStats.Compute(new long[] { 120_000_000 }, 1);
        Assert.Equal(2.0, s.AverageMbps, 6);
        Assert.Equal(2.0, s.MedianMbps, 6);
        Assert.Equal(2.0, s.PeakMbps, 6);
    }

    [Fact]
    public void Compute_TwoBusyMinutes_AveragesMediansAndPeaks()
    {
        // Minutes at 1 MB/s and 10 MB/s.
        var s = ThroughputStats.Compute(new long[] { 60_000_000, 600_000_000 }, 2);
        Assert.Equal(5.5, s.AverageMbps, 6);
        Assert.Equal(5.5, s.MedianMbps, 6);
        Assert.Equal(10.0, s.PeakMbps, 6);
    }

    [Fact]
    public void Compute_IdleMinutesCountAsZero()
    {
        // One busy minute (10 MB/s) in a 4-minute window (3 idle minutes).
        var s = ThroughputStats.Compute(new long[] { 600_000_000 }, 4);
        Assert.Equal(2.5, s.AverageMbps, 6);   // 600 MB spread across 4 minutes
        Assert.Equal(0.0, s.MedianMbps, 6);    // majority idle -> median is 0
        Assert.Equal(10.0, s.PeakMbps, 6);
    }

    [Fact]
    public void Compute_NoData_ReturnsZeros()
    {
        var s = ThroughputStats.Compute(Array.Empty<long>(), 10);
        Assert.Equal(0.0, s.AverageMbps, 6);
        Assert.Equal(0.0, s.MedianMbps, 6);
        Assert.Equal(0.0, s.PeakMbps, 6);
    }
}
