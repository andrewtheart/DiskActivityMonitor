using System.Text.Json;

namespace DiskActivityMonitor.Core.Configuration;

/// <summary>
/// Loads and saves <see cref="AppConfig"/> and notifies subscribers when the underlying
/// file changes on disk (so the collector picks up edits made from the tray app).
/// </summary>
public sealed class ConfigStore : IDisposable
{
    private readonly string _path;
    private FileSystemWatcher? _watcher;
    private readonly object _gate = new();
    private AppConfig _current;

    public event EventHandler<AppConfig>? Changed;

    public ConfigStore(string? path = null)
    {
        _path = path ?? Paths.ConfigPath;
        Paths.EnsureCreated();
        _current = LoadFromDisk();
    }

    public AppConfig Current
    {
        get { lock (_gate) return _current; }
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
        AppConfig reloaded;
        try { reloaded = LoadFromDisk(); }
        catch { return; }
        lock (_gate) { _current = reloaded; }
        Changed?.Invoke(this, reloaded);
    }

    public AppConfig Reload()
    {
        var cfg = LoadFromDisk();
        lock (_gate) { _current = cfg; }
        return cfg;
    }

    public void Save(AppConfig config)
    {
        lock (_gate) { _current = config; }
        var json = JsonSerializer.Serialize(config, AppConfig.SerializerOptions);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    private AppConfig LoadFromDisk()
    {
        if (!File.Exists(_path))
        {
            var def = new AppConfig();
            try { Save(def); } catch { /* best effort */ }
            return def;
        }

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppConfig>(json, AppConfig.SerializerOptions) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
