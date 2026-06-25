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
        Bucket.Hour => start.ToString("HH:00"),
        Bucket.Day => start.ToString("MM/dd"),
        Bucket.Week => start.ToString("MMM dd"),
        _ => start.ToString(),
    };
}
