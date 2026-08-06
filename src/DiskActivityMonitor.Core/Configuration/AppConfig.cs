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

    /// <summary>How many minutes of granular disk samples the live graph retains.</summary>
    public int LiveGraphRetentionMinutes { get; set; } = 15;

    /// <summary>How many days of minute-level history to keep before pruning.</summary>
    public int RetentionDays { get; set; } = 365;

    /// <summary>Only attribute writes to processes that exceed this many MB/min (noise filter).</summary>
    public double ProcessMinMbPerMinute { get; set; } = 0.5;

    /// <summary>
    /// Record which individual files each process writes, so opaque writers (notably the kernel
    /// <c>System</c> process) can be explained. Requires the ETW collector.
    /// </summary>
    public bool TrackFileTargets { get; set; } = true;

    /// <summary>How many of the busiest files per process are stored for each minute.</summary>
    public int FileTargetsPerProcessPerMinute { get; set; } = 15;

    /// <summary>Only list a file individually when it received at least this many KB in the minute.</summary>
    public double FileTargetMinKbPerMinute { get; set; } = 64;

    /// <summary>How many days of per-file history to keep. Far more numerous than the process rollup.</summary>
    public int FileTargetRetentionDays { get; set; } = 30;

    /// <summary>Maximum number of distinct files tracked in memory between samples.</summary>
    public int FileTargetTrackingLimit { get; set; } = 20000;

    /// <summary>Warn when the monitoring database grows past this many gigabytes.</summary>
    public double DatabaseSizeWarnGb { get; set; } = 1;

    /// <summary>Raise the database-size warning at most once per this many hours.</summary>
    public int DatabaseSizeAlertCooldownHours { get; set; } = 12;

    /// <summary>
    /// Extensions (without a leading dot) whose files are never opened for live tailing, because
    /// their contents are not text. Seeded from Yagu's binary, skip and archive extension defaults.
    /// </summary>
    public string BinaryExtensions { get; set; } = DefaultBinaryExtensions;

    /// <summary>How many trailing lines the live file tail shows when it first opens.</summary>
    public int TailInitialLines { get; set; } = 200;

    /// <summary>Maximum number of lines the live file tail keeps in memory.</summary>
    public int TailMaxLines { get; set; } = 5000;

    /// <summary>Maximum KiB decoded by one initial or incremental live-tail read.</summary>
    public int TailMaxReadKb { get; set; } = 512;

    /// <summary>Approximate maximum KiB of decoded UTF-16 text retained by the live-tail viewer.</summary>
    public int TailMaxBufferKb { get; set; } = 1024;

    /// <summary>
    /// Default non-text extensions, merged from Yagu's <c>DefaultBinaryExtensions</c>,
    /// <c>DefaultSkipExtensions</c> and <c>DefaultArchiveExtensions</c> lists so that executables,
    /// media, databases and archives are all excluded from live tailing.
    /// </summary>
    public const string DefaultBinaryExtensions =
        "exe;dll;pdb;obj;lib;so;dylib;com;scr;sys;drv;ocx;cpl;mui;winmd;pri;cat;res;resources;" +
        "o;a;lo;la;ilk;iobj;ipdb;exp;pyc;pyo;class;dex;wasm;" +
        "png;jpg;jpeg;gif;bmp;ico;tif;tiff;webp;svg;mp3;mp4;avi;mov;wmv;flv;mkv;wav;ogg;flac;" +
        "m4a;webm;heic;heif;avif;woff;woff2;ttf;eot;otf;pdf;doc;xls;ppt;" +
        "bin;dat;db;db3;sqlite;sqlite3;edb;mdb;accdb;ldb;sdf;cache;tmp;bak;etl;evtx;dmp;mdmp;" +
        "hdmp;hprof;vhd;vhdx;vmdk;pak;usm;bundle;assets;" +
        "zip;jar;war;ear;nupkg;vsix;apk;aab;aar;appx;msix;appxbundle;msixbundle;docx;xlsx;pptx;" +
        "odt;ods;odp;epub;whl;gz;tar;7z;rar;bz2;xz;iso;cab;msi;tgz;tbz2;txz;zst;zstd;br;lz4;lzma";

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
    /// Conservative lower TBW estimate applied to SSDs without an explicit per-disk rating.
    /// </summary>
    public double DefaultSsdTbw { get; set; } = 150;

    /// <summary>
    /// Optional upper bound of the default TBW range. When set (and greater than
    /// <see cref="DefaultSsdTbw"/>), endurance figures are shown as a range. Null = single value.
    /// </summary>
    public double? DefaultSsdTbwUpper { get; set; } = 600;

    /// <summary>
    /// Optional per-disk upper bound of the TBW range, keyed by disk id. When present (and greater
    /// than the lower rating), endurance percentages and projections are reported as a range.
    /// </summary>
    public Dictionary<string, double> DiskTbwRatingsUpper { get; set; } = new();

    /// <summary>Warn when the projected time to reach a drive's TBW drops below this many years.</summary>
    public double TbwProjectionWarnYears { get; set; } = 2;

    /// <summary>Raise a critical alert when projected time to TBW drops below this many years.</summary>
    public double TbwProjectionCriticalYears { get; set; } = 1;

    /// <summary>Minimum recent monitoring coverage required for calendar-rate and endurance projections.</summary>
    public double HighCoveragePercent { get; set; } = 90;

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
        if (DiskTbwRatings.TryGetValue(diskId, out var perDisk) && perDisk > 0) return null;
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
