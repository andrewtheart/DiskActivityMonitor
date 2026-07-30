namespace DiskActivityMonitor.Core.Models;

/// <summary>
/// Aggregated Windows System log Disk event 11 records for one physical-disk number.
/// </summary>
public sealed class DiskControllerErrorSummary
{
    /// <summary>Physical disk number parsed from a path such as \Device\Harddisk2\DR2.</summary>
    public required string DiskId { get; init; }

    /// <summary>Device path reported by Windows for the most recent matching event.</summary>
    public required string DevicePath { get; init; }

    public int Count { get; init; }

    public DateTime FirstUtc { get; init; }

    public DateTime LatestUtc { get; init; }
}