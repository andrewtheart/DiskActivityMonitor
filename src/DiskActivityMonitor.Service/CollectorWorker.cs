using System.Diagnostics;
using DiskActivityMonitor.Core;
using DiskActivityMonitor.Core.Alerts;
using DiskActivityMonitor.Core.Collection;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiskActivityMonitor.Service;

/// <summary>
/// Long-running collector. Samples physical-disk and per-process I/O counters on a fixed
/// interval, aggregates them into one-minute buckets, persists them to SQLite, and evaluates
/// alert rules. Designed to run as a Windows Service or a foreground console process.
/// </summary>
public sealed class CollectorWorker : BackgroundService
{
    private readonly ILogger<CollectorWorker> _log;
    private readonly MonitorRepository _repo;
    private readonly ConfigStore _configStore;

    private readonly DiskPerformanceSampler _diskSampler = new();
    private IProcessIoReader _procReader = new ProcessIoReader();
    private readonly AlertEngine _alertEngine;
    private readonly IDiskControllerErrorReader _controllerErrorReader;
    private readonly Func<ILogger<CollectorWorker>, IProcessIoReader?> _etwFactory;

    private readonly Dictionary<string, (long Read, long Write)> _diskAccum = new();
    private readonly Dictionary<string, (long Read, long Write)> _procAccum = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Process, string Path), (long Read, long Write)> _fileAccum = new(FileTargetKeyComparer.Instance);

    private DateTime _currentMinuteUtc;
    private DateTime _lastPruneUtc = DateTime.MinValue;
    private DateTime _lastDiskScanUtc = DateTime.MinValue;
    private DateTime _lastCheckpointUtc = DateTime.MinValue;
    private List<DiskInfo> _disks = new();

    public CollectorWorker(ILogger<CollectorWorker> log, ConfigStore configStore, MonitorRepository repo)
        : this(log, configStore, repo, new DiskControllerErrorReader(), EtwProcessIoReader.TryStart)
    {
    }

    internal CollectorWorker(
        ILogger<CollectorWorker> log,
        ConfigStore configStore,
        MonitorRepository repo,
        IDiskControllerErrorReader controllerErrorReader,
        Func<ILogger<CollectorWorker>, IProcessIoReader?>? etwFactory = null)
    {
        _log = log;
        _configStore = configStore;
        _repo = repo;
        _controllerErrorReader = controllerErrorReader;
        _etwFactory = etwFactory ?? (_ => null);
        _alertEngine = new AlertEngine(repo);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _repo.EnsureSchema();
            _configStore.StartWatching();
            RescanDisks();
            _log.LogInformation("Disk Activity Monitor collector started. Tracking {Count} disk(s); data at {Db}.",
                _disks.Count, Paths.DatabasePath);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Fatal error during collector startup.");
            throw;
        }

        // Prefer accurate ETW-based per-process file-write attribution; fall back to the Win32
        // I/O counters (an upper bound that mixes pipe/device I/O) when ETW is unavailable.
        var etw = _etwFactory(_log);
        if (etw is not null)
            _procReader = etw;
        _log.LogInformation("Per-process I/O attribution: {Mode}.", _procReader.Description);

        // Surface already-active conditions (including historical Disk event 11 errors)
        // immediately after startup instead of waiting for the first minute rollover.
        RunStartupMonitoring(DateTime.UtcNow);

        var sw = Stopwatch.StartNew();
        while (!stoppingToken.IsCancellationRequested)
        {
            var cfg = _configStore.Current;
            var interval = TimeSpan.FromSeconds(Math.Clamp(cfg.SampleIntervalSeconds, 1, 60));
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                double elapsed = sw.Elapsed.TotalSeconds;
                sw.Restart();
                SampleOnce(elapsed, cfg);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error during sampling iteration.");
            }
        }

        // Flush whatever is pending so we do not lose the final partial minute.
        try { FlushBuckets(_configStore.Current, _currentMinuteUtc); }
        catch (Exception ex) { _log.LogWarning(ex, "Error flushing buckets on shutdown."); }

        // Final checkpoint so the database is left fully consolidated on disk.
        try { _repo.Checkpoint(); }
        catch (Exception ex) { _log.LogWarning(ex, "Error checkpointing the WAL on shutdown."); }

        _diskSampler.Dispose();
        _procReader.Dispose();
        _log.LogInformation("Disk Activity Monitor collector stopped.");
    }

    internal void SampleOnce(double elapsedSeconds, AppConfig cfg)
    {
        var nowUtc = DateTime.UtcNow;
        var minuteUtc = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, nowUtc.Minute, 0, DateTimeKind.Utc);

        if (_currentMinuteUtc == default)
            _currentMinuteUtc = minuteUtc;

        // Minute rolled over -> persist the completed bucket and run periodic maintenance.
        if (minuteUtc != _currentMinuteUtc)
        {
            FlushBuckets(cfg, _currentMinuteUtc);
            _currentMinuteUtc = minuteUtc;
            RunPeriodicTasks(cfg, nowUtc);
        }

        // Accumulate this interval's bytes.
        var diskDeltas = _diskSampler.SampleBytes(elapsedSeconds);
        ProcessDiskDeltas(nowUtc, elapsedSeconds, diskDeltas, cfg);

        _procReader.ConfigureFileTargets(cfg.TrackFileTargets, cfg.FileTargetTrackingLimit);

        foreach (var (name, bytes) in _procReader.SampleDeltas())
            Accumulate(_procAccum, name, bytes.Read, bytes.Write);

        foreach (var delta in _procReader.SampleFileTargetDeltas())
        {
            var key = (delta.ProcessName, delta.Path);
            _fileAccum.TryGetValue(key, out var cur);
            _fileAccum[key] = (cur.Read + delta.Read, cur.Write + delta.Write);
        }
    }

    internal void ProcessDiskDeltas(
        DateTime timestampUtc,
        double elapsedSeconds,
        IReadOnlyDictionary<string, (long Read, long Write)> diskDeltas,
        AppConfig cfg)
    {
        foreach (var (diskId, bytes) in diskDeltas)
            Accumulate(_diskAccum, diskId, bytes.Read, bytes.Write);
        RecordLiveDiskSamples(timestampUtc, elapsedSeconds, diskDeltas, cfg);
    }

    internal void FlushBuckets(AppConfig cfg, DateTime minuteUtc)
    {
        if (minuteUtc == default)
            return;

        RecordCollectorHeartbeat(minuteUtc);

        if (_diskAccum.Count > 0)
        {
            var samples = _diskAccum
                .Select(kv => new DiskSample { TimestampUtc = minuteUtc, DiskId = kv.Key, ReadBytes = kv.Value.Read, WriteBytes = kv.Value.Write })
                .ToList();
            _repo.AddDiskSamples(samples);
            _diskAccum.Clear();
        }

        if (_procAccum.Count > 0)
        {
            // Suppress trivial writers to keep the per-process table compact.
            long minBytes = (long)(Math.Max(0, cfg.ProcessMinMbPerMinute) * ByteFormat.MiB);
            var samples = _procAccum
                .Where(kv => kv.Value.Write >= minBytes || kv.Value.Read >= minBytes)
                .Select(kv => new ProcessIoSample { TimestampUtc = minuteUtc, ProcessName = kv.Key, ReadBytes = kv.Value.Read, WriteBytes = kv.Value.Write })
                .ToList();
            _repo.AddProcessSamples(samples);
            _procAccum.Clear();
        }

        if (_fileAccum.Count > 0)
        {
            var fileSamples = SelectFileTargets(_fileAccum, minuteUtc, cfg);
            if (fileSamples.Count > 0)
                _repo.AddProcessFileSamples(fileSamples);
            _fileAccum.Clear();
        }
    }

    /// <summary>
    /// Keeps the busiest files per process for the minute and folds everything else into a single
    /// aggregate row, so the per-file table stays bounded without losing bytes.
    /// </summary>
    internal static List<ProcessFileIoSample> SelectFileTargets(
        IReadOnlyDictionary<(string Process, string Path), (long Read, long Write)> accumulated,
        DateTime minuteUtc,
        AppConfig cfg)
    {
        long minBytes = (long)(Math.Max(0, cfg.FileTargetMinKbPerMinute) * 1024);
        int perProcess = Math.Max(1, cfg.FileTargetsPerProcessPerMinute);
        var samples = new List<ProcessFileIoSample>();

        foreach (var group in accumulated.GroupBy(kv => kv.Key.Process, StringComparer.OrdinalIgnoreCase))
        {
            var listed = group
                .Where(kv => kv.Value.Write >= minBytes || kv.Value.Read >= minBytes)
                .OrderByDescending(kv => kv.Value.Write)
                .ThenByDescending(kv => kv.Value.Read)
                .Take(perProcess)
                .ToList();

            foreach (var kv in listed)
            {
                samples.Add(new ProcessFileIoSample
                {
                    TimestampUtc = minuteUtc,
                    ProcessName = group.Key,
                    Path = kv.Key.Path,
                    Kind = FileTargetNormalizer.Classify(kv.Key.Path),
                    ReadBytes = kv.Value.Read,
                    WriteBytes = kv.Value.Write,
                });
            }

            long otherWrite = group.Sum(kv => kv.Value.Write) - listed.Sum(kv => kv.Value.Write);
            long otherRead = group.Sum(kv => kv.Value.Read) - listed.Sum(kv => kv.Value.Read);
            if (otherWrite > 0 || otherRead > 0)
            {
                samples.Add(new ProcessFileIoSample
                {
                    TimestampUtc = minuteUtc,
                    ProcessName = group.Key,
                    Path = FileTargetNormalizer.OtherFilesPath,
                    Kind = FileTargetKind.Other,
                    ReadBytes = otherRead,
                    WriteBytes = otherWrite,
                });
            }
        }

        return samples;
    }

    internal void RecordCollectorHeartbeat(DateTime minuteUtc)
        => _repo.AddCollectorHeartbeat(minuteUtc);

    internal void RecordLiveDiskSamples(
        DateTime timestampUtc,
        double elapsedSeconds,
        IReadOnlyDictionary<string, (long Read, long Write)> deltas,
        AppConfig cfg)
    {
        if (deltas.Count == 0 || !double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return;

        int elapsedMilliseconds = (int)Math.Clamp(Math.Round(elapsedSeconds * 1000), 1, int.MaxValue);
        var samples = deltas.Select(delta => new LiveDiskSample
        {
            TimestampUtc = timestampUtc,
            DiskId = delta.Key,
            ElapsedMilliseconds = elapsedMilliseconds,
            ReadBytes = Math.Max(0, delta.Value.Read),
            WriteBytes = Math.Max(0, delta.Value.Write),
        }).ToList();
        DateTime cutoffUtc = timestampUtc.AddMinutes(-Math.Clamp(cfg.LiveGraphRetentionMinutes, 1, 120));
        _repo.AddLiveDiskSamples(samples, cutoffUtc);
    }

    internal void RunPeriodicTasks(AppConfig cfg, DateTime nowUtc)
    {
        // Re-scan disks every five minutes (handles removable media / new drives).
        if (nowUtc - _lastDiskScanUtc > TimeSpan.FromMinutes(5))
            RescanDisks();

        // Evaluate alert rules each minute.
        try
        {
            foreach (var alert in _alertEngine.Evaluate(
                _disks,
                cfg,
                nowUtc,
                cfg.HighCoveragePercent))
                _log.LogWarning("ALERT [{Severity}] {Title} - {Message}", alert.Severity, alert.Title, alert.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error evaluating alert rules.");
        }

        // Windows logs Disk event 11 when the storage stack encounters a controller-path error.
        // Count these over a trailing window so repeated USB/SATA cable, power, enclosure, port,
        // or controller instability appears in the same alert pipeline as write/endurance rules.
        MonitorControllerErrors(cfg, nowUtc);

        // Prune old data hourly.
        if (nowUtc - _lastPruneUtc > TimeSpan.FromHours(1))
        {
            try
            {
                int removed = _repo.PruneOlderThan(nowUtc.AddDays(-Math.Max(1, cfg.RetentionDays)));
                removed += _repo.PruneFileTargetsOlderThan(
                    nowUtc.AddDays(-Math.Clamp(cfg.FileTargetRetentionDays, 1, Math.Max(1, cfg.RetentionDays))));
                if (removed > 0)
                    _log.LogInformation("Pruned {Count} expired rows (retention {Days}d).", removed, cfg.RetentionDays);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Error pruning old data.");
            }
            _lastPruneUtc = nowUtc;
        }

        // Checkpoint the WAL every few minutes to keep the -wal file small (connection
        // pooling prevents the automatic checkpoint-on-close from ever truncating it).
        if (nowUtc - _lastCheckpointUtc > TimeSpan.FromMinutes(5))
        {
            try { _repo.Checkpoint(); }
            catch (Exception ex) { _log.LogWarning(ex, "Error checkpointing the WAL."); }
            _lastCheckpointUtc = nowUtc;
        }
    }

    internal void MonitorControllerErrors(AppConfig cfg, DateTime nowUtc)
    {
        if (!cfg.EnableControllerErrorAlerts)
            return;

        try
        {
            int windowDays = Math.Clamp(cfg.ControllerErrorWindowDays, 1, 365);
            var errors = _controllerErrorReader.ReadSince(nowUtc.AddDays(-windowDays));
            foreach (var alert in _alertEngine.EvaluateControllerErrors(_disks, errors, cfg, nowUtc))
                _log.LogWarning("ALERT [{Severity}] {Title} - {Message}", alert.Severity, alert.Title, alert.Message);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error reading Disk event 11 controller errors from the Windows System log.");
        }
    }

    internal void RunStartupMonitoring(DateTime nowUtc)
        => MonitorControllerErrors(_configStore.Current, nowUtc);

    private void RescanDisks()
    {
        _lastDiskScanUtc = DateTime.UtcNow;
        try
        {
            _diskSampler.Initialize();
            _disks = DiskDetector.BuildDiskMap(_diskSampler.InstanceNames);
            if (_disks.Count > 0)
                _repo.UpsertDisks(_disks);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error rescanning disks.");
        }
    }

    private static void Accumulate(Dictionary<string, (long Read, long Write)> map, string key, long read, long write)
    {
        if (map.TryGetValue(key, out var cur))
            map[key] = (cur.Read + read, cur.Write + write);
        else
            map[key] = (read, write);
    }
}
