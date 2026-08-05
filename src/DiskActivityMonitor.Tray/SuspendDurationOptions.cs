namespace DiskActivityMonitor.Tray;

/// <summary>
/// The suspension durations offered in the alert and auto-suspend toasts. When the interval
/// elapses the app resumes the process itself, so a suspension never outlives the user's intent.
/// Windows toast selection boxes are limited to five items.
/// </summary>
internal static class SuspendDurationOptions
{
    /// <summary>Choice id meaning "keep it suspended until the user resumes it explicitly".</summary>
    public const string ManualId = "manual";

    public const string DefaultId = "30m";

    public static readonly (string Id, string Label)[] Choices =
    {
        ("5m", "5 minutes"),
        ("15m", "15 minutes"),
        ("30m", "30 minutes"),
        ("1h", "1 hour"),
        (ManualId, "Until I resume it"),
    };

    /// <summary>Returns the chosen span, or null when the suspension should not expire on its own.</summary>
    public static TimeSpan? ToTimeSpan(string? id) => id switch
    {
        "5m" => TimeSpan.FromMinutes(5),
        "15m" => TimeSpan.FromMinutes(15),
        "30m" => TimeSpan.FromMinutes(30),
        "1h" => TimeSpan.FromHours(1),
        ManualId => null,
        _ => TimeSpan.FromMinutes(30),
    };

    /// <summary>Maps the configured default interval onto the nearest offered choice.</summary>
    public static string DefaultIdFor(int minutes) => minutes switch
    {
        <= 0 => ManualId,
        <= 5 => "5m",
        <= 15 => "15m",
        <= 30 => "30m",
        _ => "1h",
    };

    /// <summary>The resume instant for a configured interval, or null when it never expires.</summary>
    public static DateTime? ResumeAt(DateTime nowUtc, int minutes)
        => minutes <= 0 ? null : nowUtc.AddMinutes(minutes);
}
