namespace DiskActivityMonitor.Core.Files;

/// <summary>
/// Decides whether a file is treated as binary purely from its extension, using the
/// user-configurable semicolon-separated list in
/// <see cref="Configuration.AppConfig.BinaryExtensions"/>.
/// </summary>
/// <remarks>
/// This is deliberately an extension test rather than a content sniff: the live tail must decide
/// whether to offer to open a file without first reading a file that may be huge, locked, or
/// actively being written.
/// </remarks>
public sealed class BinaryExtensionPolicy
{
    private readonly HashSet<string> _extensions;

    /// <summary>Creates a policy from a semicolon, comma or whitespace separated extension list.</summary>
    public BinaryExtensionPolicy(string? extensionList)
    {
        _extensions = Parse(extensionList);
    }

    /// <summary>The normalised extensions this policy treats as binary, without leading dots.</summary>
    public IReadOnlyCollection<string> Extensions => _extensions;

    /// <summary>Normalises a raw list into a comparable set of bare, lower-case extensions.</summary>
    public static HashSet<string> Parse(string? extensionList)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(extensionList)) return set;

        foreach (var token in extensionList.Split([';', ',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var cleaned = token.Trim().TrimStart('*').TrimStart('.');
            if (cleaned.Length > 0) set.Add(cleaned);
        }

        return set;
    }

    /// <summary>True when the file's extension appears in the configured binary list.</summary>
    public bool IsBinary(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;

        string extension;
        try
        {
            extension = Path.GetExtension(path);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (extension.Length <= 1) return false;
        return _extensions.Contains(extension[1..]);
    }
}
