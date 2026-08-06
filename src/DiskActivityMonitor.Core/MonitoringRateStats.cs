namespace DiskActivityMonitor.Core;

public readonly record struct MonitoringRateStats(
    double TotalBytes,
    int MonitoredMinutes,
    int RequestedMinutes,
    double MonitoredBytesPerHour,
    double CalendarBytesPerHour,
    double CoveragePercent,
    bool HasHighCoverage)
{
    public static MonitoringRateStats Compute(
        double totalBytes,
        int monitoredMinutes,
        int requestedMinutes,
        double highCoveragePercent)
    {
        int requested = Math.Max(0, requestedMinutes);
        int monitored = Math.Clamp(monitoredMinutes, 0, requested);
        double coverage = requested == 0 ? 0 : monitored * 100.0 / requested;
        double monitoredRate = monitored == 0 ? 0 : totalBytes / monitored * 60.0;
        double calendarRate = requested == 0 ? 0 : totalBytes / requested * 60.0;
        double threshold = Math.Clamp(highCoveragePercent, 1, 100);
        return new MonitoringRateStats(
            totalBytes,
            monitored,
            requested,
            monitoredRate,
            calendarRate,
            coverage,
            requested > 0 && coverage >= threshold);
    }
}