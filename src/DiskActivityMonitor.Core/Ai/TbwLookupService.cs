using System.Text;
using System.Text.Json;

namespace DiskActivityMonitor.Core.Ai;

/// <summary>Whether a TBW web lookup can run on this machine right now, and what (if anything) is missing.</summary>
/// <param name="CanRun">True when a search backend + a usable local model are ready.</param>
/// <param name="Reason">Why it cannot run (shown to the user) when <paramref name="CanRun"/> is false.</param>
/// <param name="NeedsModelDownload">A search key + Foundry are present but no suitable model is cached.</param>
/// <param name="DownloadAlias">The model alias that would be downloaded on consent.</param>
/// <param name="HasUsableGpu">A discrete GPU with enough VRAM was detected.</param>
public sealed record TbwReadiness(bool CanRun, string? Reason, bool NeedsModelDownload, string? DownloadAlias, bool HasUsableGpu);

/// <summary>
/// Orchestrates the "search the web for this drive's TBW rating" feature: web search -> local model
/// extracts TBW claims per source -> aggregate into candidates with confidence scores by source
/// agreement. Uses Foundry Local (HTTP) for inference and Google/Serper for search.
/// </summary>
public sealed class TbwLookupService
{
    private readonly Configuration.AppConfig _config;
    private readonly HttpClient _http;
    private readonly Action<string>? _log;
    private readonly FoundryLocalClient _foundry;
    private readonly HardwareCapabilityDetector _detector = new();
    private AiSecrets _secrets;

    private string? _endpoint;
    private string? _resolvedModel;
    private HardwareCapabilityDetector.HardwareCapabilities _caps;

    public TbwLookupService(Configuration.AppConfig config, HttpClient http, Action<string>? log = null)
    {
        _config = config;
        _http = http;
        _log = log;
        _foundry = new FoundryLocalClient(http, log);
        _secrets = AiSecretsStore.Load();
    }

    /// <summary>Reloads API keys from the per-user secrets store / environment.</summary>
    public void ReloadSecrets() => _secrets = AiSecretsStore.Load();

    private IWebSearchProvider SearchProvider => WebSearchProviderFactory.Create(_config.WebSearchProvider, _secrets, _http);

    /// <summary>Checks whether a lookup can run and, if not, what is missing.</summary>
    public async Task<TbwReadiness> GetReadinessAsync(CancellationToken ct)
    {
        if (!_config.EnableTbwWebLookup)
            return new TbwReadiness(false, "Web TBW lookup is disabled in settings.", false, null, false);

        var provider = SearchProvider;
        if (!provider.IsConfigured)
            return new TbwReadiness(false, $"{provider.Name} is not configured. Add a search API key in Settings.", false, null, false);

        if (!FoundryLocalClient.CliAvailable)
            return new TbwReadiness(false, "Foundry Local is not installed. Install it to enable on-device web lookup.", false, null, false);

        _endpoint = await _foundry.EnsureEndpointAsync(ct).ConfigureAwait(false);
        if (_endpoint is null)
            return new TbwReadiness(false, "The Foundry Local service could not be started.", false, null, false);

        _caps = _detector.Detect();
        var cached = await _foundry.ListCachedModelIdsAsync(ct).ConfigureAwait(false);
        _resolvedModel = FoundryLocalClient.SelectCachedModel(_caps, cached, _config.TbwLookupModel);

        if (_resolvedModel is null)
        {
            var target = FoundryLocalClient.SelectDownloadTarget(_caps);
            return new TbwReadiness(false, "No compatible on-device model is installed yet.", true, target, _caps.CanUseGpu);
        }

        _log?.Invoke($"TBW lookup ready: endpoint={_endpoint}, model={_resolvedModel}, gpu={_caps.CanUseGpu}");
        return new TbwReadiness(true, null, false, null, _caps.CanUseGpu);
    }

    /// <summary>Downloads the hardware-appropriate model (after user consent) and marks it as the resolved model.</summary>
    public async Task DownloadModelAsync(IProgress<int>? progress, CancellationToken ct)
    {
        var target = FoundryLocalClient.SelectDownloadTarget(_caps);
        await _foundry.DownloadModelAsync(target, progress, ct).ConfigureAwait(false);
        var cached = await _foundry.ListCachedModelIdsAsync(ct).ConfigureAwait(false);
        _resolvedModel = FoundryLocalClient.SelectCachedModel(_caps, cached, _config.TbwLookupModel) ?? target;
    }

    /// <summary>
    /// Runs the full lookup for a drive model. Returns cached results unless <paramref name="force"/> is set.
    /// Never throws for expected conditions; returns a result whose <see cref="TbwLookupResult.Candidates"/>
    /// is empty (with a note) when nothing usable was found.
    /// </summary>
    public async Task<TbwLookupResult> LookupAsync(string driveModel, bool force, IProgress<TbwLookupProgress>? progress, CancellationToken ct)
    {
        driveModel = (driveModel ?? "").Trim();
        if (driveModel.Length == 0)
            return new TbwLookupResult("", Array.Empty<TbwCandidate>(), DateTime.UtcNow, "No drive model available.");

        if (!force && TbwLookupCache.TryGet(driveModel, out var cachedResult))
            return cachedResult!;

        if (_endpoint is null || _resolvedModel is null)
        {
            var readiness = await GetReadinessAsync(ct).ConfigureAwait(false);
            if (!readiness.CanRun)
                return new TbwLookupResult(driveModel, Array.Empty<TbwCandidate>(), DateTime.UtcNow, readiness.Reason);
        }

        try
        {
            // 1. Search the web.
            progress?.Report(new TbwLookupProgress(TbwLookupStage.Searching, driveModel));
            string query = $"\"{driveModel}\" SSD TBW endurance rating terabytes written";
            var hits = await SearchProvider.SearchAsync(query, 8, ct).ConfigureAwait(false);
            if (hits.Count == 0)
                return Finish(driveModel, Array.Empty<TbwCandidate>(), "No web results found for this drive.");

            // 2. Have the local model extract per-source TBW claims.
            progress?.Report(new TbwLookupProgress(TbwLookupStage.Analyzing, _resolvedModel));
            var claims = await ExtractClaimsAsync(driveModel, hits, ct).ConfigureAwait(false);

            // 3. Aggregate into candidates with confidence by source agreement.
            var candidates = Aggregate(claims);
            var note = candidates.Count == 0 ? "No TBW rating could be extracted from the search results." : null;
            return Finish(driveModel, candidates, note);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.Invoke($"TBW lookup failed: {ex.Message}");
            return new TbwLookupResult(driveModel, Array.Empty<TbwCandidate>(), DateTime.UtcNow, $"Lookup failed: {ex.Message}");
        }
    }

