using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiskActivityMonitor.Core.Ai;

/// <summary>Whether a TBW web lookup can run on this machine right now, and what (if anything) is missing.</summary>
/// <param name="CanRun">True when the selected search and evidence-analysis method are ready.</param>
/// <param name="Reason">Why it cannot run (shown to the user) when <paramref name="CanRun"/> is false.</param>
/// <param name="NeedsModelDownload">A search key + Foundry are present but no suitable model is cached.</param>
/// <param name="DownloadAlias">The model alias that would be downloaded on consent.</param>
/// <param name="HasUsableGpu">A discrete GPU with enough VRAM was detected.</param>
/// <param name="NeedsFoundryInstall">Foundry Local must be installed before local verification can run.</param>
public sealed record TbwReadiness(
    bool CanRun,
    string? Reason,
    bool NeedsModelDownload,
    string? DownloadAlias,
    bool HasUsableGpu,
    bool NeedsFoundryInstall = false);

/// <summary>
/// Orchestrates the "search the web for this drive's TBW rating" feature. Foundry mode combines
/// local-model extraction with deterministic evidence validation; Serper-only mode uses only the
/// deterministic parser. Both aggregate candidates by independent source agreement.
/// </summary>
public sealed class TbwLookupService
{
    private readonly Configuration.UserSettings _settings;
    private readonly Action<string>? _log;
    private readonly FoundryLocalClient _foundry;
    private readonly IWebSearchProvider? _searchProviderOverride;
    private readonly HardwareCapabilityDetector _detector = new();
    private AiSecrets _secrets;

    private string? _endpoint;
    private string? _resolvedModel;
    private HardwareCapabilityDetector.HardwareCapabilities _caps;

    public TbwLookupService(Configuration.UserSettings settings, Action<string>? log = null)
    {
        _settings = settings;
        _log = log;
        _foundry = new FoundryLocalClient(log);
        _secrets = AiSecretsStore.Load();
    }

    internal TbwLookupService(Configuration.UserSettings settings, IWebSearchProvider searchProvider, Action<string>? log = null)
        : this(settings, log)
    {
        _searchProviderOverride = searchProvider;
    }

    /// <summary>Reloads API keys from the per-user secrets store / environment.</summary>
    public void ReloadSecrets() => _secrets = AiSecretsStore.Load();

    private IWebSearchProvider SearchProvider => _searchProviderOverride ?? WebSearchProviderFactory.Create(
        _settings.TbwLookupMethod == Configuration.TbwLookupMethod.SerperOnly ? "serper" : _settings.WebSearchProvider,
        _secrets);

