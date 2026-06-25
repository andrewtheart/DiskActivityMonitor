namespace DiskActivityMonitor.Tray;

/// <summary>
/// The snooze durations offered in the alert toast. "24 hours" and "1 day" are the same span,
/// so they are represented once as "1 day".
/// </summary>
internal static class SnoozeOptions
{
    public const string DefaultId = "1h";

    /// <summary>
    /// (choiceId, label) pairs for the toast selection box. Windows toast combo boxes are
    /// limited to 5 items.
    /// </summary>
    public static readonly (string Id, string Label)[] Choices =
    {
        ("5m",  "5 minutes"),
        ("30m", "30 minutes"),
        ("1h",  "1 hour"),
        ("1d",  "1 day"),
        ("1w",  "1 week"),
    };

    public static TimeSpan ToTimeSpan(string id) => id switch
    {
        "5m" => TimeSpan.FromMinutes(5),
        "30m" => TimeSpan.FromMinutes(30),
        "1h" => TimeSpan.FromHours(1),
        "1d" => TimeSpan.FromDays(1),
        "1w" => TimeSpan.FromDays(7),
        "1mo" => TimeSpan.FromDays(30),
        _ => TimeSpan.FromHours(1),
    };
}
