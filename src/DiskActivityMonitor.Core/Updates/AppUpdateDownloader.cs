using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace DiskActivityMonitor.Core.Updates;

public readonly record struct AppUpdateDownloadProgress(long ReceivedBytes, long TotalBytes);

/// <summary>Streams a trusted GitHub release asset to a private directory and verifies it in flight.</summary>
public static class AppUpdateDownloader
{
    public const int DefaultMaxInstallerSizeMb = 256;
    private const int MaxRedirects = 5;

    public static long InstallerSizeLimitBytes(int maximumInstallerSizeMb)
    {
        int megabytes = maximumInstallerSizeMb > 0
            ? maximumInstallerSizeMb
            : DefaultMaxInstallerSizeMb;
        return checked((long)megabytes * 1024 * 1024);
    }

    public static async Task<string> DownloadAsync(
        AppReleaseAsset asset,
        string destinationRoot,
        int maximumInstallerSizeMb,
        IProgress<AppUpdateDownloadProgress>? progress = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        long maximumBytes = InstallerSizeLimitBytes(maximumInstallerSizeMb);
        ValidateAsset(asset, maximumBytes);

        bool ownsClient = httpClient is null;
        HttpClient client = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        string? downloadDirectory = null;
        try
        {
            using HttpResponseMessage response = await OpenDownloadAsync(
                client,
                asset.DownloadUri,
                cancellationToken).ConfigureAwait(false);
            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > 0 && (contentLength != asset.Size || contentLength > maximumBytes))
                throw new InvalidDataException("The installer download size does not match trusted release metadata.");

            Directory.CreateDirectory(destinationRoot);
            downloadDirectory = Path.Combine(destinationRoot, $"update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(downloadDirectory);
            string destination = Path.Combine(downloadDirectory, asset.Name);
            string partialDestination = destination + ".partial";

            long received = 0;
            string actualHash;
            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(
                partialDestination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[64 * 1024];
                while (true)
                {
                    int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    received += read;
                    if (received > asset.Size || received > maximumBytes)
                        throw new InvalidDataException("The installer download exceeded trusted release metadata.");
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(new AppUpdateDownloadProgress(received, asset.Size));
                }
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                actualHash = Convert.ToHexString(hash.GetHashAndReset());
            }

            if (received != asset.Size
                || !string.Equals(actualHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The installer failed SHA-256 integrity verification.");
            }

            File.Move(partialDestination, destination);
            return destination;
        }
        catch
        {
            if (downloadDirectory is not null)
            {
                try { Directory.Delete(downloadDirectory, recursive: true); }
                catch { }
            }
            throw;
        }
        finally
        {
            if (ownsClient)
                client.Dispose();
        }
    }

    private static void ValidateAsset(AppReleaseAsset asset, long maximumBytes)
    {
        Uri uri = asset.DownloadUri;
        string releasePrefix = $"/{AppUpdateChecker.Repository}/releases/download/";
        string fileName = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        bool safeName = asset.Name.Length > 0
            && string.Equals(Path.GetFileName(asset.Name), asset.Name, StringComparison.Ordinal)
            && asset.Name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        bool valid = safeName
            && asset.Size > 0
            && asset.Size <= maximumBytes
            && asset.Sha256.Length == 64
            && asset.Sha256.All(Uri.IsHexDigit)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && uri.AbsolutePath.StartsWith(releasePrefix, StringComparison.OrdinalIgnoreCase)
            && string.Equals(fileName, asset.Name, StringComparison.OrdinalIgnoreCase)
            && asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        if (!valid)
            throw new InvalidDataException("The release does not contain a trusted installer asset.");
    }

    private static async Task<HttpResponseMessage> OpenDownloadAsync(
        HttpClient client,
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        Uri current = initialUri;
        for (int redirects = 0; redirects <= MaxRedirects; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DiskActivityMonitor", "Updater"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest
                && response.Headers.Location is { } location)
            {
                response.Dispose();
                if (redirects == MaxRedirects)
                    throw new InvalidDataException("Too many redirects while downloading the installer.");
                Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (next.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(next.UserInfo))
                    throw new InvalidDataException("Refusing an insecure installer download redirect.");
                current = next;
                continue;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                response.Dispose();
                throw new HttpRequestException($"GitHub returned HTTP {(int)response.StatusCode}.");
            }
            return response;
        }

        throw new InvalidDataException("Too many redirects while downloading the installer.");
    }
}