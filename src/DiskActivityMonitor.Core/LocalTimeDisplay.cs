using System.Globalization;

namespace DiskActivityMonitor.Core;

public static class LocalTimeDisplay
{
    public static string ZoneId(TimeZoneInfo? timeZone = null)
        => (timeZone ?? TimeZoneInfo.Local).Id;

    public static string ZoneLabel(TimeZoneInfo? timeZone = null)
        => $"Times shown in local time ({ZoneId(timeZone)})";

    public static string FormatUtc(
        DateTime timestampUtc,
        string format,
        TimeZoneInfo? timeZone = null,
        CultureInfo? culture = null)
    {
        DateTime utc = timestampUtc.Kind switch
        {
            DateTimeKind.Utc => timestampUtc,
            DateTimeKind.Local => timestampUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc),
        };
        DateTime local = TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone ?? TimeZoneInfo.Local);
        return local.ToString(format, culture ?? CultureInfo.CurrentCulture);
    }

    public static string FormatUtcWithZone(
        DateTime timestampUtc,
        string format,
        TimeZoneInfo? timeZone = null,
        CultureInfo? culture = null)
        => $"{FormatUtc(timestampUtc, format, timeZone, culture)} ({ZoneId(timeZone)})";
}