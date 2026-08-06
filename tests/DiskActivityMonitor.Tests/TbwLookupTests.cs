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

    [Fact]
    public void ParseClaims_RejectsRatingAssociatedWithAnotherCapacityInSameResult()
    {
        var hits = new List<WebSearchHit>
        {
            new(
                "Samsung 870 EVO capacities",
                "https://example.com/870",
                "The 500GB model is rated for 300 TBW, while the 1TB model is rated for 600 TBW."),
        };

        Assert.Empty(TbwLookupService.ParseClaims(
            "[{\"tbw_tb\":300,\"source_index\":0}]",
            hits,
            "Samsung SSD 870 EVO 1TB"));
        Assert.Equal(600, Assert.Single(TbwLookupService.ParseClaims(
            "[{\"tbw_tb\":600,\"source_index\":0}]",
            hits,
            "Samsung SSD 870 EVO 1TB")).TbwTerabytes);
    }

    [Fact]
    public void ParseClaims_RejectsMarketplaceListingsAndAcceptsBoundedTbwMaxForm()
    {
        var hits = new List<WebSearchHit>
        {
            new(
                "Samsung SSD 870 EVO 1TB",
                "https://www.aliexpress.com/item/870-evo.html",
                "The Samsung 870 EVO 1TB delivers 300 TBW."),
            new(
                "Samsung 870 EVO 1TB experience",
                "https://www.reddit.com/r/storage/comments/example",
                "Samsung EVO 870 1TB (TBW max 600TB) failed after years of use."),
        };

        Assert.Empty(TbwLookupService.ParseClaims(
            "[{\"tbw_tb\":300,\"source_index\":0}]",
            hits,
            "Samsung SSD 870 EVO 1TB"));
        Assert.Equal(600, Assert.Single(TbwLookupService.ParseClaims(
            "[{\"tbw_tb\":600,\"source_index\":1}]",
            hits,
            "Samsung SSD 870 EVO 1TB")).TbwTerabytes);
        Assert.Equal(600, Assert.Single(TbwLookupService.ExtractExplicitClaims(
            "Samsung SSD 870 EVO 1TB",
            hits)).TbwTerabytes);
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
    public void SerializedLineCallback_HandlesParallelStreamsWithoutCorruption()
    {
        var lines = new List<string>();
        Action<string?> report = FoundryLocalClient.SerializeLineCallback(line => lines.Add(line));

        Parallel.For(0, 10_000, index => report(index.ToString()));
        report(null);
        FoundryLocalClient.SerializeLineCallback(null)("ignored");

        Assert.Equal(10_000, lines.Count);
        Assert.Equal(10_000, lines.Distinct().Count());
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
        var settings = new DiskActivityMonitor.Core.Configuration.UserSettings
        {
            WebSearchProvider = "serper",
            EnableTbwWebLookup = true,
        };
        var logs = new List<string>();
        var service = new TbwLookupService(settings, logs.Add);
        var progress = new List<TbwLookupStage>();

        var result = await service.LookupAsync("Samsung SSD 870 EVO 1TB", true,
            new Progress<TbwLookupProgress>(p => progress.Add(p.Stage)), CancellationToken.None);

        string diagnostics = $"Search: {result.Diagnostics?.SearchResponseJson}\nModel: {result.Diagnostics?.ModelResponseJson}";
        Assert.True(result.Candidates.Any(candidate => Math.Abs(candidate.TbwTerabytes - 600) <= 5), diagnostics);
        Assert.True(result.Candidates.All(candidate => candidate.TbwTerabytes == 600), diagnostics);
    }

    [Fact]
    public async Task GetReadiness_DisabledPerUser_DoesNotContactProviders()
    {
        var service = new TbwLookupService(
            new DiskActivityMonitor.Core.Configuration.UserSettings { EnableTbwWebLookup = false });

        var readiness = await service.GetReadinessAsync(CancellationToken.None);

        Assert.False(readiness.CanRun);
        Assert.Equal("Web TBW lookup is disabled in settings.", readiness.Reason);
    }

    [Fact]
    public async Task LookupAsync_SerperOnly_UsesDeterministicEvidenceWithoutFoundry()
    {
        var provider = new StubSearchProvider(
        [
            new("Drive 2TB specification", "https://vendor.example/spec", "The 2TB model is rated for 600 TBW."),
            new("Drive 2TB review", "https://review.example/test", "Endurance for Drive 2TB: 1200 TBW."),
            new("Drive 1TB specification", "https://wrong.example/spec", "The 1TB model is rated for 300 TBW."),
        ]);
        var service = new TbwLookupService(
            new DiskActivityMonitor.Core.Configuration.UserSettings
            {
                EnableTbwWebLookup = true,
                TbwLookupMethod = DiskActivityMonitor.Core.Configuration.TbwLookupMethod.SerperOnly,
            },
            provider);
        var progress = new List<TbwLookupProgress>();

        var result = await service.LookupAsync(
            "Drive 2TB",
            true,
            new Progress<TbwLookupProgress>(progress.Add),
            CancellationToken.None);

        Assert.Equal(DiskActivityMonitor.Core.Configuration.TbwLookupMethod.SerperOnly, result.LookupMethod);
        Assert.Equal([600d, 1200d], result.Candidates.Select(candidate => candidate.TbwTerabytes).Order().ToArray());
        Assert.DoesNotContain(result.Candidates, candidate => candidate.TbwTerabytes == 300);
        Assert.Equal("\"Drive 2TB\" TBW", provider.Query);
    }

    [Fact]
    public async Task GetReadiness_SerperOnly_DoesNotRequireFoundry()
    {
        var service = new TbwLookupService(
            new DiskActivityMonitor.Core.Configuration.UserSettings
            {
                EnableTbwWebLookup = true,
                TbwLookupMethod = DiskActivityMonitor.Core.Configuration.TbwLookupMethod.SerperOnly,
            },
            new StubSearchProvider([]));

        var readiness = await service.GetReadinessAsync(CancellationToken.None);

        Assert.True(readiness.CanRun);
        Assert.False(readiness.NeedsFoundryInstall);
        Assert.False(readiness.NeedsModelDownload);
    }

    [Theory]
    [InlineData("Foundry at http://127.0.0.1:5273", "http://127.0.0.1:5273")]
    [InlineData("https://localhost:9443 ready", "https://localhost:9443")]
    [InlineData("http" + "://[::1]:5273", "http" + "://[::1]:5273")]
    [InlineData("http" + "://192.168.1.20:5273", null)]
    [InlineData("http" + "://8.8.8.8:5273", null)]
    [InlineData("not running", null)]
    public void ParseLoopbackEndpoint_RejectsNonLocalAddresses(string output, string? expected)
        => Assert.Equal(expected, FoundryLocalClient.ParseLoopbackEndpoint(output));

    [Fact]
    public async Task ChatAsync_RejectsNonLoopbackEndpointBeforeSending()
    {
        var client = new FoundryLocalClient();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChatAsync(
            "http" + "://192.168.1.20:5273", "model", "system", "user", 10, CancellationToken.None));

        Assert.Contains("loopback", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FoundryTransport_DisablesRedirectsProxiesAndCookies()
    {
        using var handler = FoundryLocalClient.CreateLoopbackHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public void FoundryChatDiagnostics_PreserveRawResponseAndStripThinkFromParsedContent()
    {
        const string rawResponse =
            "{\"id\":\"completion-1\",\"choices\":[{\"message\":{\"content\":\"<think>private returned text</think>\\n[{\\\"tbw_tb\\\":600}]\"}}]}";

        var result = FoundryLocalClient.ParseChatResponse(rawResponse);

        Assert.Equal(rawResponse, result.RawResponseJson);
        Assert.Equal("[{\"tbw_tb\":600}]", result.Content);
        Assert.Contains("<think>private returned text</think>", result.RawResponseJson);
        Assert.DoesNotContain("<think>", result.Content);
    }

    [Fact]
    public async Task FoundryChatDiagnostics_RetainErrorResponseBodyBeforeThrowing()
    {
        const string response = "{\"error\":{\"message\":\"model rejected request\"}}";
        using var http = new HttpClient(new FixedResponseHandler(
            System.Net.HttpStatusCode.BadRequest,
            response));
        var client = new FoundryLocalClient(http);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatWithDiagnosticsAsync(
            "http://127.0.0.1:5273", "model", "system", "user", 10, CancellationToken.None));

        Assert.Equal(response, client.LastResponseJson);
    }

    [Fact]
    public void FoundryInstaller_UsesExactOfficialWingetPackageNonInteractively()
    {
        var startInfo = FoundryLocalClient.CreateInstallStartInfo("winget.exe");

        Assert.Equal("winget.exe", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(
            [
                "install",
                "--exact",
                "--id",
                "Microsoft.FoundryLocal",
                "--source",
                "winget",
                "--accept-package-agreements",
                "--accept-source-agreements",
                "--disable-interactivity",
            ],
            startInfo.ArgumentList);
    }

    private sealed class StubSearchProvider(IReadOnlyList<WebSearchHit> hits) : IWebSearchProvider
    {
        public string Name => "Serper.dev";
        public bool IsConfigured => true;
        public string? Query { get; private set; }

        public Task<IReadOnlyList<WebSearchHit>> SearchAsync(string query, int count, CancellationToken ct)
        {
            Query = query;
            return Task.FromResult(hits);
        }
    }

    [Fact]
    public void ResolveExecutablePath_FindsPathAndWindowsAppsAliases()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dam_foundry_{Guid.NewGuid():N}");
        string pathDirectory = Path.Combine(root, "path");
        string localAppData = Path.Combine(root, "local");
        string windowsApps = Path.Combine(localAppData, "Microsoft", "WindowsApps");
        Directory.CreateDirectory(pathDirectory);
        Directory.CreateDirectory(windowsApps);
        string winget = Path.Combine(pathDirectory, "winget.exe");
        string foundry = Path.Combine(windowsApps, "foundry.exe");
        File.WriteAllText(winget, "test");
        File.WriteAllText(foundry, "test");
        try
        {
            Assert.Equal(winget, FoundryLocalClient.ResolveExecutablePath("winget.exe", pathDirectory, localAppData));
            Assert.Equal(foundry, FoundryLocalClient.ResolveExecutablePath("foundry.exe", "", localAppData));
            Assert.Null(FoundryLocalClient.ResolveExecutablePath("missing.exe", "", localAppData));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WebSearchTransport_DisablesRedirectsAndCookies()
    {
        using var handler = WebSearchProviderFactory.CreateSecureHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public async Task GoogleSearch_SendsApiKeyInHeaderNotUrl()
    {
        const string apiKey = "synthetic-google-api-key";
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var provider = new GoogleCseSearchProvider(new AiSecrets
        {
            GoogleApiKey = apiKey,
            GoogleCseId = "engine-id",
        }, http);

        await provider.SearchAsync("drive model", 3, CancellationToken.None);

        Assert.NotNull(handler.RequestUri);
        Assert.DoesNotContain(apiKey, handler.RequestUri!.OriginalString);
        Assert.DoesNotContain("key=", handler.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(apiKey, handler.ApiKey);
        Assert.Equal("{\"items\":[]}", provider.LastResponseJson);
        Assert.DoesNotContain(apiKey, provider.LastResponseJson);
    }

    [Fact]
    public async Task SerperLookupDiagnostics_RetainResponseBodyWithoutRequestApiKey()
    {
        const string apiKey = "synthetic-serper-api-key";
        const string response =
            "{\"organic\":[{\"title\":\"Drive 2TB specification\",\"link\":\"https://vendor.example/spec\",\"snippet\":\"The 2TB model is rated for 600 TBW.\"}]}";
        var handler = new SerperCapturingHandler(response);
        using var http = new HttpClient(handler);
        var provider = new SerperSearchProvider(new AiSecrets { SerperApiKey = apiKey }, http);
        var service = new TbwLookupService(
            new DiskActivityMonitor.Core.Configuration.UserSettings
            {
                EnableTbwWebLookup = true,
                TbwLookupMethod = DiskActivityMonitor.Core.Configuration.TbwLookupMethod.SerperOnly,
            },
            provider);

        var result = await service.LookupAsync("Drive 2TB", true, null, CancellationToken.None);

        Assert.Equal(apiKey, handler.ApiKey);
        Assert.Equal(response, provider.LastResponseJson);
        Assert.Equal(response, result.Diagnostics?.SearchResponseJson);
        Assert.Equal("Serper.dev", result.Diagnostics?.SearchProvider);
        Assert.DoesNotContain(apiKey, result.Diagnostics!.SearchResponseJson);
        Assert.Equal(600, Assert.Single(result.Candidates).TbwTerabytes);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? ApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            ApiKey = request.Headers.TryGetValues("X-Goog-Api-Key", out var values) ? values.Single() : null;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"items\":[]}"),
            });
        }
    }

    private sealed class SerperCapturingHandler(string response) : HttpMessageHandler
    {
        public string? ApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ApiKey = request.Headers.TryGetValues("X-API-KEY", out var values) ? values.Single() : null;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(response),
            });
        }
    }

    private sealed class FixedResponseHandler(System.Net.HttpStatusCode statusCode, string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(response),
            });
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