    /// <summary>Checks whether a lookup can run and, if not, what is missing.</summary>
    public async Task<TbwReadiness> GetReadinessAsync(CancellationToken ct)
    {
        if (!_settings.EnableTbwWebLookup)
            return new TbwReadiness(false, "Web TBW lookup is disabled in settings.", false, null, false);

        var provider = SearchProvider;
        if (!provider.IsConfigured)
            return new TbwReadiness(false, $"{provider.Name} is not configured. Add a search API key in Settings.", false, null, false);

        if (_settings.TbwLookupMethod == Configuration.TbwLookupMethod.SerperOnly)
            return new TbwReadiness(true, null, false, null, false);

        if (!FoundryLocalClient.CliAvailable)
            return new TbwReadiness(
                false,
                "Foundry Local is not installed. Install the official Microsoft package through Windows Package Manager; Windows may request approval.",
                false,
                null,
                false,
                NeedsFoundryInstall: true);

        _endpoint = await _foundry.EnsureEndpointAsync(ct).ConfigureAwait(false);
        if (_endpoint is null)
            return new TbwReadiness(false, "The Foundry Local service could not be started.", false, null, false);

        _caps = _detector.Detect();
        var cached = await _foundry.ListCachedModelIdsAsync(ct).ConfigureAwait(false);
        _resolvedModel = FoundryLocalClient.SelectCachedModel(_caps, cached, _settings.TbwLookupModel);

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
        _resolvedModel = FoundryLocalClient.SelectCachedModel(_caps, cached, _settings.TbwLookupModel) ?? target;
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
            return new TbwLookupResult("", Array.Empty<TbwCandidate>(), DateTime.UtcNow, "No drive model available.", _settings.TbwLookupMethod);

        if (!force && TbwLookupCache.TryGet(driveModel, out var cachedResult)
            && cachedResult?.LookupMethod == _settings.TbwLookupMethod)
            return cachedResult!;

        if (_endpoint is null || _resolvedModel is null)
        {
            var readiness = await GetReadinessAsync(ct).ConfigureAwait(false);
            if (!readiness.CanRun)
                return new TbwLookupResult(driveModel, Array.Empty<TbwCandidate>(), DateTime.UtcNow, readiness.Reason, _settings.TbwLookupMethod);
        }

        IWebSearchProvider? providerUsed = null;
        string? searchResponseJson = null;
        string? modelResponseJson = null;
        try
        {
            // 1. Search the web.
            progress?.Report(new TbwLookupProgress(TbwLookupStage.Searching, driveModel));
            // Keep the query short: exact model + TBW yields capacity/rating snippets, while
            // extra generic endurance terms tend to surface comparisons and unrelated models.
            string query = $"\"{driveModel}\" TBW";
            providerUsed = SearchProvider;
            var hits = await providerUsed.SearchAsync(query, 8, ct).ConfigureAwait(false);
            searchResponseJson = (providerUsed as IWebSearchDiagnosticsProvider)?.LastResponseJson;
            if (hits.Count == 0)
                return Finish(
                    driveModel,
                    Array.Empty<TbwCandidate>(),
                    "No web results found for this drive.",
                    CreateDiagnostics(providerUsed, searchResponseJson, modelResponseJson));

            IReadOnlyList<TbwClaim> claims;
            if (_settings.TbwLookupMethod == Configuration.TbwLookupMethod.SerperOnly)
            {
                progress?.Report(new TbwLookupProgress(TbwLookupStage.Analyzing, "Deterministic evidence parser"));
                claims = ExtractExplicitClaims(driveModel, hits);
            }
            else
            {
                // Search snippets often contain an explicit, capacity-linked rating. Add those claims
                // deterministically so a small local model cannot lose a valid result by omitting it.
                // Both paths still pass the same source-evidence and exact-capacity validation.
                progress?.Report(new TbwLookupProgress(TbwLookupStage.Analyzing, _resolvedModel));
                var extraction = await ExtractClaimsAsync(driveModel, hits, ct).ConfigureAwait(false);
                modelResponseJson = extraction.RawResponseJson;
                claims = extraction.Claims.Concat(ExtractExplicitClaims(driveModel, hits)).ToList();
            }

            // 3. Aggregate into candidates with confidence by source agreement.
            var candidates = Aggregate(claims);
            var note = candidates.Count == 0
                ? _settings.TbwLookupMethod == Configuration.TbwLookupMethod.SerperOnly
                    ? "No explicit, capacity-matched TBW rating was present in the Serper evidence."
                    : "No TBW rating could be extracted from the search results."
                : null;
            return Finish(
                driveModel,
                candidates,
                note,
                CreateDiagnostics(providerUsed, searchResponseJson, modelResponseJson));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            searchResponseJson ??= (providerUsed as IWebSearchDiagnosticsProvider)?.LastResponseJson;
            modelResponseJson ??= _foundry.LastResponseJson;
            _log?.Invoke($"TBW lookup failed: {ex.Message}");
            return new TbwLookupResult(
                driveModel,
                Array.Empty<TbwCandidate>(),
                DateTime.UtcNow,
                $"Lookup failed: {ex.Message}",
                _settings.TbwLookupMethod,
                CreateDiagnostics(providerUsed, searchResponseJson, modelResponseJson));
        }
    }

    private TbwLookupResult Finish(
        string model,
        IReadOnlyList<TbwCandidate> candidates,
        string? note,
        TbwLookupDiagnostics? diagnostics)
    {
        var result = new TbwLookupResult(model, candidates, DateTime.UtcNow, note, _settings.TbwLookupMethod, diagnostics);
        TbwLookupCache.Put(result);
        return result;
    }

