using DiskActivityMonitor.Core.Text;

namespace DiskActivityMonitor.Tests;

/// <summary>
/// Covers the markdown subset used to render GitHub release notes in the update dialog.
/// </summary>
public sealed class MarkdownParserTests
{
    private static string Plain(string markdown) => MarkdownParser.ToPlainText(MarkdownParser.Parse(markdown));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void Parse_ReturnsNothingForEmptySource(string? markdown)
        => Assert.Empty(MarkdownParser.Parse(markdown));

    [Fact]
    public void Parse_ReadsAtxHeadingLevels()
    {
        var blocks = MarkdownParser.Parse("# One\n## Two\n### Three");

        var headings = blocks.OfType<MarkdownHeading>().ToList();
        Assert.Equal(new[] { 1, 2, 3 }, headings.Select(h => h.Level));
        Assert.Equal("One", ((MarkdownRun)headings[0].Inlines[0]).Text);
    }

    [Fact]
    public void Parse_IgnoresHashesWithoutASpace()
    {
        var blocks = MarkdownParser.Parse("#NotAHeading");

        Assert.Empty(blocks.OfType<MarkdownHeading>());
        Assert.Single(blocks.OfType<MarkdownParagraph>());
    }

    [Fact]
    public void Parse_TrimsClosingHashes()
    {
        var heading = MarkdownParser.Parse("## Title ##").OfType<MarkdownHeading>().Single();

        Assert.Equal("Title", ((MarkdownRun)heading.Inlines[0]).Text);
    }

    [Fact]
    public void Parse_GroupsConsecutiveBulletsIntoOneList()
    {
        var list = MarkdownParser.Parse("- first\n- second\n* third").OfType<MarkdownList>().Single();

        Assert.False(list.Ordered);
        Assert.Equal(3, list.Items.Count);
        Assert.All(list.Items, item => Assert.Equal("\u2022", item.Marker));
    }

    [Fact]
    public void Parse_RecordsOrderedListsAndTheirMarkers()
    {
        var list = MarkdownParser.Parse("1. first\n2. second").OfType<MarkdownList>().Single();

        Assert.True(list.Ordered);
        Assert.Equal(new[] { "1.", "2." }, list.Items.Select(i => i.Marker));
    }

    [Fact]
    public void Parse_SeparatesOrderedFromUnorderedRuns()
    {
        var lists = MarkdownParser.Parse("- bullet\n1. numbered").OfType<MarkdownList>().ToList();

        Assert.Equal(2, lists.Count);
        Assert.False(lists[0].Ordered);
        Assert.True(lists[1].Ordered);
    }

    [Fact]
    public void Parse_TracksNestingDepth()
    {
        var list = MarkdownParser.Parse("- top\n  - nested\n    - deeper").OfType<MarkdownList>().Single();

        Assert.Equal(new[] { 0, 1, 2 }, list.Items.Select(i => i.Depth));
    }

    [Fact]
    public void Parse_CapturesFencedCodeVerbatim()
    {
        var code = MarkdownParser.Parse("```powershell\n$a = 1\n## not a heading\n```").OfType<MarkdownCodeBlock>().Single();

        Assert.Equal("powershell", code.Language);
        Assert.Contains("$a = 1", code.Text);
        Assert.Contains("## not a heading", code.Text);
    }

    [Fact]
    public void Parse_ClosesAnUnterminatedFenceAtTheEnd()
    {
        var code = MarkdownParser.Parse("```\nstill code").OfType<MarkdownCodeBlock>().Single();

        Assert.Equal("still code", code.Text);
    }

    [Fact]
    public void Parse_RecognisesThematicBreaks()
    {
        Assert.Single(MarkdownParser.Parse("above\n\n---\n\nbelow").OfType<MarkdownRule>());
        Assert.Single(MarkdownParser.Parse("***").OfType<MarkdownRule>());
    }

    [Fact]
    public void Parse_JoinsWrappedParagraphLines()
    {
        var paragraph = MarkdownParser.Parse("one\ntwo\n\nthree").OfType<MarkdownParagraph>().First();

        Assert.Equal("one two", ((MarkdownRun)paragraph.Inlines[0]).Text);
    }

