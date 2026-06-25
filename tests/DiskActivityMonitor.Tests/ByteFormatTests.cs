using DiskActivityMonitor.Core;

namespace DiskActivityMonitor.Tests;

public class ByteFormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    public void Humanize_SmallValues_ReturnsBytes(double input, string expected)
    {
        Assert.Equal(expected, ByteFormat.Humanize(input));
    }

    [Theory]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10240, "10 KB")]
    [InlineData(1048575, "1024 KB")]
    public void Humanize_KilobytesRange(double input, string expected)
    {
        Assert.Equal(expected, ByteFormat.Humanize(input));
    }

    [Theory]
    [InlineData(1048576, "1 MB")]
    [InlineData(1572864, "1.5 MB")]
    [InlineData(104857600, "100 MB")]
    public void Humanize_MegabytesRange(double input, string expected)
    {
        Assert.Equal(expected, ByteFormat.Humanize(input));
    }

    [Theory]
    [InlineData(1073741824, "1 GB")]
    [InlineData(5368709120, "5 GB")]
    public void Humanize_GigabytesRange(double input, string expected)
    {
        Assert.Equal(expected, ByteFormat.Humanize(input));
    }

    [Theory]
    [InlineData(1099511627776, "1 TB")]
    [InlineData(2199023255552, "2 TB")]
    public void Humanize_TerabytesRange(double input, string expected)
    {
        Assert.Equal(expected, ByteFormat.Humanize(input));
    }

    [Fact]
    public void Humanize_NegativeValue_ShowsNegativeWithCorrectUnit()
    {
        // Negative values should use the absolute value for unit selection
        var result = ByteFormat.Humanize(-1073741824);
        Assert.Equal("-1 GB", result);
    }

    [Theory]
    [InlineData(1073741824, "s", "1 GB/s")]
    [InlineData(1048576, "h", "1 MB/h")]
    public void HumanizeRate_FormatsCorrectly(double bytes, string perUnit, string expected)
    {
        Assert.Equal(expected, ByteFormat.HumanizeRate(bytes, perUnit));
    }

    [Fact]
    public void Constants_AreCorrect()
    {
        Assert.Equal(1024, ByteFormat.KiB);
        Assert.Equal(1024 * 1024, ByteFormat.MiB);
        Assert.Equal(1024d * 1024 * 1024, ByteFormat.GiB);
        Assert.Equal(1024d * 1024 * 1024 * 1024, ByteFormat.TiB);
    }

    [Theory]
    [InlineData(1500, "1.46 KB")]   // 1500/1024 = 1.46...
    [InlineData(1600000, "1.53 MB")] // 1600000/1048576 = 1.525...
    public void Humanize_FractionalValues_RoundsToTwoDecimals(double input, string expected)
    {
        Assert.Equal(expected, ByteFormat.Humanize(input));
    }
}
