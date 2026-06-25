namespace DiskActivityMonitor.Core.Collection;

/// <summary>
/// Supplies per-process byte deltas (read/write) aggregated by process name since the
/// previous call. Implementations differ in accuracy: the API-counter reader returns an
/// upper bound that mixes file/pipe/device I/O, while the ETW reader attributes real file
/// writes to the originating process.
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
}
