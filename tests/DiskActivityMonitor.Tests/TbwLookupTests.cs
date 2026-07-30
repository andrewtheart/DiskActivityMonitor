using DiskActivityMonitor.Core.Ai;
using System.Net.Http;
using Xunit;

namespace DiskActivityMonitor.Tests;

public class TbwLookupTests
{
    [Fact]
    public void Aggregate_ScoresConfidenceByDistinctSourceAgreement()
    {
        // 3 sources say 500 TBW, 1 says 200 TBW -> 500 is higher confidence.
        var claims = new List<TbwClaim>
        {
            new(500, "techpowerup.com", "https://techpowerup.com/a", null),
            new(500, "tomshardware.com", "https://tomshardware.com/b", null),
            new(500, "storagereview.com", "https://storagereview.com/c", null),
            new(200, "randomblog.com", "https://randomblog.com/d", null),
        };

        var candidates = TbwLookupService.Aggregate(claims);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(500, candidates[0].TbwTerabytes);   // highest confidence first
        Assert.Equal(3, candidates[0].SourceCount);
        Assert.Equal(0.75, candidates[0].Confidence, 3);
        Assert.Equal(200, candidates[1].TbwTerabytes);
        Assert.Equal(1, candidates[1].SourceCount);
        Assert.Equal(0.25, candidates[1].Confidence, 3);
    }

    [Fact]
    public void Aggregate_CountsEachSourceDomainOnce()
    {
        var claims = new List<TbwClaim>
        {
            new(600, "x.com", "https://x.com/1", null),
            new(600, "x.com", "https://x.com/2", null), // same domain, repeated -> one vote
        };

        var candidates = TbwLookupService.Aggregate(claims);

        Assert.Single(candidates);
        Assert.Equal(1, candidates[0].SourceCount);
        Assert.Equal(1.0, candidates[0].Confidence, 3);
    }

    [Fact]
    public void Aggregate_MergesNearlyIdenticalValuesIntoOneBucket()
    {
        var claims = new List<TbwClaim>
        {
            new(500, "a.com", "https://a.com/1", null),
            new(502, "b.com", "https://b.com/1", null), // within 5 TB -> same candidate
        };

        var candidates = TbwLookupService.Aggregate(claims);

        Assert.Single(candidates);
        Assert.Equal(2, candidates[0].SourceCount);
        Assert.Equal(501, candidates[0].TbwTerabytes); // rounded mean of the bucket
    }

    [Fact]
    public void Aggregate_EmptyClaims_ReturnsEmpty()
        => Assert.Empty(TbwLookupService.Aggregate(new List<TbwClaim>()));

    [Fact]
    public void ParseClaims_AttributesToSourcesAndFiltersBadEntries()
    {
        var hits = new List<WebSearchHit>
        {
            new("Result 0", "https://techpowerup.com/x", "Rated endurance: 300 TBW."),
            new("Result 1", "https://www.tomshardware.com/y", "The drive is warrantied for 600 terabytes written."),
        };
        // Includes prose around the JSON, a zero value, and an out-of-range index (both dropped).
        string raw =
            "Sure, here is the data: " +
            "[ {\"tbw_tb\": 300, \"source_index\": 0, \"quote\": \"300 TBW\"}, " +
            "  {\"tbw_tb\": 600, \"source_index\": 1}, " +
            "  {\"tbw_tb\": 0, \"source_index\": 0}, " +
            "  {\"tbw_tb\": 300, \"source_index\": 9} ] done.";

        var claims = TbwLookupService.ParseClaims(raw, hits);

        Assert.Equal(2, claims.Count);
        Assert.Contains(claims, c => c.TbwTerabytes == 300 && c.SourceDomain == "techpowerup.com");
        Assert.Contains(claims, c => c.TbwTerabytes == 600 && c.SourceDomain == "tomshardware.com");
    }

    [Fact]
    public void ParseClaims_RejectsRatingMissingFromSourceEvidence()
    {
        var hits = new List<WebSearchHit>
        {
            new("Samsung 990 PRO 2TB", "https://example.com/990", "Five-year warranty; TBW varies by capacity."),
        };
        string raw = "[{\"tbw_tb\":150,\"source_index\":0,\"quote\":\"150 TBW\"}]";

        var claims = TbwLookupService.ParseClaims(raw, hits, "Samsung SSD 990 PRO 2TB");

        Assert.Empty(claims);
    }

    [Fact]
    public void ParseClaims_RequiresRequestedCapacityInFamilyResult()
    {
        var hits = new List<WebSearchHit>
        {
            new("Samsung 990 PRO family", "https://example.com/990", "The 1TB model is rated for 600 TBW."),
        };
        string raw = "[{\"tbw_tb\":600,\"source_index\":0}]";

        var claims = TbwLookupService.ParseClaims(raw, hits, "Samsung SSD 990 PRO 2TB");

        Assert.Empty(claims);
    }

