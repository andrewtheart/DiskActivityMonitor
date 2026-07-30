using DiskActivityMonitor.Core.Models;

namespace DiskActivityMonitor.Core.Collection;

public enum SmartScanGrade
{
    Healthy,
    Attention,
    Critical,
    Limited,
    Unavailable,
}

/// <summary>Read-only, point-in-time drive health report used by the tray's live SMART scan UI.</summary>
public sealed class SmartHealthScanResult
{
    public required string DiskId { get; init; }
    public required string DevicePath { get; init; }
    public required string DisplayName { get; init; }
    public string Model { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    public string FirmwareVersion { get; init; } = "";
    public string BusType { get; init; } = "Unknown";
    public string WindowsHealth { get; init; } = "Unknown";
    public string OperationalStatus { get; init; } = "Unknown";
    public string DeviceStatus { get; init; } = "Unknown";
    public string SmartAccess { get; init; } = "Not available";
    public DateTime ScannedUtc { get; init; }
    public bool DiskPresent { get; init; }
    public bool SmartTelemetryAvailable { get; init; }
    public int ControllerErrorCount { get; init; }
    public int? TemperatureC { get; init; }
    public int? TemperatureMaxC { get; init; }
    public int? WearPercent { get; init; }
    public int? AvailableSparePercent { get; init; }
    public int? CriticalWarning { get; init; }
    public long? PowerOnHours { get; init; }
    public long? UnsafeShutdowns { get; init; }
    public long? MediaErrors { get; init; }
    public long? ReadErrorsTotal { get; init; }
    public long? ReadErrorsUncorrected { get; init; }
    public long? WriteErrorsTotal { get; init; }
    public long? WriteErrorsUncorrected { get; init; }
    public long? LifetimeBytesWritten { get; init; }
    public long? LifetimeBytesRead { get; init; }
    public SmartScanGrade Grade { get; init; }
    public required string Headline { get; init; }
    public required string Summary { get; init; }
    public IReadOnlyList<string> Findings { get; init; } = [];
}

/// <summary>
/// Performs a safe, read-only health scan using MSFT_PhysicalDisk, its reliability counters,
/// Win32_DiskDrive, and the direct NVMe SMART/Health log. It deliberately never sends an ATA/NVMe
/// self-test command and never enables ATA pass-through for USB/removable disks.
/// </summary>
public static class SmartHealthScanner
{
    public sealed record Evidence(
        DiskInfo? DetectedDisk = null,
        SmartLifetimeReader.SmartLifetime? Direct = null,
        string WindowsHealth = "Not reported",
        string OperationalStatus = "Not reported",
        string DeviceStatus = "Unknown",
        string BusType = "Unknown",
        string FirmwareVersion = "",
        int? TemperatureMaxC = null,
        long? ReadErrorsTotal = null,
        long? ReadErrorsUncorrected = null,
        long? WriteErrorsTotal = null,
        long? WriteErrorsUncorrected = null,
        bool ReliabilityAvailable = false);

