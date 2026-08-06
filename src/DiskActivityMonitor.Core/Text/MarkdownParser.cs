namespace DiskActivityMonitor.Core.Text;

/// <summary>A styled run of text inside a markdown block.</summary>
public abstract record MarkdownInline;

/// <summary>Plain text carrying any combination of emphasis.</summary>
public sealed record MarkdownRun(string Text, bool Bold = false, bool Italic = false, bool Code = false) : MarkdownInline;

/// <summary>A hyperlink. Only http and https destinations survive parsing.</summary>
public sealed record MarkdownLink(string Text, string Url) : MarkdownInline;

/// <summary>A block-level element.</summary>
public abstract record MarkdownBlock;

/// <summary>An ATX heading, levels 1-6.</summary>
public sealed record MarkdownHeading(int Level, IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

/// <summary>A run of text separated from its neighbours by a blank line.</summary>
public sealed record MarkdownParagraph(IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

/// <summary>One entry in a list, including its nesting depth and rendered marker.</summary>
public sealed record MarkdownListItem(int Depth, string Marker, IReadOnlyList<MarkdownInline> Inlines);

/// <summary>A consecutive group of list items.</summary>
public sealed record MarkdownList(bool Ordered, IReadOnlyList<MarkdownListItem> Items) : MarkdownBlock;

/// <summary>A fenced or indented code block.</summary>
public sealed record MarkdownCodeBlock(string Language, string Text) : MarkdownBlock;

/// <summary>A thematic break.</summary>
public sealed record MarkdownRule : MarkdownBlock;

/// <summary>
/// Parses the subset of Markdown that appears in GitHub release notes into a block model that a
/// UI can render.
/// </summary>
/// <remarks>
/// This is deliberately not a complete CommonMark implementation. Release notes use headings,
/// lists, fenced code, emphasis, inline code and links, so those are supported and anything else
/// degrades to readable plain text. Keeping it here - free of any UI type - means the parsing
/// rules can be tested directly and no third-party markdown dependency is shipped.
/// </remarks>
public static class MarkdownParser
{
    /// <summary>Parses markdown source into renderable blocks.</summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        var blocks = new List<MarkdownBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var paragraph = new List<string>();
        var listItems = new List<MarkdownListItem>();
        bool listOrdered = false;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new MarkdownParagraph(ParseInlines(string.Join(" ", paragraph))));
            paragraph.Clear();
        }

        void FlushList()
        {
            if (listItems.Count == 0) return;
            blocks.Add(new MarkdownList(listOrdered, listItems.ToList()));
            listItems.Clear();
        }

        void FlushAll()
        {
            FlushParagraph();
            FlushList();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();

            if (trimmed.Length == 0)
            {
                FlushAll();
                continue;
            }

            // Fenced code runs to the closing fence, or to the end of the document.
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushAll();
                var fence = trimmed[..3];
                var language = trimmed[3..].Trim();
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].Trim().StartsWith(fence, StringComparison.Ordinal))
                {
                    code.Add(lines[i]);
                    i++;
                }
                blocks.Add(new MarkdownCodeBlock(language, string.Join(Environment.NewLine, code)));
                continue;
            }

            if (IsThematicBreak(trimmed))
            {
                FlushAll();
                blocks.Add(new MarkdownRule());
                continue;
            }

            if (TryParseHeading(trimmed, out int level, out string headingText))
            {
                FlushAll();
                blocks.Add(new MarkdownHeading(level, ParseInlines(headingText)));
                continue;
            }

            if (TryParseListItem(raw, out int depth, out bool ordered, out string marker, out string itemText))
            {
                FlushParagraph();
                // A change of list kind starts a new list rather than mixing markers.
                if (listItems.Count > 0 && ordered != listOrdered) FlushList();
                listOrdered = ordered;
                listItems.Add(new MarkdownListItem(depth, marker, ParseInlines(itemText)));
                continue;
            }

            // A plain line directly under a list item continues that item.
            if (listItems.Count > 0 && raw.StartsWith("  ", StringComparison.Ordinal))
            {
                var last = listItems[^1];
                listItems[^1] = last with { Inlines = Merge(last.Inlines, ParseInlines(" " + trimmed)) };
                continue;
            }

            FlushList();
            paragraph.Add(trimmed);
        }

        FlushAll();
        return blocks;
    }

    private static IReadOnlyList<MarkdownInline> Merge(IReadOnlyList<MarkdownInline> first, IReadOnlyList<MarkdownInline> second)
        => first.Concat(second).ToList();

    private static bool IsThematicBreak(string trimmed)
    {
        if (trimmed.Length < 3) return false;
        char c = trimmed[0];
        if (c is not ('-' or '*' or '_')) return false;
        return trimmed.All(ch => ch == c || char.IsWhiteSpace(ch)) && trimmed.Count(ch => ch == c) >= 3;
    }

    private static bool TryParseHeading(string trimmed, out int level, out string text)
    {
        level = 0;
        text = "";
        int hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;

        if (hashes is < 1 or > 6) return false;
        if (hashes >= trimmed.Length || !char.IsWhiteSpace(trimmed[hashes])) return false;

        level = hashes;
        text = trimmed[hashes..].Trim().TrimEnd('#').Trim();
        return true;
    }

    private static bool TryParseListItem(string raw, out int depth, out bool ordered, out string marker, out string text)
    {
        depth = 0;
        ordered = false;
        marker = "";
        text = "";

        int indent = 0;
        while (indent < raw.Length && (raw[indent] == ' ' || raw[indent] == '\t')) indent++;

        var rest = raw[indent..];
        if (rest.Length < 2) return false;

        // Two spaces (or one tab) per nesting level, matching how the notes are authored.
        depth = raw[..indent].Replace("\t", "  ").Length / 2;

        if (rest[0] is '-' or '*' or '+' && char.IsWhiteSpace(rest[1]))
        {
            marker = "\u2022";
            text = rest[2..].Trim();
            return true;
        }

        int digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits])) digits++;
        if (digits > 0 && digits + 1 < rest.Length && rest[digits] is '.' or ')' && char.IsWhiteSpace(rest[digits + 1]))
        {
            ordered = true;
            marker = rest[..digits] + ".";
            text = rest[(digits + 2)..].Trim();
            return true;
        }

        depth = 0;
        return false;
    }

    /// <summary>Splits a line into styled runs and links.</summary>
    internal static IReadOnlyList<MarkdownInline> ParseInlines(string text)
    {
        var inlines = new List<MarkdownInline>();
        if (string.IsNullOrEmpty(text)) return inlines;

        var buffer = new System.Text.StringBuilder();
        bool bold = false, italic = false;

        void Flush()
        {
            if (buffer.Length == 0) return;
            inlines.Add(new MarkdownRun(buffer.ToString(), bold, italic));
            buffer.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            // Inline code wins over emphasis, so its contents are never re-interpreted.
            if (c == '`')
            {
                int close = text.IndexOf('`', i + 1);
                if (close > i)
                {
                    Flush();
                    inlines.Add(new MarkdownRun(text[(i + 1)..close], bold, italic, Code: true));
                    i = close;
                    continue;
                }
            }

            if (c == '[')
            {
                int closeText = text.IndexOf(']', i + 1);
                if (closeText > i && closeText + 1 < text.Length && text[closeText + 1] == '(')
                {
                    int closeUrl = text.IndexOf(')', closeText + 2);
                    if (closeUrl > closeText)
                    {
                        var label = text[(i + 1)..closeText];
                        var url = text[(closeText + 2)..closeUrl].Trim();
                        Flush();
                        if (IsSafeUrl(url)) inlines.Add(new MarkdownLink(label, url));
                        else inlines.Add(new MarkdownRun(label, bold, italic));
                        i = closeUrl;
                        continue;
                    }
                }
            }

            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                Flush();
                bold = !bold;
                i++;
                continue;
            }

            if (c == '*' || (c == '_' && IsWordBoundary(text, i)))
            {
                Flush();
                italic = !italic;
                continue;
            }

            buffer.Append(c);
        }

        Flush();
        return inlines;
    }

    /// <summary>Underscores inside words (snake_case identifiers) must not start emphasis.</summary>
    private static bool IsWordBoundary(string text, int index)
    {
        bool beforeIsWord = index > 0 && char.IsLetterOrDigit(text[index - 1]);
        bool afterIsWord = index + 1 < text.Length && char.IsLetterOrDigit(text[index + 1]);
        return !(beforeIsWord && afterIsWord);
    }

    /// <summary>
    /// Only absolute http(s) destinations are treated as links. Release notes are third-party
    /// content, so schemes such as file: or javascript: must never become clickable.
    /// </summary>
    public static bool IsSafeUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Flattens blocks back to plain text, used for accessibility and fallbacks.</summary>
    public static string ToPlainText(IReadOnlyList<MarkdownBlock> blocks)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case MarkdownHeading heading:
                    sb.AppendLine(Flatten(heading.Inlines));
                    break;
                case MarkdownParagraph paragraph:
                    sb.AppendLine(Flatten(paragraph.Inlines));
                    break;
                case MarkdownList list:
                    foreach (var item in list.Items)
                        sb.AppendLine($"{new string(' ', item.Depth * 2)}{item.Marker} {Flatten(item.Inlines)}");
                    break;
                case MarkdownCodeBlock code:
                    sb.AppendLine(code.Text);
                    break;
                case MarkdownRule:
                    sb.AppendLine("---");
                    break;
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string Flatten(IReadOnlyList<MarkdownInline> inlines)
        => string.Concat(inlines.Select(inline => inline switch
        {
            MarkdownRun run => run.Text,
            MarkdownLink link => link.Text,
            _ => "",
        }));
}
