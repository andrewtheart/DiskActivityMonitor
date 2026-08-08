using System.Globalization;
using DiskActivityMonitor.Core;

namespace DiskActivityMonitor.Tests;

public sealed class LocalTimeDisplayTests
{
    private static readonly TimeZoneInfo TestZone = TimeZoneInfo.CreateCustomTimeZone(
        "Test/UTC+05:30",
        TimeSpan.FromMinutes(330),
        "Test UTC+05:30",
        "Test UTC+05:30");

    [Fact]
    public void FormatUtc_ConvertsToRequestedTimeZone()
    {
        var timestampUtc = new DateTime(2026, 1, 2, 4, 5, 6, DateTimeKind.Utc);

        string result = LocalTimeDisplay.FormatUtc(
            timestampUtc,
            "yyyy-MM-dd HH:mm:ss",
            TestZone,
            CultureInfo.InvariantCulture);

        Assert.Equal("2026-01-02 9:35:06 AM", result);
    }

    [Fact]
    public void FormatUtc_TreatsUnspecifiedRepositoryTimestampAsUtc()
    {
        var timestamp = new DateTime(2026, 1, 2, 4, 5, 6, DateTimeKind.Unspecified);

        string result = LocalTimeDisplay.FormatUtc(
            timestamp,
            "HH:mm",
            TestZone,
            CultureInfo.InvariantCulture);

        Assert.Equal("9:35 AM", result);
    }

    [Fact]
    public void ZoneLabel_IncludesCanonicalTimeZoneIdentifier()
        => Assert.Equal("Times shown in local time (Test/UTC+05:30)", LocalTimeDisplay.ZoneLabel(TestZone));

    [Fact]
    public void FormatUtcWithZone_AppendsCanonicalTimeZoneIdentifier()
    {
        var timestampUtc = new DateTime(2026, 1, 2, 4, 5, 6, DateTimeKind.Utc);

        string result = LocalTimeDisplay.FormatUtcWithZone(
            timestampUtc,
            "HH:mm",
            TestZone,
            CultureInfo.InvariantCulture);

        Assert.Equal("9:35 AM (Test/UTC+05:30)", result);
    }

    [Theory]
    [InlineData("t", "9:35 AM")]
    [InlineData("T", "9:35:06 AM")]
    [InlineData("g", "01/02/2026 9:35 AM")]
    [InlineData("G", "01/02/2026 9:35:06 AM")]
    [InlineData("f", "Friday, 02 January 2026 9:35 AM")]
    [InlineData("F", "Friday, 02 January 2026 9:35:06 AM")]
    public void FormatUtc_StandardFormatsAlwaysUseTwelveHourTime(string format, string expected)
    {
        var timestampUtc = new DateTime(2026, 1, 2, 4, 5, 6, DateTimeKind.Utc);

        Assert.Equal(
            expected,
            LocalTimeDisplay.FormatUtc(timestampUtc, format, TestZone, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void FormatUtc_UsesLiteralEnglishDesignatorsRegardlessOfCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.DateTimeFormat.AMDesignator = "morning";
        culture.DateTimeFormat.PMDesignator = "evening";
        var timestampUtc = new DateTime(2026, 1, 2, 16, 5, 6, DateTimeKind.Utc);

        string result = LocalTimeDisplay.FormatUtc(timestampUtc, "h:mm tt", TestZone, culture);

        Assert.Equal("9:35 PM", result);
    }
}