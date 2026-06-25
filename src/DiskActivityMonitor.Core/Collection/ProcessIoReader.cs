using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiskActivityMonitor.Core.Collection;

/// <summary>
/// Reads cumulative per-process I/O byte counters via the Win32 GetProcessIoCounters API
/// and converts them into per-interval deltas, aggregated by process name. This attributes
/// disk pressure to the noisiest applications.
///
/// Note: Windows I/O transfer counts include file, pipe and device I/O combined, so the
/// numbers are an upper-bound proxy for physical disk writes, useful for spotting culprits.
/// This is the fallback reader used when an ETW kernel session cannot be started (e.g. the
/// collector is not running elevated); the ETW reader gives far more accurate attribution.
/// </summary>
public sealed class ProcessIoReader : IProcessIoReader
{
    public string Description => "Win32 process I/O counters (upper bound: file + pipe + device I/O)";

    /// <summary>No unmanaged session to release; present to satisfy <see cref="IProcessIoReader"/>.</summary>
    public void Dispose() { }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS counters);

    private readonly Dictionary<int, (string Name, ulong LastRead, ulong LastWrite)> _last = new();

    /// <summary>
    /// Samples all accessible processes and returns the bytes read/written by each process
    /// name since the previous call. The first call primes the baseline and returns empty.
    /// </summary>
    public Dictionary<string, (long Read, long Write)> SampleDeltas()
    {
        var result = new Dictionary<string, (long Read, long Write)>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<int>();
        bool primed = _last.Count > 0;

        foreach (var proc in Process.GetProcesses())
        {
            int pid = proc.Id;
            string name;
            try { name = proc.ProcessName; }
            catch { proc.Dispose(); continue; }

            seen.Add(pid);

            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero)
            {
                proc.Dispose();
                continue;
            }

            try
            {
                if (!GetProcessIoCounters(handle, out var counters))
                    continue;

                ulong read = counters.ReadTransferCount;
                ulong write = counters.WriteTransferCount;

                if (_last.TryGetValue(pid, out var prev) && string.Equals(prev.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    long dRead = read >= prev.LastRead ? (long)(read - prev.LastRead) : (long)read;
                    long dWrite = write >= prev.LastWrite ? (long)(write - prev.LastWrite) : (long)write;
                    if (primed && (dRead != 0 || dWrite != 0))
                        Add(result, name, dRead, dWrite);
                }
                // else: new process this interval - record baseline, attribute nothing yet.

                _last[pid] = (name, read, write);
            }
            finally
            {
                CloseHandle(handle);
                proc.Dispose();
            }
        }

        // Drop processes that have exited so the map does not grow unbounded.
        if (_last.Count > seen.Count)
        {
            foreach (var pid in _last.Keys.Where(k => !seen.Contains(k)).ToList())
                _last.Remove(pid);
        }

        return result;
    }

    private static void Add(Dictionary<string, (long Read, long Write)> map, string name, long read, long write)
    {
        if (map.TryGetValue(name, out var cur))
            map[name] = (cur.Read + read, cur.Write + write);
        else
            map[name] = (read, write);
    }
}
