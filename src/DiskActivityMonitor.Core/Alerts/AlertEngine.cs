using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Models;

namespace DiskActivityMonitor.Core.Alerts;

/// <summary>
/// Evaluates rolling-window write thresholds for SSDs and noisy processes, persisting any
/// new alerts. Each rule has a stable key so repeats are throttled by a configurable cooldown.
/// </summary>
public sealed class AlertEngine
{
    private readonly MonitorRepository _repo;

    public AlertEngine(MonitorRepository repo) => _repo = repo;

    /// <summary>
    /// Checks all rules at <paramref name="nowUtc"/> and returns the alerts that were newly
    /// raised (already persisted). Returns an empty list when nothing tripped or everything
    /// is still in cooldown.
    /// </summary>
    public List<AlertRecord> Evaluate(IEnumerable<DiskInfo> disks, AppConfig cfg, DateTime nowUtc)
    {
        var raised = new List<AlertRecord>();

        // A global "snooze all alerts" suppresses every rule until it expires.
        if (_repo.IsGlobalSnoozeActive(nowUtc))
            return raised;

        var cooldown = TimeSpan.FromMinutes(Math.Max(1, cfg.AlertCooldownMinutes));

        foreach (var disk in disks.Where(d => d.IsSsd))
        {
            // SMART-reported lifetime endurance used - the most accurate "how close to the limit" signal.
            if (disk.WearPercent is int wearPct && cfg.SsdWearWarnPercent > 0 && wearPct >= cfg.SsdWearWarnPercent)
            {
                Emit(raised, cooldown, nowUtc,
                    ruleKey: $"ssd-wear:{disk.DiskId}",
                    severity: wearPct >= 95 ? AlertSeverity.Critical : AlertSeverity.Warning,
                    title: $"{disk.DisplayName} SSD is nearing end of life",
                    message: $"SMART reports {wearPct}% of rated write endurance used on {disk.DisplayName}. Back up important data and plan a replacement.",
                    value: wearPct,
                    threshold: cfg.SsdWearWarnPercent);
            }

            // Rolling 1-hour write volume.
            var hourWrite = _repo.GetDiskTotals(disk.DiskId, nowUtc.AddHours(-1), nowUtc).Write;
            TryRaise(raised, cfg, cooldown, nowUtc,
                ruleKey: $"ssd-1h:{disk.DiskId}",
                value: hourWrite,
                thresholdBytes: cfg.SsdWarnGbPerHour * ByteFormat.GiB,
                severity: AlertSeverity.Warning,
                title: $"High SSD write rate on {disk.DisplayName}",
                buildMessage: (v, t) => $"{ByteFormat.Humanize(v)} written in the last hour (threshold {ByteFormat.Humanize(t)}).");

            // Rolling 24-hour write volume - critical takes precedence over warning.
            var dayWrite = _repo.GetDiskTotals(disk.DiskId, nowUtc.AddHours(-24), nowUtc).Write;
            var critThreshold = cfg.SsdCriticalGbPerDay * ByteFormat.GiB;
            var warnThreshold = cfg.SsdWarnGbPerDay * ByteFormat.GiB;
            if (dayWrite >= critThreshold)
            {
                TryRaise(raised, cfg, cooldown, nowUtc,
                    ruleKey: $"ssd-24h:{disk.DiskId}",
                    value: dayWrite,
                    thresholdBytes: critThreshold,
                    severity: AlertSeverity.Critical,
                    title: $"Very high daily SSD writes on {disk.DisplayName}",
                    buildMessage: (v, t) => $"{ByteFormat.Humanize(v)} written in 24h (critical threshold {ByteFormat.Humanize(t)}). Sustained activity at this level shortens SSD life.");
            }
            else
            {
                TryRaise(raised, cfg, cooldown, nowUtc,
                    ruleKey: $"ssd-24h:{disk.DiskId}",
                    value: dayWrite,
                    thresholdBytes: warnThreshold,
                    severity: AlertSeverity.Warning,
                    title: $"Elevated daily SSD writes on {disk.DisplayName}",
                    buildMessage: (v, t) => $"{ByteFormat.Humanize(v)} written in the last 24 hours (threshold {ByteFormat.Humanize(t)}).");
            }

            // Endurance projection: at the recent average write rate, when will this SSD reach
            // its TBW endurance rating? Only evaluated once at least a day of history exists.
            var earliest = _repo.GetEarliestSample(disk.DiskId);
            if (earliest is not null && nowUtc - earliest.Value >= TimeSpan.FromHours(24))
            {
                double avgPerDay = nowUtc - earliest.Value >= TimeSpan.FromDays(7)
                    ? _repo.GetDiskTotals(disk.DiskId, nowUtc.AddDays(-7), nowUtc).Write / 7.0
                    : _repo.GetDiskTotals(disk.DiskId, earliest.Value, nowUtc).Write / Math.Max(1.0 / 24, (nowUtc - earliest.Value).TotalDays);

                if (avgPerDay > 0)
                {
                    double tbwLow = cfg.EffectiveTbw(disk.DiskId);
                    double? tbwHigh = cfg.EffectiveTbwUpper(disk.DiskId);
                    double yearsLow = tbwLow * 1_000_000_000_000d / (avgPerDay * 365.0);
                    double yearsHigh = (tbwHigh ?? tbwLow) * 1_000_000_000_000d / (avgPerDay * 365.0);
                    AlertSeverity? severity = yearsLow <= cfg.TbwProjectionCriticalYears ? AlertSeverity.Critical
                        : yearsLow <= cfg.TbwProjectionWarnYears ? AlertSeverity.Warning
                        : null;
                    if (severity is not null)
                    {
                        bool estimated = !cfg.DiskTbwRatings.ContainsKey(disk.DiskId);
                        string rating = tbwHigh.HasValue
                            ? $"{tbwLow:0.#} to {tbwHigh:0.#} TBW {(estimated ? "estimate" : "range")}"
                            : $"{tbwLow:0.#} TBW rating";
                        string projection = tbwHigh.HasValue
                            ? $"{FormatYears(yearsLow)} to {FormatYears(yearsHigh)}"
                            : FormatYears(yearsLow);
                        Emit(raised, cooldown, nowUtc,
                            ruleKey: $"tbw-life:{disk.DiskId}",
                            severity: severity.Value,
                            title: $"{disk.DisplayName} is on track to wear out",
                            message: $"At the recent average of {ByteFormat.Humanize(avgPerDay)}/day, {disk.DisplayName} would reach its {rating} in about {projection}.",
                            value: yearsLow,
                            threshold: cfg.TbwProjectionWarnYears);
                    }
                }
            }
        }

        // Noisiest process in the last hour.
        var topProcs = _repo.GetTopProcesses(nowUtc.AddHours(-1), nowUtc, topN: 3);
        var procThreshold = cfg.ProcessWarnGbPerHour * ByteFormat.GiB;
        var snoozedProcs = _repo.GetActiveProcessSnoozes(nowUtc);
        foreach (var p in topProcs)
        {
            // Skip processes the user has snoozed from a toast.
            if (snoozedProcs.Contains(p.ProcessName)) continue;

            TryRaise(raised, cfg, cooldown, nowUtc,
                ruleKey: $"proc-1h:{p.ProcessName}",
                value: p.WriteBytes,
                thresholdBytes: procThreshold,
                severity: AlertSeverity.Warning,
                title: $"Process '{p.ProcessName}' is requesting heavy file writes",
                buildMessage: (v, t) =>
                    $"{p.ProcessName} logical file-write requests \u2014 {FormatBreakdown(nowUtc, (f, to) => _repo.GetProcessWrite(p.ProcessName, f, to))} (threshold {ByteFormat.Humanize(t)}/h). Physical disk writes may be lower.");
        }

        // Combined: all processes together in the last hour.
        var allHourWrite = _repo.GetAllProcessesWrite(nowUtc.AddHours(-1), nowUtc);
        TryRaise(raised, cfg, cooldown, nowUtc,
            ruleKey: "procs-all-1h",
            value: allHourWrite,
            thresholdBytes: cfg.AllProcessesWarnGbPerHour * ByteFormat.GiB,
            severity: AlertSeverity.Warning,
            title: "All processes combined are requesting heavy file writes",
            buildMessage: (v, t) =>
                $"All processes combined logical file-write requests \u2014 {FormatBreakdown(nowUtc, (f, to) => _repo.GetAllProcessesWrite(f, to))} (threshold {ByteFormat.Humanize(t)}/h). Physical disk writes may be lower.");

        return raised;
    }

