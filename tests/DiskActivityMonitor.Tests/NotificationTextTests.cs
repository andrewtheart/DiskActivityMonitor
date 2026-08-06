using DiskActivityMonitor.Core;
using DiskActivityMonitor.Core.Collection;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Models;
using DiskActivityMonitor.Tray;

namespace DiskActivityMonitor.Tests;

public sealed class NotificationTextTests
{
    [Theory]
    [InlineData("proc-1h:System", AlertSeverity.Warning, 8, 5,
        "High logical writes: System",
        "5m: 1 GB, 15m: 2 GB, 30m: 3 GB, 24h: 24 GB. 1h limit: 5 GB.")]
    [InlineData("procs-all-1h", AlertSeverity.Warning, 24, 20,
        "High combined logical writes",
        "5m: 1 GB, 15m: 2 GB, 30m: 3 GB, 24h: 24 GB. 1h limit: 20 GB.")]
    [InlineData("ssd-wear:0", AlertSeverity.Warning, 92, 85,
        "Original title",
        "SMART endurance used: 92% (warning 85%). Back up data and plan replacement.")]
    [InlineData("ssd-1h:0", AlertSeverity.Warning, 18.75, 10,
        "Original title",
        "5m: 1 GB, 15m: 2 GB, 30m: 3 GB, 24h: 24 GB. 1h limit: 10 GB.")]
    [InlineData("ssd-24h:0", AlertSeverity.Warning, 145, 100,
        "Original title",
        "5m: 1 GB, 15m: 2 GB, 30m: 3 GB, 24h: 24 GB. 24h limit: 100 GB.")]
    [InlineData("ssd-24h:0", AlertSeverity.Critical, 310, 250,
        "Original title",
        "5m: 1 GB, 15m: 2 GB, 30m: 3 GB, 24h: 24 GB. 24h critical limit: 250 GB.")]
    [InlineData("tbw-life:0", AlertSeverity.Warning, 3.2, 5,
        "Original title",
        "Projected life: ~3.2 years at the recent write rate.")]
    [InlineData("disk-controller:2", AlertSeverity.Warning, 3, 3,
        "Original title",
        "Windows logged 3 Disk event 11 errors. Back up data; check cable, port, power, enclosure, or controller.")]
    [InlineData("disk-controller:2", AlertSeverity.Warning, 1, 1,
        "Original title",
        "Windows logged 1 Disk event 11 error. Back up data; check cable, port, power, enclosure, or controller.")]
    public void AlertToastText_CondensesKnownRules(
        string ruleKey,
        AlertSeverity severity,
        double valueGbOrScalar,
        double thresholdGbOrScalar,
        string expectedTitle,
        string expectedBody)
    {
        bool byteRule = ruleKey.StartsWith("proc-1h:", StringComparison.Ordinal)
            || ruleKey is "procs-all-1h"
            || ruleKey.StartsWith("ssd-1h:", StringComparison.Ordinal)
            || ruleKey.StartsWith("ssd-24h:", StringComparison.Ordinal);
        var alert = new AlertRecord
        {
            TimestampUtc = DateTime.UtcNow,
            Severity = severity,
            RuleKey = ruleKey,
            Title = "Original title",
            Message = "Full stored diagnostic message",
            Value = byteRule ? valueGbOrScalar * ByteFormat.GiB : valueGbOrScalar,
            Threshold = byteRule ? thresholdGbOrScalar * ByteFormat.GiB : thresholdGbOrScalar,
        };

        var text = TrayController.FormatAlertToastText(alert, Windows());

        Assert.Equal(expectedTitle, text.Title);
        Assert.Equal(expectedBody, text.Body);
        Assert.Equal("Full stored diagnostic message", alert.Message);
    }

    [Fact]
    public void AlertToastText_UsesFullTextForUnknownFutureRule()
    {
        var alert = new AlertRecord
        {
            TimestampUtc = DateTime.UtcNow,
            RuleKey = "future-rule",
            Title = "Future title",
            Message = "Future body",
        };

        Assert.Equal(("Future title", "Future body"), TrayController.FormatAlertToastText(alert, Windows()));
    }

    [Fact]
    public void WriteWindows_QueriesEveryRequiredDurationAtTheLastCompletedMinute()
    {
        var queried = new List<TimeSpan>();
        DateTime now = new(2026, 8, 5, 12, 34, 56, DateTimeKind.Utc);

        var windows = TrayController.GetWriteWindows(now, (from, to) =>
        {
            Assert.Equal(new DateTime(2026, 8, 5, 12, 34, 0, DateTimeKind.Utc), to);
            queried.Add(to - from);
            return (long)(to - from).TotalMinutes;
        });

        Assert.Equal([TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(24)], queried);
        Assert.Equal(new TrayController.WriteWindowStats(5, 15, 30, 1440), windows);
    }

    [Fact]
    public void SuspendConfirmationText_AlwaysIncludesEveryWriteWindow()
    {
        var rule = Rule();

        Assert.Equal(
            ("High logical writes: HandBrake", "5m: 1 GB, 15m: 2 GB, 30m: 3 GB, 24h: 24 GB. 1h limit: 5 GB. Suspend?"),
            TrayController.FormatSuspendConfirmationText(rule, Windows()));
    }

    [Fact]
    public void AutoSuspendText_CoversTimedManualDeniedAndExitedOutcomes()
    {
        var rule = Rule();
        Assert.Equal(
            ("HandBrake suspended", "5m: 1 GB, 15m: 2 GB, 30m: 3 GB, 24h: 24 GB. 1h limit: 5 GB. Resumes in 30 min."),
            TrayController.FormatAutoSuspendText(rule, Windows(), new ProcessControl.Result(1, 1, false), 30));
        Assert.Equal(
            ("HandBrake suspended", "5m: 1 GB, 15m: 2 GB, 30m: 3 GB, 24h: 24 GB. 1h limit: 5 GB. Resume manually."),
            TrayController.FormatAutoSuspendText(rule, Windows(), new ProcessControl.Result(1, 1, false), 0));
        Assert.Equal(
            ("Could not suspend HandBrake", "Write limit exceeded; access denied. Elevation may be required."),
            TrayController.FormatAutoSuspendText(rule, Windows(), new ProcessControl.Result(1, 0, true), 30));
        Assert.Equal(
            ("HandBrake not suspended", "Write limit exceeded, but the process had exited."),
            TrayController.FormatAutoSuspendText(rule, Windows(), new ProcessControl.Result(0, 0, false), 30));
    }

    [Fact]
    public void ResumeText_IsCompactForBothOutcomes()
    {
        Assert.Equal(
            ("HandBrake resumed", "Suspension expired; process resumed automatically."),
            TrayController.FormatResumeText("HandBrake", new ProcessControl.Result(1, 1, false)));
        Assert.Equal(
            ("HandBrake no longer suspended", "Suspension expired; process exited or was already resumed."),
            TrayController.FormatResumeText("HandBrake", new ProcessControl.Result(0, 0, false)));
    }

    private static AutoSuspendRule Rule() => new()
    {
        ProcessName = "HandBrake",
        ThresholdGbPerHour = 5,
        Enabled = true,
    };

    private static TrayController.WriteWindowStats Windows() => new(
        (long)ByteFormat.GiB,
        (long)(2 * ByteFormat.GiB),
        (long)(3 * ByteFormat.GiB),
        (long)(24 * ByteFormat.GiB));
}