    private TbwLookupDiagnostics? CreateDiagnostics(
        IWebSearchProvider? provider,
        string? searchResponseJson,
        string? modelResponseJson)
    {
        if (string.IsNullOrWhiteSpace(searchResponseJson) && string.IsNullOrWhiteSpace(modelResponseJson))
            return null;
        return new TbwLookupDiagnostics(
            provider?.Name ?? "Search provider",
            searchResponseJson,
            modelResponseJson is null ? null : _resolvedModel,
            modelResponseJson);
    }

    private sealed record ModelExtraction(IReadOnlyList<TbwClaim> Claims, string RawResponseJson);

    private async Task<ModelExtraction> ExtractClaimsAsync(string driveModel, IReadOnlyList<WebSearchHit> hits, CancellationToken ct)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < hits.Count; i++)
            sb.AppendLine($"[{i}] domain={hits[i].Domain} | {hits[i].Title}\n    {hits[i].Snippet}");

        string system =
            "You extract SSD endurance ratings (TBW, terabytes written) from web search results. " +
            "Only use numbers that clearly refer to the total rated TBW / endurance for the EXACT drive model asked about. " +
            "The exact number must appear in the supplied title or snippet; never fill in a missing value from memory or infer a table cell. " +
            "When a product family has multiple capacities, only use a rating explicitly associated with the requested capacity. " +
            "Convert petabytes to terabytes (1 PB = 1000 TB). Ignore capacities (GB/TB of storage), DWPD, warranty years, and prices. " +
            "Respond with ONLY a JSON array (no prose) of objects: " +
            "[{\"tbw_tb\": <number>, \"source_index\": <int from the list>, \"quote\": \"<short supporting text>\"}]. " +
            "If a result states no TBW rating, omit it. If none do, return [].";

        string noThink = (_resolvedModel ?? "").Contains("qwen3", StringComparison.OrdinalIgnoreCase) ? " /no_think" : "";
        string user = $"Drive model: {driveModel}\n\nSearch results:\n{sb}\n\nReturn the JSON array now.{noThink}";

        var chat = await _foundry.ChatWithDiagnosticsAsync(
            _endpoint!, _resolvedModel!, system, user, maxTokens: 800, ct).ConfigureAwait(false);
        return new ModelExtraction(ParseClaims(chat.Content, hits, driveModel), chat.RawResponseJson);
    }

    /// <summary>
    /// Parses the model's JSON claims and attributes each to a real source. A claim is accepted only
    /// when the source title/snippet contains the claimed TBW value (or equivalent PBW value), which
    /// prevents a local model from filling missing search-result data from memory or hallucination.
    /// </summary>
    public static IReadOnlyList<TbwClaim> ParseClaims(string raw, IReadOnlyList<WebSearchHit> hits, string? driveModel = null)
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
                if (!SourceSupportsClaim(hits[idx], tbw, driveModel)) continue;

                string? quote = el.TryGetProperty("quote", out var qv) && qv.ValueKind == JsonValueKind.String ? qv.GetString() : null;
                claims.Add(new TbwClaim(tbw, hits[idx].Domain, hits[idx].Url, quote));
            }
        }
        catch { /* malformed model output -> no claims */ }
        return claims;
    }

    private static bool SourceSupportsClaim(WebSearchHit hit, double tbw, string? driveModel)
    {
        if (IsMarketplaceSource(hit)) return false;

        string evidence = NormalizeEvidence($"{hit.Title} {hit.Snippet}");
        string tbwNumber = Math.Round(tbw).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string tbwPattern = $@"(?<!\d){Regex.Escape(tbwNumber)}(?:\.0+)?(?!\d)";
        const string enduranceUnit = @"(?:TBW|TB\s*(?:\(\s*TBW\s*\))?|terabytes?\s+written)";
        const string reverseEndurancePrefix =
            @"(?:TBW(?:\s*\(\s*terabytes?\s+written\s*\))?|terabytes?\s+written)\s*(?:max(?:imum)?|rated|endurance)?\s*[:=,-]?\s*";
        var match = Regex.Match(
            evidence,
            $@"(?i)(?:{tbwPattern}\s*{enduranceUnit}|{reverseEndurancePrefix}{tbwPattern}\s*(?:TB)?)");

        // A source may state the endurance in PB/PBW while the model correctly converts it to TBW.
        if (!match.Success && tbw >= 1000)
        {
            string pbNumber = (tbw / 1000d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            match = Regex.Match(
                evidence,
                $@"(?i)(?<![\d.]){Regex.Escape(pbNumber)}(?![\d.])\s*(?:PBW|PB\s+written|petabytes?\s+written)");
        }

        if (!match.Success) return false;

        Match exactRating = Regex.Match(
            evidence,
            $@"(?i){tbwPattern}\s*{enduranceUnit}");
        int ratingIndex = match.Index;
        int ratingLength = match.Length;
        if (exactRating.Success)
        {
            ratingIndex = exactRating.Index;
            ratingLength = exactRating.Length;
        }
        else
        {
            Match numeric = Regex.Match(match.Value, tbwPattern);
            if (numeric.Success)
            {
                ratingIndex = match.Index + numeric.Index;
                ratingLength = numeric.Length;
            }
        }

        return IsRatingAssociatedWithRequestedCapacity(evidence, ratingIndex, ratingLength, driveModel);
    }

    private static bool IsRatingAssociatedWithRequestedCapacity(
        string evidence,
        int ratingIndex,
        int ratingLength,
        string? driveModel)
    {
        double? requested = ParseCapacityGb(driveModel ?? "");
        if (requested is null) return true;

        var enduranceValueSpans = Regex.Matches(
                evidence,
                @"(?i)(?<rating>\d+(?:\.\d+)?)\s*(?:TBW\b|TB\s*\(\s*TBW\s*\)|terabytes?\s+(?:written|endurance)|PBW\b|PB\s+(?:written|endurance))")
            .Cast<Match>()
            .Select(match => match.Groups["rating"])
            .Concat(Regex.Matches(
                    evidence,
                    @"(?i)(?:TBW(?:\s*\(\s*terabytes?\s+written\s*\))?|terabytes?\s+written)\s*(?:max(?:imum)?|rated|endurance)?\s*[:=,-]?\s*(?<rating>\d+(?:\.\d+)?)\s*(?:TB)?")
                .Cast<Match>()
                .Select(match => match.Groups["rating"]))
            .Select(group => (group.Index, group.Length))
            .ToList();
        var capacities = Regex.Matches(evidence, @"(?i)(?<value>\d+(?:\.\d+)?)\s*(?<unit>TB|GB)\b")
            .Cast<Match>()
            .Where(capacity => !enduranceValueSpans.Any(span =>
                                   Overlaps(capacity.Index, capacity.Length, span.Index, span.Length)) &&
                               !Overlaps(capacity.Index, capacity.Length, ratingIndex, ratingLength))
            .Select(capacity => new
            {
                Match = capacity,
                Gb = double.Parse(capacity.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture) *
                     (capacity.Groups["unit"].Value.Equals("TB", StringComparison.OrdinalIgnoreCase) ? 1000d : 1d),
            })
            .ToList();

        int clauseStart = evidence.LastIndexOfAny([',', ';'], Math.Max(0, ratingIndex - 1)) + 1;
        int nextComma = evidence.IndexOf(',', ratingIndex + ratingLength);
        int nextSemicolon = evidence.IndexOf(';', ratingIndex + ratingLength);
        int clauseEnd = new[] { nextComma, nextSemicolon }
            .Where(index => index >= 0)
            .DefaultIfEmpty(evidence.Length)
            .Min();
        var sameClause = capacities
            .Where(capacity => capacity.Match.Index >= clauseStart && capacity.Match.Index < clauseEnd)
            .ToList();
        var nearest = (sameClause.Count > 0 ? sameClause : capacities)
            .OrderBy(capacity => Distance(ratingIndex, ratingLength, capacity.Match))
            .FirstOrDefault();

        return nearest is not null && Distance(ratingIndex, ratingLength, nearest.Match) <= 120 &&
               Math.Abs(nearest.Gb - requested.Value) <= 1;
    }

    /// <summary>
    /// Extracts values explicitly printed in search snippets. When a snippet contains several
    /// family ratings, a value is accepted only when the nearest named capacity matches the
    /// requested drive capacity (e.g. 360 TBW nearest "1TB model").
    /// </summary>
    public static IReadOnlyList<TbwClaim> ExtractExplicitClaims(string driveModel, IReadOnlyList<WebSearchHit> hits)
    {
        var requested = ParseCapacityGb(driveModel);
        var claims = new List<TbwClaim>();

        foreach (var hit in hits)
        {
            string evidence = NormalizeEvidence($"{hit.Title} {hit.Snippet}");
            var ratings = Regex.Matches(
                    evidence,
                    @"(?i)(?<rating>\d+(?:\.\d+)?)\s*(?:TBW\b|TB\s*\(\s*TBW\s*\)|terabytes?\s+(?:written|endurance))")
                .Cast<Match>()
                .Concat(Regex.Matches(
                        evidence,
                        @"(?i)(?:TBW(?:\s*\(\s*terabytes?\s+written\s*\))?|terabytes?\s+written)\s*(?:max(?:imum)?|rated|endurance)?\s*[:=,-]?\s*(?<rating>\d+(?:\.\d+)?)\s*(?:TB)?")
                    .Cast<Match>())
                .OrderBy(match => match.Index)
                .ToList();
            if (ratings.Count == 0) continue;

            var capacities = Regex.Matches(evidence, @"(?i)(?<value>\d+(?:\.\d+)?)\s*(?<unit>TB|GB)\b")
                .Where(m => !ratings.Any(r => Overlaps(m, r)))
                .Select(m => new
                {
                    Match = m,
                    Gb = double.Parse(m.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture) *
                         (m.Groups["unit"].Value.Equals("TB", StringComparison.OrdinalIgnoreCase) ? 1000d : 1d),
                })
                .ToList();

            foreach (Match ratingMatch in ratings)
            {
                double rating = double.Parse(ratingMatch.Groups["rating"].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (rating <= 0 || rating > 100000 || !SourceSupportsClaim(hit, rating, driveModel))
                    continue;

                if (requested is not null)
                {
                    var nearest = capacities
                        .OrderBy(c => Distance(ratingMatch, c.Match))
                        .FirstOrDefault();
                    if (nearest is null || Distance(ratingMatch, nearest.Match) > 120 ||
                        Math.Abs(nearest.Gb - requested.Value) > 1)
                        continue;
                }

                claims.Add(new TbwClaim(rating, hit.Domain, hit.Url, ratingMatch.Value));
            }
        }

        return claims;
    }

    private static double? ParseCapacityGb(string text)
    {
        var match = Regex.Match(text ?? "", @"(?i)(?<value>\d+(?:\.\d+)?)\s*(?<unit>TB|GB)\b");
        if (!match.Success) return null;
        double value = double.Parse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture);
        return match.Groups["unit"].Value.Equals("TB", StringComparison.OrdinalIgnoreCase) ? value * 1000d : value;
    }

    private static string NormalizeEvidence(string evidence)
        => Regex.Replace(evidence, @"(?<=\d),(?=\d{3}\b)", string.Empty);

    private static bool IsMarketplaceSource(WebSearchHit hit)
    {
        string domain = hit.Domain;
        return domain.EndsWith("aliexpress.com", StringComparison.OrdinalIgnoreCase) ||
               domain.EndsWith("amazon.com", StringComparison.OrdinalIgnoreCase) ||
               domain.EndsWith("amazon.co.uk", StringComparison.OrdinalIgnoreCase) ||
               domain.EndsWith("ebay.com", StringComparison.OrdinalIgnoreCase) ||
               domain.EndsWith("temu.com", StringComparison.OrdinalIgnoreCase) ||
               domain.EndsWith("walmart.com", StringComparison.OrdinalIgnoreCase);
    }

    private static int Distance(Match a, Match b)
        => Math.Abs((a.Index + a.Length / 2) - (b.Index + b.Length / 2));

    private static int Distance(int index, int length, Match other)
        => Math.Abs((index + length / 2) - (other.Index + other.Length / 2));

    private static bool Overlaps(Match a, Match b)
        => a.Index < b.Index + b.Length && b.Index < a.Index + a.Length;

    private static bool Overlaps(int firstIndex, int firstLength, int secondIndex, int secondLength)
        => firstIndex < secondIndex + secondLength && secondIndex < firstIndex + firstLength;

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
