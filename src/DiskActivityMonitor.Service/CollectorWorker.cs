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

    private readonly Dictionary<string, (long Read, long Write)> _diskAccum = new();
    private readonly Dictionary<string, (long Read, long Write)> _procAccum = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _currentMinuteUtc;
    private DateTime _lastPruneUtc = DateTime.MinValue;
    private DateTime _lastDiskScanUtc = DateTime.MinValue;
    private DateTime _lastCheckpointUtc = DateTime.MinValue;
    private List<DiskInfo> _disks = new();

    public CollectorWorker(ILogger<CollectorWorker> log, ConfigStore configStore, MonitorRepository repo)
    {
        _log = log;
        _configStore = configStore;
        _repo = repo;
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
        var etw = EtwProcessIoReader.TryStart(_log);
        if (etw is not null)
            _procReader = etw;
        _log.LogInformation("Per-process I/O attribution: {Mode}.", _procReader.Description);

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
        try { FlushBuckets(_configStore.Current); }
        catch (Exception ex) { _log.LogWarning(ex, "Error flushing buckets on shutdown."); }

        // Final checkpoint so the database is left fully consolidated on disk.
        try { _repo.Checkpoint(); }
        catch (Exception ex) { _log.LogWarning(ex, "Error checkpointing the WAL on shutdown."); }

        _diskSampler.Dispose();
        _procReader.Dispose();
        _log.LogInformation("Disk Activity Monitor collector stopped.");
    }

    private void SampleOnce(double elapsedSeconds, AppConfig cfg)
    {
        var nowUtc = DateTime.UtcNow;
        var minuteUtc = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, nowUtc.Minute, 0, DateTimeKind.Utc);

        if (_currentMinuteUtc == default)
            _currentMinuteUtc = minuteUtc;

        // Minute rolled over -> persist the completed bucket and run periodic maintenance.
        if (minuteUtc != _currentMinuteUtc)
        {
            FlushBuckets(cfg);
            _currentMinuteUtc = minuteUtc;
            RunPeriodicTasks(cfg, nowUtc);
        }

        // Accumulate this interval's bytes.
        foreach (var (diskId, bytes) in _diskSampler.SampleBytes(elapsedSeconds))
            Accumulate(_diskAccum, diskId, bytes.Read, bytes.Write);

        foreach (var (name, bytes) in _procReader.SampleDeltas())
            Accumulate(_procAccum, name, bytes.Read, bytes.Write);
    }

    private void FlushBuckets(AppConfig cfg)
    {
        if (_currentMinuteUtc == default)
            return;

        if (_diskAccum.Count > 0)
        {
            var samples = _diskAccum
                .Select(kv => new DiskSample { TimestampUtc = _currentMinuteUtc, DiskId = kv.Key, ReadBytes = kv.Value.Read, WriteBytes = kv.Value.Write })
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
                .Select(kv => new ProcessIoSample { TimestampUtc = _currentMinuteUtc, ProcessName = kv.Key, ReadBytes = kv.Value.Read, WriteBytes = kv.Value.Write })
                .ToList();
            _repo.AddProcessSamples(samples);
            _procAccum.Clear();
        }
    }

    private void RunPeriodicTasks(AppConfig cfg, DateTime nowUtc)
    {
        // Re-scan disks every five minutes (handles removable media / new drives).
        if (nowUtc - _lastDiskScanUtc > TimeSpan.FromMinutes(5))
            RescanDisks();

        // Evaluate alert rules each minute.
        try
        {
            foreach (var alert in _alertEngine.Evaluate(_disks, cfg, nowUtc))
                _log.LogWarning("ALERT [{Severity}] {Title} - {Message}", alert.Severity, alert.Title, alert.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error evaluating alert rules.");
        }

        // Prune old data hourly.
        if (nowUtc - _lastPruneUtc > TimeSpan.FromHours(1))
        {
            try
            {
                int removed = _repo.PruneOlderThan(nowUtc.AddDays(-Math.Max(1, cfg.RetentionDays)));
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
