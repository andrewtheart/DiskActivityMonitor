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

        Assert.Equal("2026-01-02 09:35:06", result);
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

        Assert.Equal("09:35", result);
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

        Assert.Equal("09:35 (Test/UTC+05:30)", result);
    }
}