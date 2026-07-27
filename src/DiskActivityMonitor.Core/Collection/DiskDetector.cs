using System.Management;
using DiskActivityMonitor.Core.Models;

namespace DiskActivityMonitor.Core.Collection;

/// <summary>
/// Discovers physical disks and classifies them (SSD vs HDD) using the Windows Storage
/// WMI provider (MSFT_PhysicalDisk). Correlates with PhysicalDisk performance-counter
/// instance names so trend data and media type line up.
/// </summary>
public static class DiskDetector
{
    private sealed record DiskMeta(string FriendlyName, DiskMediaType MediaType, long Size, string Serial, int? Wear, ushort BusType);

    /// <summary>
    /// Builds <see cref="DiskInfo"/> records from PhysicalDisk perf-counter instance names
    /// (e.g. "0 C:", "1 D: E:"), enriched with WMI media-type / model / serial data.
    /// The "_Total" instance is ignored by callers.
    /// </summary>
    public static List<DiskInfo> BuildDiskMap(IEnumerable<string> instanceNames)
    {
        var meta = QueryWmiMeta();
        var result = new List<DiskInfo>();

        foreach (var instance in instanceNames)
        {
            if (string.Equals(instance, "_Total", StringComparison.OrdinalIgnoreCase))
                continue;

            var (diskId, volumes) = ParseInstance(instance);
            if (diskId is null) continue;

            var info = new DiskInfo
            {
                DiskId = diskId,
                InstanceName = instance,
                Volumes = volumes,
            };

            meta.TryGetValue(diskId, out var m);
            if (m is not null)
            {
                info.FriendlyName = m.FriendlyName;
                info.MediaType = m.MediaType;
                info.SizeBytes = m.Size;
                info.SerialNumber = m.Serial;
                info.WearPercent = m.Wear;
            }

            // Read the drive's own lifetime write/read totals (NVMe Data Units / ATA 241-242).
            // This is the authoritative endurance figure; the NVMe wear % refines the WMI value.
            // The ATA pass-through needs a raw read/write disk handle that Windows Controlled Folder
            // Access blocks (with a user notification) on USB / virtual disks, so it is only allowed
            // for internal ATA/SATA drives. NVMe is read via a harmless zero-access query regardless.
            if (int.TryParse(diskId, out var driveNumber))
            {
                bool allowAta = m is not null && SupportsAtaSmart(m.BusType);
                var life = SmartLifetimeReader.Read(driveNumber, allowAta);
                if (life is { } l)
                {
                    info.LifetimeBytesWritten = l.BytesWritten > 0 ? l.BytesWritten : null;
                    info.LifetimeBytesRead = l.BytesRead > 0 ? l.BytesRead : null;
                    info.WearPercent = l.PercentUsed ?? info.WearPercent;
                }
            }

            result.Add(info);
        }

        return result;
    }

    private static (string? DiskId, string Volumes) ParseInstance(string instance)
    {
        return TryParseInstance(instance, out var id, out var vol) ? (id, vol) : (null, "");
    }

    /// <summary>
    /// Parses a PhysicalDisk perf-counter instance name ("0 C:", "1 D: E:") into the disk
    /// number and its volume letters. Returns false for "_Total" or unparsable instances.
    /// </summary>
    public static bool TryParseInstance(string instance, out string diskId, out string volumes)
    {
        diskId = "";
        volumes = "";
        if (string.IsNullOrWhiteSpace(instance) ||
            string.Equals(instance, "_Total", StringComparison.OrdinalIgnoreCase))
            return false;

        // Instance looks like "0 C:" or "1 D: E:". Leading token is the physical disk number.
        var parts = instance.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out _)) return false;
        diskId = parts[0];
        volumes = parts.Length > 1 ? string.Join(' ', parts[1..]) : "";
        return true;
    }

    private static Dictionary<string, DiskMeta> QueryWmiMeta()
    {
        var map = new Dictionary<string, DiskMeta>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            var query = new ObjectQuery(
                "SELECT DeviceId, FriendlyName, MediaType, Size, SerialNumber, SpindleSpeed, BusType FROM MSFT_PhysicalDisk");
            using var searcher = new ManagementObjectSearcher(scope, query);
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                var deviceId = mo["DeviceId"]?.ToString();
                if (string.IsNullOrEmpty(deviceId)) continue;

                ushort rawMedia = ToUInt16(mo["MediaType"]);
                uint spindle = ToUInt32(mo["SpindleSpeed"]);
                var media = ClassifyMedia(rawMedia, spindle);

                map[deviceId] = new DiskMeta(
                    FriendlyName: mo["FriendlyName"]?.ToString()?.Trim() ?? "",
                    MediaType: media,
                    Size: (long)ToUInt64(mo["Size"]),
                    Serial: mo["SerialNumber"]?.ToString()?.Trim() ?? "",
                    Wear: mo is ManagementObject disk ? TryReadWear(disk) : null,
                    BusType: ToUInt16(mo["BusType"]));
            }
        }
        catch
        {
            // Storage WMI provider unavailable (older OS or insufficient rights):
            // disks remain classified as Unknown, which the UI surfaces honestly.
        }
        return map;
    }

    private static DiskMediaType ClassifyMedia(ushort rawMedia, uint spindleSpeed) => rawMedia switch
    {
        3 => DiskMediaType.Hdd,
        4 => DiskMediaType.Ssd,
        5 => DiskMediaType.Scm,
        // MediaType unspecified: a zero spindle speed strongly implies solid state.
        _ => spindleSpeed == 0 ? DiskMediaType.Ssd : DiskMediaType.Unknown,
    };

    /// <summary>
    /// True only for buses where the ATA SMART pass-through is meaningful: ATAPI (2), ATA (3) and
    /// SATA (11). The pass-through opens a raw read/write disk handle, which Windows Controlled
    /// Folder Access blocks — and notifies the user about — on USB, SD and virtual disks, so it is
    /// never attempted there (those buses do not expose the ATA 241/242 lifetime attributes anyway).
    /// </summary>
    private static bool SupportsAtaSmart(ushort busType) => busType is 2 or 3 or 11;

    /// <summary>
    /// Reads the drive's SMART-derived wear indicator ("Percentage Used") via the associated
    /// MSFT_StorageReliabilityCounter. Returns 0-100, or null when the drive does not report it
    /// or the process lacks rights (reliability counters typically require elevation).
    /// </summary>
    private static int? TryReadWear(ManagementObject disk)
    {
        try
        {
            using var related = disk.GetRelated("MSFT_StorageReliabilityCounter");
            foreach (ManagementBaseObject rc in related)
            {
                using (rc)
                {
                    var w = rc["Wear"];
                    if (w is null) continue;
                    int val = Convert.ToInt32(w);
                    if (val is >= 0 and <= 100) return val;
                }
            }
        }
        catch
        {
            // Unsupported drive or access denied - wear stays unknown.
        }
        return null;
    }

    private static ushort ToUInt16(object? v) => v is null ? (ushort)0 : Convert.ToUInt16(v);
    private static uint ToUInt32(object? v) => v is null ? 0u : Convert.ToUInt32(v);
    private static ulong ToUInt64(object? v) => v is null ? 0ul : Convert.ToUInt64(v);
}
