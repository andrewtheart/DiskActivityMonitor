using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace DiskActivityMonitor.Core.Ai;

/// <summary>A pluggable web search backend used by the TBW lookup.</summary>
public interface IWebSearchProvider
{
    /// <summary>Display name of the backend.</summary>
    string Name { get; }

    /// <summary>True when the provider has the credentials it needs to run.</summary>
    bool IsConfigured { get; }

    /// <summary>Runs a search and returns up to <paramref name="count"/> hits.</summary>
    Task<IReadOnlyList<WebSearchHit>> SearchAsync(string query, int count, CancellationToken ct);
}

/// <summary>Creates the configured web search provider.</summary>
public static class WebSearchProviderFactory
{
    /// <summary>Returns the provider selected by <paramref name="providerName"/> ("google" or "serper").</summary>
    public static IWebSearchProvider Create(string? providerName, AiSecrets secrets, HttpClient http) =>
        (providerName ?? "google").Trim().ToLowerInvariant() switch
        {
            "serper" => new SerperSearchProvider(secrets, http),
            _ => new GoogleCseSearchProvider(secrets, http),
        };
}

/// <summary>Google Programmable Search via the official Custom Search JSON API (100 queries/day free).</summary>
public sealed class GoogleCseSearchProvider(AiSecrets secrets, HttpClient http) : IWebSearchProvider
{
    public string Name => "Google Custom Search";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(secrets.GoogleApiKey) && !string.IsNullOrWhiteSpace(secrets.GoogleCseId);

    public async Task<IReadOnlyList<WebSearchHit>> SearchAsync(string query, int count, CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("Google Custom Search key/engine id not configured.");
        int num = Math.Clamp(count, 1, 10);
        string uri = "https://www.googleapis.com/customsearch/v1?key=" + Uri.EscapeDataString(secrets.GoogleApiKey!) +
                     "&cx=" + Uri.EscapeDataString(secrets.GoogleCseId!) +
                     "&q=" + Uri.EscapeDataString(query) + "&num=" + num;

        using var resp = await http.GetAsync(uri, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        var hits = new List<WebSearchHit>();
        if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in items.EnumerateArray())
            {
                hits.Add(new WebSearchHit(
                    GetString(it, "title"),
                    GetString(it, "link"),
                    GetString(it, "snippet")));
                if (hits.Count >= num) break;
            }
        }
        return hits;
    }

    private static string GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
}

/// <summary>serper.dev Google Search API (single API key).</summary>
public sealed class SerperSearchProvider(AiSecrets secrets, HttpClient http) : IWebSearchProvider
{
    public string Name => "Serper.dev";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(secrets.SerperApiKey);

    public async Task<IReadOnlyList<WebSearchHit>> SearchAsync(string query, int count, CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("Serper.dev API key not configured.");
        int num = Math.Clamp(count, 1, 10);

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://google.serper.dev/search");
        req.Headers.Add("X-API-KEY", secrets.SerperApiKey);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { q = query, num }), Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        var hits = new List<WebSearchHit>();
        if (doc.RootElement.TryGetProperty("organic", out var organic) && organic.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in organic.EnumerateArray())
            {
                hits.Add(new WebSearchHit(
                    GetString(it, "title"),
                    GetString(it, "link"),
                    GetString(it, "snippet")));
                if (hits.Count >= num) break;
            }
        }
        return hits;
    }

    private static string GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
}
