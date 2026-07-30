using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskActivityMonitor.Core.Ai;

/// <summary>
/// Web-search + AI API keys. Stored per-user (NOT in the machine-wide config.json, which standard
/// users can read), with environment-variable fallback so keys can be provided without a file.
/// </summary>
public sealed class AiSecrets
{
    /// <summary>Google Cloud API key with the Custom Search API enabled.</summary>
    public string? GoogleApiKey { get; set; }

    /// <summary>Programmable Search Engine id (cx), configured to search the entire web.</summary>
    public string? GoogleCseId { get; set; }

    /// <summary>serper.dev API key.</summary>
    public string? SerperApiKey { get; set; }
}

/// <summary>Loads and saves <see cref="AiSecrets"/> from a per-user file, falling back to env vars.</summary>
public static class AiSecretsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DiskActivityMonitor.AiSecrets.v1");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskActivityMonitor");

    /// <summary>Per-user secrets file path (%LOCALAPPDATA%\DiskActivityMonitor\ai-secrets.json).</summary>
    public static string FilePath => Path.Combine(Dir, "ai-secrets.json");

    /// <summary>Loads secrets from disk, then fills any blank value from the matching environment variable.</summary>
    public static AiSecrets Load() => LoadFromFile(FilePath, Environment.GetEnvironmentVariable);

    internal static AiSecrets LoadFromFile(
        string path,
        Func<string, string?> environmentLookup,
        Action<string, AiSecrets>? migrationWriter = null)
    {
        AiSecrets secrets = new();
        bool migratePlaintext = false;
        try
        {
            if (File.Exists(path))
            {
                var persisted = JsonSerializer.Deserialize<PersistedSecrets>(File.ReadAllText(path), Options) ?? new PersistedSecrets();
                secrets.GoogleApiKey = Unprotect(persisted.GoogleApiKeyProtected) ?? persisted.GoogleApiKey;
                secrets.GoogleCseId = persisted.GoogleCseId;
                secrets.SerperApiKey = Unprotect(persisted.SerperApiKeyProtected) ?? persisted.SerperApiKey;
                migratePlaintext = !string.IsNullOrWhiteSpace(persisted.GoogleApiKey) || !string.IsNullOrWhiteSpace(persisted.SerperApiKey);
            }
        }
        catch { /* corrupt file -> treat as empty */ }

        secrets.GoogleApiKey = Coalesce(secrets.GoogleApiKey, environmentLookup("GOOGLE_API_KEY"));
        secrets.GoogleCseId = Coalesce(secrets.GoogleCseId, environmentLookup("GOOGLE_CSE_ID"));
        secrets.SerperApiKey = Coalesce(secrets.SerperApiKey, environmentLookup("SERPER_API_KEY"));

        if (migratePlaintext)
        {
            try { (migrationWriter ?? SaveToFile)(path, secrets); }
            catch { /* loading the usable key is more important than migration */ }
        }
        return secrets;
    }

    /// <summary>Persists API keys encrypted for the current Windows user via DPAPI.</summary>
    public static void Save(AiSecrets secrets) => SaveToFile(FilePath, secrets);

    internal static void SaveToFile(string path, AiSecrets secrets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var persisted = new PersistedSecrets
        {
            GoogleApiKeyProtected = Protect(secrets.GoogleApiKey),
            GoogleCseId = Coalesce(secrets.GoogleCseId, null),
            SerperApiKeyProtected = Protect(secrets.SerperApiKey),
        };
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(persisted, Options));
        File.Move(temp, path, overwrite: true);
    }

    private static string? Coalesce(string? a, string? b) => string.IsNullOrWhiteSpace(a) ? b : a;

    private static string? Protect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue)) return null;
        try
        {
            byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch { return null; }
    }

    private sealed class PersistedSecrets
    {
        public string? GoogleApiKeyProtected { get; set; }
        public string? GoogleCseId { get; set; }
        public string? SerperApiKeyProtected { get; set; }

        // Legacy plaintext fields are read only for one-time migration and never written again.
        public string? GoogleApiKey { get; set; }
        public string? SerperApiKey { get; set; }
    }
}
