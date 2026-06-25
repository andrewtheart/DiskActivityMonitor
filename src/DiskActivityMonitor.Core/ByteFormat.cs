namespace DiskActivityMonitor.Core;

/// <summary>Human-readable formatting helpers shared by the service and tray app.</summary>
public static class ByteFormat
{
    public const double KiB = 1024d;
    public const double MiB = 1024d * 1024d;
    public const double GiB = 1024d * 1024d * 1024d;
    public const double TiB = 1024d * 1024d * 1024d * 1024d;

    /// <summary>Formats a byte count using binary units (KB/MB/GB/TB = 1024-based).</summary>
    public static string Humanize(double bytes)
    {
        double abs = Math.Abs(bytes);
        return abs switch
        {
            >= TiB => $"{bytes / TiB:0.##} TB",
            >= GiB => $"{bytes / GiB:0.##} GB",
            >= MiB => $"{bytes / MiB:0.##} MB",
            >= KiB => $"{bytes / KiB:0.##} KB",
            _ => $"{bytes:0} B",
        };
    }

    /// <summary>Formats a per-unit-time rate, e.g. "12.3 MB/h".</summary>
    public static string HumanizeRate(double bytes, string perUnit) => $"{Humanize(bytes)}/{perUnit}";
}
