using DiskActivityMonitor.Core;

namespace DiskActivityMonitor.Tests;

public class ByteFormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(512, "512 B")]
    [InlineData(999, "999 B")]
    public void Humanize_SmallValues_ReturnsBytes(double input, string expected)
    {
        Assert.Equal(expected, ByteFormat.Humanize(input));
    }

    [Theory]
    [InlineData(1000, "1 KB")]
    [InlineData(1024, "1.02 KB")]
    [InlineData(1500, "1.5 KB")]
    [InlineData(10000, "10 KB")]
    [InlineData(999_994, "999.99 KB")]
    public void Humanize_KilobytesRange(double input, string expected)
    {
        Assert.Equal(expected, ByteFormat.Humanize(input));
    }

    [Theory]
    [InlineData(1_000_000, "1 MB")]
    [InlineData(1_500_000, "1.5 MB")]
    [InlineData(1_048_576, "1.05 MB")]
    [InlineData(100_000_000, "100 MB")]
    public void Humanize_MegabytesRange(double input, string expected)
    {
        Assert.Equal(expected, ByteFormat.Humanize(input));
    }

    [Theory]
    [InlineData(1_000_000_000, "1 GB")]
    [InlineData(5_000_000_000, "5 GB")]
    [InlineData(1_073_741_824, "1.07 GB")]
    public void Humanize_GigabytesRange(double input, string expected)
    {
        Assert.Equal(expected, ByteFormat.Humanize(input));
    }

    [Theory]
    [InlineData(1_000_000_000_000, "1 TB")]
    [InlineData(2_000_000_000_000, "2 TB")]
    [InlineData(1_099_511_627_776, "1.1 TB")]
    public void Humanize_TerabytesRange(double input, string expected)
    {
        Assert.Equal(expected, ByteFormat.Humanize(input));
    }

    [Fact]
    public void Humanize_LifetimeWrites_MatchDecimalTbwRatings()
    {
        // Issue #3: 252.21 TB written was shown as "229.39 TB" (binary TiB) beside decimal TBW ratings.
        const double lifetimeWritten = 252_210_000_000_000;

        Assert.Equal("252.21 TB", ByteFormat.Humanize(lifetimeWritten));
        Assert.Equal(168.14, Math.Round(lifetimeWritten / (150 * ByteFormat.TB) * 100, 2));
    }

    [Fact]
    public void Humanize_NegativeValue_ShowsNegativeWithCorrectUnit()
    {
        // Negative values should use the absolute value for unit selection
        var result = ByteFormat.Humanize(-1_000_000_000);
        Assert.Equal("-1 GB", result);
    }

    [Theory]
    [InlineData(1_000_000_000, "s", "1 GB/s")]
    [InlineData(1_000_000, "h", "1 MB/h")]
    public void HumanizeRate_FormatsCorrectly(double bytes, string perUnit, string expected)
    {
        Assert.Equal(expected, ByteFormat.HumanizeRate(bytes, perUnit));
    }

    [Fact]
    public void Constants_AreCorrect()
    {
        Assert.Equal(1_000, ByteFormat.KB);
        Assert.Equal(1_000_000, ByteFormat.MB);
        Assert.Equal(1_000_000_000d, ByteFormat.GB);
        Assert.Equal(1_000_000_000_000d, ByteFormat.TB);
        Assert.Equal(1024, ByteFormat.KiB);
        Assert.Equal(1024 * 1024, ByteFormat.MiB);
        Assert.Equal(1024d * 1024 * 1024, ByteFormat.GiB);
        Assert.Equal(1024d * 1024 * 1024 * 1024, ByteFormat.TiB);
    }

    [Theory]
    [InlineData(1_456, "1.46 KB")]
    [InlineData(1_526_000, "1.53 MB")]
    public void Humanize_FractionalValues_RoundsToTwoDecimals(double input, string expected)
    {
        Assert.Equal(expected, ByteFormat.Humanize(input));
    }
}
