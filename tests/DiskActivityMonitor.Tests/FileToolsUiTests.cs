using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Tray;
using System.Windows;

namespace DiskActivityMonitor.Tests;

/// <summary>
/// Covers the presentation logic added for the per-file drill-down analytics, lock diagnosis and
/// database growth messages. These are pure statics, so no WPF dispatcher is required.
/// </summary>
public sealed class FileToolsUiTests
{
    // ------------------------------------------------------------------ analytics text

    [Fact]
    public void DescribeProcessShare_ReportsPercentageOfAllWrites()
    {
        string text = MainWindow.DescribeProcessShare("chrome.exe", 250, 1000);

        Assert.Contains("chrome.exe", text);
        Assert.Contains("25", text);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    public void DescribeProcessShare_HandlesNoActivity(long processWrite, long totalWrite)
        => Assert.Contains("No write activity", MainWindow.DescribeProcessShare("x.exe", processWrite, totalWrite));

    [Fact]
    public void DescribeProcessShare_ClampsAboveTotal()
        => Assert.Contains("100", MainWindow.DescribeProcessShare("x.exe", 5000, 1000));

    [Fact]
    public void DescribeTimeline_ExplainsAnEmptySeries()
        => Assert.Contains("No per-minute samples", MainWindow.DescribeTimeline(0, 48));

    [Fact]
    public void DescribeTimeline_ReportsSampleAndBucketCounts()
    {
        string text = MainWindow.DescribeTimeline(120, 48);

        Assert.Contains("120", text);
        Assert.Contains("48", text);
    }

    // ------------------------------------------------------------------ timeline bucketing

    [Fact]
    public void BuildTimelineBars_PlacesSamplesInTheCorrectBucket()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMinutes(48);
        var minutes = new List<(DateTime, long)>
        {
            (start, 100),
            (start.AddMinutes(47), 900),
        };

        var bars = MainWindow.BuildTimelineBars(minutes, start, end);

        Assert.Equal(48, bars.Count);
        Assert.Equal(100, bars[0].Value);
        Assert.Equal(900, bars[47].Value);
        // The peak bucket is highlighted so bursts stand out.
        Assert.True(bars[47].Highlight);
        Assert.False(bars[0].Highlight);
    }

    [Fact]
    public void BuildTimelineBars_NeverExceedsTheBucketCeiling()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddDays(30);

        var bars = MainWindow.BuildTimelineBars(Array.Empty<(DateTime, long)>(), start, end);

