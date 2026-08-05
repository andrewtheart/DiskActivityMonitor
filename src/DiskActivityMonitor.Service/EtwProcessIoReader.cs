using DiskActivityMonitor.Core.Collection;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;

namespace DiskActivityMonitor.Service;

/// <summary>
/// Attributes real per-process I/O by subscribing to the Windows kernel ETW <c>FileIO</c>
/// provider and summing the byte size of each file read/write to the process that issued it.
///
/// This is dramatically more accurate than <see cref="ProcessIoReader"/> for spotting which
/// application is actually writing to disk: kernel file-write events only fire for genuine
/// file-system writes, so named-pipe, device-ioctl, console and other non-disk I/O (which
/// inflate the Win32 GetProcessIoCounters transfer counts) are excluded. Pipe/mailslot
/// pseudo-file writes are filtered out by name as a second line of defence.
/// These are still logical file-I/O requests above the cache/storage stack, not physical device
/// writes. Windows may coalesce, defer or eliminate some requests before they reach a disk.
///
/// Requires the process to run elevated (the Windows Service runs as LocalSystem). When a
/// session cannot be started, <see cref="TryStart"/> returns <c>null</c> and the collector
/// falls back to the API-counter reader.
/// </summary>
public sealed class EtwProcessIoReader : IProcessIoReader
{
    private const string SessionName = "DiskActivityMonitorKernel";

    private readonly TraceEventSession _session;
    private readonly Thread _pumpThread;
    private readonly ServiceHostNameResolver _serviceNames = new();
    private readonly object _gate = new();
    private Dictionary<string, (long Read, long Write)> _accum = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _fileGate = new();
    private Dictionary<(string Process, string Path), (long Read, long Write)> _fileAccum = new(FileTargetKeyComparer.Instance);
    private Dictionary<string, string> _volumeMap = FileTargetNormalizer.BuildVolumeMap();
    private DateTime _volumeMapRefreshedUtc = DateTime.UtcNow;
    private volatile bool _trackFileTargets;
    private volatile int _fileTrackingLimit = 20000;

    public string Description => "ETW logical file-I/O requests (per process/service; excludes pipe/device I/O)";

    private EtwProcessIoReader(TraceEventSession session)
    {
        _session = session;
        _session.EnableKernelProvider(
            KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.FileIOInit);

        _session.Source.Kernel.FileIOWrite += d => Record(d, isWrite: true);
        _session.Source.Kernel.FileIORead += d => Record(d, isWrite: false);

        _pumpThread = new Thread(PumpEvents) { IsBackground = true, Name = "DAM-ETW" };
        _pumpThread.Start();
    }

    /// <summary>
    /// Attempts to start a real-time kernel file-I/O session. Returns the reader on success, or
    /// <c>null</c> if ETW kernel tracing is unavailable (typically because the process is not
    /// elevated). The supplied logger records the failure reason at debug level.
    /// </summary>
    public static EtwProcessIoReader? TryStart(ILogger? log = null)
    {
        TraceEventSession? session = null;
        try
        {
            // A previously crashed instance may have left the kernel session running; recreating
            // it with the same name restarts it cleanly.
            session = new TraceEventSession(SessionName) { StopOnDispose = true };
            return new EtwProcessIoReader(session);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Could not start ETW kernel I/O session; falling back to Win32 process I/O counters. Run the collector elevated for accurate per-process attribution.");
            try { session?.Dispose(); } catch { /* best effort */ }
            return null;
        }
    }

    private void PumpEvents()
    {
        try { _session.Source.Process(); }
        catch (Exception) { /* session disposed -> Process() returns/throws; nothing to do */ }
    }

    private void Record(FileIOReadWriteTraceData data, bool isWrite)
    {
        try
        {
            long size = data.IoSize;
            if (size <= 0) return;

            string name = data.ProcessName;
            if (string.IsNullOrEmpty(name)) return;

            string? fileName = data.FileName;
            if (!IsRealFileTarget(fileName)) return;

            name = _serviceNames.Resolve(name, data.ProcessID);

            lock (_gate)
            {
                _accum.TryGetValue(name, out var cur);
                _accum[name] = isWrite
                    ? (cur.Read, cur.Write + size)
                    : (cur.Read + size, cur.Write);
            }

            if (_trackFileTargets)
                RecordFileTarget(name, fileName!, size, isWrite);
        }
        catch
        {
            // A single malformed event must never tear down the pump thread.
        }
    }

    private void RecordFileTarget(string processName, string fileName, long size, bool isWrite)
    {
        lock (_fileGate)
        {
            // Volumes can be mounted at any time; refresh occasionally so their paths resolve.
            if (DateTime.UtcNow - _volumeMapRefreshedUtc > TimeSpan.FromMinutes(5))
            {
                _volumeMap = FileTargetNormalizer.BuildVolumeMap();
                _volumeMapRefreshedUtc = DateTime.UtcNow;
            }

            string path = FileTargetNormalizer.Normalize(fileName, _volumeMap);
            if (path.Length == 0) return;

            var key = (processName, path);
            if (!_fileAccum.TryGetValue(key, out var cur) && _fileAccum.Count >= _fileTrackingLimit)
                return; // Bounded: keep accumulating known files rather than growing without limit.

            _fileAccum[key] = isWrite
                ? (cur.Read, cur.Write + size)
                : (cur.Read + size, cur.Write);
        }
    }

    public void ConfigureFileTargets(bool enabled, int trackingLimit)
    {
        _fileTrackingLimit = Math.Max(1, trackingLimit);
        if (_trackFileTargets == enabled) return;

        _trackFileTargets = enabled;
        if (!enabled)
            lock (_fileGate) _fileAccum.Clear();
    }

    public IReadOnlyCollection<FileTargetDelta> SampleFileTargetDeltas()
    {
        lock (_fileGate)
        {
            if (_fileAccum.Count == 0) return [];
            var snapshot = _fileAccum;
            _fileAccum = new Dictionary<(string, string), (long Read, long Write)>(FileTargetKeyComparer.Instance);
            return snapshot
                .Select(kv => new FileTargetDelta(kv.Key.Process, kv.Key.Path, kv.Value.Read, kv.Value.Write))
                .ToList();
        }
    }

    /// <summary>
    /// Excludes pseudo-file targets that are not backed by a disk volume (named pipes, mailslots)
    /// and unnamed file objects. Everything else - drive-letter paths, volume device paths and
    /// UNC network paths - counts as real file I/O.
    /// </summary>
    private static bool IsRealFileTarget(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        if (fileName.Contains("\\NamedPipe\\", StringComparison.OrdinalIgnoreCase)) return false;
        if (fileName.Contains("\\MailSlot\\", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public Dictionary<string, (long Read, long Write)> SampleDeltas()
    {
        lock (_gate)
        {
            var snapshot = _accum;
            _accum = new Dictionary<string, (long Read, long Write)>(StringComparer.OrdinalIgnoreCase);
            return snapshot;
        }
    }

    public void Dispose()
    {
        try { _session.Dispose(); } catch { /* best effort */ }
        try { if (!_pumpThread.Join(TimeSpan.FromSeconds(2))) { /* daemon thread, let it die with the process */ } }
        catch { /* ignore */ }
        _serviceNames.Dispose();
    }
}
