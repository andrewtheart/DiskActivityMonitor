using System.Text.Json;

namespace DiskActivityMonitor.Core.Configuration;

public enum TbwLookupMethod
{
    FoundryLocal,
    SerperOnly,
}

/// <summary>Settings that control actions taken in the current interactive user's session.</summary>
public sealed class UserSettings
{
    public List<AutoSuspendRule> AutoSuspendRules { get; set; } = new();
    public bool EnableNotifications { get; set; } = true;

    /// <summary>
    /// How long a suspension lasts before the app resumes the process automatically. Zero means
    /// suspensions stay in place until they are resumed manually.
    /// </summary>
    public int DefaultSuspendMinutes { get; set; } = 30;

    public bool EnableTbwWebLookup { get; set; } = true;
    public bool SuppressTbwOnlineSetupPrompt { get; set; }
    public string WebSearchProvider { get; set; } = "serper";
    public TbwLookupMethod TbwLookupMethod { get; set; } = TbwLookupMethod.FoundryLocal;
    public string? TbwLookupModel { get; set; }
}

/// <summary>Persists action-bearing settings beneath the current user's LocalAppData directory.</summary>
public sealed class UserSettingsStore
{
    private const long MaximumFileSize = 1024 * 1024;
    private readonly string _path;
    private readonly object _gate = new();
    private UserSettings _current;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskActivityMonitor",
        "user-settings.json");

    public UserSettingsStore(string? path = null, string? legacyConfigPath = null)
    {
        bool useDefaultPath = path is null;
        _path = path ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (File.Exists(_path))
        {
            _current = LoadFromDisk();
            return;
        }

        string? migrationPath = legacyConfigPath ?? (useDefaultPath ? Paths.ConfigPath : null);
        if (migrationPath is not null
            && TryLoadLegacyPreferences(migrationPath, out var migrated)
            && migrated is not null)
        {
            _current = migrated;
            try { Save(migrated); } catch { /* migration is best effort */ }
        }
        else
        {
            _current = new UserSettings();
        }
    }

    public UserSettings Current
    {
        get { lock (_gate) return Clone(_current); }
    }

    public void Save(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            Persist(Clone(settings));
        }
    }

    public void Update(Action<UserSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            var next = Clone(_current);
            update(next);
            Persist(Clone(next));
        }
    }

    private void Persist(UserSettings snapshot)
    {
        string json = JsonSerializer.Serialize(snapshot, AppConfig.SerializerOptions);
        AtomicFile.WriteAllText(_path, json);
        _current = snapshot;
    }

    private UserSettings LoadFromDisk()
    {
        try
        {
            string json = AtomicFile.ReadAllText(_path, MaximumFileSize);
            return Clone(JsonSerializer.Deserialize<UserSettings>(json, AppConfig.SerializerOptions)
                ?? new UserSettings());
        }
        catch
        {
            return new UserSettings();
        }
    }

    private static bool TryLoadLegacyPreferences(string path, out UserSettings? settings)
    {
        settings = null;
        try
        {
            using var document = JsonDocument.Parse(AtomicFile.ReadAllText(path, MaximumFileSize));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var root = document.RootElement;
            var migrated = new UserSettings();
            bool found = false;
            found |= TryGetBoolean(root, "enableNotifications", value => migrated.EnableNotifications = value);
            found |= TryGetBoolean(root, "enableTbwWebLookup", value => migrated.EnableTbwWebLookup = value);
            found |= TryGetBoolean(root, "suppressTbwOnlineSetupPrompt", value => migrated.SuppressTbwOnlineSetupPrompt = value);
            found |= TryGetString(root, "webSearchProvider", value => migrated.WebSearchProvider = value);
            found |= TryGetString(root, "tbwLookupModel", value => migrated.TbwLookupModel = value);

            // Legacy rules were machine-wide and writable by every local user. Importing them
            // could make a victim's tray suspend processes, so they require manual re-creation.
            settings = found ? Clone(migrated) : null;
            return found;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetBoolean(JsonElement root, string name, Action<bool> assign)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        assign(value.GetBoolean());
        return true;
    }

    private static bool TryGetString(JsonElement root, string name, Action<string> assign)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return false;
        string text = value.GetString() ?? "";
        if (text.Length > 256)
            return false;
        assign(text);
        return true;
    }

    private static UserSettings Clone(UserSettings source) => new()
    {
        EnableNotifications = source.EnableNotifications,
        DefaultSuspendMinutes = source.DefaultSuspendMinutes < 0 ? 0 : source.DefaultSuspendMinutes,
        EnableTbwWebLookup = source.EnableTbwWebLookup,
        SuppressTbwOnlineSetupPrompt = source.SuppressTbwOnlineSetupPrompt,
        WebSearchProvider = string.IsNullOrWhiteSpace(source.WebSearchProvider) ? "serper" : source.WebSearchProvider,
        TbwLookupMethod = source.TbwLookupMethod,
        TbwLookupModel = source.TbwLookupModel,
        AutoSuspendRules = source.AutoSuspendRules?.Select(rule => new AutoSuspendRule
        {
            ProcessName = rule.ProcessName,
            ThresholdGbPerHour = rule.ThresholdGbPerHour,
            Mode = rule.Mode,
            Enabled = rule.Enabled,
            ExecutablePath = rule.ExecutablePath,
        }).ToList() ?? [],
    };
}