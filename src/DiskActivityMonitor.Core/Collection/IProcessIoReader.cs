namespace DiskActivityMonitor.Core.Collection;

/// <summary>Bytes one process moved against one file since the previous sample.</summary>
public readonly record struct FileTargetDelta(string ProcessName, string Path, long Read, long Write);

/// <summary>
/// Compares (process, file) accumulator keys. Windows process names and paths are
/// case-insensitive, so one file must never be ranked as two competing targets.
/// </summary>
public sealed class FileTargetKeyComparer : IEqualityComparer<(string Process, string Path)>
{
    public static readonly FileTargetKeyComparer Instance = new();

    public bool Equals((string Process, string Path) x, (string Process, string Path) y)
        => string.Equals(x.Process, y.Process, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((string Process, string Path) obj)
        => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Process),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path));
}

/// <summary>
/// Supplies per-process byte deltas (read/write) aggregated by process name since the
/// previous call. Implementations differ in accuracy: the API-counter reader returns an
/// upper bound that mixes file/pipe/device I/O, while the ETW reader attributes real file
/// write requests to the originating process. Neither figure is a physical-disk byte count:
/// Windows may coalesce, cache or eliminate logical requests before they reach a device.
/// </summary>
public interface IProcessIoReader : IDisposable
{
    /// <summary>A short human-readable description of how this reader measures I/O, for logging.</summary>
    string Description { get; }

    /// <summary>
    /// Returns the bytes read/written by each process name since the previous call and resets
    /// the running totals. The first call may return empty while a baseline is established.
    /// </summary>
    Dictionary<string, (long Read, long Write)> SampleDeltas();

    /// <summary>
    /// Turns per-file attribution on or off and bounds how many distinct files are tracked at
    /// once. Readers that cannot see file names ignore this.
    /// </summary>
    void ConfigureFileTargets(bool enabled, int trackingLimit) { }

    /// <summary>
    /// Returns per-file byte deltas since the previous call, or an empty collection when the
    /// reader cannot attribute I/O to individual files (or the feature is disabled).
    /// </summary>
    IReadOnlyCollection<FileTargetDelta> SampleFileTargetDeltas() => [];
}
