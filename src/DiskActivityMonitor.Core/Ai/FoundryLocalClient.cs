using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiskActivityMonitor.Core.Ai;

public sealed record FoundryChatResult(string Content, string RawResponseJson);

/// <summary>
/// Talks to a local Foundry Local install over its OpenAI-compatible HTTP endpoint, and manages
/// models via the `foundry` CLI. The inference service is a separate process, so native genai
/// crashes cannot take down the host app (unlike an in-process SDK).
/// </summary>
public sealed partial class FoundryLocalClient
{
    internal const string WingetPackageId = "Microsoft.FoundryLocal";

    private static readonly HttpClient LoopbackHttp = new(CreateLoopbackHandler(), disposeHandler: true)
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    private readonly Action<string>? _log;
    private readonly HttpClient _http;
    private string? _endpoint;

    public FoundryLocalClient(Action<string>? log = null)
    {
        _log = log;
        _http = LoopbackHttp;
    }

    internal FoundryLocalClient(HttpClient http, Action<string>? log = null)
    {
        _log = log;
        _http = http;
    }

    internal string? LastResponseJson { get; private set; }

    internal static SocketsHttpHandler CreateLoopbackHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    // Tool-calling-capable families, in rough preference order (best instruction/tool followers first).
    private static readonly string[] PreferredFamilies =
    [
        "qwen3-8b", "qwen3-14b", "qwen3-4b", "qwen2.5-7b", "phi-4-mini", "phi-4",
        "mistral-nemo", "gpt-oss-20b", "qwen3-1.7b", "qwen2.5-1.5b", "ministral", "smollm3",
    ];

    // Model families that cannot do general text extraction / tool following.
    private static readonly string[] ExcludedFragments =
        ["whisper", "embedding", "-asr", "speech", "-vl-", "-coder", "reasoning", "deepseek-r1", "nemotron"];

    [GeneratedRegex(@"https?://(?:localhost|\[[0-9a-f:]+\]|[0-9a-f:.]+):\d+", RegexOptions.IgnoreCase)]
    private static partial Regex EndpointRegex();
    [GeneratedRegex(@"(\d{1,3})\s*%")] private static partial Regex PercentRegex();

    /// <summary>True when the `foundry` CLI is available to the current Windows user.</summary>
    public static bool CliAvailable => ResolveExecutablePath("foundry.exe") is not null;

    internal static string? ResolveExecutablePath(
        string executableName,
        string? path = null,
        string? localAppData = null)
    {
        try
        {
            foreach (string rawDirectory in (path ?? Environment.GetEnvironmentVariable("PATH") ?? "")
                         .Split(Path.PathSeparator))
            {
                string directory = rawDirectory.Trim().Trim('"');
                if (directory.Length == 0) continue;
                string candidate = Path.Combine(directory, executableName);
                if (File.Exists(candidate)) return candidate;
            }

            string windowsApps = Path.Combine(
                localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "WindowsApps");
            string alias = Path.Combine(windowsApps, executableName);
            return File.Exists(alias) ? alias : null;
        }
        catch { return null; }
    }

    /// <summary>Installs the fixed Microsoft Foundry Local package and verifies its CLI.</summary>
    public static async Task InstallAsync(IProgress<string>? progress, CancellationToken ct)
    {
        string winget = ResolveExecutablePath("winget.exe")
            ?? throw new InvalidOperationException(
                "Windows Package Manager (winget) is unavailable. Install App Installer from Microsoft Store, then try again.");

        progress?.Report("Downloading and installing the official Microsoft package...");
        int installExitCode = await RunProcessAsync(CreateInstallStartInfo(winget), null, ct).ConfigureAwait(false);
        if (installExitCode != 0)
            throw new InvalidOperationException(
                $"Foundry Local installation failed (winget exit code 0x{unchecked((uint)installExitCode):X8}). " +
                "Check the internet connection and Windows Package Manager, then try again.");

        progress?.Report("Verifying the Foundry Local command...");
        string foundry = ResolveExecutablePath("foundry.exe")
            ?? throw new InvalidOperationException(
                "Foundry Local was installed, but its command is not available. Sign out and back in, then try again.");
        int verificationExitCode = await RunProcessAsync(foundry, "--version", null, ct).ConfigureAwait(false);
        if (verificationExitCode != 0)
            throw new InvalidOperationException(
                $"Foundry Local was installed, but verification failed (exit code 0x{unchecked((uint)verificationExitCode):X8}).");
    }

