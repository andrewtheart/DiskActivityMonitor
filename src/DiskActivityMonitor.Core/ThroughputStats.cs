namespace DiskActivityMonitor.Core;

/// <summary>
/// Average, median and peak disk throughput (decimal MB/s) computed from per-minute I/O totals over
/// a period. Minutes with no recorded activity are treated as zero-activity minutes so the figures
/// reflect the whole period, not just the busy minutes.
/// </summary>
public readonly record struct ThroughputStats(double AverageMbps, double MedianMbps, double PeakMbps)
{
    private const double BytesPerMinuteToMbps = 1.0 / 60.0 / 1_000_000.0;

    /// <summary>
    /// Computes throughput statistics from the recorded minutes' total byte counts.
    /// <paramref name="totalMinutes"/> is the number of minutes in the whole period; any minutes not
    /// present in <paramref name="perMinuteBytes"/> are counted as zero-activity minutes.
    /// </summary>
    public static ThroughputStats Compute(IReadOnlyList<long> perMinuteBytes, int totalMinutes)
    {
        int minutes = Math.Max(totalMinutes, perMinuteBytes.Count);
        if (minutes <= 0) return default;

        double sum = 0;
        long peak = 0;
        foreach (long b in perMinuteBytes)
        {
            sum += b;
            if (b > peak) peak = b;
        }

        // Median across the whole period: absent minutes sort first as zeros.
        long[] sorted = perMinuteBytes.OrderBy(b => b).ToArray();
        int zeros = minutes - sorted.Length;
        long At(int i) => i < zeros ? 0 : sorted[i - zeros];
        double medianBytes = minutes % 2 == 1
            ? At(minutes / 2)
            : (At(minutes / 2 - 1) + At(minutes / 2)) / 2.0;

        return new ThroughputStats(
            sum / minutes * BytesPerMinuteToMbps,
            medianBytes * BytesPerMinuteToMbps,
            peak * BytesPerMinuteToMbps);
    }
}
