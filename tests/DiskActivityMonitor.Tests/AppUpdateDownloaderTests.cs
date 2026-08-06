using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using DiskActivityMonitor.Core.Updates;

namespace DiskActivityMonitor.Tests;

public sealed class AppUpdateDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dam_update_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task DownloadAsync_StreamsReportsProgressAndReturnsVerifiedFile()
    {
        byte[] payload = Encoding.UTF8.GetBytes("installer payload");
        AppReleaseAsset asset = Asset(payload);
        var handler = new ResponseHandler(_ => Response(HttpStatusCode.OK, payload));
        using var client = new HttpClient(handler);
        var progressValues = new List<AppUpdateDownloadProgress>();
        var progress = new InlineProgress(value => progressValues.Add(value));

        string path = await AppUpdateDownloader.DownloadAsync(asset, _root, 1, progress, client);

        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Assert.StartsWith(_root, path, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(_root, Path.GetDirectoryName(path));
        Assert.Equal(payload.Length, Assert.Single(progressValues).ReceivedBytes);
        Assert.True(await AppUpdateChecker.VerifyDownloadedAssetAsync(path, asset));
    }

    [Fact]
    public async Task DownloadAsync_DeletesPartialDownloadWhenHashDoesNotMatch()
    {
        byte[] payload = Encoding.UTF8.GetBytes("installer payload");
        AppReleaseAsset asset = Asset(payload) with { Sha256 = new string('0', 64) };
        using var client = new HttpClient(new ResponseHandler(_ => Response(HttpStatusCode.OK, payload)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AppUpdateDownloader.DownloadAsync(asset, _root, 1, httpClient: client));

        Assert.True(!Directory.Exists(_root) || Directory.GetFileSystemEntries(_root).Length == 0);
    }

    [Fact]
    public async Task DownloadAsync_RejectsAssetLargerThanConfiguredLimitBeforeRequest()
    {
        byte[] payload = [1, 2, 3];
        AppReleaseAsset asset = Asset(payload) with { Size = 2 * 1024 * 1024 };
        var handler = new ResponseHandler(_ => Response(HttpStatusCode.OK, payload));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AppUpdateDownloader.DownloadAsync(asset, _root, 1, httpClient: client));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void InstallerSizeLimitBytes_UsesDefaultForNonPositiveValues()
    {
        long expected = (long)AppUpdateDownloader.DefaultMaxInstallerSizeMb * 1024 * 1024;
        Assert.Equal(expected, AppUpdateDownloader.InstallerSizeLimitBytes(0));
        Assert.Equal(expected, AppUpdateDownloader.InstallerSizeLimitBytes(-1));
        Assert.Equal(5L * 1024 * 1024, AppUpdateDownloader.InstallerSizeLimitBytes(5));
    }

    [Fact]
    public async Task DownloadAsync_RejectsMismatchedContentLength()
    {
        byte[] payload = Encoding.UTF8.GetBytes("installer payload");
        AppReleaseAsset asset = Asset(payload);
        using var client = new HttpClient(new ResponseHandler(_ =>
        {
            var response = Response(HttpStatusCode.OK, payload);
            response.Content.Headers.ContentLength = payload.Length + 1;
            return response;
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AppUpdateDownloader.DownloadAsync(asset, _root, 1, httpClient: client));
    }

    [Fact]
    public async Task DownloadAsync_RejectsRelativeRedirectToNonHttps()
    {
        byte[] payload = [1, 2, 3];
        int count = 0;
        using var client = new HttpClient(new ResponseHandler(request =>
        {
            if (count++ == 0)
            {
                return new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("/setup.exe", UriKind.Relative) },
                };
            }

            return Response(HttpStatusCode.OK, payload);
        }));
        AppReleaseAsset asset = Asset(payload) with
        {
            DownloadUri = new Uri("https://github.com/andrewtheart/DiskActivityMonitor/releases/download/v1.4.13/DiskActivityMonitor-Setup-1.4.13-x64.exe"),
        };

        string path = await AppUpdateDownloader.DownloadAsync(asset, _root, 1, httpClient: client);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task DownloadAsync_RejectsTooManyRedirects()
    {
        byte[] payload = [1, 2, 3];
        using var client = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://github.com/andrewtheart/DiskActivityMonitor/releases/download/v1.4.13/next.exe") },
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AppUpdateDownloader.DownloadAsync(Asset(payload), _root, 1, httpClient: client));
    }

    [Fact]
    public async Task DownloadAsync_RejectsNonOkResponse()
    {
        byte[] payload = [1, 2, 3];
        using var client = new HttpClient(new ResponseHandler(_ => Response(HttpStatusCode.NotFound, payload)));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            AppUpdateDownloader.DownloadAsync(Asset(payload), _root, 1, httpClient: client));
    }

    [Fact]
    public async Task DownloadAsync_CleansUpEvenWhenDeleteThrows()
    {
        byte[] payload = Encoding.UTF8.GetBytes("installer payload");
        AppReleaseAsset asset = Asset(payload);
        string destinationRoot = Path.Combine(Path.GetTempPath(), $"dam_update_file_root_{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(destinationRoot, "not a directory");
        using var client = new HttpClient(new ResponseHandler(_ => Response(HttpStatusCode.OK, payload)));
        try
        {
            await Assert.ThrowsAsync<IOException>(() =>
                AppUpdateDownloader.DownloadAsync(asset, destinationRoot, 1, httpClient: client));
        }
        finally
        {
            try { File.Delete(destinationRoot); } catch { }
        }
    }

    [Fact]
    public async Task DownloadAsync_RejectsRedirectAwayFromHttps()
    {
        byte[] payload = [1, 2, 3];
        var insecureRedirect = new UriBuilder(Uri.UriSchemeHttp, "example.test") { Path = "setup.exe" }.Uri;
        using var client = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = insecureRedirect },
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AppUpdateDownloader.DownloadAsync(Asset(payload), _root, 1, httpClient: client));
    }

    [Fact]
    public async Task DownloadAsync_RejectsPathLikeAssetNamesBeforeCreatingFiles()
    {
        byte[] payload = [1, 2, 3];
        AppReleaseAsset asset = Asset(payload) with
        {
            Name = @"..\evil.exe",
            DownloadUri = new Uri("https://github.com/andrewtheart/DiskActivityMonitor/releases/download/v1.4.13/..%5Cevil.exe"),
        };
        using var client = new HttpClient(new ResponseHandler(_ => Response(HttpStatusCode.OK, payload)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AppUpdateDownloader.DownloadAsync(asset, _root, 1, httpClient: client));

        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "evil.exe")));
    }

    private static AppReleaseAsset Asset(byte[] payload)
    {
        const string name = "DiskActivityMonitor-Setup-1.4.13-x64.exe";
        return new AppReleaseAsset(
            name,
            new Uri($"https://github.com/andrewtheart/DiskActivityMonitor/releases/download/v1.4.13/{name}"),
            payload.Length,
            Convert.ToHexString(SHA256.HashData(payload)));
    }

    private static HttpResponseMessage Response(HttpStatusCode status, byte[] payload)
        => new(status) { Content = new ByteArrayContent(payload) };

    private sealed class InlineProgress(Action<AppUpdateDownloadProgress> report)
        : IProgress<AppUpdateDownloadProgress>
    {
        public void Report(AppUpdateDownloadProgress value) => report(value);
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(response(request));
        }
    }
}