    private TbwLookupResult Finish(string model, IReadOnlyList<TbwCandidate> candidates, string? note)
    {
        var result = new TbwLookupResult(model, candidates, DateTime.UtcNow, note);
        TbwLookupCache.Put(result);
        return result;
    }

    private async Task<IReadOnlyList<TbwClaim>> ExtractClaimsAsync(string driveModel, IReadOnlyList<WebSearchHit> hits, CancellationToken ct)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < hits.Count; i++)
            sb.AppendLine($"[{i}] domain={hits[i].Domain} | {hits[i].Title}\n    {hits[i].Snippet}");

        string system =
            "You extract SSD endurance ratings (TBW, terabytes written) from web search results. " +
            "Only use numbers that clearly refer to the total rated TBW / endurance for the EXACT drive model asked about. " +
            "Convert petabytes to terabytes (1 PB = 1000 TB). Ignore capacities (GB/TB of storage), DWPD, warranty years, and prices. " +
            "Respond with ONLY a JSON array (no prose) of objects: " +
            "[{\"tbw_tb\": <number>, \"source_index\": <int from the list>, \"quote\": \"<short supporting text>\"}]. " +
            "If a result states no TBW rating, omit it. If none do, return [].";

        string noThink = (_resolvedModel ?? "").Contains("qwen3", StringComparison.OrdinalIgnoreCase) ? " /no_think" : "";
        string user = $"Drive model: {driveModel}\n\nSearch results:\n{sb}\n\nReturn the JSON array now.{noThink}";

        string raw = await _foundry.ChatAsync(_endpoint!, _resolvedModel!, system, user, maxTokens: 800, ct).ConfigureAwait(false);
        return ParseClaims(raw, hits);
    }

    /// <summary>Parses the model's JSON array of claims, attributing each to a real source hit by index.</summary>
    public static IReadOnlyList<TbwClaim> ParseClaims(string raw, IReadOnlyList<WebSearchHit> hits)
    {
        var claims = new List<TbwClaim>();
        int start = raw.IndexOf('[');
        int end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return claims;

        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("tbw_tb", out var tv)) continue;
                double tbw = tv.ValueKind == JsonValueKind.Number ? tv.GetDouble()
                    : (tv.ValueKind == JsonValueKind.String && double.TryParse(tv.GetString(), out var d) ? d : 0);
                if (tbw <= 0 || tbw > 100000) continue;

                int idx = el.TryGetProperty("source_index", out var iv) && iv.ValueKind == JsonValueKind.Number ? iv.GetInt32() : -1;
                if (idx < 0 || idx >= hits.Count) continue;

                string? quote = el.TryGetProperty("quote", out var qv) && qv.ValueKind == JsonValueKind.String ? qv.GetString() : null;
                claims.Add(new TbwClaim(tbw, hits[idx].Domain, hits[idx].Url, quote));
            }
        }
        catch { /* malformed model output -> no claims */ }
        return claims;
    }

    /// <summary>
    /// Aggregates per-source claims into candidate TBW values with a confidence score. Each distinct
    /// source domain gets one vote per value (rounded to the nearest 5 TB); confidence is the share of
    /// distinct sources that agree on that value (e.g. 3 of 4 sources -> 0.75).
    /// </summary>
    public static IReadOnlyList<TbwCandidate> Aggregate(IReadOnlyList<TbwClaim> claims)
    {
        if (claims.Count == 0) return Array.Empty<TbwCandidate>();

        // One vote per (domain, bucket): a source that repeats a value doesn't count twice.
        static double Bucket(double tbw) => Math.Round(tbw / 5.0) * 5.0;
        var voted = claims
            .GroupBy(c => (c.SourceDomain, Bucket(c.TbwTerabytes)))
            .Select(g => new { Domain = g.Key.SourceDomain, Value = g.Key.Item2, Exact = g.Average(c => c.TbwTerabytes), Url = g.First().SourceUrl })
            .ToList();

        int totalSources = voted.Select(v => v.Domain).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (totalSources == 0) return Array.Empty<TbwCandidate>();

        return voted
            .GroupBy(v => v.Value)
            .Select(g =>
            {
                var sources = g.Select(x => x.Domain).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                double representative = Math.Round(g.Average(x => x.Exact));
                return new TbwCandidate(
                    representative,
                    Math.Round((double)sources.Count / totalSources, 2),
                    sources.Count,
                    sources,
                    g.First().Url);
            })
            .OrderByDescending(c => c.Confidence)
            .ThenByDescending(c => c.SourceCount)
            .ToList();
    }
}
