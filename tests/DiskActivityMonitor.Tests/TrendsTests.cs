using DiskActivityMonitor.Core;

namespace DiskActivityMonitor.Tests;

public class TrendsTests
{
    // ────────────────────────────────────────── Build

    [Fact]
    public void Build_HourBucket_CreatesCorrectCount()
    {
        var now = new DateTime(2025, 6, 1, 14, 30, 0, DateTimeKind.Local);
        var result = Trends.Build([], Trends.Bucket.Hour, 12, now);

        Assert.Equal(12, result.Count);
        // All buckets should be zero
        Assert.All(result, b => { Assert.Equal(0, b.ReadBytes); Assert.Equal(0, b.WriteBytes); });
    }

    [Fact]
    public void Build_DayBucket_CreatesCorrectCount()
    {
        var now = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Local);
        var result = Trends.Build([], Trends.Bucket.Day, 7, now);

        Assert.Equal(7, result.Count);
        Assert.Equal(now.Date, result[^1].BucketStartLocal);
        Assert.Equal(now.Date.AddDays(-6), result[0].BucketStartLocal);
    }

    [Fact]
    public void Build_AssignsDataToCorrectBucket()
    {
        var now = new DateTime(2025, 6, 1, 14, 0, 0, DateTimeKind.Local);
        var utcOffset = TimeZoneInfo.Local.GetUtcOffset(now);

        // Create hourly data that maps into local hour 13:xx
        var hourUtc = new DateTime(2025, 6, 1, 13, 0, 0, DateTimeKind.Utc) - utcOffset + TimeSpan.FromHours(utcOffset.TotalHours);
        // Just use a time we know maps to 13:00 local
        var dataHour = now.AddHours(-1).ToUniversalTime();

        var data = new[]
        {
            (dataHour, Read: 1000L, Write: 2000L),
        };

        var result = Trends.Build(data, Trends.Bucket.Hour, 3, now);
        // One of the buckets should have the data
        Assert.Contains(result, b => b.ReadBytes == 1000 && b.WriteBytes == 2000);
    }

    [Fact]
    public void Build_EmptyInput_AllBucketsZero()
    {
        var now = new DateTime(2025, 6, 1, 14, 0, 0, DateTimeKind.Local);
        var result = Trends.Build([], Trends.Bucket.Hour, 5, now);

        Assert.All(result, b =>
        {
            Assert.Equal(0, b.ReadBytes);
            Assert.Equal(0, b.WriteBytes);
        });
    }

    [Fact]
    public void Build_MultipleDataPointsSameBucket_Accumulates()
    {
        var now = new DateTime(2025, 6, 1, 14, 0, 0, DateTimeKind.Local);
        var hourStart = now.AddHours(-1);
        var utcStart = hourStart.ToUniversalTime();

        var data = new[]
        {
            (utcStart, Read: 100L, Write: 200L),
            (utcStart.AddMinutes(30), Read: 300L, Write: 400L), // same hour bucket
        };

        var result = Trends.Build(data, Trends.Bucket.Hour, 3, now);
        var bucket = result.FirstOrDefault(b => b.ReadBytes > 0);
        Assert.NotNull(bucket);
        Assert.Equal(400, bucket.ReadBytes);  // 100 + 300
        Assert.Equal(600, bucket.WriteBytes); // 200 + 400
    }

    // ────────────────────────────────────────── AlignDown

    [Theory]
    [InlineData(2025, 6, 1, 14, 37, 2025, 6, 1, 14, 0)]
    [InlineData(2025, 6, 1, 0, 0, 2025, 6, 1, 0, 0)]
    public void AlignDown_Hour_TruncatesToHour(int y, int m, int d, int h, int min, int ey, int em, int ed, int eh, int emin)
    {
        var input = new DateTime(y, m, d, h, min, 0, DateTimeKind.Local);
        var expected = new DateTime(ey, em, ed, eh, emin, 0, DateTimeKind.Local);
        Assert.Equal(expected, Trends.AlignDown(input, Trends.Bucket.Hour));
    }

    [Fact]
    public void AlignDown_Day_TruncatesToDate()
    {
        var input = new DateTime(2025, 6, 15, 14, 30, 0, DateTimeKind.Local);
        Assert.Equal(new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Local), Trends.AlignDown(input, Trends.Bucket.Day));
    }

    [Fact]
    public void AlignDown_Week_TruncatesToMonday()
    {
        // June 15, 2025 is a Sunday
        var sunday = new DateTime(2025, 6, 15, 14, 30, 0, DateTimeKind.Local);
        var monday = new DateTime(2025, 6, 9, 0, 0, 0, DateTimeKind.Local);
        Assert.Equal(monday, Trends.AlignDown(sunday, Trends.Bucket.Week));
    }

    [Fact]
    public void AlignDown_Week_MondayStaysMonday()
    {
        var monday = new DateTime(2025, 6, 9, 10, 0, 0, DateTimeKind.Local);
        var expected = new DateTime(2025, 6, 9, 0, 0, 0, DateTimeKind.Local);
        Assert.Equal(expected, Trends.AlignDown(monday, Trends.Bucket.Week));
    }

    // ────────────────────────────────────────── Step

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(5)]
    public void Step_Hour_AddsCorrectHours(int steps)
    {
        var start = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Local);
        Assert.Equal(start.AddHours(steps), Trends.Step(start, Trends.Bucket.Hour, steps));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-3)]
    public void Step_Day_AddsCorrectDays(int steps)
    {
        var start = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Local);
        Assert.Equal(start.AddDays(steps), Trends.Step(start, Trends.Bucket.Day, steps));
    }

    [Theory]
    [InlineData(1, 7)]
    [InlineData(-2, -14)]
    public void Step_Week_AddsCorrectWeeks(int steps, int expectedDays)
    {
        var start = new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Local); // a Monday
        Assert.Equal(start.AddDays(expectedDays), Trends.Step(start, Trends.Bucket.Week, steps));
    }

    // ────────────────────────────────────────── Label

    [Fact]
    public void Label_Hour_FormatsHHmm()
    {
        var dt = new DateTime(2025, 6, 1, 14, 0, 0, DateTimeKind.Local);
        Assert.Equal("14:00", Trends.Label(dt, Trends.Bucket.Hour));
    }

    [Fact]
    public void Label_Day_FormatsMMdd()
    {
        var dt = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Local);
        Assert.Equal("06/01", Trends.Label(dt, Trends.Bucket.Day));
    }

    [Fact]
    public void Label_Week_FormatsMMMdd()
    {
        var dt = new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Local);
        Assert.Equal("Jun 02", Trends.Label(dt, Trends.Bucket.Week));
    }
}
