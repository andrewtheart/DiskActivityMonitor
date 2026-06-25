using System.Globalization;

namespace DiskActivityMonitor.Cli;

/// <summary>Parsed command arguments: positionals plus --key value / --key=value / --flag options.</summary>
internal sealed class CliArgs
{
    private static readonly HashSet<string> FlagNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "full", "global", "unacked", "json", "help", "h",
    };

    public List<string> Positionals { get; } = new();
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);

    public static CliArgs Parse(IReadOnlyList<string> tokens)
    {
        var a = new CliArgs();
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.StartsWith("--", StringComparison.Ordinal) || (t.StartsWith('-') && t.Length > 1 && !char.IsDigit(t[1])))
            {
                var body = t.TrimStart('-');
                int eq = body.IndexOf('=');
                if (eq >= 0)
                {
                    a._options[body[..eq]] = body[(eq + 1)..];
                }
                else if (FlagNames.Contains(body))
                {
                    a._options[body] = "true";
                }
                else if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith('-'))
                {
                    a._options[body] = tokens[++i];
                }
                else
                {
                    a._options[body] = "true";
                }
            }
            else
            {
                a.Positionals.Add(t);
            }
        }
        return a;
    }

    public bool Flag(params string[] names) => names.Any(n => _options.ContainsKey(n) && _options[n] != "false");

    public string? Opt(params string[] names)
    {
        foreach (var n in names)
            if (_options.TryGetValue(n, out var v)) return v;
        return null;
    }

    public int IntOpt(string[] names, int fallback)
        => int.TryParse(Opt(names), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public string? Positional(int index) => index < Positionals.Count ? Positionals[index] : null;
}

/// <summary>Console output helpers: aligned tables, headers, and severity coloring.</summary>
internal static class Out
{
    public static void Header(string text)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(text);
        Console.ForegroundColor = prev;
    }

    public static void Error(string text)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(text);
        Console.ForegroundColor = prev;
    }

    public static void Dim(string text)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(text);
        Console.ForegroundColor = prev;
    }

    /// <summary>Right-align flags per column; default is left-align.</summary>
    public static void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows, bool[]? rightAlign = null)
    {
        int cols = headers.Count;
        var widths = new int[cols];
        for (int c = 0; c < cols; c++) widths[c] = headers[c].Length;
        foreach (var row in rows)
            for (int c = 0; c < cols && c < row.Count; c++)
                widths[c] = Math.Max(widths[c], row[c]?.Length ?? 0);

        string Cell(string s, int c)
        {
            bool right = rightAlign is not null && c < rightAlign.Length && rightAlign[c];
            return right ? s.PadLeft(widths[c]) : s.PadRight(widths[c]);
        }

        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(string.Join("  ", headers.Select((h, c) => Cell(h, c))).TrimEnd());
        Console.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
        Console.ForegroundColor = prev;

        foreach (var row in rows)
            Console.WriteLine(string.Join("  ", Enumerable.Range(0, cols).Select(c => Cell(c < row.Count ? row[c] ?? "" : "", c))).TrimEnd());
    }
}

/// <summary>Parses durations like "5m", "30m", "1h", "2d", "1w", "90s"; a bare number means minutes.</summary>
internal static class Duration
{
    public static TimeSpan? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().ToLowerInvariant();
        char unit = text[^1];
        string numPart = char.IsLetter(unit) ? text[..^1] : text;
        if (!double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || n < 0)
            return null;
        return char.IsLetter(unit)
            ? unit switch
            {
                's' => TimeSpan.FromSeconds(n),
                'm' => TimeSpan.FromMinutes(n),
                'h' => TimeSpan.FromHours(n),
                'd' => TimeSpan.FromDays(n),
                'w' => TimeSpan.FromDays(n * 7),
                _ => null,
            }
            : TimeSpan.FromMinutes(n);
    }

    public static string Humanize(TimeSpan ts)
    {
        if (ts.TotalDays >= 7 && ts.TotalDays % 7 == 0) return $"{ts.TotalDays / 7:0.#}w";
        if (ts.TotalDays >= 1) return $"{ts.TotalDays:0.#}d";
        if (ts.TotalHours >= 1) return $"{ts.TotalHours:0.#}h";
        if (ts.TotalMinutes >= 1) return $"{ts.TotalMinutes:0.#}m";
        return $"{ts.TotalSeconds:0}s";
    }
}
