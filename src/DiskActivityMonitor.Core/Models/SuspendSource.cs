namespace DiskActivityMonitor.Core.Models;

/// <summary>Why a process is currently suspended by Disk Activity Monitor.</summary>
public enum SuspendSource
{
    /// <summary>The user suspended it explicitly (alert toast or dashboard).</summary>
    Manual = 0,

    /// <summary>An auto-suspend rule suspended it after it exceeded its write limit.</summary>
    AutoRule = 1,
}