    internal static ProcessStartInfo CreateInstallStartInfo(string wingetPath)
    {
        var startInfo = CreateRedirectedStartInfo(wingetPath);
        foreach (string argument in new[]
                 {
                     "install",
                     "--exact",
                     "--id",
                     WingetPackageId,
                     "--source",
                     "winget",
                     "--accept-package-agreements",
                     "--accept-source-agreements",
                     "--disable-interactivity",
                 })
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    /// <summary>Discovers (and starts, if needed) the Foundry Local service and returns its base URL, or null.</summary>
    public async Task<string?> EnsureEndpointAsync(CancellationToken ct)
    {
        if (_endpoint is not null) return _endpoint;
        if (!CliAvailable) return null;

        _endpoint = await DiscoverEndpointAsync(ct).ConfigureAwait(false);
        if (_endpoint is null)
        {
            _log?.Invoke("Foundry service not running; starting it...");
            await RunProcessAsync(ResolveExecutablePath("foundry.exe")!, "service start", null, ct).ConfigureAwait(false);
            _endpoint = await DiscoverEndpointAsync(ct).ConfigureAwait(false);
        }
        return _endpoint;
    }

    private async Task<string?> DiscoverEndpointAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        try { await RunProcessAsync(ResolveExecutablePath("foundry.exe")!, "service status", line => sb.AppendLine(line), ct).ConfigureAwait(false); }
        catch { return null; }
        return ParseLoopbackEndpoint(sb.ToString());
    }

