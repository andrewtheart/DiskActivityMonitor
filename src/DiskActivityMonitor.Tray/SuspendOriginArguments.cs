using DiskActivityMonitor.Core.Models;

namespace DiskActivityMonitor.Tray;

/// <summary>
/// Maps the <c>source</c> argument carried by suspend toasts onto a <see cref="SuspendSource"/>.
/// A confirmation raised by an auto-suspend rule is still rule-driven even though the user
/// approved it, so the toast has to say which one it was.
/// </summary>
internal static class SuspendOriginArguments
{
    public const string Rule = "rule";

    /// <summary>Toasts without the argument are ad-hoc actions the user started themselves.</summary>
    public static SuspendSource ToSource(string? argument)
        => string.Equals(argument, Rule, StringComparison.Ordinal)
            ? SuspendSource.AutoRule
            : SuspendSource.Manual;
}
