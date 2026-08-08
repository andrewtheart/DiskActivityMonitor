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
        var displayCulture = (CultureInfo)(culture ?? CultureInfo.CurrentCulture).Clone();
        displayCulture.DateTimeFormat.AMDesignator = "AM";
        displayCulture.DateTimeFormat.PMDesignator = "PM";
        return local.ToString(ToStandardTimeFormat(format, displayCulture), displayCulture);
    }

    private static string ToStandardTimeFormat(string format, CultureInfo culture)
    {
        string normalized = format switch
        {
            "t" => "h:mm tt",
            "T" => "h:mm:ss tt",
            "g" => $"{culture.DateTimeFormat.ShortDatePattern} h:mm tt",
            "G" => $"{culture.DateTimeFormat.ShortDatePattern} h:mm:ss tt",
            "f" => $"{culture.DateTimeFormat.LongDatePattern} h:mm tt",
            "F" => $"{culture.DateTimeFormat.LongDatePattern} h:mm:ss tt",
            _ => format.Replace("HH", "h", StringComparison.Ordinal)
                       .Replace("H", "h", StringComparison.Ordinal),
        };

        bool hasHour = format.Contains('H') || format.Contains('h')
            || format is "t" or "T" or "g" or "G" or "f" or "F";
        return hasHour && !normalized.Contains('t') ? normalized + " tt" : normalized;
    }

    public static string FormatUtcWithZone(
        DateTime timestampUtc,
        string format,
        TimeZoneInfo? timeZone = null,
        CultureInfo? culture = null)
        => $"{FormatUtc(timestampUtc, format, timeZone, culture)} ({ZoneId(timeZone)})";
}