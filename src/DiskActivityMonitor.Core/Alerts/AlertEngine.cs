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
    public List<AlertRecord> Evaluate(
        IEnumerable<DiskInfo> disks,
        AppConfig cfg,
        DateTime nowUtc,
        double highCoveragePercent = 90)
    {
        var raised = new List<AlertRecord>();
        var diskList = disks.ToList();

        // A global "snooze all alerts" suppresses every rule until it expires.
        if (_repo.IsGlobalSnoozeActive(nowUtc))
            return raised;

        var cooldown = TimeSpan.FromMinutes(Math.Max(1, cfg.AlertCooldownMinutes));

        foreach (var disk in diskList.Where(d => d.IsSsd))
        {
            EvaluateEndurance(raised, disk, cfg, cooldown, nowUtc, highCoveragePercent);

            // Rolling 1-hour write volume.
            var hourWrite = _repo.GetDiskTotals(disk.DiskId, nowUtc.AddHours(-1), nowUtc).Write;
            TryRaise(raised, cfg, cooldown, nowUtc,
                ruleKey: $"ssd-1h:{disk.DiskId}",
                value: hourWrite,
                thresholdBytes: cfg.SsdWarnGbPerHour * ByteFormat.GB,
                severity: AlertSeverity.Warning,
                title: $"High SSD write rate on {disk.DisplayName}",
                buildMessage: (v, t) => $"{ByteFormat.Humanize(v)} written in the last hour (threshold {ByteFormat.Humanize(t)}).");

            // Rolling 24-hour write volume - critical takes precedence over warning.
            var dayWrite = _repo.GetDiskTotals(disk.DiskId, nowUtc.AddHours(-24), nowUtc).Write;
            var critThreshold = cfg.SsdCriticalGbPerDay * ByteFormat.GB;
            var warnThreshold = cfg.SsdWarnGbPerDay * ByteFormat.GB;
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

        }

        // Noisiest process in the last hour.
        var topProcs = _repo.GetTopProcesses(nowUtc.AddHours(-1), nowUtc, topN: 3);
        var procThreshold = cfg.ProcessWarnGbPerHour * ByteFormat.GB;
        var snoozedProcs = _repo.GetActiveProcessSnoozes(nowUtc);
        string? physicalDiskWrites = null;
        string PhysicalDiskWrites() => physicalDiskWrites ??= FormatPhysicalDiskWrites(diskList, nowUtc);
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
                    $"{p.ProcessName} logical file-write requests \u2014 {FormatBreakdown(nowUtc, (f, to) => _repo.GetProcessWrite(p.ProcessName, f, to))} (threshold {ByteFormat.Humanize(t)}/h). Physical disk writes may be lower. {PhysicalDiskWrites()}");
        }

        // Combined: all processes together in the last hour.
        var allHourWrite = _repo.GetAllProcessesWrite(nowUtc.AddHours(-1), nowUtc);
        TryRaise(raised, cfg, cooldown, nowUtc,
            ruleKey: "procs-all-1h",
            value: allHourWrite,
            thresholdBytes: cfg.AllProcessesWarnGbPerHour * ByteFormat.GB,
            severity: AlertSeverity.Warning,
            title: "All processes combined are requesting heavy file writes",
            buildMessage: (v, t) =>
                $"All processes combined logical file-write requests \u2014 {FormatBreakdown(nowUtc, (f, to) => _repo.GetAllProcessesWrite(f, to))} (threshold {ByteFormat.Humanize(t)}/h). Physical disk writes may be lower. {PhysicalDiskWrites()}");

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
            string latest = LocalTimeDisplay.FormatUtcWithZone(error.LatestUtc, "MMM d, yyyy h:mm tt");
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

    private string FormatPhysicalDiskWrites(IEnumerable<DiskInfo> disks, DateTime nowUtc)
    {
        var end = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, nowUtc.Minute, 0, DateTimeKind.Utc);
        var writes = disks
            .Select(d => (Disk: d, Write: _repo.GetDiskTotals(d.DiskId, end.AddHours(-1), end).Write))
            .Where(x => x.Write > 0)
            .OrderByDescending(x => x.Write)
            .ToList();

        if (writes.Count == 0)
            return "No per-drive physical writes were recorded in the last completed hour.";

        string summary = string.Join("; ", writes.Select(x => $"{x.Disk.DisplayName}: {ByteFormat.Humanize(x.Write)}"));
        return $"Physical writes by drive (all processes, last hour): {summary}. Process requests cannot be assigned to one drive exactly.";
    }

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
        if (_repo.IsAlertRuleSnoozed(ruleKey, nowUtc))
            return;

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

    private void EvaluateEndurance(
        List<AlertRecord> raised,
        DiskInfo disk,
        AppConfig config,
        TimeSpan cooldown,
        DateTime nowUtc,
        double highCoveragePercent)
    {
        EnduranceAlertThreshold threshold = config.EffectiveEnduranceAlert(disk.DiskId);
        var reasons = new List<string>();
        double alertValue = 0;
        double alertThreshold = 0;
        // Replacement advice needs evidence about this drive; an assumed default TBW is not.
        bool driveSpecificEvidence = false;
        bool smartMentioned = false;

        bool ratingConfigured = config.HasConfiguredTbw(disk.DiskId);
        double tbwLow = config.EffectiveTbw(disk.DiskId);
        double? tbwHigh = config.EffectiveTbwUpper(disk.DiskId);
        double tbwLowBytes = tbwLow * ByteFormat.TB;
        double tbwHighBytes = (tbwHigh ?? tbwLow) * ByteFormat.TB;
        string tbwLabel = tbwHigh is double high ? $"{tbwLow:0.#} to {high:0.#} TBW" : $"{tbwLow:0.#} TBW";
        string ratingText = ratingConfigured
            ? $"this drive's configured {tbwLabel} rating"
            : $"the assumed {tbwLabel} default rating";
        int? smartWear = disk.WearPercent;
        long? lifetimeWritten = disk.LifetimeBytesWritten;

        if (threshold.EnableRemainingPercent && smartWear is int wear)
        {
            double remaining = Math.Clamp(100.0 - wear, 0, 100);
            if (remaining <= threshold.RemainingPercent)
            {
                reasons.Add($"The drive's SMART data reports {wear}% of its endurance used ({remaining:0.##}% remaining); the warning threshold is {threshold.RemainingPercent:0.##}% remaining.");
                alertValue = remaining;
                alertThreshold = threshold.RemainingPercent;
                driveSpecificEvidence = true;
                smartMentioned = true;
            }
        }

        // The assumed default rating is only a fallback for drives that report no SMART wear.
        if (threshold.EnableTbwConsumed
            && lifetimeWritten is long written
            && tbwLowBytes > 0
            && (ratingConfigured || smartWear is null))
        {
            double usedOfLower = written / tbwLowBytes * 100.0;
            if (usedOfLower >= threshold.TbwConsumedPercent)
            {
                string share = tbwHigh.HasValue
                    ? $"{written / tbwHighBytes * 100.0:0.##}% to {usedOfLower:0.##}%"
                    : $"{usedOfLower:0.##}%";
                string basis = tbwHigh.HasValue ? "of the rating's lower bound" : "of the rating";
                string caveat = ratingConfigured
                    ? ""
                    : " This drive reports no SMART wear and has no configured TBW rating, so this is only an estimate.";
                reasons.Add($"Lifetime writes of {ByteFormat.Humanize(written)} are {share} of {ratingText}; the warning threshold is {threshold.TbwConsumedPercent:0.##}% {basis}.{caveat}");
                alertValue = usedOfLower;
                alertThreshold = threshold.TbwConsumedPercent;
                driveSpecificEvidence |= ratingConfigured;
            }
        }

        DateTime? earliest = _repo.GetEarliestSample(disk.DiskId);
        if (threshold.EnableProjectedLife
            && earliest is DateTime first
            && nowUtc - first >= TimeSpan.FromHours(24))
        {
            MonitoringRateStats recentRate = _repo.GetRecentDiskWriteRate(
                disk.DiskId,
                nowUtc,
                highCoveragePercent,
                config.EnduranceCoverageWindowDays);
            double avgPerDay = recentRate.MonitoredBytesPerHour * 24.0;
            if (recentRate.HasHighCoverage && avgPerDay > 0)
            {
                double daysLow;
                double daysHigh;
                string target;
                bool smartProjection = !ratingConfigured && smartWear is > 0 && lifetimeWritten is not null;
                if (smartProjection)
                {
                    // Whole-percent SMART wear means the true value lies between w and w+1.
                    int w = smartWear!.Value;
                    double lifetime = lifetimeWritten!.Value;
                    daysLow = Math.Max(0, lifetime * (100.0 / (w + 1) - 1)) / avgPerDay;
                    daysHigh = Math.Max(0, lifetime * (100.0 / w - 1)) / avgPerDay;
                    target = $"until the drive's SMART wear reaches 100%, extrapolated from {w}% used after {ByteFormat.Humanize(lifetime)} written";
                }
                else
                {
                    double consumed = lifetimeWritten
                        ?? _repo.GetDiskTotals(disk.DiskId, first, nowUtc).Write;
                    daysLow = Math.Max(0, tbwLowBytes - consumed) / avgPerDay;
                    daysHigh = Math.Max(0, tbwHighBytes - consumed) / avgPerDay;
                    target = $"until {ratingText} is reached";
                }

                if (daysLow <= threshold.RemainingLifeDays)
                {
                    string projection = FormatRemainingTime(daysLow);
                    string upper = FormatRemainingTime(daysHigh);
                    if (upper != projection)
                        projection += $" to {upper}";
                    reasons.Add($"Projected remaining life is {projection} at {ByteFormat.Humanize(avgPerDay)}/day {target}; the warning threshold is {FormatRemainingTime(threshold.RemainingLifeDays)}.");
                    alertValue = daysLow;
                    alertThreshold = threshold.RemainingLifeDays;
                    driveSpecificEvidence |= ratingConfigured || smartProjection;
                    smartMentioned |= smartProjection;
                }
            }
        }

        if (reasons.Count == 0)
            return;

        if (smartWear is int reported && !smartMentioned)
            reasons.Add($"The drive's SMART data reports {reported}% of its endurance used.");
        reasons.Add(driveSpecificEvidence
            ? "Back up important data and plan drive replacement."
            : "Back up important data, and set this drive's rated TBW in Settings (or use Look up rated TBW) for an accurate estimate.");

        Emit(
            raised,
            cooldown,
            nowUtc,
            ruleKey: $"endurance-health:{disk.DiskId}",
            severity: AlertSeverity.Warning,
            title: $"Endurance warning for {disk.DisplayName}",
            message: string.Join(" ", reasons),
            value: alertValue,
            threshold: alertThreshold);
    }

    internal static string FormatRemainingTime(double days)
    {
        if (!double.IsFinite(days) || days < 0) return "an unknown time";
        if (days < 2) return $"{Math.Max(0, days):0.#} days";
        if (days < 60) return $"{days:0} days";
        if (days < 730) return $"{days / (365.25 / 12.0):0.#} months";
        return $"{days / 365.25:0.#} years";
    }
}
