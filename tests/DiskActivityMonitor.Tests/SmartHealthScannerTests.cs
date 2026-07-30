using DiskActivityMonitor.Core.Collection;
using DiskActivityMonitor.Core.Models;

namespace DiskActivityMonitor.Tests;

public class SmartHealthScannerTests
{
    private static readonly DateTime ScanTime = new(2026, 7, 28, 12, 34, 56, DateTimeKind.Utc);

    private static DiskInfo Disk(string id = "2") => new()
    {
        DiskId = id, InstanceName = $"{id} F:", FriendlyName = "Original model",
        SerialNumber = "ORIGINAL", Volumes = "F:", MediaType = DiskMediaType.Ssd,
    };

    private static DiskInfo Detected() => new()
    {
        DiskId = "2", InstanceName = "2 F:", FriendlyName = "Detected model",
        SerialNumber = " SERIAL ", Volumes = "F:", MediaType = DiskMediaType.Ssd,
    };

    [Fact]
    public void Scan_ProbeSuccess_ReportsProgressAndUsesInjectedClock()
    {
        var progress = new RecordingProgress();
        var result = SmartHealthScanner.Scan(Disk(), 4, progress, _ => CompleteEvidence(), new FixedTimeProvider(ScanTime));

        Assert.Equal([
            "Locating the physical disk in Windows Storage...",
            "Checking device status and firmware...",
            "Requesting direct NVMe SMART/Health telemetry...",
            "Preparing the health report...",
        ], progress.Messages);
        AssertCompleteResult(result);
    }