    internal static string? ParseLoopbackEndpoint(string output)
    {
        foreach (Match match in EndpointRegex().Matches(output ?? ""))
        {
            if (Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
                && uri.IsLoopback
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                return uri.GetLeftPart(UriPartial.Authority);
        }
        return null;
    }

    /// <summary>Model ids already downloaded on the device (parsed from `foundry cache list`).</summary>
    public async Task<IReadOnlyList<string>> ListCachedModelIdsAsync(CancellationToken ct)
    {
        var ids = new List<string>();
        var lines = new List<string>();
        try { await RunProcessAsync(ResolveExecutablePath("foundry.exe")!, "cache list", lines.Add, ct).ConfigureAwait(false); }
        catch { return ids; }
        foreach (var raw in lines)
        {
            // Lines look like:  "💾 qwen3-8b   qwen3-8b-cuda-gpu:2"  — take the last whitespace token.
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('[') || line.StartsWith("Models cached") || line.StartsWith("Alias")) continue;
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var id = tokens.LastOrDefault(t => t.Contains(':') || t.Contains('-'));
            if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>Picks the best already-cached tool-calling model for the given hardware, or null if none suitable.</summary>
    public static string? SelectCachedModel(HardwareCapabilityDetector.HardwareCapabilities caps, IReadOnlyList<string> cachedIds, string? preferredOverride)
    {
        if (!string.IsNullOrWhiteSpace(preferredOverride))
        {
            var exact = cachedIds.FirstOrDefault(id => id.Equals(preferredOverride, StringComparison.OrdinalIgnoreCase))
                      ?? cachedIds.FirstOrDefault(id => id.Contains(preferredOverride, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }

        string best = null!;
        int bestScore = int.MinValue;
        foreach (var id in cachedIds)
        {
            var lower = id.ToLowerInvariant();
            if (ExcludedFragments.Any(f => lower.Contains(f))) continue;

            int famIdx = Array.FindIndex(PreferredFamilies, f => lower.Contains(f));
            if (famIdx < 0) continue;

            string device = DeviceOf(lower);
            int deviceTier = caps.PreferredDeviceFragments.ToList().IndexOf(device);
            if (deviceTier < 0) continue; // variant targets hardware we don't have

            int score = (PreferredFamilies.Length - famIdx) * 100 + (caps.PreferredDeviceFragments.Count - deviceTier) * 10;
            if (score > bestScore) { bestScore = score; best = id; }
        }
        return best;
    }

    /// <summary>Alias to download when nothing suitable is cached, chosen for the detected hardware.</summary>
    public static string SelectDownloadTarget(HardwareCapabilityDetector.HardwareCapabilities caps) =>
        caps.CanUseGpu || caps.HasNpu ? "qwen2.5-7b" : "phi-4-mini";

    /// <summary>Downloads a model via the CLI, reporting integer percent progress.</summary>
    public async Task DownloadModelAsync(string alias, IProgress<int>? progress, CancellationToken ct)
    {
        await RunProcessAsync(ResolveExecutablePath("foundry.exe")!, $"model download {alias}", line =>
        {
            var m = PercentRegex().Match(line);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var pct)) progress?.Report(Math.Clamp(pct, 0, 100));
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Runs a single chat completion and returns the assistant's text content.</summary>
    public async Task<string> ChatAsync(string endpoint, string model, string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct)
        => (await ChatWithDiagnosticsAsync(endpoint, model, systemPrompt, userPrompt, maxTokens, ct).ConfigureAwait(false)).Content;

    /// <summary>Runs a chat completion and also retains the exact local API response body.</summary>
    public async Task<FoundryChatResult> ChatWithDiagnosticsAsync(
        string endpoint,
        string model,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        CancellationToken ct)
    {
        string trustedEndpoint = ParseLoopbackEndpoint(endpoint)
            ?? throw new InvalidOperationException("Foundry Local endpoint must use a loopback address.");
        var body = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            temperature = 0.1,
            max_tokens = maxTokens,
        };

        LastResponseJson = null;
        using var resp = await _http.PostAsJsonAsync($"{trustedEndpoint}/v1/chat/completions", body, ct).ConfigureAwait(false);
        string rawResponse = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        LastResponseJson = rawResponse;
        resp.EnsureSuccessStatusCode();
        return ParseChatResponse(rawResponse);
    }

    internal static FoundryChatResult ParseChatResponse(string rawResponse)
    {
        using var doc = JsonDocument.Parse(rawResponse);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content").GetString() ?? "";
        return new FoundryChatResult(StripThink(content), rawResponse);
    }

    /// <summary>Removes Qwen3-style &lt;think&gt;...&lt;/think&gt; reasoning blocks from a response.</summary>
    private static string StripThink(string s)
    {
        int end = s.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        return end >= 0 ? s[(end + "</think>".Length)..].Trim() : s.Trim();
    }

    private static string DeviceOf(string id)
    {
        if (id.Contains("-cpu") || id.Contains("generic-cpu")) return "cpu";
        if (id.Contains("-npu") || id.Contains("qnn") || id.Contains("vitisai") || id.Contains("openvino-npu")) return "npu";
        if (id.Contains("-gpu") || id.Contains("cuda") || id.Contains("directml") || id.Contains("trtrtx") || id.Contains("tensorrt")) return "gpu";
        return "cpu";
    }

    private static async Task<int> RunProcessAsync(string file, string args, Action<string>? onLine, CancellationToken ct)
    {
        var startInfo = CreateRedirectedStartInfo(file);
        startInfo.Arguments = args;
        return await RunProcessAsync(startInfo, onLine, ct).ConfigureAwait(false);
    }

    private static ProcessStartInfo CreateRedirectedStartInfo(string file) => new()
    {
        FileName = file,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    private static async Task<int> RunProcessAsync(ProcessStartInfo startInfo, Action<string>? onLine, CancellationToken ct)
    {
        using var proc = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine?.Invoke(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine?.Invoke(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* process already exited or cannot be terminated */ }
            throw;
        }
        return proc.ExitCode;
    }
}
