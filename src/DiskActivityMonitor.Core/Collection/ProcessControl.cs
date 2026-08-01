using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DiskActivityMonitor.Core.Collection;

/// <summary>
/// Suspends and resumes processes by image identity using the native NtSuspendProcess /
/// NtResumeProcess APIs. Suspending freezes every thread in the process, halting its I/O until
/// it is resumed. The caller must have sufficient rights over the target process (a same-user
/// process is fine; an elevated/other-user process requires the caller to be elevated).
/// </summary>
public static class ProcessControl
{
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr processHandle);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr processHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private const int PROCESS_SUSPEND_RESUME = 0x0800;
    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ERROR_ACCESS_DENIED = 5;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr processHandle,
        int flags,
        StringBuilder executablePath,
        ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        IntPtr processHandle,
        out System.Runtime.InteropServices.ComTypes.FILETIME creationTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME exitTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

    /// <summary>Stable identity captured from the same handle used to suspend a process.</summary>
    public sealed record ProcessIdentity(int ProcessId, long CreationTimeFileTimeUtc, string ExecutablePath);

    /// <summary>Outcome of a suspend/resume call across every instance of a process name.</summary>
    /// <param name="Matched">How many running processes carried the image name.</param>
    /// <param name="Affected">How many were successfully suspended/resumed.</param>
    /// <param name="AccessDenied">True when instances were found but none could be opened/changed.</param>
    public readonly record struct Result(
        int Matched,
        int Affected,
        bool AccessDenied,
        IReadOnlyList<ProcessIdentity>? AffectedProcesses = null,
        IReadOnlyList<ProcessIdentity>? UnresolvedProcesses = null,
        bool IdentityUnavailable = false)
    {
        public IReadOnlyList<ProcessIdentity> Processes => AffectedProcesses ?? [];
        public IReadOnlyList<ProcessIdentity> Unresolved => UnresolvedProcesses ?? [];
    }

    /// <summary>Suspends matching processes, optionally constrained to one executable path.</summary>
    public static Result Suspend(string processName, string? executablePath = null)
        => ForEach(processName, executablePath, suspend: true);

    /// <summary>Resumes matching processes, optionally constrained to one executable path.</summary>
    public static Result Resume(string processName, string? executablePath = null)
        => ForEach(processName, executablePath, suspend: false);

    /// <summary>Resumes only processes whose PID, creation time, and executable path still match.</summary>
    public static Result Resume(IReadOnlyCollection<ProcessIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        int matched = 0;
        int affected = 0;
        bool anyDenied = false;
        bool identityUnavailable = false;
        var affectedProcesses = new List<ProcessIdentity>();
        var unresolvedProcesses = new List<ProcessIdentity>();

        foreach (var expected in identities.DistinctBy(identity => identity.ProcessId))
        {
            IntPtr handle = OpenProcess(
                PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION,
                false,
                expected.ProcessId);
            if (handle == IntPtr.Zero)
            {
                if (Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED)
                {
                    anyDenied = true;
                    unresolvedProcesses.Add(expected);
                }
                continue;
            }

            try
            {
                if (!TryReadIdentity(handle, expected.ProcessId, out var current))
                    continue;
                if (!SameIdentity(current, expected))
                {
                    identityUnavailable = true;
                    unresolvedProcesses.Add(expected);
                    continue;
                }

                matched++;
                if (NtResumeProcess(handle) == 0)
                {
                    affected++;
                    affectedProcesses.Add(current);
                }
                else
                {
                    anyDenied = true;
                    unresolvedProcesses.Add(expected);
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        return new Result(
            matched,
            affected,
            anyDenied,
            affectedProcesses,
            unresolvedProcesses,
            identityUnavailable);
    }

    /// <summary>True when at least one matching process is currently running.</summary>
    public static bool IsRunning(string processName, string? executablePath = null)
    {
        var procs = Process.GetProcessesByName(processName);
        try
        {
            if (string.IsNullOrWhiteSpace(executablePath))
                return procs.Length > 0;

            string expectedPath = NormalizePath(executablePath);
            foreach (var process in procs)
            {
                IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);
                if (handle == IntPtr.Zero)
                    continue;
                try
                {
                    if (TryReadIdentity(handle, process.Id, out var identity)
                        && PathsEqual(identity.ExecutablePath, expectedPath))
                        return true;
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
            return false;
        }
        finally
        {
            foreach (var process in procs)
                process.Dispose();
        }
    }

    private static Result ForEach(string processName, string? executablePath, bool suspend)
    {
        var procs = Process.GetProcessesByName(processName);
        int affected = 0;
        int matched = 0;
        bool anyDenied = false;
        var affectedProcesses = new List<ProcessIdentity>();
        string? expectedPath = string.IsNullOrWhiteSpace(executablePath)
            ? null
            : NormalizePath(executablePath);
        foreach (var process in procs)
        {
            IntPtr handle = OpenProcess(
                PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION,
                false,
                process.Id);
            if (handle == IntPtr.Zero)
            {
                anyDenied |= Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED;
                process.Dispose();
                continue;
            }
            try
            {
                if (!TryReadIdentity(handle, process.Id, out var identity))
                {
                    anyDenied = true;
                    continue;
                }
                if (expectedPath is not null && !PathsEqual(identity.ExecutablePath, expectedPath))
                    continue;

                matched++;
                int status = suspend ? NtSuspendProcess(handle) : NtResumeProcess(handle);
                if (status == 0)
                {
                    affected++;
                    affectedProcesses.Add(identity);
                }
                else
                {
                    anyDenied = true;
                }
            }
            finally
            {
                CloseHandle(handle);
                process.Dispose();
            }
        }
        return new Result(matched, affected, anyDenied && affected == 0, affectedProcesses);
    }

    private static bool TryReadIdentity(IntPtr handle, int processId, out ProcessIdentity identity)
    {
        var path = new StringBuilder(32768);
        int size = path.Capacity;
        if (!QueryFullProcessImageName(handle, 0, path, ref size)
            || !GetProcessTimes(handle, out var creation, out _, out _, out _))
        {
            identity = null!;
            return false;
        }

        long creationTime = ((long)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime;
        identity = new ProcessIdentity(processId, creationTime, NormalizePath(path.ToString()));
        return true;
    }

    private static bool SameIdentity(ProcessIdentity current, ProcessIdentity expected)
        => current.ProcessId == expected.ProcessId
            && current.CreationTimeFileTimeUtc == expected.CreationTimeFileTimeUtc
            && PathsEqual(current.ExecutablePath, expected.ExecutablePath);

    private static bool PathsEqual(string left, string right)
        => string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
