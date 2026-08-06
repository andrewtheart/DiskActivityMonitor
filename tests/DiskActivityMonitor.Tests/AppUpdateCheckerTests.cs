using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DiskActivityMonitor.Core.Updates;

namespace DiskActivityMonitor.Tests;

public sealed class AppUpdateCheckerTests
{
    [Fact]
    public void ShouldAutoCheck_Branches()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.True(AppUpdateChecker.ShouldAutoCheck(null, now, TimeSpan.FromDays(7)));
        Assert.False(AppUpdateChecker.ShouldAutoCheck(now, now, TimeSpan.FromDays(7)));
        Assert.True(AppUpdateChecker.ShouldAutoCheck(now.AddDays(-8), now, TimeSpan.FromDays(7)));
    }

    [Theory]
    [InlineData("1.4.12", 1, 4, 12)]
    [InlineData("v2.0.1", 2, 0, 1)]
    [InlineData("1.4.12+commit", 1, 4, 12)]
    public void TryParseVersion_AcceptsDamVersions(string text, int major, int minor, int build)
    {
        Assert.True(AppUpdateChecker.TryParseVersion(text, out Version version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.4")]
    [InlineData("1.4.12-beta")]
    public void TryParseVersion_RejectsUnverifiableVersions(string? text)
        => Assert.False(AppUpdateChecker.TryParseVersion(text, out _));

    [Fact]
    public void SelectInstallerAsset_RequiresOneExactArchitectureMatchedAsset()
    {
        var version = new Version(1, 4, 13);
        string digest = "sha256:" + new string('a', 64);
        var assets = new[]
        {
            ("DiskActivityMonitor-Setup-1.4.13-x64.exe", "https://github.com/andrewtheart/DiskActivityMonitor/releases/download/v1.4.13/DiskActivityMonitor-Setup-1.4.13-x64.exe", 100L, (string?)digest),
            ("DiskActivityMonitor-Setup-1.4.13-x86.exe", "https://github.com/andrewtheart/DiskActivityMonitor/releases/download/v1.4.13/DiskActivityMonitor-Setup-1.4.13-x86.exe", 90L, (string?)digest),
        };

        AppReleaseAsset? selected = AppUpdateChecker.SelectInstallerAsset(assets, version, Architecture.X64);

        Assert.NotNull(selected);
        Assert.Equal("DiskActivityMonitor-Setup-1.4.13-x64.exe", selected.Name);
        Assert.Equal(100, selected.Size);
        Assert.Equal(new string('A', 64), selected.Sha256);
        Assert.Null(AppUpdateChecker.SelectInstallerAsset(assets, version, Architecture.Arm64));
        Assert.Null(AppUpdateChecker.SelectInstallerAsset([assets[0], assets[0]], version, Architecture.X64));
    }

    [Fact]
    public void SelectInstallerAsset_PreservesBuildMetadataInThePublishedAssetName()
    {
        string name = "DiskActivityMonitor-Setup-1.4.13+portable-x64.exe";
        var asset = (name,
            $"https://github.com/andrewtheart/DiskActivityMonitor/releases/download/v1.4.13+portable/{name}",
            100L,
            (string?)("sha256:" + new string('a', 64)));

        Assert.Equal(name, AppUpdateChecker.SelectInstallerAsset([asset], "v1.4.13+portable", Architecture.X64)?.Name);
    }

    [Theory]
    [InlineData("https://example.com/andrewtheart/DiskActivityMonitor/releases/download/v1.4.13/DiskActivityMonitor-Setup-1.4.13-x64.exe")]
    [InlineData("https://github.com/other/repo/releases/download/v1.4.13/DiskActivityMonitor-Setup-1.4.13-x64.exe")]
    [InlineData("https://github.com/andrewtheart/DiskActivityMonitor/releases/download/v1.4.13/other.exe")]
    [InlineData("https://github.com/andrewtheart/DiskActivityMonitor/releases/download/v1.4.13/DiskActivityMonitor-Setup-1.4.13-x64.exe?x=1")]
    public void SelectInstallerAsset_RejectsUntrustedUrls(string url)
    {
        var asset = ("DiskActivityMonitor-Setup-1.4.13-x64.exe", url, 100L, (string?)("sha256:" + new string('a', 64)));

        Assert.Null(AppUpdateChecker.SelectInstallerAsset([asset], new Version(1, 4, 13), Architecture.X64));
    }

    [Fact]
    public void SelectInstallerAsset_RejectsUnencryptedTransport()
    {
        const string name = "DiskActivityMonitor-Setup-1.4.13-x64.exe";
        var builder = new UriBuilder(Uri.UriSchemeHttp, "github.com")
        {
            Path = $"andrewtheart/DiskActivityMonitor/releases/download/v1.4.13/{name}",
        };
        var asset = (name, builder.Uri.AbsoluteUri, 100L, (string?)("sha256:" + new string('a', 64)));

        Assert.Null(AppUpdateChecker.SelectInstallerAsset([asset], new Version(1, 4, 13), Architecture.X64));
    }

    [Fact]
    public async Task CheckLatestAsync_ParsesAThreePartReleaseAndExactCurrentArchitecture()
    {
        string arch = RuntimeInformation.ProcessArchitecture == Architecture.X86 ? "x86" : "x64";
        string name = $"DiskActivityMonitor-Setup-1.4.13-{arch}.exe";
        string json = $$"""
            {
              "tag_name": "v1.4.13",
              "name": "Disk Activity Monitor 1.4.13",
              "body": "notes",
              "html_url": "https://github.com/andrewtheart/DiskActivityMonitor/releases/tag/v1.4.13",
              "draft": false,
              "prerelease": false,
              "published_at": "2026-08-05T12:00:00Z",
              "assets": [
                {
                  "name": "{{name}}",
                  "browser_download_url": "https://github.com/andrewtheart/DiskActivityMonitor/releases/download/v1.4.13/{{name}}",
                  "size": 3,
                  "digest": "sha256:{{new string('b', 64)}}"
                }
              ]
            }
            """;
        using var client = new HttpClient(new StaticHandler(json));

        AppUpdateCheckResult? result = await AppUpdateChecker.CheckLatestAsync("1.4.12", client);

        Assert.NotNull(result);
        Assert.True(result.UpdateAvailable);
        Assert.Equal(new Version(1, 4, 13), result.LatestVersion);
        Assert.Equal(name, result.Release!.Installer.Name);
        Assert.Equal("notes", result.Release.ReleaseNotes);
    }

    [Fact]
    public async Task CheckLatestAsync_ReturnsNullForInvalidCurrentVersionOrHttpFailure()
    {
        using var client = new HttpClient(new ThrowingHandler(new HttpRequestException("network")));
        Assert.Null(await AppUpdateChecker.CheckLatestAsync("bad-version", client));
        Assert.Null(await AppUpdateChecker.CheckLatestAsync("1.4.12", client));
    }

    [Fact]
    public async Task CheckLatestAsync_ReturnsNullForNonSuccessStatus()
    {
        using var client = new HttpClient(new StaticStatusHandler(HttpStatusCode.Forbidden));
        Assert.Null(await AppUpdateChecker.CheckLatestAsync("1.4.12", client));
    }

    [Fact]
    public async Task CheckLatestAsync_ReturnsCurrentForDraftPrereleaseOrMissingTag()
    {
        const string baseJson = """
            {
              "tag_name": "v1.4.13",
              "name": "Disk Activity Monitor 1.4.13",
              "body": "notes",
              "html_url": "https://github.com/andrewtheart/DiskActivityMonitor/releases/tag/v1.4.13",
              "draft": false,
              "prerelease": false,
              "assets": []
            }
            """;

        string[] variants =
        [
            baseJson.Replace("\"draft\": false", "\"draft\": true", StringComparison.Ordinal),
            baseJson.Replace("\"prerelease\": false", "\"prerelease\": true", StringComparison.Ordinal),
            baseJson.Replace("\"tag_name\": \"v1.4.13\"", "\"tag_name\": null", StringComparison.Ordinal),
        ];

        foreach (string json in variants)
        {
            using var client = new HttpClient(new StaticHandler(json));
            AppUpdateCheckResult? result = await AppUpdateChecker.CheckLatestAsync("1.4.12", client);
            Assert.NotNull(result);
            Assert.Equal(new Version(1, 4, 12), result.CurrentVersion);
            Assert.Equal(new Version(1, 4, 12), result.LatestVersion);
            Assert.Null(result.Release);
        }
    }

    [Fact]
    public async Task CheckLatestAsync_UsesFallbackReleasePageAndTruncatesNotes()
    {
        string arch = RuntimeInformation.ProcessArchitecture == Architecture.X86 ? "x86" : "x64";
        string name = $"DiskActivityMonitor-Setup-1.4.13-{arch}.exe";
        string longBody = new string('x', AppUpdateChecker.MaxReleaseNotesChars + 100);
        string json = $$"""
            {
              "tag_name": "v1.4.13",
              "name": " ",
              "body": "{{longBody}}",
              "html_url": "https://example.com/release",
              "draft": false,
              "prerelease": false,
              "assets": [
                {
                  "name": "{{name}}",
                  "browser_download_url": "https://github.com/andrewtheart/DiskActivityMonitor/releases/download/v1.4.13/{{name}}",
                  "size": 3,
                  "digest": "sha256:{{new string('b', 64)}}"
                }
              ]
            }
            """;
        using var client = new HttpClient(new StaticHandler(json));

        AppUpdateCheckResult? result = await AppUpdateChecker.CheckLatestAsync("1.4.12", client);

        Assert.NotNull(result?.Release);
        Assert.Equal(AppUpdateChecker.LatestReleasePage, result.Release!.ReleasePage);
        Assert.StartsWith("Disk Activity Monitor 1.4.13", result.Release.Name, StringComparison.Ordinal);
        Assert.EndsWith("[Release notes truncated by Disk Activity Monitor.]", result.Release.ReleaseNotes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckLatestAsync_ReturnsLatestWithoutRelease_WhenNotNewer()
    {
        string arch = RuntimeInformation.ProcessArchitecture == Architecture.X86 ? "x86" : "x64";
        string name = $"DiskActivityMonitor-Setup-1.4.12-{arch}.exe";
        string json = $$"""
            {
              "tag_name": "v1.4.12",
              "name": "Disk Activity Monitor 1.4.12",
              "body": "notes",
              "html_url": "https://github.com/andrewtheart/DiskActivityMonitor/releases/tag/v1.4.12",
              "draft": false,
              "prerelease": false,
              "assets": [
                {
                  "name": "{{name}}",
                  "browser_download_url": "https://github.com/andrewtheart/DiskActivityMonitor/releases/download/v1.4.12/{{name}}",
                  "size": 3,
                  "digest": "sha256:{{new string('b', 64)}}"
                }
              ]
            }
            """;
        using var client = new HttpClient(new StaticHandler(json));

        AppUpdateCheckResult? result = await AppUpdateChecker.CheckLatestAsync("1.4.12", client);

        Assert.NotNull(result);
        Assert.Equal(new Version(1, 4, 12), result.LatestVersion);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task CheckLatestAsync_ReturnsNullWhenMetadataTooLarge()
    {
        byte[] payload = new byte[AppUpdateChecker.MaxReleaseMetadataBytes + 1];
        Array.Fill(payload, (byte)'x');
        using var client = new HttpClient(new BytePayloadHandler(payload));

        Assert.Null(await AppUpdateChecker.CheckLatestAsync("1.4.12", client));
    }

    [Fact]
    public async Task CheckLatestAsync_DisposesOwnedClientFromFactory()
    {
        var handler = new StaticStatusHandler(HttpStatusCode.Forbidden);
        var client = new DisposableTrackingHttpClient(handler);
        Func<HttpClient> previous = AppUpdateChecker.CreateHttpClient;
        AppUpdateChecker.CreateHttpClient = () => client;
        try
        {
            Assert.Null(await AppUpdateChecker.CheckLatestAsync("1.4.12", httpClient: null));
            Assert.True(client.WasDisposed);
        }
        finally
        {
            AppUpdateChecker.CreateHttpClient = previous;
        }
    }

    [Fact]
    public async Task VerifyDownloadedAssetAsync_ChecksLengthAndSha256()
    {
        string path = Path.GetTempFileName();
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes("installer");
            await File.WriteAllBytesAsync(path, bytes);
            var asset = new AppReleaseAsset(
                "setup.exe",
                new Uri("https://github.com/andrewtheart/DiskActivityMonitor/releases/download/v1/setup.exe"),
                bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)));

            Assert.True(await AppUpdateChecker.VerifyDownloadedAssetAsync(path, asset));
            Assert.False(await AppUpdateChecker.VerifyDownloadedAssetAsync(path, asset with { Size = bytes.Length + 1 }));
            Assert.False(await AppUpdateChecker.VerifyDownloadedAssetAsync(path, asset with { Sha256 = new string('0', 64) }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenVerifiedAssetAsync_DeniesReplacementUntilCallerReleasesTheHandle()
    {
        string path = Path.GetTempFileName();
        byte[] bytes = Encoding.UTF8.GetBytes("installer");
        await File.WriteAllBytesAsync(path, bytes);
        var asset = new AppReleaseAsset(
            "setup.exe",
            new Uri("https://github.com/andrewtheart/DiskActivityMonitor/releases/download/v1/setup.exe"),
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)));
        try
        {
            await using VerifiedAppUpdateInstaller? verified = await AppUpdateChecker.OpenVerifiedAssetAsync(path, asset);
            Assert.NotNull(verified);
            Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenVerifiedAssetAsync_ReturnsNullWhenPathCannotBeOpened()
    {
        string path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe");
        var asset = new AppReleaseAsset(
            "setup.exe",
            new Uri("https://github.com/andrewtheart/DiskActivityMonitor/releases/download/v1/setup.exe"),
            1,
            new string('0', 64));

        Assert.Null(await AppUpdateChecker.OpenVerifiedAssetAsync(path, asset));
    }

    [Fact]
    public async Task OpenVerifiedAssetAsync_ReturnsNullWhenHashOperationThrows()
    {
        string path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("installer"));
        var asset = new AppReleaseAsset(
            "setup.exe",
            new Uri("https://github.com/andrewtheart/DiskActivityMonitor/releases/download/v1/setup.exe"),
            new FileInfo(path).Length,
            new string('0', 64));
        Func<Stream, CancellationToken, ValueTask<byte[]>> previous = AppUpdateChecker.HashDataAsync;
        AppUpdateChecker.HashDataAsync = (_, _) => ValueTask.FromException<byte[]>(new InvalidDataException("forced"));
        try
        {
            Assert.Null(await AppUpdateChecker.OpenVerifiedAssetAsync(path, asset));
        }
        finally
        {
            AppUpdateChecker.HashDataAsync = previous;
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenVerifiedAssetAsync_RejectsAJunctionAnywhereInTheLaunchPath()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dam_update_junction_{Guid.NewGuid():N}");
        string target = Path.Combine(root, "target");
        string junction = Path.Combine(root, "junction");
        Directory.CreateDirectory(target);
        byte[] bytes = Encoding.UTF8.GetBytes("installer");
        await File.WriteAllBytesAsync(Path.Combine(target, "setup.exe"), bytes);
        using (Process process = Process.Start(new ProcessStartInfo(
            "cmd.exe",
            $"/d /c mklink /J \"{junction}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!)
        {
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        var asset = new AppReleaseAsset(
            "setup.exe",
            new Uri("https://github.com/andrewtheart/DiskActivityMonitor/releases/download/v1/setup.exe"),
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)));
        try
        {
            Assert.Null(await AppUpdateChecker.OpenVerifiedAssetAsync(Path.Combine(junction, "setup.exe"), asset));
        }
        finally
        {
            try { Directory.Delete(junction); } catch { }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OpenVerifiedAssetAsync_DeniesAncestorRenameThroughLaunch()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dam_update_pathlock_{Guid.NewGuid():N}");
        string directory = Path.Combine(root, "download");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "setup.exe");
        byte[] bytes = Encoding.UTF8.GetBytes("installer");
        await File.WriteAllBytesAsync(path, bytes);
        var asset = new AppReleaseAsset(
            "setup.exe",
            new Uri("https://github.com/andrewtheart/DiskActivityMonitor/releases/download/v1/setup.exe"),
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)));
        try
        {
            await using VerifiedAppUpdateInstaller? verified = await AppUpdateChecker.OpenVerifiedAssetAsync(path, asset);
            Assert.NotNull(verified);
            Assert.ThrowsAny<IOException>(() => Directory.Move(directory, directory + "-moved"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StaticHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class StaticStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class BytePayloadHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
    }

    private sealed class DisposableTrackingHttpClient(HttpMessageHandler handler) : HttpClient(handler)
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}