using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskActivityMonitor.Core.Ai;

/// <summary>
/// Per-user, on-disk cache of TBW lookup results keyed by drive model, so we search the web at most
/// once per drive model (per the "once per disk if unknown, then cache" behaviour).
/// </summary>
public static class TbwLookupCache
{
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskActivityMonitor");

    private static string FilePath => Path.Combine(Dir, "tbw-lookup-cache.json");

    private static string Key(string model) => (model ?? "").Trim().ToLowerInvariant();

    /// <summary>Returns a cached result for the model, if present.</summary>
    public static bool TryGet(string model, out TbwLookupResult? result)
    {
        result = null;
        var key = Key(model);
        if (key.Length == 0) return false;
        lock (Gate)
        {
            var map = Load();
            return map.TryGetValue(key, out result) && result is not null;
        }
    }

    /// <summary>Stores (or replaces) the cached result for its model.</summary>
    public static void Put(TbwLookupResult result)
    {
        var key = Key(result.Model);
        if (key.Length == 0) return;
        lock (Gate)
        {
            var map = Load();
            map[key] = result;
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(map, Options));
            }
            catch { /* best-effort cache */ }
        }
    }

    /// <summary>Removes a cached result (e.g. to force a re-search).</summary>
    public static void Remove(string model)
    {
        var key = Key(model);
        lock (Gate)
        {
            var map = Load();
            if (map.Remove(key))
            {
                try { File.WriteAllText(FilePath, JsonSerializer.Serialize(map, Options)); } catch { /* ignore */ }
            }
        }
    }

    private static Dictionary<string, TbwLookupResult> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, TbwLookupResult>>(File.ReadAllText(FilePath), Options)
                       ?? new Dictionary<string, TbwLookupResult>();
        }
        catch { /* corrupt cache -> start fresh */ }
        return new Dictionary<string, TbwLookupResult>();
    }
}
