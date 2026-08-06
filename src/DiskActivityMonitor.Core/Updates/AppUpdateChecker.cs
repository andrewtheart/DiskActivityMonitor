using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskActivityMonitor.Core.Updates;

public sealed record AppReleaseAsset(string Name, Uri DownloadUri, long Size, string Sha256);

public sealed record AppReleaseInfo(
    Version Version,
    string Tag,
    string Name,
    string ReleaseNotes,
    Uri ReleasePage,
    DateTimeOffset? PublishedUtc,
    AppReleaseAsset Installer);

public sealed record AppUpdateCheckResult(Version CurrentVersion, Version LatestVersion, AppReleaseInfo? Release)
{
    public bool UpdateAvailable => LatestVersion > CurrentVersion;
}

/// <summary>
/// A verified installer plus no-follow directory handles that prevent every path component from
/// being renamed or replaced before process creation.
/// </summary>
public sealed class VerifiedAppUpdateInstaller : IAsyncDisposable
{
    private readonly IReadOnlyList<SafeFileHandle> _directoryHandles;

    internal VerifiedAppUpdateInstaller(string path, FileStream stream, IReadOnlyList<SafeFileHandle> directoryHandles)
    {
        Path = path;
        Stream = stream;
        _directoryHandles = directoryHandles;
    }

    public string Path { get; }
    public FileStream Stream { get; }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync().ConfigureAwait(false);
        foreach (SafeFileHandle handle in _directoryHandles.Reverse())
            handle.Dispose();
    }
}

public enum AppUpdateCheckMode
{
    Prompt = 0,
    Automatic = 1,
    Manual = 2,
    Off = 3,
}

