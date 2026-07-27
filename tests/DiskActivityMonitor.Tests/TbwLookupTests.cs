using DiskActivityMonitor.Core.Ai;
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
            new("Result 0", "https://techpowerup.com/x", "..."),
            new("Result 1", "https://www.tomshardware.com/y", "..."),
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