    // ------------------------------------------------------------------ inline styling

    [Fact]
    public void Inlines_ApplyBoldAndItalic()
    {
        var runs = MarkdownParser.ParseInlines("plain **bold** and *italic*").OfType<MarkdownRun>().ToList();

        Assert.Contains(runs, r => r.Text == "bold" && r.Bold);
        Assert.Contains(runs, r => r.Text == "italic" && r.Italic);
        Assert.Contains(runs, r => r.Text.StartsWith("plain", StringComparison.Ordinal) && !r.Bold && !r.Italic);
    }

    [Fact]
    public void Inlines_TreatBackticksAsLiteralCode()
    {
        var code = MarkdownParser.ParseInlines("run `dam --status **now**`").OfType<MarkdownRun>().Single(r => r.Code);

        Assert.Equal("dam --status **now**", code.Text);
    }

    [Fact]
    public void Inlines_DoNotItaliciseUnderscoresInsideWords()
    {
        var runs = MarkdownParser.ParseInlines("file_target_name").OfType<MarkdownRun>().ToList();

        Assert.Single(runs);
        Assert.Equal("file_target_name", runs[0].Text);
        Assert.False(runs[0].Italic);
    }

    [Fact]
    public void Inlines_ExtractHttpLinks()
    {
        var link = MarkdownParser.ParseInlines("see [the release](https://github.com/x/y/releases)").OfType<MarkdownLink>().Single();

        Assert.Equal("the release", link.Text);
        Assert.Equal("https://github.com/x/y/releases", link.Url);
    }

    [Fact]
    public void Inlines_DowngradeUnsafeLinkSchemesToPlainText()
    {
        var inlines = MarkdownParser.ParseInlines("[click](javascript:alert(1))");

        Assert.Empty(inlines.OfType<MarkdownLink>());
        Assert.Contains(inlines.OfType<MarkdownRun>(), r => r.Text == "click");
    }

    [Theory]
    [InlineData("https://github.com", true)]
    [InlineData("http://example.com", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/System32", false)]
    [InlineData("/relative/path", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSafeUrl_AllowsOnlyHttpAndHttps(string? url, bool expected)
        => Assert.Equal(expected, MarkdownParser.IsSafeUrl(url));

    // ------------------------------------------------------------------ realistic notes

    [Fact]
    public void Parse_HandlesATypicalReleaseNote()
    {
        const string notes = """
            ## What's changed

            - Added a **live tail** for text files
            - Fixed `handle.exe` discovery on PATH
              - Also checks the database folder

            ## Installation

            Download the installer and run it.

            ```powershell
            .\DiskActivityMonitor-Setup-1.4.11-x64.exe
            ```

            ## Full changelog

            [Compare v1.4.10...v1.4.11](https://github.com/andrewtheart/DiskActivityMonitor/compare/v1.4.10...v1.4.11)
            """;

        var blocks = MarkdownParser.Parse(notes);

        Assert.Equal(3, blocks.OfType<MarkdownHeading>().Count());
        Assert.Single(blocks.OfType<MarkdownCodeBlock>());
        Assert.Single(blocks.OfType<MarkdownList>());

        var list = blocks.OfType<MarkdownList>().Single();
        Assert.Equal(3, list.Items.Count);
        Assert.Equal(1, list.Items[2].Depth);

        // The compare link survives as a real link.
        var link = blocks.OfType<MarkdownParagraph>()
            .SelectMany(p => p.Inlines)
            .OfType<MarkdownLink>()
            .Single();
        Assert.StartsWith("https://github.com/", link.Url, StringComparison.Ordinal);

        // Nothing is lost when flattened for accessibility.
        string plain = Plain(notes);
        Assert.Contains("What's changed", plain);
        Assert.Contains("live tail", plain);
        Assert.DoesNotContain("**", plain);
    }

    [Fact]
    public void ToPlainText_StripsMarkupFromEveryBlockKind()
    {
        string plain = Plain("# Title\n\n- **item**\n\n---\n\n```\ncode\n```");

        Assert.Contains("Title", plain);
        Assert.Contains("item", plain);
        Assert.Contains("---", plain);
        Assert.Contains("code", plain);
        Assert.DoesNotContain("**", plain);
    }
}
