using DiskActivityMonitor.Core;
using DiskActivityMonitor.Core.Collection;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Models;

namespace DiskActivityMonitor.Tray;

/// <summary>What happened (or needs to happen) for one auto-suspend rule during evaluation.</summary>
internal enum SuspendOutcome { ConfirmNeeded, AutoSuspended, AutoSuspendFailed }

/// <summary>A single auto-suspend decision produced by <see cref="AutoSuspendManager.Evaluate"/>.</summary>
internal sealed record SuspendEvent(AutoSuspendRule Rule, long WrittenBytes, SuspendOutcome Outcome, ProcessControl.Result Result);

/// <summary>A suspension whose interval elapsed and which the app therefore resumed.</summary>
internal sealed record ExpiredSuspension(string ProcessName, SuspendSource Source, ProcessControl.Result Result);

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
    private readonly UserSettingsStore _userSettings;
    private readonly Dictionary<string, DateTime> _lastPrompt = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failedResumes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PromptCooldown = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ResumeRetryInterval = TimeSpan.FromMinutes(5);

    public AutoSuspendManager(MonitorRepository repo, UserSettingsStore userSettings)
    {
        _repo = repo;
        _userSettings = userSettings;
    }

    /// <summary>
    /// Checks every enabled rule at <paramref name="nowUtc"/>. Auto-mode rules whose target is
    /// over its limit are suspended immediately; confirm-mode rules return a <see cref="SuspendEvent"/>
    /// asking the caller to prompt the user. Already-suspended or not-running targets are skipped.
    /// </summary>
    public List<SuspendEvent> Evaluate(DateTime nowUtc)
    {
        var events = new List<SuspendEvent>();
        var rules = _userSettings.Current.AutoSuspendRules;
        if (rules.Count == 0) return events;

        // Per-process data is bucketed per minute, so align the window to the last completed minute.
        var end = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, nowUtc.Minute, 0, DateTimeKind.Utc);
        var from = end.AddHours(-1);
        var suspended = _repo.GetSuspendedProcessNames();

        foreach (var rule in rules)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.ProcessName)) continue;
            if (suspended.Contains(rule.ProcessName)) continue;       // already suspended by us
            if (!ProcessControl.IsRunning(rule.ProcessName, rule.ExecutablePath)) continue; // nothing to act on

            long written = _repo.GetProcessWrite(rule.ProcessName, from, end);
            double thresholdBytes = rule.ThresholdGbPerHour * ByteFormat.GiB;
            if (thresholdBytes <= 0 || written < thresholdBytes) continue;

            if (CanAutoSuspend(rule))
            {
                var result = Suspend(rule.ProcessName, rule.ExecutablePath, SuspendSource.AutoRule, nowUtc);
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

    /// <summary>Suspends matching processes and records their exact identities.</summary>
    public ProcessControl.Result Suspend(
        string name,
        string? executablePath = null,
        SuspendSource source = SuspendSource.Manual,
        DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        return SuspendTracked(
            _repo,
            name,
            executablePath,
            SuspendDurationOptions.ResumeAt(now, _userSettings.Current.DefaultSuspendMinutes),
            source,
            now);
    }

    /// <summary>Resumes only the identities previously suspended by this app.</summary>
    public ProcessControl.Result Resume(string name)
    {
        var result = ResumeTracked(_repo, name);
        _lastPrompt.Remove(name);
        return result;
    }

    /// <summary>
    /// Resumes every suspension whose chosen interval has elapsed. Suspensions recorded without a
    /// deadline ("until I resume it") are left alone.
    /// </summary>
    public List<ExpiredSuspension> ResumeExpired(DateTime nowUtc)
    {
        var resumed = new List<ExpiredSuspension>();
        foreach (var state in _repo.GetSuspendedProcessStates().Where(s => s.IsDue(nowUtc)))
        {
            var result = ResumeTracked(_repo, state.Name);
            _lastPrompt.Remove(state.Name);

            var remaining = _repo.GetSuspendedProcessState(state.Name);
            if (remaining is null || result.IdentityUnavailable)
            {
                // Either it is running again, or the exact identity is gone and never can be
                // resumed - in both cases the app must stop claiming it holds the process.
                if (remaining is not null)
                    _repo.RemoveSuspendedProcess(state.Name);
                _failedResumes.Remove(state.Name);
                resumed.Add(new ExpiredSuspension(state.Name, state.Source, result));
                continue;
            }

            // Some threads could not be released (typically access denied). Back off rather than
            // retrying - and re-notifying - on every tick.
            _repo.AddSuspendedProcess(
                remaining.Name,
                remaining.SuspendedUtc,
                remaining.ExecutablePath,
                remaining.ProcessIdentities,
                nowUtc + ResumeRetryInterval,
                remaining.Source);
            if (_failedResumes.Add(state.Name))
                resumed.Add(new ExpiredSuspension(state.Name, state.Source, result));
        }
        return resumed;
    }

    internal static ProcessControl.Result SuspendTracked(
        MonitorRepository repo,
        string name,
        string? executablePath,
        DateTime? resumeAtUtc = null,
        SuspendSource source = SuspendSource.Manual,
        DateTime? nowUtc = null)
    {
        var result = ProcessControl.Suspend(name, executablePath);
        if (result.Affected > 0)
            repo.AddSuspendedProcess(name, nowUtc ?? DateTime.UtcNow, executablePath, result.Processes, resumeAtUtc, source);
        return result;
    }

    internal static ProcessControl.Result ResumeTracked(MonitorRepository repo, string name)
    {
        var state = repo.GetSuspendedProcessState(name);
        if (state is not { ProcessIdentities.Count: > 0 })
            return new ProcessControl.Result(0, 0, false, IdentityUnavailable: true);

        var result = ProcessControl.Resume(state.ProcessIdentities);
        if (result.Unresolved.Count > 0)
            repo.AddSuspendedProcess(
                state.Name,
                state.SuspendedUtc,
                state.ExecutablePath,
                result.Unresolved,
                state.ResumeAtUtc,
                state.Source);
        else
            repo.RemoveSuspendedProcess(name);
        return result;
    }

    internal static bool CanAutoSuspend(AutoSuspendRule rule)
        => rule.Mode == SuspendMode.Auto && !string.IsNullOrWhiteSpace(rule.ExecutablePath);
}