/// <summary>Queries and validates Disk Activity Monitor's official GitHub latest release.</summary>
public static class AppUpdateChecker
{
    public const string Repository = "andrewtheart/DiskActivityMonitor";
    public static readonly TimeSpan DefaultAutoCheckInterval = TimeSpan.FromDays(7);
    public static readonly Uri LatestReleaseApi = new($"https://api.github.com/repos/{Repository}/releases/latest");
    public static readonly Uri LatestReleasePage = new($"https://github.com/{Repository}/releases/latest");
    public const int MaxReleaseMetadataBytes = 512 * 1024;
    public const int MaxReleaseNotesChars = 64 * 1024;
    internal static Func<HttpClient> CreateHttpClient = () => new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    internal static Func<Stream, CancellationToken, ValueTask<byte[]>> HashDataAsync =
        (stream, cancellationToken) => System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken);

    public static bool ShouldAutoCheck(DateTimeOffset? lastCheckUtc, DateTimeOffset nowUtc, TimeSpan interval)
        => lastCheckUtc is not { } last || nowUtc - last >= interval;

    public static bool TryParseVersion(string? value, out Version version)
        => TryParseVersion(value, out version, out _);

    private static bool TryParseVersion(string? value, out Version version, out string assetVersion)
    {
        string text = (value ?? string.Empty).Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];
        assetVersion = text;
        int metadata = text.IndexOf('+');
        if (metadata >= 0)
            text = text[..metadata];

        if (!text.Contains('-', StringComparison.Ordinal)
            && Version.TryParse(text, out Version? parsed)
            && parsed.Major >= 0
            && parsed.Minor >= 0
            && parsed.Build >= 0)
        {
            version = parsed;
            return true;
        }

        version = new Version(0, 0, 0);
        assetVersion = string.Empty;
        return false;
    }

    public static AppReleaseAsset? SelectInstallerAsset(
        IEnumerable<(string Name, string Url, long Size, string? Digest)> assets,
        Version releaseVersion,
        Architecture architecture)
        => SelectInstallerAsset(assets, releaseVersion.ToString(), architecture);

    public static AppReleaseAsset? SelectInstallerAsset(
        IEnumerable<(string Name, string Url, long Size, string? Digest)> assets,
        string releaseVersion,
        Architecture architecture)
    {
        if (!TryParseVersion(releaseVersion, out _, out string assetVersion))
            return null;
        string arch = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            _ => string.Empty,
        };
        if (arch.Length == 0)
            return null;

        string expected = $"DiskActivityMonitor-Setup-{assetVersion}-{arch}.exe";
        var matches = assets.Where(asset =>
            string.Equals(asset.Name, expected, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1
            || matches[0].Size <= 0
            || !Uri.TryCreate(matches[0].Url, UriKind.Absolute, out Uri? uri)
            || !IsAllowedInstallerUri(uri, expected))
        {
            return null;
        }

        string digest = matches[0].Digest ?? string.Empty;
        if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            digest = digest[7..];
        if (digest.Length != 64 || !digest.All(Uri.IsHexDigit))
            return null;

        return new AppReleaseAsset(expected, uri, matches[0].Size, digest.ToUpperInvariant());
    }

    public static async Task<AppUpdateCheckResult?> CheckLatestAsync(
        string currentVersion,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseVersion(currentVersion, out Version current))
            return null;

        bool ownsClient = httpClient is null;
        HttpClient client = httpClient ?? CreateHttpClient();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DiskActivityMonitor", current.ToString()));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            byte[] json = await ReadBoundedAsync(stream, MaxReleaseMetadataBytes, cancellationToken).ConfigureAwait(false);
            GitHubReleaseDto? dto = JsonSerializer.Deserialize(json, AppUpdateJsonContext.Default.GitHubReleaseDto);
            if (dto is null || dto.Draft || dto.Prerelease || !TryParseVersion(dto.TagName, out Version latest, out _))
                return new AppUpdateCheckResult(current, current, null);

            AppReleaseAsset? installer = SelectInstallerAsset(
                (dto.Assets ?? []).Select(asset => (
                    asset.Name ?? string.Empty,
                    asset.BrowserDownloadUrl ?? string.Empty,
                    asset.Size,
                    asset.Digest)),
                dto.TagName!,
                RuntimeInformation.ProcessArchitecture);
            if (installer is null)
                return new AppUpdateCheckResult(current, latest, null);

            Uri releasePage = Uri.TryCreate(dto.HtmlUrl, UriKind.Absolute, out Uri? page)
                && page.Scheme == Uri.UriSchemeHttps
                && string.Equals(page.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                ? page
                : LatestReleasePage;
            string notes = dto.Body ?? string.Empty;
            if (notes.Length > MaxReleaseNotesChars)
                notes = notes[..MaxReleaseNotesChars] + "\n\n[Release notes truncated by Disk Activity Monitor.]";
            var release = new AppReleaseInfo(
                latest,
                dto.TagName!,
                string.IsNullOrWhiteSpace(dto.Name) ? $"Disk Activity Monitor {latest}" : dto.Name,
                notes,
                releasePage,
                dto.PublishedAt,
                installer);
            return new AppUpdateCheckResult(current, latest, latest > current ? release : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidDataException)
        {
            return null;
        }
        finally
        {
            if (ownsClient)
                client.Dispose();
        }
    }

    public static async Task<bool> VerifyDownloadedAssetAsync(
        string path,
        AppReleaseAsset asset,
        CancellationToken cancellationToken = default)
    {
        await using VerifiedAppUpdateInstaller? installer = await OpenVerifiedAssetAsync(
            path,
            asset,
            cancellationToken).ConfigureAwait(false);
        return installer is not null;
    }

    /// <summary>
    /// Opens and verifies an installer while denying write/delete sharing. The caller keeps the
    /// returned handle alive through process creation so verified bytes cannot be swapped first.
    /// </summary>
    public static async Task<VerifiedAppUpdateInstaller?> OpenVerifiedAssetAsync(
        string path,
        AppReleaseAsset asset,
        CancellationToken cancellationToken = default)
    {
        var directoryHandles = new List<SafeFileHandle>();
        FileStream? stream = null;
        try
        {
            string fullPath = Path.GetFullPath(path);
            foreach (string directory in PathComponents(Path.GetDirectoryName(fullPath)!))
                directoryHandles.Add(OpenNoFollow(directory, directory: true));

            SafeFileHandle fileHandle = OpenNoFollow(fullPath, directory: false);
            stream = new FileStream(fileHandle, FileAccess.Read);
            if (stream.Length != asset.Size)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                DisposeHandles(directoryHandles);
                return null;
            }
            byte[] hash = await HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(Convert.ToHexString(hash), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                DisposeHandles(directoryHandles);
                return null;
            }
            stream.Position = 0;
            return new VerifiedAppUpdateInstaller(fullPath, stream, directoryHandles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or NotSupportedException or InvalidDataException)
        {
            if (stream is not null)
                await stream.DisposeAsync().ConfigureAwait(false);
            DisposeHandles(directoryHandles);
            return null;
        }
    }

    private static IEnumerable<string> PathComponents(string directory)
    {
        string full = Path.GetFullPath(directory);
        string root = Path.GetPathRoot(full) ?? throw new NotSupportedException("The installer path has no root.");
        yield return root;
        string relative = full[root.Length..];
        string current = root;
        foreach (string component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            yield return current;
        }
    }

    private static SafeFileHandle OpenNoFollow(string path, bool directory)
    {
        const uint GenericRead = 0x80000000;
        const uint ReadAttributes = 0x00000080;
        const uint ShareRead = 0x00000001;
        const uint ShareWrite = 0x00000002;
        const uint OpenExisting = 3;
        const uint BackupSemantics = 0x02000000;
        const uint OpenReparsePoint = 0x00200000;
        const uint SequentialScan = 0x08000000;
        SafeFileHandle handle = CreateFileW(
            path,
            directory ? ReadAttributes : GenericRead,
            directory ? ShareRead | ShareWrite : ShareRead,
            IntPtr.Zero,
            OpenExisting,
            OpenReparsePoint | (directory ? BackupSemantics : SequentialScan),
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        if (!GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileAttributeTagInfo,
                out FileAttributeTagInfo info,
                (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        if ((info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            handle.Dispose();
            throw new InvalidDataException("The installer path contains a reparse point.");
        }
        return handle;
    }

    private static void DisposeHandles(IEnumerable<SafeFileHandle> handles)
    {
        foreach (SafeFileHandle handle in handles.Reverse())
            handle.Dispose();
    }

    private enum FileInfoByHandleClass
    {
        FileAttributeTagInfo = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        out FileAttributeTagInfo fileInformation,
        uint bufferSize);

    private static bool IsAllowedInstallerUri(Uri uri, string expectedName)
    {
        string releasePrefix = $"/{Repository}/releases/download/";
        string fileName = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        return uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && uri.AbsolutePath.StartsWith(releasePrefix, StringComparison.OrdinalIgnoreCase)
            && string.Equals(fileName, expectedName, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length + read > maximumBytes)
                throw new InvalidDataException("Release metadata is too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}

internal sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
    [JsonPropertyName("assets")] public GitHubReleaseAssetDto[]? Assets { get; set; }
}

internal sealed class GitHubReleaseAssetDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("digest")] public string? Digest { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(GitHubReleaseDto))]
internal sealed partial class AppUpdateJsonContext : JsonSerializerContext;