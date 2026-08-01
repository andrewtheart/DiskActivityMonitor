using DiskActivityMonitor.Core.Ai;

namespace DiskActivityMonitor.Tests;

public sealed class AiSecretsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dam_secrets_{Guid.NewGuid():N}");
    private string PathName => Path.Combine(_dir, "ai-secrets.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void SaveAndLoad_EncryptsKeysWithDpapiAndPreservesCseId()
    {
        const string google = "google-secret-key";
        const string serper = "synthetic-serper-key-for-dpapi-test-only";
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathName + ".tmp", "sentinel");
        AiSecretsStore.SaveToFile(PathName, new AiSecrets
        {
            GoogleApiKey = $" {google} ",
            GoogleCseId = " engine-id ",
            SerperApiKey = serper,
        });

        string json = File.ReadAllText(PathName);
        Assert.DoesNotContain(google, json);
        Assert.DoesNotContain(serper, json);
        Assert.Contains("googleApiKeyProtected", json);
        Assert.Contains("serperApiKeyProtected", json);

        var loaded = AiSecretsStore.LoadFromFile(PathName, _ => null);
        Assert.Equal(google, loaded.GoogleApiKey);
        Assert.Equal(" engine-id ", loaded.GoogleCseId);
        Assert.Equal(serper, loaded.SerperApiKey);
        Assert.Equal("sentinel", File.ReadAllText(PathName + ".tmp"));
    }

    [Fact]
    public void Load_LegacyPlaintext_MigratesFileToDpapi()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathName, """
            {"googleApiKey":"old-google","googleCseId":"cx","serperApiKey":"old-serper-key-1234567890"}
            """);

        var loaded = AiSecretsStore.LoadFromFile(PathName, _ => null);

        Assert.Equal("old-google", loaded.GoogleApiKey);
        Assert.Equal("old-serper-key-1234567890", loaded.SerperApiKey);
        string migrated = File.ReadAllText(PathName);
        Assert.DoesNotContain("old-google", migrated);
        Assert.DoesNotContain("old-serper-key", migrated);
    }

    [Fact]
    public void Load_CorruptProtectedValues_UsesEnvironmentFallback()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathName, """
            {"googleApiKeyProtected":"not-base64","serperApiKeyProtected":"not-base64"}
            """);
        var environment = new Dictionary<string, string?>
        {
            ["GOOGLE_API_KEY"] = "env-google",
            ["GOOGLE_CSE_ID"] = "env-cx",
            ["SERPER_API_KEY"] = "env-serper",
        };

        var loaded = AiSecretsStore.LoadFromFile(PathName, name => environment[name]);

        Assert.Equal("env-google", loaded.GoogleApiKey);
        Assert.Equal("env-cx", loaded.GoogleCseId);
        Assert.Equal("env-serper", loaded.SerperApiKey);
    }

    [Fact]
    public void Load_MissingOrCorruptFile_ReturnsEnvironmentOrEmpty()
    {
        var missing = AiSecretsStore.LoadFromFile(PathName, name => name == "SERPER_API_KEY" ? "fallback" : null);
        Assert.Equal("fallback", missing.SerperApiKey);

        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathName, "not json");
        var corrupt = AiSecretsStore.LoadFromFile(PathName, _ => null);
        Assert.Null(corrupt.GoogleApiKey);
        Assert.Null(corrupt.SerperApiKey);

        File.WriteAllText(PathName, "null");
        var nullJson = AiSecretsStore.LoadFromFile(PathName, _ => null);
        Assert.Null(nullJson.SerperApiKey);
    }

    [Fact]
    public void Save_BlankSecrets_OmitsProtectedValues()
    {
        AiSecretsStore.SaveToFile(PathName, new AiSecrets { GoogleApiKey = " ", SerperApiKey = null });
        string json = File.ReadAllText(PathName);
        Assert.DoesNotContain("Protected", json, StringComparison.OrdinalIgnoreCase);
        var loaded = AiSecretsStore.LoadFromFile(PathName, _ => null);
        Assert.Null(loaded.GoogleApiKey);
        Assert.Null(loaded.SerperApiKey);
    }

    [Fact]
    public void Load_LegacyMigrationFailure_StillReturnsKey()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathName, "{\"serperApiKey\":\"legacy-test-key-value-123456\"}");
        var loaded = AiSecretsStore.LoadFromFile(PathName, _ => null,
            (_, _) => throw new IOException("read-only"));
        Assert.Equal("legacy-test-key-value-123456", loaded.SerperApiKey);
    }
}
