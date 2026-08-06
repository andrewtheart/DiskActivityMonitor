using System.Text.Json;

namespace DiskActivityMonitor.Core.Configuration;

/// <summary>
/// Loads and saves <see cref="AppConfig"/> and notifies subscribers when the underlying
/// file changes on disk (so the collector picks up edits made from the tray app).
/// </summary>
public sealed class ConfigStore : IDisposable
{
    private const long MaximumFileSize = 1024 * 1024;
    private readonly string _path;
    private FileSystemWatcher? _watcher;
    private readonly object _gate = new();
    private AppConfig _current;

    public event EventHandler<AppConfig>? Changed;

    public ConfigStore(string? path = null)
    {
        _path = path ?? Paths.ConfigPath;
        Paths.EnsureCreated();
        _current = TryLoadFromDisk() ?? new AppConfig();
        if (!File.Exists(_path))
        {
            try { Save(_current); } catch { /* best effort */ }
        }
    }

    public AppConfig Current
    {
        get { lock (_gate) return Clone(_current); }
    }

    /// <summary>Begins watching the config file for external edits.</summary>
    public void StartWatching()
    {
        if (_watcher is not null) return;
        var dir = Path.GetDirectoryName(_path)!;
        var file = Path.GetFileName(_path);
        _watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Editors often fire several events; a tiny delay lets the write settle.
        try { Thread.Sleep(150); } catch { /* ignore */ }
        AppConfig? snapshot;
        lock (_gate)
        {
            var reloaded = TryLoadFromDisk();
            if (reloaded is null) return;
            _current = Clone(reloaded);
            snapshot = Clone(_current);
        }
        Changed?.Invoke(this, snapshot);
    }

    public AppConfig Reload()
    {
        lock (_gate)
        {
            var cfg = TryLoadFromDisk();
            if (cfg is not null)
                _current = Clone(cfg);
            return Clone(_current);
        }
    }

    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            Persist(Clone(config));
        }
    }

    public void Update(Action<AppConfig> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            var next = Clone(_current);
            update(next);
            Persist(Clone(next));
        }
    }

    private void Persist(AppConfig snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, AppConfig.SerializerOptions);
        AtomicFile.WriteAllText(_path, json);
        _current = snapshot;
    }

    private AppConfig? TryLoadFromDisk()
    {
        try
        {
            var json = AtomicFile.ReadAllText(_path, MaximumFileSize);
            var config = JsonSerializer.Deserialize<AppConfig>(json, AppConfig.SerializerOptions);
            if (config is not null)
                MigrateLegacyTbwDefault(json, config);
            return config is null ? null : Clone(config);
        }
        catch
        {
            return null;
        }
    }

    private static void MigrateLegacyTbwDefault(string json, AppConfig config)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("defaultSsdTbw", out var lower)
            && lower.ValueKind == JsonValueKind.Number
            && lower.TryGetDouble(out double value)
            && value == 750
            && !root.TryGetProperty("defaultSsdTbwUpper", out _))
        {
            config.DefaultSsdTbw = 150;
            config.DefaultSsdTbwUpper = 600;
        }
    }

    private static AppConfig Clone(AppConfig source) => new()
    {
        SampleIntervalSeconds = source.SampleIntervalSeconds,
        DashboardRefreshSeconds = source.DashboardRefreshSeconds,
        LiveGraphRetentionMinutes = Math.Clamp(source.LiveGraphRetentionMinutes, 1, 120),
        RetentionDays = source.RetentionDays,
        ProcessMinMbPerMinute = source.ProcessMinMbPerMinute,
        TrackFileTargets = source.TrackFileTargets,
        FileTargetsPerProcessPerMinute = source.FileTargetsPerProcessPerMinute,
        FileTargetMinKbPerMinute = source.FileTargetMinKbPerMinute,
        FileTargetRetentionDays = source.FileTargetRetentionDays,
        FileTargetTrackingLimit = source.FileTargetTrackingLimit,
        DatabaseSizeWarnGb = source.DatabaseSizeWarnGb,
        DatabaseSizeAlertCooldownHours = source.DatabaseSizeAlertCooldownHours,
        BinaryExtensions = source.BinaryExtensions,
        TailInitialLines = source.TailInitialLines,
        TailMaxLines = source.TailMaxLines,
        TailMaxReadKb = Math.Clamp(source.TailMaxReadKb, 64, 16384),
        TailMaxBufferKb = Math.Clamp(source.TailMaxBufferKb, 128, 32768),
        SsdWarnGbPerHour = source.SsdWarnGbPerHour,
        SsdWarnGbPerDay = source.SsdWarnGbPerDay,
        SsdCriticalGbPerDay = source.SsdCriticalGbPerDay,
        ProcessWarnGbPerHour = source.ProcessWarnGbPerHour,
        AllProcessesWarnGbPerHour = source.AllProcessesWarnGbPerHour,
        AlertCooldownMinutes = source.AlertCooldownMinutes,
        EnableControllerErrorAlerts = source.EnableControllerErrorAlerts,
        ControllerErrorWindowDays = source.ControllerErrorWindowDays,
        ControllerErrorWarnCount = source.ControllerErrorWarnCount,
        ControllerErrorCriticalCount = source.ControllerErrorCriticalCount,
        DiskTbwRatings = source.DiskTbwRatings is null ? new() : new(source.DiskTbwRatings),
        DefaultSsdTbw = source.DefaultSsdTbw,
        DefaultSsdTbwUpper = source.DefaultSsdTbwUpper,
        DiskTbwRatingsUpper = source.DiskTbwRatingsUpper is null ? new() : new(source.DiskTbwRatingsUpper),
        TbwProjectionWarnYears = source.TbwProjectionWarnYears,
        TbwProjectionCriticalYears = source.TbwProjectionCriticalYears,
        HighCoveragePercent = double.IsFinite(source.HighCoveragePercent)
            ? Math.Clamp(source.HighCoveragePercent, 1, 100)
            : 90,
        SsdWearWarnPercent = source.SsdWearWarnPercent,
    };

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
