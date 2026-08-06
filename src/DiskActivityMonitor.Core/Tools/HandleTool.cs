using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace DiskActivityMonitor.Core.Tools;

/// <summary>Outcome of a <c>handle.exe</c> invocation.</summary>
/// <param name="Success">True when the tool ran and produced output.</param>
/// <param name="Output">Raw standard output.</param>
/// <param name="Error">Human-readable failure reason, else null.</param>
/// <param name="Elevated">Whether the calling process was elevated, which changes result completeness.</param>
public sealed record HandleRunResult(bool Success, string Output, string? Error, bool Elevated);

/// <summary>
/// Locates, installs and runs Sysinternals <c>handle.exe</c>.
/// </summary>
/// <remarks>
/// The tool is looked for beside the monitoring database first (where this class installs it) and
/// then on <c>PATH</c>, so a machine-wide copy is preferred over downloading another one. Downloads
/// come only from the official Sysinternals HTTPS endpoint.
/// </remarks>
public static class HandleTool
{
    /// <summary>Official Sysinternals download for the Handle utility.</summary>
    public const string DownloadUrl = "https://download.sysinternals.com/files/Handle.zip";

    /// <summary>Host the archive must come from; guards against redirects to untrusted origins.</summary>
    public const string DownloadHost = "download.sysinternals.com";

    /// <summary>Executable names in preference order for the current architecture.</summary>
    public static IReadOnlyList<string> CandidateNames { get; } = BuildCandidateNames();

    private static IReadOnlyList<string> BuildCandidateNames() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => new[] { "handle64a.exe", "handle64.exe", "handle.exe" },
        Architecture.X64 => new[] { "handle64.exe", "handle.exe" },
        _ => new[] { "handle.exe", "handle64.exe" },
    };

    /// <summary>
    /// Returns the full path to an available Handle executable, or null when it is not installed.
    /// Searches the install directory first, then every <c>PATH</c> entry.
    /// </summary>
    public static string? Locate(string? installDirectory = null)
    {
        string directory = installDirectory ?? Paths.BaseDirectory;

        foreach (var name in CandidateNames)
        {
            var local = Path.Combine(directory, name);
            if (File.Exists(local)) return local;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathValue)) return null;

        foreach (var segment in pathValue.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(segment)) continue;

            string folder;
            try
            {
                folder = Environment.ExpandEnvironmentVariables(segment.Trim().Trim('"'));
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (folder.Length == 0 || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;

            foreach (var name in CandidateNames)
            {
                try
                {
                    var candidate = Path.Combine(folder, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entry - skip it.
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Downloads Handle from Sysinternals and extracts the executables next to the database.
    /// Returns the path of the installed executable.
    /// </summary>
    /// <exception cref="InvalidOperationException">The archive did not contain a Handle executable.</exception>
    public static async Task<string> InstallAsync(
        string? installDirectory = null,
        HttpMessageHandler? handler = null,
        CancellationToken cancellationToken = default)
    {
        string directory = installDirectory ?? Paths.BaseDirectory;
        Directory.CreateDirectory(directory);

        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromMinutes(2);

        var uri = new Uri(DownloadUrl);
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals(DownloadHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The Handle download URL is not the official Sysinternals endpoint.");
        }

        using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // The final response must still be the trusted host after any redirects.
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is not null
            && (!finalUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !finalUri.Host.Equals(DownloadHost, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"The Handle download redirected to an untrusted host: {finalUri.Host}.");
        }

        using var archiveStream = new MemoryStream();
        await response.Content.CopyToAsync(archiveStream, cancellationToken).ConfigureAwait(false);
        archiveStream.Position = 0;

        return Extract(archiveStream, directory);
    }

    /// <summary>
    /// Extracts only the Handle executables and EULA from an archive into
    /// <paramref name="directory"/>, then returns the preferred executable path.
    /// </summary>
    internal static string Extract(Stream archiveStream, string directory)
    {
        var allowed = new HashSet<string>(CandidateNames, StringComparer.OrdinalIgnoreCase) { "Eula.txt" };
        string fullDirectory = Path.GetFullPath(directory);

        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true))
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.Name.Length == 0 || !allowed.Contains(entry.Name)) continue;

                // Flatten to the entry name and verify containment, defeating zip-slip paths.
                string destination = Path.GetFullPath(Path.Combine(fullDirectory, entry.Name));
                if (!destination.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;

                entry.ExtractToFile(destination, overwrite: true);
            }
        }

        foreach (string name in CandidateNames)
        {
            string candidate = Path.Combine(fullDirectory, name);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new InvalidOperationException("The downloaded archive did not contain a Handle executable.");
    }

    /// <summary>Lists every open handle held by processes matching <paramref name="processName"/>.</summary>
    public static Task<HandleRunResult> ListProcessHandlesAsync(
        string executablePath,
        string processName,
        CancellationToken cancellationToken = default)
        => RunAsync(executablePath, new[] { "-p", processName }, cancellationToken);

    /// <summary>Finds every process holding an open handle to <paramref name="path"/>.</summary>
    public static Task<HandleRunResult> FindPathHandlesAsync(
        string executablePath,
        string path,
        CancellationToken cancellationToken = default)
        => RunAsync(executablePath, new[] { "-u", path }, cancellationToken);

    /// <summary>
    /// Runs Handle with the supplied arguments. Arguments are passed through
    /// <see cref="ProcessStartInfo.ArgumentList"/>, so user-supplied values cannot inject commands.
    /// </summary>
    private static async Task<HandleRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        bool elevated = IsElevated();

        var info = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
        };

        // -accepteula suppresses the first-run EULA dialog; -nobanner removes the version header.
        info.ArgumentList.Add("-accepteula");
        info.ArgumentList.Add("-nobanner");
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return new HandleRunResult(false, "", "Handle could not be started.", elevated);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return new HandleRunResult(false, "", "Handle did not finish within 45 seconds.", elevated);
            }

            string output = await stdoutTask.ConfigureAwait(false);
            string error = await stderrTask.ConfigureAwait(false);

            if (output.Length == 0 && error.Length > 0)
                return new HandleRunResult(false, "", error.Trim(), elevated);

            return new HandleRunResult(true, output, null, elevated);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new HandleRunResult(false, "", $"Handle could not be started: {ex.Message}", elevated);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The process already exited.
        }
    }

    /// <summary>
    /// True when the current process runs elevated. Handle can only enumerate handles owned by
    /// other users' processes when elevated, so results are otherwise incomplete.
    /// </summary>
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
