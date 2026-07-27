namespace DiskActivityMonitor.Core.Ai;

/// <summary>Progress stages surfaced to the UI while a TBW web lookup runs.</summary>
public enum TbwLookupStage
{
    Idle,
    DetectingHardware,
    StartingRuntime,
    DownloadingModel,
    Searching,
    Analyzing,
    Done,
    /// <summary>The lookup cannot run (no Foundry Local, no search key, etc.).</summary>
    Unavailable,
    Error,
}

/// <summary>A progress notification emitted during a lookup.</summary>
/// <param name="Stage">Current stage.</param>
/// <param name="Detail">Human-readable detail (model alias, search query, error message...).</param>
/// <param name="Percent">Optional 0-100 progress for downloads.</param>
public sealed record TbwLookupProgress(TbwLookupStage Stage, string? Detail = null, int? Percent = null);

/// <summary>A single web search hit.</summary>
public sealed record WebSearchHit(string Title, string Url, string Snippet)
{
    /// <summary>Registrable-ish host for the URL (used to count distinct sources), e.g. "techpowerup.com".</summary>
    public string Domain => ExtractDomain(Url);

    private static string ExtractDomain(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            var host = u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
            return host.ToLowerInvariant();
        }
        return url;
    }
}

/// <summary>One TBW value the model extracted from a specific source page.</summary>
/// <param name="TbwTerabytes">Rated endurance in terabytes written.</param>
/// <param name="SourceDomain">Domain the value came from.</param>
/// <param name="SourceUrl">Full source URL.</param>
/// <param name="Quote">Optional supporting snippet.</param>
public sealed record TbwClaim(double TbwTerabytes, string SourceDomain, string SourceUrl, string? Quote);

/// <summary>An aggregated TBW candidate value with a confidence score derived from source agreement.</summary>
/// <param name="TbwTerabytes">The (rounded) candidate TBW value.</param>
/// <param name="Confidence">0-1 score: share of distinct sources that agree on this value.</param>
/// <param name="SourceCount">Number of distinct sources citing this value.</param>
/// <param name="Sources">Distinct source domains citing this value.</param>
/// <param name="SampleUrl">A representative source URL for this value.</param>
public sealed record TbwCandidate(
    double TbwTerabytes,
    double Confidence,
    int SourceCount,
    IReadOnlyList<string> Sources,
    string? SampleUrl);

/// <summary>The result of a TBW web lookup for one drive model.</summary>
/// <param name="Model">The drive model that was searched.</param>
/// <param name="Candidates">Candidate TBW values ordered by confidence (highest first).</param>
/// <param name="RetrievedUtc">When the lookup completed.</param>
/// <param name="Note">Optional note (e.g. why it is empty or unavailable).</param>
public sealed record TbwLookupResult(
    string Model,
    IReadOnlyList<TbwCandidate> Candidates,
    DateTime RetrievedUtc,
    string? Note = null)
{
    public bool HasCandidates => Candidates.Count > 0;
}
