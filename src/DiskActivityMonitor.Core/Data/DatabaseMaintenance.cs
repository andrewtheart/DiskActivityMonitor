namespace DiskActivityMonitor.Core.Data;

/// <summary>On-disk footprint of the monitoring database.</summary>
/// <param name="MainBytes">Size of the database file itself.</param>
/// <param name="WalBytes">Size of the write-ahead log, which can be a large share of the total.</param>
/// <param name="ShmBytes">Size of the shared-memory index file.</param>
public readonly record struct DatabaseSize(long MainBytes, long WalBytes, long ShmBytes)
{
    /// <summary>Combined size of every file that makes up the database.</summary>
    public long TotalBytes => MainBytes + WalBytes + ShmBytes;
}

/// <summary>Outcome of a compaction request.</summary>
/// <param name="Success">True when the rebuild completed.</param>
/// <param name="BeforeBytes">Total size before compaction.</param>
/// <param name="AfterBytes">Total size afterwards.</param>
/// <param name="Error">Failure reason, else null.</param>
public sealed record CompactionResult(bool Success, long BeforeBytes, long AfterBytes, string? Error)
{
    /// <summary>Bytes returned to the file system; never negative.</summary>
    public long ReclaimedBytes => Math.Max(0, BeforeBytes - AfterBytes);
}

/// <summary>
/// Measures the monitoring database and rebuilds it on request.
/// </summary>
/// <remarks>
/// Size is reported across the main file plus its <c>-wal</c> and <c>-shm</c> companions, because a
/// busy collector can hold a substantial amount of data in the log that the user would otherwise
/// not see accounted for.
/// </remarks>
public static class DatabaseMaintenance
{
    private const long BytesPerGb = 1024L * 1024 * 1024;

    /// <summary>Measures the database and its companion files.</summary>
    public static DatabaseSize Measure(string? databasePath = null)
    {
        string path = databasePath ?? Paths.DatabasePath;
        return new DatabaseSize(FileLength(path), FileLength(path + "-wal"), FileLength(path + "-shm"));
    }

    /// <summary>Converts a gigabyte threshold into bytes, treating non-positive values as disabled.</summary>
    public static long ThresholdBytes(double warnGb)
        => warnGb <= 0 ? 0 : (long)Math.Round(warnGb * BytesPerGb);

    /// <summary>
    /// True when the database has grown past the configured threshold and the previous warning is
    /// older than the cooldown. A non-positive threshold disables the check entirely.
    /// </summary>
    public static bool ShouldWarn(long totalBytes, double warnGb, DateTime? lastWarnUtc, DateTime nowUtc, int cooldownHours)
    {
        long threshold = ThresholdBytes(warnGb);
        if (threshold <= 0 || totalBytes < threshold) return false;
        if (lastWarnUtc is not DateTime last) return true;

        return nowUtc - last >= TimeSpan.FromHours(Math.Max(0, cooldownHours));
    }

    /// <summary>Rebuilds the database and reports how much space was reclaimed.</summary>
    public static CompactionResult Compact(MonitorRepository repository, string? databasePath = null)
    {
        ArgumentNullException.ThrowIfNull(repository);

        string path = databasePath ?? Paths.DatabasePath;
        long before = Measure(path).TotalBytes;

        try
        {
            repository.Vacuum();
        }
        catch (Exception ex)
        {
            return new CompactionResult(false, before, before, ex.Message);
        }

        return new CompactionResult(true, before, Measure(path).TotalBytes, null);
    }

    private static long FileLength(string path)
        => FileLength(path, static candidate =>
        {
            var info = new FileInfo(candidate);
            return info.Exists ? info.Length : 0;
        });

    internal static long FileLength(string path, Func<string, long> readLength)
    {
        try
        {
            return readLength(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