    /// <summary>
    /// Evaluates aggregated Windows System log Disk event 11 records. Device numbers are mapped
    /// to the currently detected disk/volumes when possible, while retaining the reported device
    /// path because removable-disk numbers can be reassigned after reconnecting hardware.
    /// </summary>
    public List<AlertRecord> EvaluateControllerErrors(
        IEnumerable<DiskInfo> disks,
        IEnumerable<DiskControllerErrorSummary> errors,
        AppConfig cfg,
        DateTime nowUtc)
    {
        var raised = new List<AlertRecord>();
        if (!cfg.EnableControllerErrorAlerts || cfg.ControllerErrorWarnCount <= 0 || _repo.IsGlobalSnoozeActive(nowUtc))
            return raised;

        var cooldown = TimeSpan.FromMinutes(Math.Max(1, cfg.AlertCooldownMinutes));
        int windowDays = Math.Clamp(cfg.ControllerErrorWindowDays, 1, 365);
        int warnCount = Math.Max(1, cfg.ControllerErrorWarnCount);
        int criticalCount = cfg.ControllerErrorCriticalCount > 0
            ? Math.Max(warnCount, cfg.ControllerErrorCriticalCount)
            : int.MaxValue;
        var disksById = disks
            .GroupBy(d => d.DiskId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var error in errors.Where(e => e.Count >= warnCount))
        {
            disksById.TryGetValue(error.DiskId, out var disk);
            bool critical = error.Count >= criticalCount;
            var severity = critical ? AlertSeverity.Critical : AlertSeverity.Warning;
            int threshold = critical ? criticalCount : warnCount;
            string target = disk is not null && !string.IsNullOrWhiteSpace(disk.Volumes)
                ? disk.Volumes.Trim()
                : $"Harddisk{error.DiskId}";
            string mapping = disk is null
                ? "The physical disk is not currently present, so no volume mapping is available."
                : $"That device number currently maps to {disk.DisplayName}.";
            string latest = LocalTimeDisplay.FormatUtcWithZone(error.LatestUtc, "MMM d, yyyy HH:mm");
            string countWord = error.Count == 1 ? "error" : "errors";

            Emit(raised, cooldown, nowUtc,
                ruleKey: $"disk-controller:{error.DiskId}",
                severity: severity,
                title: $"{(critical ? "Repeated" : "Storage")} controller errors on {target}",
                message: $"Windows logged {error.Count} Disk event 11 controller {countWord} for {error.DevicePath} in the last {windowDays} days; latest {latest}. {mapping} A drive may still report Healthy/Online while USB/SATA cable, port, power, enclosure, or controller instability causes intermittent read failures. Back up important data and inspect or change the connection.",
                value: error.Count,
                threshold: threshold);
        }

        return raised;
    }

