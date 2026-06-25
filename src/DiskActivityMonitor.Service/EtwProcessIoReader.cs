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
    private readonly object _gate = new();
    private Dictionary<string, (long Read, long Write)> _accum = new(StringComparer.OrdinalIgnoreCase);

    public string Description => "ETW kernel file-write events (real per-process file I/O, excludes pipe/device I/O)";

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

            if (!IsRealFileTarget(data.FileName)) return;

            lock (_gate)
            {
                _accum.TryGetValue(name, out var cur);
                _accum[name] = isWrite
                    ? (cur.Read, cur.Write + size)
                    : (cur.Read + size, cur.Write);
            }
        }
        catch
        {
            // A single malformed event must never tear down the pump thread.
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
    }
}