    [Fact]
    public void Scan_ProbeThrows_ReturnsUnavailableWithoutProgress()
    {
        var result = SmartHealthScanner.Scan(Disk("bad"), -2, probe: _ => throw new InvalidOperationException());
        Assert.Equal(SmartScanGrade.Unavailable, result.Grade);
        Assert.False(result.DiskPresent);
        Assert.Equal(0, result.ControllerErrorCount);
        Assert.Equal("Physical disk not available", result.Headline);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Scan_DefaultProbe_CoversDetectedAndInvalidDiskIds()
    {
        var live = SmartHealthScanner.Scan(Disk("0"));
        var invalid = SmartHealthScanner.Scan(Disk("not-a-number"));
        Assert.Equal("0", live.DiskId);
        Assert.Equal("not-a-number", invalid.DiskId);
    }

    [Fact]
    public void Assess_CompleteEvidence_PopulatesEveryResultProperty()
        => AssertCompleteResult(SmartHealthScanner.Assess(Disk(), 4, CompleteEvidence(), ScanTime));

    [Theory]
    [InlineData(false, false, "Not reported", "Not reported", "Unknown", "Unknown", "Not exposed to this process", SmartScanGrade.Unavailable)]
    [InlineData(true, false, "Healthy", "OK", "OK", "USB", "Not exposed by USB bridge", SmartScanGrade.Limited)]
    [InlineData(true, true, "Healthy", "OK", "OK", "SATA", "Windows reliability counters", SmartScanGrade.Healthy)]
    public void Assess_AccessModesAndGrades(bool present, bool reliability, string health, string operational,
        string status, string bus, string expectedAccess, SmartScanGrade expectedGrade)
    {
        var evidence = new SmartHealthScanner.Evidence(present ? Detected() : null, null, health, operational,
            status, bus, ReliabilityAvailable: reliability);
        var result = SmartHealthScanner.Assess(Disk(), 0, evidence, ScanTime);
        Assert.Equal(expectedAccess, result.SmartAccess);
        Assert.Equal(expectedGrade, result.Grade);
    }

    [Fact]
    public void Assess_DirectOnly_UsesDirectAccess()
    {
        var result = SmartHealthScanner.Assess(Disk(), 0,
            new SmartHealthScanner.Evidence(Detected(), Direct(), "Healthy", "OK", "OK"), ScanTime);
        Assert.Equal("Direct NVMe SMART/Health", result.SmartAccess);
    }

    [Fact]
    public void Assess_AllFindingsAndCriticalSummary()
    {
        var direct = new SmartLifetimeReader.SmartLifetime(10, 20, 7, 55, 91, 3, 100, 2, 4);
        var evidence = new SmartHealthScanner.Evidence(Detected(), direct, "Unhealthy", "Lost communication",
            "Error", "USB", "FW", 70, 11, 2, 12, 3, true);
        var result = SmartHealthScanner.Assess(Disk(), 1, evidence, ScanTime);

        Assert.Equal(SmartScanGrade.Critical, result.Grade);
        Assert.Equal("Drive health warning detected", result.Headline);
        Assert.Contains("Back up important data now", result.Summary);
        Assert.Contains(result.Findings, f => f.Contains("controller error."));
        Assert.Contains(result.Findings, f => f.Contains("55\u00B0C"));
        Assert.Contains(result.Findings, f => f.Contains("7%"));
        Assert.Contains(result.Findings, f => f.Contains("91%"));
        Assert.Contains(result.Findings, f => f.Contains("0x03"));
        Assert.Contains(result.Findings, f => f.Contains("4 media"));
        Assert.Contains(result.Findings, f => f.Contains("2 read, 3 write"));
        Assert.Contains(result.Findings, f => f.StartsWith("Recommended:"));
    }

    [Fact]
    public void Assess_AttentionUsesPluralAndLimitedFindings()
    {
        var usb = SmartHealthScanner.Assess(Disk(), 2,
            new SmartHealthScanner.Evidence(Detected(), BusType: "USB", WindowsHealth: "Healthy", OperationalStatus: "OK", DeviceStatus: "OK"), ScanTime);
        var sata = SmartHealthScanner.Assess(Disk(), 0,
            new SmartHealthScanner.Evidence(Detected(), BusType: "SATA", WindowsHealth: "Healthy", OperationalStatus: "OK", DeviceStatus: "OK"), ScanTime);
        Assert.Equal(SmartScanGrade.Attention, usb.Grade);
        Assert.Contains("2 controller errors", usb.Summary);
        Assert.Contains(usb.Findings, f => f.Contains("USB bridge"));
        Assert.Contains(sata.Findings, f => f.Contains("unavailable to this process"));
    }

    [Fact]
    public void Assess_HealthyAndLimitedSummaries()
    {
        var healthy = SmartHealthScanner.Assess(Disk(), 0,
            new SmartHealthScanner.Evidence(Detected(), Direct(), "Healthy", "OK", "OK", "NVMe"), ScanTime);
        var limited = SmartHealthScanner.Assess(Disk(), 0,
            new SmartHealthScanner.Evidence(Detected(), BusType: "USB", WindowsHealth: "Healthy", OperationalStatus: "OK", DeviceStatus: "OK"), ScanTime);
        Assert.Equal("No drive-level warning signs detected", healthy.Headline);
        Assert.Contains("point-in-time", healthy.Summary);
        Assert.Equal("Windows reports healthy; SMART access is limited", limited.Headline);
        Assert.Contains("USB path", limited.Summary);
    }

    [Fact]
    public void Assess_WarningHealthAndSingleControllerErrorCoverNestedPolicies()
    {
        var warning = SmartHealthScanner.Assess(Disk(), 0,
            new SmartHealthScanner.Evidence(Detected(), Direct(), "Warning", "Not reported", "OK"), ScanTime);
        var single = SmartHealthScanner.Assess(Disk(), 1,
            new SmartHealthScanner.Evidence(Detected(), Direct(), "Healthy", "OK", "OK"), ScanTime);

        Assert.Equal(SmartScanGrade.Attention, warning.Grade);
        Assert.Contains("1 controller error.", single.Summary);
    }

    [Theory]
    [MemberData(nameof(CriticalSignals))]
    public void DetermineGrade_EachCriticalSignalWins(ushort? health, ushort[] operations, string status,
        int? warning, long? media, long? read, long? write)
        => Assert.Equal(SmartScanGrade.Critical, SmartHealthScanner.DetermineGrade(
            true, true, health, operations, status, warning, media, read, write, 1));

    public static TheoryData<ushort?, ushort[], string, int?, long?, long?, long?> CriticalSignals => new()
    {
        { 2, [2], "OK", 0, 0, 0, 0 },
        { 0, [5], "OK", 0, 0, 0, 0 }, { 0, [6], "OK", 0, 0, 0, 0 },
        { 0, [7], "OK", 0, 0, 0, 0 }, { 0, [13], "OK", 0, 0, 0, 0 },
        { 0, [16], "OK", 0, 0, 0, 0 }, { 0, [2], "Error", 0, 0, 0, 0 },
        { 0, [2], "Pred Fail", 0, 0, 0, 0 }, { 0, [2], "OK", 1, 0, 0, 0 },
        { 0, [2], "OK", 0, 1, 0, 0 }, { 0, [2], "OK", 0, 0, 1, 0 },
        { 0, [2], "OK", 0, 0, 0, 1 },
    };

    [Theory]
    [InlineData((ushort)1, "OK", 0)]
    [InlineData((ushort)0, "Degraded", 0)]
    [InlineData((ushort)0, "OK", 1)]
    public void DetermineGrade_AttentionSignals(ushort health, string status, int errors)
        => Assert.Equal(SmartScanGrade.Attention, SmartHealthScanner.DetermineGrade(
            true, true, health, [2], status, 0, 0, 0, 0, errors));

    [Theory]
    [InlineData((ushort)3)]
    [InlineData((ushort)4)]
    [InlineData((ushort)12)]
    public void DetermineGrade_AttentionOperationalSignals(ushort operation)
        => Assert.Equal(SmartScanGrade.Attention, SmartHealthScanner.DetermineGrade(
            true, true, 0, [operation], "OK", 0, 0, 0, 0, 0));

    [Fact]
    public void DetermineGrade_CoversMissingHealthyAndLimited()
    {
        Assert.Equal(SmartScanGrade.Unavailable, SmartHealthScanner.DetermineGrade(false, true, 2, [6], "Error", 1, 1, 1, 1, 1));
        Assert.Equal(SmartScanGrade.Healthy, SmartHealthScanner.DetermineGrade(true, true, 0, null, "OK", 0, 0, 0, 0, 0));
        Assert.Equal(SmartScanGrade.Limited, SmartHealthScanner.DetermineGrade(true, false, 0, [], "OK", 0, 0, 0, 0, 0));
        Assert.Equal(SmartScanGrade.Healthy, SmartHealthScanner.DetermineGrade(true, true, null, [], "OK", 0, 0, 0, 0, 0));
    }

    [Theory]
    [InlineData(null, "Not reported")]
    [InlineData((ushort)0, "Healthy")]
    [InlineData((ushort)1, "Warning")]
    [InlineData((ushort)2, "Unhealthy")]
    [InlineData((ushort)5, "Unknown")]
    [InlineData((ushort)99, "Status 99")]
    public void DescribeHealthStatus_AllArms(ushort? value, string expected)
        => Assert.Equal(expected, SmartHealthScanner.DescribeHealthStatus(value));

    [Theory]
    [InlineData(null, "Unknown")]
    [InlineData((ushort)1, "SCSI")]
    [InlineData((ushort)2, "ATAPI")]
    [InlineData((ushort)3, "ATA")]
    [InlineData((ushort)4, "IEEE 1394")]
    [InlineData((ushort)6, "Fibre Channel")]
    [InlineData((ushort)7, "USB")]
    [InlineData((ushort)8, "RAID")]
    [InlineData((ushort)9, "iSCSI")]
    [InlineData((ushort)10, "SAS")]
    [InlineData((ushort)11, "SATA")]
    [InlineData((ushort)12, "SD")]
    [InlineData((ushort)13, "MMC")]
    [InlineData((ushort)14, "Virtual")]
    [InlineData((ushort)15, "File-backed virtual")]
    [InlineData((ushort)16, "Storage Spaces")]
    [InlineData((ushort)17, "NVMe")]
    [InlineData((ushort)18, "Storage-class memory")]
    [InlineData((ushort)19, "UFS")]
    [InlineData((ushort)99, "Bus 99")]
    public void DescribeBusType_AllArms(ushort? value, string expected)
        => Assert.Equal(expected, SmartHealthScanner.DescribeBusType(value));

    [Fact]
    public void DescribeOperationalStatus_AllArmsAndDuplicates()
    {
        Assert.Equal("Not reported", SmartHealthScanner.DescribeOperationalStatus([]));
        Assert.Equal("Unknown, Other, OK, Degraded, Stressed, Predictive failure, Error, Non-recoverable error, Starting, Stopping, Stopped, In service, No contact, Lost communication, Aborted, Dormant, Supporting entity in error, Completed, Power mode, Status 99",
            SmartHealthScanner.DescribeOperationalStatus([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 99, 2]));
    }

    private static SmartHealthScanner.Evidence CompleteEvidence() => new(
        Detected(), Direct(), "Healthy", "OK", "OK", "NVMe", " FW ", 60, 101, 0, 202, 0, true);

    private static SmartLifetimeReader.SmartLifetime Direct() => new(1_000, 2_000, 5, 42, 95, 0, 300, 4, 0);

    private static void AssertCompleteResult(SmartHealthScanResult result)
    {
        Assert.Equal("2", result.DiskId); Assert.Equal(@"\\.\PhysicalDrive2", result.DevicePath);
        Assert.Contains("F:", result.DisplayName); Assert.Equal("Detected model", result.Model);
        Assert.Equal("SERIAL", result.SerialNumber); Assert.Equal("FW", result.FirmwareVersion);
        Assert.Equal("NVMe", result.BusType); Assert.Equal("Healthy", result.WindowsHealth);
        Assert.Equal("OK", result.OperationalStatus); Assert.Equal("OK", result.DeviceStatus);
        Assert.Equal("Direct NVMe + Windows counters", result.SmartAccess); Assert.Equal(ScanTime, result.ScannedUtc);
        Assert.True(result.DiskPresent); Assert.True(result.SmartTelemetryAvailable); Assert.Equal(4, result.ControllerErrorCount);
        Assert.Equal(42, result.TemperatureC); Assert.Equal(60, result.TemperatureMaxC); Assert.Equal(5, result.WearPercent);
        Assert.Equal(95, result.AvailableSparePercent); Assert.Equal(0, result.CriticalWarning); Assert.Equal(300, result.PowerOnHours);
        Assert.Equal(4, result.UnsafeShutdowns); Assert.Equal(0, result.MediaErrors); Assert.Equal(101, result.ReadErrorsTotal);
        Assert.Equal(0, result.ReadErrorsUncorrected); Assert.Equal(202, result.WriteErrorsTotal); Assert.Equal(0, result.WriteErrorsUncorrected);
        Assert.Equal(1_000, result.LifetimeBytesWritten); Assert.Equal(2_000, result.LifetimeBytesRead);
        Assert.Equal(SmartScanGrade.Attention, result.Grade); Assert.Equal("Connection instability needs attention", result.Headline);
        Assert.Contains("4 controller errors", result.Summary); Assert.NotEmpty(result.Findings);
    }

    private sealed class RecordingProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];
        public void Report(string value) => Messages.Add(value);
    }

    private sealed class FixedTimeProvider(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc);
    }
}