    [Theory]
    [InlineData("Crucial MX500 1TB is rated for 360TB (TBW).", 360)]
    [InlineData("TBW (Terabytes Written), 1200TB for the 2TB model.", 1200)]
    public void ParseClaims_AcceptsCommonCapacitySpecificEvidenceFormats(string snippet, double tbw)
    {
        var hits = new List<WebSearchHit>
        {
            new("Exact model specification", "https://example.com/spec", snippet),
        };
        string raw = $"[{{\"tbw_tb\":{tbw},\"source_index\":0}}]";
        string model = tbw == 360 ? "Crucial MX500 1TB" : "Samsung SSD 990 PRO 2TB";

        var claims = TbwLookupService.ParseClaims(raw, hits, model);

        Assert.Single(claims);
        Assert.Equal(tbw, claims[0].TbwTerabytes);
    }

    [Fact]
    public void ExtractExplicitClaims_MapsFamilyRatingsToNearestCapacity()
    {
        var hits = new List<WebSearchHit>
        {
            new(
                "Crucial MX500 SSD Review",
                "https://example.com/mx500",
                "The 500GB model can handle 180TB (TBW) and 360TB (TBW) for the 1TB model."),
        };

        var claims = TbwLookupService.ExtractExplicitClaims("Crucial MX500 1TB", hits);

        var claim = Assert.Single(claims);
        Assert.Equal(360, claim.TbwTerabytes);
    }

    [Fact]
    public void ExtractExplicitClaims_RejectsWrongCapacityRating()
    {
        var hits = new List<WebSearchHit>
        {
            new("SSD family", "https://example.com/family", "The 1TB model is rated for 600 TBW."),
        };

        var claims = TbwLookupService.ExtractExplicitClaims("SSD family 2TB", hits);

        Assert.Empty(claims);
    }

    [Fact]
    public void ParseClaims_AcceptsConvertedPbwEvidence()
    {
        var hits = new List<WebSearchHit>
        {
            new("2TB drive", "https://example.com/spec", "The 2TB model is rated for 1.2 PBW."),
        };
        var claims = TbwLookupService.ParseClaims("[{\"tbw_tb\":1200,\"source_index\":0}]", hits, "Drive 2TB");
        Assert.Single(claims);
        Assert.Equal(1200, claims[0].TbwTerabytes);
    }

    [Fact]
    public void ExtractExplicitClaims_CoversNoCapacityNullModelAndGbCapacity()
    {
        var noCapacity = TbwLookupService.ExtractExplicitClaims(null!,
            [new("Drive", "https://example.com/a", "Rated endurance is 500 TBW.")]);
        var gb = TbwLookupService.ExtractExplicitClaims("Drive 500GB",
            [new("Drive 500GB", "https://example.com/b", "The 500GB model is rated for 180 TBW.")]);

        Assert.Equal(500, Assert.Single(noCapacity).TbwTerabytes);
        Assert.Equal(180, Assert.Single(gb).TbwTerabytes);
    }

    [Fact]
    public async Task LookupAsync_LiveSerperAndLocalModel_ProducesVerifiedCandidate()
    {
        var config = new DiskActivityMonitor.Core.Configuration.AppConfig
        {
            WebSearchProvider = "serper",
            EnableTbwWebLookup = true,
        };
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var logs = new List<string>();
        var service = new TbwLookupService(config, http, logs.Add);
        var progress = new List<TbwLookupStage>();

        var result = await service.LookupAsync("Samsung SSD 870 EVO 1TB", true,
            new Progress<TbwLookupProgress>(p => progress.Add(p.Stage)), CancellationToken.None);

        Assert.Contains(result.Candidates, candidate => Math.Abs(candidate.TbwTerabytes - 600) <= 5);
        Assert.DoesNotContain(result.Candidates, candidate => candidate.TbwTerabytes != 600);
    }

    [Fact]
    public void ParseClaims_NoJsonArray_ReturnsEmpty()
        => Assert.Empty(TbwLookupService.ParseClaims("I could not find any TBW rating.", new List<WebSearchHit>()));

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4080", "PCI\\VEN_10DE&DEV_2704", true)]
    [InlineData("Microsoft Basic Render Driver", "PCI\\VEN_0000", false)]
    [InlineData("Some Adapter", "ROOT\\BasicDisplay", false)]           // not on a physical bus
    [InlineData("VMware SVGA 3D", "PCI\\VEN_15AD", false)]              // virtual adapter
    public void IsHardwareAccelerator_ClassifiesDevices(string desc, string deviceId, bool expected)
        => Assert.Equal(expected, HardwareCapabilityDetector.IsHardwareAccelerator(desc, deviceId));
}
