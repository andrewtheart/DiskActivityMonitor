using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiskActivityMonitor.Core.Collection;

/// <summary>
/// Suspends and resumes processes by image name using the native NtSuspendProcess /
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

    /// <summary>Outcome of a suspend/resume call across every instance of a process name.</summary>
    /// <param name="Matched">How many running processes carried the image name.</param>
    /// <param name="Affected">How many were successfully suspended/resumed.</param>
    /// <param name="AccessDenied">True when instances were found but none could be opened/changed.</param>
    public readonly record struct Result(int Matched, int Affected, bool AccessDenied);

    /// <summary>Suspends every running process with the given image name.</summary>
    public static Result Suspend(string processName) => ForEach(processName, suspend: true);

    /// <summary>Resumes every running process with the given image name.</summary>
    public static Result Resume(string processName) => ForEach(processName, suspend: false);

    /// <summary>True when at least one process with this image name is currently running.</summary>
    public static bool IsRunning(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    private static Result ForEach(string processName, bool suspend)
    {
        var procs = Process.GetProcessesByName(processName);
        int affected = 0;
        bool anyDenied = false;
        foreach (var p in procs)
        {
            IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, p.Id);
            if (h == IntPtr.Zero)
            {
                anyDenied = true;
                p.Dispose();
                continue;
            }
            try
            {
                int status = suspend ? NtSuspendProcess(h) : NtResumeProcess(h);
                if (status == 0) affected++;
                else anyDenied = true;
            }
            finally
            {
                CloseHandle(h);
                p.Dispose();
            }
        }
        return new Result(procs.Length, affected, anyDenied && affected == 0);
    }
}