    public static SmartHealthScanResult Scan(
        DiskInfo disk,
        int controllerErrorCount = 0,
        IProgress<string>? progress = null,
        Func<DiskInfo, Evidence>? probe = null,
        TimeProvider? timeProvider = null)
    {
        progress?.Report("Locating the physical disk in Windows Storage...");
        Evidence evidence;
        try
        {
            evidence = (probe ?? ProbeWindows)(disk);
        }
        catch
        {
            evidence = new Evidence();
        }
        progress?.Report("Checking device status and firmware...");
        progress?.Report("Requesting direct NVMe SMART/Health telemetry...");
        var result = Assess(disk, controllerErrorCount, evidence, (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime);
        progress?.Report("Preparing the health report...");
        return result;
    }

    public static SmartHealthScanResult Assess(DiskInfo disk, int controllerErrorCount, Evidence evidence, DateTime scannedUtc)
    {
        bool present = evidence.DetectedDisk is not null;
        var direct = evidence.Direct;
        bool directAvailable = direct is not null;
        bool smartAvailable = evidence.ReliabilityAvailable || directAvailable;
        var detected = evidence.DetectedDisk ?? disk;
        var grade = DetermineGrade(present, smartAvailable,
            evidence.WindowsHealth == "Unhealthy" ? (ushort)2 : evidence.WindowsHealth == "Warning" ? (ushort)1 : (ushort?)0,
            evidence.OperationalStatus == "OK" ? [2] : [], evidence.DeviceStatus,
            direct?.CriticalWarning, direct?.MediaErrors, evidence.ReadErrorsUncorrected,
            evidence.WriteErrorsUncorrected, controllerErrorCount);
        string smartAccess = directAvailable && evidence.ReliabilityAvailable ? "Direct NVMe + Windows counters"
            : directAvailable ? "Direct NVMe SMART/Health"
            : evidence.ReliabilityAvailable ? "Windows reliability counters"
            : evidence.BusType == "USB" ? "Not exposed by USB bridge" : "Not exposed to this process";
        var (headline, summary) = BuildSummary(grade, evidence.WindowsHealth, evidence.OperationalStatus,
            controllerErrorCount, evidence.BusType);
        var findings = BuildFindings(present, evidence.WindowsHealth, evidence.OperationalStatus,
            evidence.DeviceStatus, evidence.BusType, smartAvailable, controllerErrorCount,
            direct?.TemperatureC, direct?.PercentUsed, direct?.AvailableSparePercent,
            direct?.CriticalWarning, direct?.MediaErrors, evidence.ReadErrorsUncorrected,
            evidence.WriteErrorsUncorrected);
        return new SmartHealthScanResult
        {
            DiskId = disk.DiskId, DevicePath = $@"\\.\PhysicalDrive{disk.DiskId}", DisplayName = disk.DisplayName,
            Model = detected.FriendlyName, SerialNumber = detected.SerialNumber.Trim(), FirmwareVersion = evidence.FirmwareVersion.Trim(),
            BusType = evidence.BusType, WindowsHealth = evidence.WindowsHealth, OperationalStatus = evidence.OperationalStatus,
            DeviceStatus = evidence.DeviceStatus, SmartAccess = smartAccess, ScannedUtc = scannedUtc,
            DiskPresent = present, SmartTelemetryAvailable = smartAvailable, ControllerErrorCount = Math.Max(0, controllerErrorCount),
            TemperatureC = direct?.TemperatureC, TemperatureMaxC = evidence.TemperatureMaxC, WearPercent = direct?.PercentUsed,
            AvailableSparePercent = direct?.AvailableSparePercent, CriticalWarning = direct?.CriticalWarning,
            PowerOnHours = direct?.PowerOnHours, UnsafeShutdowns = direct?.UnsafeShutdowns, MediaErrors = direct?.MediaErrors,
            ReadErrorsTotal = evidence.ReadErrorsTotal, ReadErrorsUncorrected = evidence.ReadErrorsUncorrected,
            WriteErrorsTotal = evidence.WriteErrorsTotal, WriteErrorsUncorrected = evidence.WriteErrorsUncorrected,
            LifetimeBytesWritten = direct?.BytesWritten, LifetimeBytesRead = direct?.BytesRead,
            Grade = grade, Headline = headline, Summary = summary, Findings = findings,
        };
    }

    private static Evidence ProbeWindows(DiskInfo disk)
    {
        var detected = DiskDetector.BuildDiskMap([disk.InstanceName])
            .FirstOrDefault(candidate => candidate.DiskId == disk.DiskId);
        SmartLifetimeReader.SmartLifetime? direct = int.TryParse(disk.DiskId, out int number)
            ? SmartLifetimeReader.Read(number, allowAtaPassthrough: false) : null;
        return new Evidence(detected, direct,
            detected is null ? "Not reported" : "Healthy",
            detected is null ? "Not reported" : "OK");
    }

    public static SmartScanGrade DetermineGrade(
        bool diskPresent,
        bool smartTelemetryAvailable,
        ushort? healthStatus,
        IReadOnlyCollection<ushort>? operationalStatuses,
        string? win32Status,
        int? criticalWarning,
        long? mediaErrors,
        long? readErrorsUncorrected,
        long? writeErrorsUncorrected,
        int controllerErrorCount)
    {
        if (!diskPresent) return SmartScanGrade.Unavailable;

        var operational = operationalStatuses ?? [];
        bool critical = healthStatus == 2 ||
                        operational.Any(s => s is 5 or 6 or 7 or 13 or 16) ||
                        string.Equals(win32Status, "Error", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(win32Status, "Pred Fail", StringComparison.OrdinalIgnoreCase) ||
                        criticalWarning > 0 || mediaErrors > 0 ||
                        readErrorsUncorrected > 0 || writeErrorsUncorrected > 0;
        if (critical) return SmartScanGrade.Critical;

        bool attention = healthStatus == 1 || operational.Any(s => s is 3 or 4 or 12) ||
                         string.Equals(win32Status, "Degraded", StringComparison.OrdinalIgnoreCase) ||
                         controllerErrorCount > 0;
        if (attention) return SmartScanGrade.Attention;

        return smartTelemetryAvailable ? SmartScanGrade.Healthy : SmartScanGrade.Limited;
    }

    public static string DescribeHealthStatus(ushort? status) => status switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        5 => "Unknown",
        null => "Not reported",
        _ => $"Status {status}",
    };

    public static string DescribeBusType(ushort? busType) => busType switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 6 => "Fibre Channel",
        7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD",
        13 => "MMC", 14 => "Virtual", 15 => "File-backed virtual", 16 => "Storage Spaces",
        17 => "NVMe", 18 => "Storage-class memory", 19 => "UFS", null => "Unknown", _ => $"Bus {busType}",
    };

