using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskActivityMonitor.Core.Configuration;

/// <summary>
/// User-tunable settings shared by the collector and tray app. Persisted as JSON at
/// <see cref="Paths.ConfigPath"/>. All byte thresholds are expressed in gigabytes in the
/// file for readability and converted to bytes by the alert engine.
/// </summary>
public sealed class AppConfig
{
    /// <summary>How often the collector reads the performance counters, in seconds.</summary>
    public int SampleIntervalSeconds { get; set; } = 5;

    /// <summary>How often the tray dashboard re-reads the database and redraws tables, graphs and stats, in seconds.</summary>
    public int DashboardRefreshSeconds { get; set; } = 15;

    /// <summary>How many days of minute-level history to keep before pruning.</summary>
    public int RetentionDays { get; set; } = 365;

    /// <summary>Only attribute writes to processes that exceed this many MB/min (noise filter).</summary>
    public double ProcessMinMbPerMinute { get; set; } = 0.5;

    /// <summary>Alert when an SSD exceeds this many GB written in a rolling 1-hour window.</summary>
    public double SsdWarnGbPerHour { get; set; } = 10;

    /// <summary>Alert when an SSD exceeds this many GB written in a rolling 24-hour window.</summary>
    public double SsdWarnGbPerDay { get; set; } = 100;

    /// <summary>Critical alert when an SSD exceeds this many GB written in a rolling 24-hour window.</summary>
    public double SsdCriticalGbPerDay { get; set; } = 250;

    /// <summary>Alert when a single process writes more than this many GB in a rolling 1-hour window.</summary>
    public double ProcessWarnGbPerHour { get; set; } = 5;

    /// <summary>Alert when all processes combined write more than this many GB in a rolling 1-hour window.</summary>
    public double AllProcessesWarnGbPerHour { get; set; } = 20;

    /// <summary>Minimum minutes between repeat alerts for the same rule + scope.</summary>
    public int AlertCooldownMinutes { get; set; } = 5;

    /// <summary>Monitor the Windows System log for Disk event 11 controller errors.</summary>
    public bool EnableControllerErrorAlerts { get; set; } = true;

    /// <summary>Trailing window, in days, used to count Disk event 11 controller errors.</summary>
    public int ControllerErrorWindowDays { get; set; } = 14;

    /// <summary>Raise a warning after this many controller errors occur inside the trailing window.</summary>
    public int ControllerErrorWarnCount { get; set; } = 3;

    /// <summary>Raise a critical alert after this many controller errors occur inside the trailing window.</summary>
    public int ControllerErrorCriticalCount { get; set; } = 10;

    /// <summary>
    /// Per-disk manufacturer TBW (terabytes-written) endurance rating override, keyed by disk
    /// id. When a disk has no entry, <see cref="DefaultSsdTbw"/> is used instead.
    /// </summary>
    public Dictionary<string, double> DiskTbwRatings { get; set; } = new();

    /// <summary>
    /// Default TBW endurance rating (terabytes written) applied to SSDs without an explicit
    /// per-disk override. Typical consumer NVMe drives are rated around 600-1200 TBW.
    /// </summary>
    public double DefaultSsdTbw { get; set; } = 750;

    /// <summary>
    /// Optional upper bound of the default TBW range. When set (and greater than
    /// <see cref="DefaultSsdTbw"/>), endurance figures are shown as a range. Null = single value.
    /// </summary>
    public double? DefaultSsdTbwUpper { get; set; }

    /// <summary>
    /// Optional per-disk upper bound of the TBW range, keyed by disk id. When present (and greater
    /// than the lower rating), endurance percentages and projections are reported as a range.
    /// </summary>
    public Dictionary<string, double> DiskTbwRatingsUpper { get; set; } = new();

    /// <summary>Warn when the projected time to reach a drive's TBW drops below this many years.</summary>
    public double TbwProjectionWarnYears { get; set; } = 2;

    /// <summary>Raise a critical alert when projected time to TBW drops below this many years.</summary>
    public double TbwProjectionCriticalYears { get; set; } = 1;

    /// <summary>Warn when a drive's SMART-reported endurance used (percentage) reaches this level.</summary>
    public double SsdWearWarnPercent { get; set; } = 90;

    /// <summary>Returns the effective TBW rating for a disk: its per-disk override, else the default.</summary>
    public double EffectiveTbw(string diskId)
        => DiskTbwRatings.TryGetValue(diskId, out var t) && t > 0 ? t : DefaultSsdTbw;

    /// <summary>
    /// Returns the optional upper bound of a disk's TBW range (per-disk override, else the global
    /// default upper), or null when no range is configured or the upper does not exceed the lower.
    /// </summary>
    public double? EffectiveTbwUpper(string diskId)
    {
        double lower = EffectiveTbw(diskId);
        if (DiskTbwRatingsUpper.TryGetValue(diskId, out var u) && u > lower) return u;
        if (DefaultSsdTbwUpper is double d && d > lower) return d;
        return null;
    }

    [JsonIgnore]
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
