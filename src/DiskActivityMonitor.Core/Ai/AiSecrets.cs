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
    public static AiSecrets Load()
    {
        AiSecrets s = new();
        try
        {
            if (File.Exists(FilePath))
                s = JsonSerializer.Deserialize<AiSecrets>(File.ReadAllText(FilePath), Options) ?? new AiSecrets();
        }
        catch { /* corrupt file -> treat as empty */ }

        s.GoogleApiKey = Coalesce(s.GoogleApiKey, Environment.GetEnvironmentVariable("GOOGLE_API_KEY"));
        s.GoogleCseId = Coalesce(s.GoogleCseId, Environment.GetEnvironmentVariable("GOOGLE_CSE_ID"));
        s.SerperApiKey = Coalesce(s.SerperApiKey, Environment.GetEnvironmentVariable("SERPER_API_KEY"));
        return s;
    }

    /// <summary>Persists secrets to the per-user file.</summary>
    public static void Save(AiSecrets secrets)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(secrets, Options));
    }

    private static string? Coalesce(string? a, string? b) => string.IsNullOrWhiteSpace(a) ? b : a;
}