    public static string DescribeOperationalStatus(IEnumerable<ushort> statuses)
    {
        var text = statuses.Select(s => s switch
        {
            0 => "Unknown", 1 => "Other", 2 => "OK", 3 => "Degraded", 4 => "Stressed",
            5 => "Predictive failure", 6 => "Error", 7 => "Non-recoverable error", 8 => "Starting",
            9 => "Stopping", 10 => "Stopped", 11 => "In service", 12 => "No contact",
            13 => "Lost communication", 14 => "Aborted", 15 => "Dormant",
            16 => "Supporting entity in error", 17 => "Completed", 18 => "Power mode",
            _ => $"Status {s}",
        }).Distinct().ToList();
        return text.Count == 0 ? "Not reported" : string.Join(", ", text);
    }

    private static (string Headline, string Summary) BuildSummary(
        SmartScanGrade grade,
        string health,
        string operational,
        int controllerErrorCount,
        string busType) => grade switch
    {
        SmartScanGrade.Critical => (
            "Drive health warning detected",
            $"The live scan found a SMART or Windows storage fault. Windows health is {health}; operational status is {operational}. Back up important data now."),
        SmartScanGrade.Attention => (
            "Connection instability needs attention",
            $"Windows currently reports {health}, but this alert records {controllerErrorCount} controller error{(controllerErrorCount == 1 ? "" : "s")}. SMART cannot rule out cable, port, power, or enclosure faults."),
        SmartScanGrade.Healthy => (
            "No drive-level warning signs detected",
            "Live SMART telemetry and Windows storage health report no current drive-level warning. Continue monitoring because a point-in-time scan cannot prove a connection is stable."),
        SmartScanGrade.Limited => (
            "Windows reports healthy; SMART access is limited",
            $"Windows reports {health}, but detailed SMART telemetry is not exposed over this {busType} path. This is not a clean bill of health."),
        _ => ("Physical disk not available", "Windows could not locate this physical disk for a live scan."),
    };

    private static IReadOnlyList<string> BuildFindings(
        bool present,
        string health,
        string operational,
        string win32Status,
        string busType,
        bool smartAvailable,
        int controllerErrorCount,
        int? temperature,
        int? wear,
        int? spare,
        int? criticalWarning,
        long? mediaErrors,
        long? readErrorsUncorrected,
        long? writeErrorsUncorrected)
    {
        var findings = new List<string>();
        if (!present) return findings;

        findings.Add($"Windows storage health: {health}; operational status: {operational}; device status: {win32Status}.");
        if (controllerErrorCount > 0)
            findings.Add($"Alert context: {controllerErrorCount} Disk event 11 controller error{(controllerErrorCount == 1 ? "" : "s")}. These occur in the connection/controller path and may not change SMART health.");
        if (!smartAvailable)
            findings.Add(busType == "USB"
                ? "The USB bridge did not expose detailed SMART telemetry. Try the manufacturer's diagnostic tool or connect the drive directly if supported."
                : "Detailed SMART telemetry was unavailable to this process or storage controller.");
        if (temperature is int temp) findings.Add($"Current drive temperature: {temp}\u00B0C.");
        if (wear is int wearValue) findings.Add($"Reported endurance used: {wearValue}%.");
        if (spare is int spareValue) findings.Add($"NVMe available spare: {spareValue}%.");
        if (criticalWarning > 0) findings.Add($"NVMe critical-warning flags are set (0x{criticalWarning:X2}).");
        if (mediaErrors > 0) findings.Add($"NVMe reports {mediaErrors:N0} media/data-integrity error(s).");
        if (readErrorsUncorrected > 0 || writeErrorsUncorrected > 0)
            findings.Add($"Uncorrected errors reported: {readErrorsUncorrected ?? 0:N0} read, {writeErrorsUncorrected ?? 0:N0} write.");
        if (controllerErrorCount > 0)
            findings.Add("Recommended: back up important data, reseat or replace the cable, try another port and power source, and test the enclosure/controller.");
        return findings;
    }

}