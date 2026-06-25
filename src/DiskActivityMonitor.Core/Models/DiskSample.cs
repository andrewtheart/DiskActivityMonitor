namespace DiskActivityMonitor.Core.Models;

/// <summary>
/// Aggregated read/write byte totals for a single disk over one minute bucket.
/// This is the finest-grained record persisted by the collector.
/// </summary>
public sealed class DiskSample
{
    /// <summary>UTC start of the one-minute bucket (truncated to the minute).</summary>
    public required DateTime TimestampUtc { get; init; }

    public required string DiskId { get; init; }

    public long ReadBytes { get; set; }

    public long WriteBytes { get; set; }
}

/// <summary>
/// Per-process I/O byte totals over one minute bucket. Used to attribute disk pressure
/// to the noisiest processes. Note: Windows I/O counters include file, pipe and device
/// I/O, so this is a close proxy for - but not identical to - physical disk writes.
/// </summary>
public sealed class ProcessIoSample
{
    public required DateTime TimestampUtc { get; init; }

    public required string ProcessName { get; init; }

    public long ReadBytes { get; set; }

    public long WriteBytes { get; set; }
}

/// <summary>One aggregated point on a trend chart (hour / day / week bucket).</summary>
public sealed class TrendBucket
{
    public required DateTime BucketStartLocal { get; init; }
    public long ReadBytes { get; set; }
    public long WriteBytes { get; set; }
}

/// <summary>A process ranked by how much it wrote during a window.</summary>
public sealed class ProcessRank
{
    public required string ProcessName { get; init; }
    public long WriteBytes { get; set; }
    public long ReadBytes { get; set; }
}
