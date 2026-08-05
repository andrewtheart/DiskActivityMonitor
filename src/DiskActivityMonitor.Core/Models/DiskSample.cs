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
/// Per-process logical I/O byte totals over one minute bucket. Used to identify software that
/// generates file-write pressure. These are application-requested bytes above the cache/storage
/// stack, not physical-disk bytes; the fallback reader additionally includes pipe/device I/O.
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

/// <summary>
/// Logical I/O one process performed against one file during a minute bucket. This is what turns
/// an opaque writer such as the kernel <c>System</c> process into an explanation.
/// </summary>
public sealed class ProcessFileIoSample
{
    public required DateTime TimestampUtc { get; init; }
    public required string ProcessName { get; init; }
    public required string Path { get; init; }
    public required FileTargetKind Kind { get; init; }
    public long ReadBytes { get; set; }
    public long WriteBytes { get; set; }
}

/// <summary>A file ranked by how much one process wrote to it during a window.</summary>
public sealed class FileTargetRank
{
    public required string Path { get; init; }
    public required FileTargetKind Kind { get; init; }
    public long WriteBytes { get; set; }
    public long ReadBytes { get; set; }
}
