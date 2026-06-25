namespace DiskActivityMonitor.Core.Models;

/// <summary>Physical media classification reported by the storage subsystem.</summary>
public enum DiskMediaType
{
    Unknown = 0,
    Hdd = 3,
    Ssd = 4,
    Scm = 5, // Storage-class memory (e.g. Intel Optane)
}

/// <summary>
/// Metadata about a physical disk, correlating the PhysicalDisk performance-counter
/// instance (e.g. "0 C:") with storage information from WMI (media type, size, serial).
/// </summary>
public sealed class DiskInfo
{
    /// <summary>Physical disk number, e.g. "0". Matches the leading token of the perf-counter instance.</summary>
    public required string DiskId { get; init; }

    /// <summary>The raw PhysicalDisk perf-counter instance name, e.g. "0 C:".</summary>
    public required string InstanceName { get; init; }

    /// <summary>Friendly model name from WMI, e.g. "Samsung SSD 990 PRO 2TB".</summary>
    public string FriendlyName { get; set; } = "";

    /// <summary>Mounted volume letters served by this disk, e.g. "C: D:".</summary>
    public string Volumes { get; set; } = "";

    public DiskMediaType MediaType { get; set; } = DiskMediaType.Unknown;

    public long SizeBytes { get; set; }

    public string SerialNumber { get; set; } = "";

    /// <summary>SMART-reported percentage of rated write endurance consumed (0-100), or null if the drive does not report it / access was denied.</summary>
    public int? WearPercent { get; set; }

    /// <summary>
    /// Total host bytes written over the drive's entire lifetime, read from the device itself
    /// (NVMe "Data Units Written" or ATA SMART attribute 241). Null when the drive does not
    /// expose it. Unlike the perf-counter totals, this survives reboots and OS reinstalls.
    /// </summary>
    public long? LifetimeBytesWritten { get; set; }

    /// <summary>Total host bytes read over the drive's lifetime (NVMe "Data Units Read" / ATA attribute 242), or null.</summary>
    public long? LifetimeBytesRead { get; set; }

    public bool IsSsd => MediaType is DiskMediaType.Ssd or DiskMediaType.Scm;

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Volumes)
            ? (string.IsNullOrWhiteSpace(FriendlyName) ? $"Disk {DiskId}" : FriendlyName)
            : $"{Volumes.Trim()}  ({(string.IsNullOrWhiteSpace(FriendlyName) ? $"Disk {DiskId}" : FriendlyName)})";
}