    // Rolling windows shown in process write-volume alert messages.
    private static readonly (string Label, int Minutes)[] BreakdownWindows =
    {
        ("1m", 1), ("5m", 5), ("15m", 15), ("30m", 30), ("1h", 60),
    };

    /// <summary>Formats write totals across the recent rolling windows, e.g. "1m: 0.8 GB, 5m: 4.1 GB, ...".</summary>
    private static string FormatBreakdown(DateTime nowUtc, Func<DateTime, DateTime, long> windowWrite)
    {
        // Writes are bucketed per minute and the current partial minute isn't flushed yet, so
        // align the windows to the last completed minute; otherwise the 1-minute window reads 0.
        var end = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, nowUtc.Minute, 0, DateTimeKind.Utc);
        return string.Join(", ", BreakdownWindows.Select(w => $"{w.Label}: {ByteFormat.Humanize(windowWrite(end.AddMinutes(-w.Minutes), end))}"));
    }

    private void TryRaise(
        List<AlertRecord> raised,
        AppConfig cfg,
        TimeSpan cooldown,
        DateTime nowUtc,
        string ruleKey,
        double value,
        double thresholdBytes,
        AlertSeverity severity,
        string title,
        Func<double, double, string> buildMessage)
    {
        if (thresholdBytes <= 0 || value < thresholdBytes)
            return;

        Emit(raised, cooldown, nowUtc, ruleKey, severity, title, buildMessage(value, thresholdBytes), value, thresholdBytes);
    }

    private void Emit(
        List<AlertRecord> raised,
        TimeSpan cooldown,
        DateTime nowUtc,
        string ruleKey,
        AlertSeverity severity,
        string title,
        string message,
        double value,
        double threshold)
    {
        var last = _repo.GetLastAlertTime(ruleKey);
        if (last is not null && nowUtc - last.Value < cooldown)
            return;

        var alert = new AlertRecord
        {
            TimestampUtc = nowUtc,
            Severity = severity,
            RuleKey = ruleKey,
            Title = title,
            Message = message,
            Value = value,
            Threshold = threshold,
        };
        alert.Id = _repo.InsertAlert(alert);
        raised.Add(alert);
    }

    private static string FormatYears(double years)
    {
        if (double.IsNaN(years) || years <= 0) return "an unknown time";
        if (years >= 1) return $"{years:0.0} years";
        int months = Math.Max(1, (int)Math.Round(years * 12));
        return months == 1 ? "1 month" : $"{months} months";
    }
}
