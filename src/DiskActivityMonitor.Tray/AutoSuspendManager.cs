using DiskActivityMonitor.Core;
using DiskActivityMonitor.Core.Collection;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;

namespace DiskActivityMonitor.Tray;

/// <summary>What happened (or needs to happen) for one auto-suspend rule during evaluation.</summary>
internal enum SuspendOutcome { ConfirmNeeded, AutoSuspended, AutoSuspendFailed }

/// <summary>A single auto-suspend decision produced by <see cref="AutoSuspendManager.Evaluate"/>.</summary>
internal sealed record SuspendEvent(AutoSuspendRule Rule, long WrittenBytes, SuspendOutcome Outcome, ProcessControl.Result Result);

/// <summary>
/// Evaluates the configured auto-suspend rules against recent per-process write volume and
/// carries out (or requests confirmation for) suspensions. Suspended processes are tracked in
/// the database so they can be listed and resumed, and so a rule does not re-fire while its
/// target is already suspended. Confirm-mode rules are rate-limited so the user is not prompted
/// on every evaluation tick.
/// </summary>
internal sealed class AutoSuspendManager
{
    private readonly MonitorRepository _repo;
    private readonly ConfigStore _config;
    private readonly Dictionary<string, DateTime> _lastPrompt = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PromptCooldown = TimeSpan.FromMinutes(10);

    public AutoSuspendManager(MonitorRepository repo, ConfigStore config)
    {
        _repo = repo;
        _config = config;
    }

    /// <summary>
    /// Checks every enabled rule at <paramref name="nowUtc"/>. Auto-mode rules whose target is
    /// over its limit are suspended immediately; confirm-mode rules return a <see cref="SuspendEvent"/>
    /// asking the caller to prompt the user. Already-suspended or not-running targets are skipped.
    /// </summary>
    public List<SuspendEvent> Evaluate(DateTime nowUtc)
    {
        var events = new List<SuspendEvent>();
        var rules = _config.Current.AutoSuspendRules;
        if (rules.Count == 0) return events;

        // Per-process data is bucketed per minute, so align the window to the last completed minute.
        var end = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, nowUtc.Minute, 0, DateTimeKind.Utc);
        var from = end.AddHours(-1);
        var suspended = _repo.GetSuspendedProcessNames();

        foreach (var rule in rules)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.ProcessName)) continue;
            if (suspended.Contains(rule.ProcessName)) continue;       // already suspended by us
            if (!ProcessControl.IsRunning(rule.ProcessName)) continue; // nothing to act on

            long written = _repo.GetProcessWrite(rule.ProcessName, from, end);
            double thresholdBytes = rule.ThresholdGbPerHour * ByteFormat.GiB;
            if (thresholdBytes <= 0 || written < thresholdBytes) continue;

            if (rule.Mode == SuspendMode.Auto)
            {
                var result = Suspend(rule.ProcessName);
                events.Add(new SuspendEvent(rule, written,
                    result.Affected > 0 ? SuspendOutcome.AutoSuspended : SuspendOutcome.AutoSuspendFailed, result));
            }
            else
            {
                // Don't re-prompt for the same process within the cooldown.
                if (_lastPrompt.TryGetValue(rule.ProcessName, out var last) && nowUtc - last < PromptCooldown)
                    continue;
                _lastPrompt[rule.ProcessName] = nowUtc;
                events.Add(new SuspendEvent(rule, written, SuspendOutcome.ConfirmNeeded, default));
            }
        }

        return events;
    }

    /// <summary>Suspends a process by name and records it as suspended when at least one instance was frozen.</summary>
    public ProcessControl.Result Suspend(string name)
    {
        var result = ProcessControl.Suspend(name);
        if (result.Affected > 0)
            _repo.AddSuspendedProcess(name, DateTime.UtcNow);
        return result;
    }

    /// <summary>Resumes a process by name and clears its suspended record and prompt cooldown.</summary>
    public ProcessControl.Result Resume(string name)
    {
        var result = ProcessControl.Resume(name);
        _repo.RemoveSuspendedProcess(name);
        _lastPrompt.Remove(name);
        return result;
    }
}
