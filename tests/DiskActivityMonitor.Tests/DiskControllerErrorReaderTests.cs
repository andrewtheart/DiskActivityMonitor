using DiskActivityMonitor.Service;

namespace DiskActivityMonitor.Tests;

public class DiskControllerErrorReaderTests
{
    [Theory]
    [InlineData(@"\Device\Harddisk2\DR2", "2")]
    [InlineData(@"\device\HARDDISK10", "10")]
    public void TryParseDiskId_AcceptsExactDevicePaths(string path, string expected)
    {
        Assert.True(DiskControllerErrorReader.TryParseDiskId(path, out string id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"prefix\Device\Harddisk2\DR2")]
    [InlineData(@"\Device\Harddisk\DR2")]
    [InlineData(@"\Device\Harddisk2\bad")]
    public void TryParseDiskId_RejectsMalformedPaths(string? path)
    {
        Assert.False(DiskControllerErrorReader.TryParseDiskId(path, out string id));
        Assert.Empty(id);
    }

    [Fact]
    public void Aggregate_FiltersGroupsTracksTimesAndSortsNumerically()
    {
        var cutoff = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        var localAtCutoff = cutoff.ToLocalTime();
        var events = new DiskControllerEvent[]
        {
            new(null, @"\Device\Harddisk2\DR2"),
            new(cutoff.AddSeconds(-1), @"\Device\Harddisk2\DR2"),
            new(cutoff, "malformed"),
            new(localAtCutoff, @"\Device\Harddisk10\DR10"),
            new(cutoff.AddMinutes(3), @"\Device\Harddisk2\DR1"),
            new(cutoff.AddMinutes(1), @"\Device\Harddisk2\DR2"),
            new(cutoff.AddMinutes(3), @"\Device\Harddisk2\DR9"),
        };

        var result = DiskControllerErrorReader.Aggregate(events, cutoff.ToLocalTime());

        Assert.Equal(["2", "10"], result.Select(x => x.DiskId));
        var disk2 = result[0];
        Assert.Equal(3, disk2.Count);
        Assert.Equal(cutoff.AddMinutes(1), disk2.FirstUtc);
        Assert.Equal(cutoff.AddMinutes(3), disk2.LatestUtc);
        Assert.Equal(@"\Device\Harddisk2\DR9", disk2.DevicePath);
        Assert.Equal(1, result[1].Count);
    }

    [Fact]
    public void Aggregate_Empty_ReturnsEmpty()
        => Assert.Empty(DiskControllerErrorReader.Aggregate([], DateTime.UtcNow));

    [Fact]
    public void Reader_UsesInjectedSourceAndLiveSystemSource()
    {
        bool called = false;
        var now = DateTime.UtcNow;
        var injected = new DiskControllerErrorReader(() =>
        {
            called = true;
            return [new DiskControllerEvent(now, @"\Device\Harddisk3\DR3")];
        });
        Assert.Single(injected.ReadSince(now.AddMinutes(-1)));
        Assert.True(called);

        var live = new DiskControllerErrorReader().ReadSince(now.AddDays(-14));
        Assert.All(live, summary => Assert.True(summary.Count > 0));
    }

    [Fact]
    public void Project_HandlesNullAndNonNullEventFields()
    {
        Assert.Equal(new DiskControllerEvent(null, null), DiskControllerErrorReader.Project(null, null));
        var local = DateTime.Now;
        var projected = DiskControllerErrorReader.Project(local, 123);
        Assert.Equal(local.ToUniversalTime(), projected.TimestampUtc);
        Assert.Equal("123", projected.DevicePath);
    }
}
