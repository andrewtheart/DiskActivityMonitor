using System.Globalization;
using System.Text.RegularExpressions;

namespace DiskActivityMonitor.Core.Tools;

/// <summary>One open handle reported by Sysinternals Handle.</summary>
/// <param name="ProcessName">Owning image name, e.g. <c>chrome.exe</c>.</param>
/// <param name="ProcessId">Owning process id.</param>
/// <param name="User">Owning account when Handle reported one, else null.</param>
/// <param name="HandleId">Hexadecimal handle value.</param>
/// <param name="Type">Object type, e.g. <c>File</c>, <c>Key</c>, <c>Section</c>.</param>
/// <param name="Access">Access mask shorthand such as <c>RW-</c>, when present.</param>
/// <param name="Name">Object name, typically a path for File handles.</param>
public sealed record HandleEntry(
    string ProcessName,
    int ProcessId,
    string? User,
    string? HandleId,
    string Type,
    string? Access,
    string Name);

/// <summary>
/// Parses the plain-text output of Sysinternals <c>handle.exe</c>.
/// </summary>
/// <remarks>
/// Two shapes are produced by the tool and both are accepted here:
/// <list type="bullet">
/// <item><description>
/// Search mode (<c>handle.exe &lt;fragment&gt;</c>) emits one self-contained line per match:
/// <c>chrome.exe   pid: 1234  DESKTOP\user  4C8: C:\path\file.txt</c>
/// </description></item>
/// <item><description>
/// Process mode (<c>handle.exe -p &lt;name|pid&gt;</c>) emits a process header followed by indented
/// handle rows: <c>  4C8: File  (RW-)   C:\path\file.txt</c>
/// </description></item>
/// </list>
/// Parsing is deliberately pure so it can be tested without invoking the tool.
/// </remarks>
public static class HandleOutputParser
{
    // "chrome.exe  pid: 1234  DESKTOP\user  4C8: C:\path" or a bare process header.
    private static readonly Regex OwnerLine = new(
        @"^(?<name>\S.*?)\s+pid:\s*(?<pid>\d+)\s*(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Trailing "<hex>: <object>" fragment of a search-mode line.
    private static readonly Regex InlineHandle = new(
        @"^(?<handle>[0-9A-Fa-f]+):\s+(?<name>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Indented "  4C8: File  (RW-)   C:\path" row under a process header.
    private static readonly Regex EntryLine = new(
        @"^\s+(?<handle>[0-9A-Fa-f]+):\s+(?<type>\S+)\s*(?:\((?<access>[^)]*)\))?\s*(?<name>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Parses Handle output into a flat list of entries.</summary>
    public static IReadOnlyList<HandleEntry> Parse(string? output)
    {
        var results = new List<HandleEntry>();
        if (string.IsNullOrWhiteSpace(output)) return results;

        string currentProcess = "";
        int currentPid = 0;
        string? currentUser = null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            // Separator rules and the "no matching handles" trailer carry no data.
            if (line.TrimStart().StartsWith("---", StringComparison.Ordinal)) continue;

            // An indented row only makes sense once a process header has been seen.
            if (char.IsWhiteSpace(line[0]) && currentProcess.Length > 0)
            {
                var entry = EntryLine.Match(line);
                if (entry.Success)
                {
                    string name = entry.Groups["name"].Value.Trim();
                    string type = entry.Groups["type"].Value.Trim();
                    if (name.Length > 0 || type.Length > 0)
                    {
                        results.Add(new HandleEntry(
                            currentProcess,
                            currentPid,
                            currentUser,
                            entry.Groups["handle"].Value,
                            type,
                            entry.Groups["access"].Success ? entry.Groups["access"].Value.Trim() : null,
                            name));
                    }
                }
                continue;
            }

            var owner = OwnerLine.Match(line);
            if (!owner.Success) continue;

            currentProcess = owner.Groups["name"].Value.Trim();
            currentPid = int.TryParse(owner.Groups["pid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
                ? pid
                : 0;

            var rest = owner.Groups["rest"].Value.Trim();
            currentUser = null;
            if (rest.Length == 0) continue;

            // Search mode packs the user (optional) and the matched object onto the same line.
            var split = SplitTrailingHandle(rest);
            if (split.Handle is null)
            {
                currentUser = rest;
                continue;
            }

            currentUser = string.IsNullOrWhiteSpace(split.User) ? null : split.User;
            results.Add(new HandleEntry(
                currentProcess,
                currentPid,
                currentUser,
                split.Handle,
                "File",
                null,
                split.Name ?? ""));
        }

        return results;
    }

    /// <summary>
    /// Returns the distinct processes holding a handle whose name matches <paramref name="path"/>.
    /// Handle reports NT device paths for some volumes, so matching is done on the file name plus
    /// the full path suffix rather than on strict equality.
    /// </summary>
    public static IReadOnlyList<HandleEntry> FindLockers(string? output, string path)
    {
        var all = Parse(output);
        if (all.Count == 0 || string.IsNullOrWhiteSpace(path)) return Array.Empty<HandleEntry>();

        string full = path.Replace('/', '\\');
        string withoutDrive = StripDriveRoot(full);

        var matches = all.Where(e =>
            e.Name.Length > 0
            && (e.Type.Length == 0 || e.Type.Equals("File", StringComparison.OrdinalIgnoreCase))
            && Matches(e.Name.Replace('/', '\\'), full, withoutDrive));

        // One process can hold several handles to the same file; report each process once.
        return matches
            .GroupBy(e => e.ProcessId)
            .Select(g => g.First())
            .OrderBy(e => e.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool Matches(string candidate, string full, string withoutDrive)
        => candidate.Equals(full, StringComparison.OrdinalIgnoreCase)
           || (withoutDrive.Length > 0 && candidate.EndsWith(withoutDrive, StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes a drive letter or UNC root so NT device paths can still be matched.</summary>
    private static string StripDriveRoot(string path)
    {
        if (path.Length >= 3 && path[1] == ':' && path[2] == '\\') return path[2..];
        return path;
    }

    /// <summary>Splits a search-mode remainder into its optional user and trailing handle/object.</summary>
    private static (string? User, string? Handle, string? Name) SplitTrailingHandle(string rest)
    {
        // Handle separates columns with runs of spaces; the object column is always last.
        var columns = Regex.Split(rest, @"\s{2,}")
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .ToArray();

        if (columns.Length == 0) return (null, null, null);

        var last = InlineHandle.Match(columns[^1]);
        if (!last.Success)
        {
            // Some builds use a single space before the handle column.
            int colon = rest.IndexOf(':');
            if (colon <= 0) return (null, null, null);

            int start = rest.LastIndexOf(' ', colon) + 1;
            var candidate = InlineHandle.Match(rest[start..].Trim());
            if (!candidate.Success) return (null, null, null);

            string user = rest[..start].Trim();
            return (user.Length > 0 ? user : null, candidate.Groups["handle"].Value, candidate.Groups["name"].Value.Trim());
        }

        string? owner = columns.Length > 1 ? string.Join(' ', columns[..^1]) : null;
        return (owner, last.Groups["handle"].Value, last.Groups["name"].Value.Trim());
    }
}
