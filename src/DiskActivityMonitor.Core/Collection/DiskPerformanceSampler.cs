using System.Diagnostics;

namespace DiskActivityMonitor.Core.Collection;

/// <summary>
/// Samples the Windows "PhysicalDisk\Disk Read/Write Bytes/sec" performance counters and
/// converts the per-second rates into byte totals over the elapsed sampling interval.
///
/// These per-disk counters are the authoritative source for SSD-wear monitoring because
/// they reflect bytes that actually reached the device.
/// </summary>
public sealed class DiskPerformanceSampler : IDisposable
{
    private sealed record DiskCounters(string DiskId, PerformanceCounter Read, PerformanceCounter Write);

    private List<DiskCounters> _counters = new();

    public IReadOnlyList<string> InstanceNames { get; private set; } = Array.Empty<string>();

    public DiskPerformanceSampler()
    {
        Initialize();
    }

    /// <summary>(Re)builds the counter set from the current PhysicalDisk instances.</summary>
    public void Initialize()
    {
        DisposeCounters();
        var fresh = new List<DiskCounters>();
        var category = new PerformanceCounterCategory("PhysicalDisk");
        var instances = category.GetInstanceNames();
        InstanceNames = instances;

        foreach (var instance in instances)
        {
            if (!DiskDetector.TryParseInstance(instance, out var diskId, out _))
                continue;
            try
            {
                var read = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", instance, readOnly: true);
                var write = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", instance, readOnly: true);
                // Prime: the first NextValue() of a rate counter always returns 0.
                read.NextValue();
                write.NextValue();
                fresh.Add(new DiskCounters(diskId, read, write));
            }
            catch
            {
                // A disk can disappear (removable media) between enumeration and binding.
            }
        }

        _counters = fresh;
    }

    /// <summary>
    /// Returns bytes read/written per disk since the previous sample, computed as
    /// (current rate in bytes/sec) * elapsedSeconds.
    /// </summary>
    public Dictionary<string, (long Read, long Write)> SampleBytes(double elapsedSeconds)
    {
        var result = new Dictionary<string, (long Read, long Write)>();
        if (elapsedSeconds <= 0) return result;

        foreach (var c in _counters)
        {
            try
            {
                long read = (long)(c.Read.NextValue() * elapsedSeconds);
                long write = (long)(c.Write.NextValue() * elapsedSeconds);
                if (read < 0) read = 0;
                if (write < 0) write = 0;

                if (result.TryGetValue(c.DiskId, out var cur))
                    result[c.DiskId] = (cur.Read + read, cur.Write + write);
                else
                    result[c.DiskId] = (read, write);
            }
            catch
            {
                // Skip a transiently unavailable counter; it recovers on the next Initialize().
            }
        }

        return result;
    }

    private void DisposeCounters()
    {
        foreach (var c in _counters)
        {
            c.Read.Dispose();
            c.Write.Dispose();
        }
        _counters.Clear();
    }

    public void Dispose() => DisposeCounters();
}
