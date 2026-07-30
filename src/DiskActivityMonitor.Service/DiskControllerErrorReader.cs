using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using DiskActivityMonitor.Core.Models;

namespace DiskActivityMonitor.Service;

internal readonly record struct DiskControllerEvent(DateTime? TimestampUtc, string? DevicePath);

internal interface IDiskControllerErrorReader
{
    IReadOnlyList<DiskControllerErrorSummary> ReadSince(DateTime sinceUtc);
}

/// <summary>Reads Windows System log Disk event 11 records and aggregates them by physical disk.</summary>
internal sealed class DiskControllerErrorReader : IDiskControllerErrorReader
{
    private readonly Func<IEnumerable<DiskControllerEvent>> _eventSource;

    public DiskControllerErrorReader() : this(ReadSystemEvents) { }

    internal DiskControllerErrorReader(Func<IEnumerable<DiskControllerEvent>> eventSource)
        => _eventSource = eventSource;

    public IReadOnlyList<DiskControllerErrorSummary> ReadSince(DateTime sinceUtc)
        => Aggregate(_eventSource(), sinceUtc);

    internal static IReadOnlyList<DiskControllerErrorSummary> Aggregate(
        IEnumerable<DiskControllerEvent> events,
        DateTime sinceUtc)
    {
        sinceUtc = sinceUtc.Kind == DateTimeKind.Utc ? sinceUtc : sinceUtc.ToUniversalTime();
        var groups = new Dictionary<string, ErrorAccumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in events)
        {
            if (entry.TimestampUtc is not DateTime timestamp)
                continue;

            DateTime timestampUtc = timestamp.Kind == DateTimeKind.Utc ? timestamp : timestamp.ToUniversalTime();
            if (timestampUtc < sinceUtc)
                continue;
            if (!TryParseDiskId(entry.DevicePath, out string diskId))
                continue;

            if (!groups.TryGetValue(diskId, out var accumulator))
            {
                accumulator = new ErrorAccumulator(entry.DevicePath!, timestampUtc);
                groups.Add(diskId, accumulator);
            }
            accumulator.Add(entry.DevicePath!, timestampUtc);
        }

        return groups.Select(pair => pair.Value.ToSummary(pair.Key))
            .OrderBy(summary => int.Parse(summary.DiskId, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
    }

    internal static bool TryParseDiskId(string? devicePath, out string diskId)
    {
        var match = Regex.Match(devicePath ?? "", @"^\\Device\\Harddisk(?<disk>\d+)(?:\\DR\d+)?$", RegexOptions.IgnoreCase);
        diskId = match.Success ? match.Groups["disk"].Value : "";
        return match.Success;
    }

    private static IEnumerable<DiskControllerEvent> ReadSystemEvents()
    {
        var query = new EventLogQuery("System", PathType.LogName, "*[System[Provider[@Name='disk'] and EventID=11]]")
        {
            ReverseDirection = true,
        };
        using var reader = new EventLogReader(query);
        EventRecord? record;
        while ((record = reader.ReadEvent()) is not null)
        {
            using (record)
                yield return Project(record.TimeCreated, record.Properties[0].Value);
        }
    }

    internal static DiskControllerEvent Project(DateTime? timestamp, object? path)
        => new(timestamp?.ToUniversalTime(), path?.ToString());

    private sealed class ErrorAccumulator(string devicePath, DateTime timestampUtc)
    {
        private int _count;
        private DateTime _firstUtc = timestampUtc;
        private DateTime _latestUtc = timestampUtc;
        private string _latestDevicePath = devicePath;

        public void Add(string path, DateTime timestamp)
        {
            _count++;
            if (timestamp < _firstUtc) _firstUtc = timestamp;
            if (timestamp >= _latestUtc)
            {
                _latestUtc = timestamp;
                _latestDevicePath = path;
            }
        }

        public DiskControllerErrorSummary ToSummary(string diskId) => new()
        {
            DiskId = diskId,
            DevicePath = _latestDevicePath,
            Count = _count,
            FirstUtc = _firstUtc,
            LatestUtc = _latestUtc,
        };
    }
}
