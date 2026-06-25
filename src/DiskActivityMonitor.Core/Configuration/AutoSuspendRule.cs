namespace DiskActivityMonitor.Core.Configuration;

/// <summary>How an auto-suspend rule reacts when a process exceeds its write threshold.</summary>
public enum SuspendMode
{
    /// <summary>Show a toast asking the user to confirm before suspending (the default).</summary>
    Confirm = 0,

    /// <summary>Suspend the process immediately, then notify the user it happened.</summary>
    Auto = 1,
}

/// <summary>
/// A user-defined rule that suspends a process when its rolling 1-hour write volume exceeds a
/// threshold. Matched against the process image name without extension (e.g. "Yagu").
/// </summary>
public sealed class AutoSuspendRule
{
    /// <summary>Process image name without extension (the match key, e.g. "Yagu").</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>Suspend when the process writes more than this many GB in a rolling 1-hour window.</summary>
    public double ThresholdGbPerHour { get; set; } = 5;

    /// <summary>Whether to suspend automatically or ask the user for confirmation first.</summary>
    public SuspendMode Mode { get; set; } = SuspendMode.Confirm;

    /// <summary>When false, the rule is ignored.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Optional full path to the executable the rule was created from (for display only). Set when
    /// the user browses for an as-yet-unseen process; empty when picked from already-seen processes.
    /// </summary>
    public string? ExecutablePath { get; set; }
}
