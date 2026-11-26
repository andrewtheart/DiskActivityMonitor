namespace DiskActivityMonitor.Core;

/// <summary>Human-readable formatting helpers shared by the service and tray app.</summary>
public static class ByteFormat
{
    // Decimal SI units, matching drive capacities and manufacturer TBW ratings.
    public const double KB = 1_000d;
    public const double MB = 1_000_000d;
    public const double GB = 1_000_000_000d;
    public const double TB = 1_000_000_000_000d;

    // Binary IEC units, used only for memory-sized settings that are labelled KiB.
    public const double KiB = 1024d;
    public const double MiB = 1024d * 1024d;
    public const double GiB = 1024d * 1024d * 1024d;
    public const double TiB = 1024d * 1024d * 1024d * 1024d;

    /// <summary>Formats a byte count using decimal SI units (1 KB = 1,000 bytes; 1 TB = 10^12 bytes).</summary>
    public static string Humanize(double bytes)
    {
        double abs = Math.Abs(bytes);
        return abs switch
        {
            >= TB => $"{bytes / TB:0.##} TB",
            >= GB => $"{bytes / GB:0.##} GB",
            >= MB => $"{bytes / MB:0.##} MB",
            >= KB => $"{bytes / KB:0.##} KB",
            _ => $"{bytes:0} B",
        };
    }

    /// <summary>Formats a per-unit-time rate, e.g. "12.3 MB/h".</summary>
    public static string HumanizeRate(double bytes, string perUnit) => $"{Humanize(bytes)}/{perUnit}";
}