        Assert.Equal(48, bars.Count);
        Assert.All(bars, b => Assert.Equal(0, b.Value));
        Assert.All(bars, b => Assert.False(b.Highlight));
    }

    [Fact]
    public void BuildTimelineBars_AggregatesMultipleMinutesIntoOneBucket()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMinutes(96); // Two minutes per bucket.
        var minutes = new List<(DateTime, long)> { (start, 10), (start.AddMinutes(1), 15) };

        var bars = MainWindow.BuildTimelineBars(minutes, start, end);

        Assert.Equal(48, bars.Count);
        Assert.Equal(25, bars[0].Value);
    }

    [Fact]
    public void BuildTimelineBars_IgnoresSamplesBeforeTheWindow()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var minutes = new List<(DateTime, long)> { (start.AddMinutes(-10), 500) };

        var bars = MainWindow.BuildTimelineBars(minutes, start, start.AddMinutes(10));

        Assert.All(bars, b => Assert.Equal(0, b.Value));
    }

    [Fact]
    public void BuildTimelineBars_HandlesAZeroLengthWindow()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var bars = MainWindow.BuildTimelineBars(Array.Empty<(DateTime, long)>(), start, start);

        Assert.Single(bars);
    }

    // ------------------------------------------------------------------ extension grouping

    [Theory]
    [InlineData(@"C:\a\b.LOG", ".log")]
    [InlineData(@"C:\a\b.txt", ".txt")]
    [InlineData(@"C:\a\noextension", "(no extension)")]
    [InlineData(@"C:\a\b.", "(no extension)")]
    [InlineData("\0", "(no extension)")]
    public void ExtensionLabel_NormalisesForGrouping(string path, string expected)
        => Assert.Equal(expected, MainWindow.ExtensionLabel(path));

    // ------------------------------------------------------------------ lock diagnosis text

    [Fact]
    public void DescribeLockers_UsesSingularForOneHolder()
        => Assert.Contains("1 process currently has this file open", MainWindow.DescribeLockers(1, elevated: true));

    [Fact]
    public void DescribeLockers_UsesPluralForSeveralHolders()
        => Assert.Contains("3 processes", MainWindow.DescribeLockers(3, elevated: true));

    [Fact]
    public void DescribeLockers_ElevatedNoResultRulesOutALock()
    {
        string text = MainWindow.DescribeLockers(0, elevated: true);

        Assert.Contains("permission", text);
        Assert.DoesNotContain("administrator", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeLockers_UnelevatedNoResultWarnsAboutIncompleteVisibility()
        => Assert.Contains("administrator", MainWindow.DescribeLockers(0, elevated: false), StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ database messages

    [Fact]
    public void FormatDatabaseWarning_StatesSizeAndThreshold()
    {
        string text = MainWindow.FormatDatabaseWarning(2L * 1024 * 1024 * 1024, 1);

        Assert.Contains("2", text);
        Assert.Contains("GB threshold", text);
    }

    [Fact]
    public void FormatDatabaseDetail_BreaksDownEveryDatabaseFile()
    {
        string text = MainWindow.FormatDatabaseDetail(new DatabaseSize(100, 200, 300));

        Assert.Contains("write-ahead log", text);
        Assert.Contains("index", text);
    }

    [Fact]
    public void FormatCompactionResult_ReportsReclaimedSpace()
    {
        var result = new CompactionResult(true, 2000, 1000, null);

        Assert.Contains("Reclaimed", MainWindow.FormatCompactionResult(result));
    }

    [Fact]
    public void FormatCompactionResult_SurfacesTheFailureReason()
    {
        var result = new CompactionResult(false, 2000, 2000, "database is locked");

        Assert.Contains("database is locked", MainWindow.FormatCompactionResult(result));
    }

    [Fact]
    public void ReclaimedBytes_IsNeverNegative()
        => Assert.Equal(0, new CompactionResult(true, 100, 500, null).ReclaimedBytes);

    [Theory]
    [InlineData(false, WindowState.Normal, true, false)]
    [InlineData(true, WindowState.Minimized, true, false)]
    [InlineData(true, WindowState.Normal, false, false)]
    [InlineData(true, WindowState.Normal, true, true)]
    public void IsDashboardInForeground_RequiresVisibleRestoredActiveWindow(
        bool isVisible, WindowState windowState, bool isActive, bool expected)
        => Assert.Equal(expected,
            MainWindow.IsDashboardInForeground(isVisible, windowState, isActive));

    // ------------------------------------------------------------------ extension list editing

    [Fact]
    public void NormalizeExtensionList_SortsAndDeduplicates()
        => Assert.Equal("dll;exe", MainWindow.NormalizeExtensionList(".exe; dll ;EXE", "fallback"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NormalizeExtensionList_KeepsTheFallbackWhenCleared(string? text)
        => Assert.Equal(AppConfig.DefaultBinaryExtensions,
            MainWindow.NormalizeExtensionList(text, AppConfig.DefaultBinaryExtensions));

    [Fact]
    public void TrimTailLines_RemovesOldLinesUntilWithinCharacterLimit()
    {
        var lines = new List<string> { "old", "middle", "new" };

        bool trimmed = MainWindow.TrimTailLines(lines, maxChars: 9);

        Assert.True(trimmed);
        Assert.Equal(new[] { "middle", "new" }, lines);
    }

    [Fact]
    public void TrimTailLines_KeepsNewestFragmentOfOneHugeLine()
    {
        var lines = new List<string> { "0123456789" };

        Assert.True(MainWindow.TrimTailLines(lines, maxChars: 4));
        Assert.Equal("6789", Assert.Single(lines));
        Assert.False(MainWindow.TrimTailLines(lines, maxChars: 4));
    }
}
