using System.Globalization;
using DiskActivityMonitor.Core.Models;

namespace DiskActivityMonitor.Core;

/// <summary>
/// Rolls hour-aligned UTC totals (as produced by the repository) into a fixed series of
/// local-time buckets - hour, day or week - suitable for charting trends. Empty buckets are
/// emitted with zero values so charts show real gaps in activity.
/// </summary>
public static class Trends
{
    public enum Bucket { Hour, Day, Week }

    public sealed record TotalWrittenPoint(DateTime TimestampUtc, long TotalBytes);

    /// <summary>
    /// Builds a cumulative total-written series from write totals whose timestamps mark the end
    /// of each bucket. The selected range boundaries are always represented so idle periods draw
    /// as a flat line instead of disappearing.
    /// </summary>
    public static IReadOnlyList<TotalWrittenPoint> BuildCumulative(
        IEnumerable<(DateTime BucketEndUtc, long WriteBytes)> writes,
        DateTime fromUtc,
        DateTime toUtc,
        long totalAtStart)
    {
        if (toUtc <= fromUtc)
            return [];

        long total = Math.Max(0, totalAtStart);
        var points = new List<TotalWrittenPoint> { new(fromUtc, total) };

        foreach (var (bucketEndUtc, writeBytes) in writes.OrderBy(item => item.BucketEndUtc))
        {
            if (bucketEndUtc <= fromUtc || bucketEndUtc > toUtc)
                continue;

            long increment = Math.Max(0, writeBytes);
            total = increment > long.MaxValue - total ? long.MaxValue : total + increment;

            var point = new TotalWrittenPoint(bucketEndUtc, total);
            if (points[^1].TimestampUtc == bucketEndUtc)
                points[^1] = point;
            else
                points.Add(point);
        }

        if (points[^1].TimestampUtc < toUtc)
            points.Add(new TotalWrittenPoint(toUtc, total));

        return points;
    }

    public static IReadOnlyList<TrendBucket> Build(
        IEnumerable<(DateTime HourStartUtc, long Read, long Write)> hourly,
        Bucket bucket,
        int count,
        DateTime nowLocal)
    {
        var anchor = AlignDown(nowLocal, bucket);
        var buckets = new List<TrendBucket>(count);
        for (int i = count - 1; i >= 0; i--)
            buckets.Add(new TrendBucket { BucketStartLocal = Step(anchor, bucket, -i) });

        // Index buckets by start for assignment.
        foreach (var (hourUtc, read, write) in hourly)
        {
            var local = DateTime.SpecifyKind(hourUtc, DateTimeKind.Utc).ToLocalTime();
            var start = AlignDown(local, bucket);
            // Find the matching bucket (series is short: 12-30 entries).
            for (int i = 0; i < buckets.Count; i++)
            {
                if (buckets[i].BucketStartLocal == start)
                {
                    buckets[i].ReadBytes += read;
                    buckets[i].WriteBytes += write;
                    break;
                }
            }
        }

        return buckets;
    }

    public static DateTime AlignDown(DateTime local, Bucket bucket) => bucket switch
    {
        Bucket.Hour => new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Local),
        Bucket.Day => local.Date,
        Bucket.Week => StartOfWeek(local.Date),
        _ => local,
    };

    public static DateTime Step(DateTime start, Bucket bucket, int steps) => bucket switch
    {
        Bucket.Hour => start.AddHours(steps),
        Bucket.Day => start.AddDays(steps),
        Bucket.Week => start.AddDays(7 * steps),
        _ => start,
    };

    private static DateTime StartOfWeek(DateTime date)
    {
        int diff = (7 + (int)date.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        return date.AddDays(-diff).Date;
    }

    public static string Label(DateTime start, Bucket bucket) => bucket switch
    {
        Bucket.Hour => start.ToString("h:00 tt", CultureInfo.InvariantCulture),
        Bucket.Day => start.ToString("MM/dd"),
        Bucket.Week => start.ToString("MMM dd"),
        _ => start.ToString(),
    };
}
