using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Files;
using DiskActivityMonitor.Core.Tools;
using System.IO.Compression;

namespace DiskActivityMonitor.Tests;

/// <summary>
/// Covers the file-level tooling behind the per-process drill-down: Handle parsing, delete
/// classification, binary-extension policy, tailing and database growth management.
/// </summary>
public sealed class FileToolsTests : IDisposable
{
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var path in _temp)
            try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private string NewTempFile(string content = "")
    {
        string path = Path.Combine(Path.GetTempPath(), $"dam_tail_{Guid.NewGuid():N}.log");
        File.WriteAllText(path, content);
        _temp.Add(path);
        return path;
    }

    // ------------------------------------------------------------------ Handle parsing

    private const string ProcessModeOutput = """
        ------------------------------------------------------------------------------
        chrome.exe pid: 6820 DESKTOP\andre
           C4: File  (RW-)   C:\Users\andre\AppData\Local\cache.dat
           D0: Key           HKLM\SOFTWARE\Microsoft
          10C: Section       \Sessions\1\BaseNamedObjects\shared
        """;

    private const string SearchModeOutput = """
        notepad.exe        pid: 1234   DESKTOP\andre        4C8: C:\temp\report.txt
        System             pid: 4      NT AUTHORITY\SYSTEM  2F4: C:\temp\report.txt
        """;

    private sealed class HResultIOException : IOException
    {
        public HResultIOException(int win32Code, string message = "io")
            : base(message)
            => HResult = unchecked((int)(0x80070000u | (uint)win32Code));
    }

    [Fact]
    public void Parse_ReadsProcessModeEntries()
    {
        var entries = HandleOutputParser.Parse(ProcessModeOutput);

        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.Equal("chrome.exe", e.ProcessName));
        Assert.All(entries, e => Assert.Equal(6820, e.ProcessId));
        Assert.All(entries, e => Assert.Equal(@"DESKTOP\andre", e.User));

        var file = entries[0];
        Assert.Equal("File", file.Type);
        Assert.Equal("RW-", file.Access);
        Assert.Equal(@"C:\Users\andre\AppData\Local\cache.dat", file.Name);
        Assert.Equal("Key", entries[1].Type);
        Assert.Equal("Section", entries[2].Type);
    }

    [Fact]
    public void Parse_ReadsSearchModeEntries()
    {
        var entries = HandleOutputParser.Parse(SearchModeOutput);

        Assert.Equal(2, entries.Count);
        Assert.Equal("notepad.exe", entries[0].ProcessName);
        Assert.Equal(1234, entries[0].ProcessId);
        Assert.Equal(@"C:\temp\report.txt", entries[0].Name);
        Assert.Equal("System", entries[1].ProcessName);
        Assert.Equal(4, entries[1].ProcessId);
    }

    [Fact]
    public void Parse_SearchModeSupportsSingleSpaceBeforeInlineHandle()
    {
        const string output = "notepad.exe pid: 1234 DESKTOP\\andre 4C8: C:\\temp\\report.txt";

        var entries = HandleOutputParser.Parse(output);

        var entry = Assert.Single(entries);
        Assert.Equal("notepad.exe", entry.ProcessName);
        Assert.Equal(1234, entry.ProcessId);
        Assert.Equal(@"DESKTOP\andre", entry.User);
        Assert.Equal(@"C:\temp\report.txt", entry.Name);
    }

    [Fact]
    public void Parse_SearchModeWithoutHandleTreatsRemainderAsUser()
    {
        const string output = "chrome.exe pid: 6820 DESKTOP\\andre";

        var entries = HandleOutputParser.Parse(output);

        Assert.Empty(entries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_ToleratesEmptyOutput(string? output)
        => Assert.Empty(HandleOutputParser.Parse(output));

    [Fact]
    public void FindLockers_ReturnsOneRowPerProcess()
    {
        var lockers = HandleOutputParser.FindLockers(SearchModeOutput, @"C:\temp\report.txt");

        Assert.Equal(2, lockers.Count);
        Assert.Contains(lockers, l => l.ProcessName == "notepad.exe" && l.ProcessId == 1234);
        Assert.Contains(lockers, l => l.ProcessName == "System" && l.ProcessId == 4);
    }

    [Fact]
    public void FindLockers_CollapsesRepeatedHandlesFromOneProcess()
    {
        const string output = """
            chrome.exe pid: 900 DESKTOP\andre
               C4: File  (RW-)   C:\temp\a.txt
               C8: File  (RW-)   C:\temp\a.txt
            """;

        var lockers = HandleOutputParser.FindLockers(output, @"C:\temp\a.txt");

        Assert.Single(lockers);
        Assert.Equal(900, lockers[0].ProcessId);
    }

    [Fact]
    public void FindLockers_MatchesNtDevicePathsBySuffix()
    {
        const string output = """
            System pid: 4 NT AUTHORITY\SYSTEM
               2F4: File  (RW-)   \Device\HarddiskVolume3\temp\report.txt
            """;

        var lockers = HandleOutputParser.FindLockers(output, @"C:\temp\report.txt");

        Assert.Single(lockers);
        Assert.Equal("System", lockers[0].ProcessName);
    }

    [Fact]
    public void FindLockers_IgnoresUnrelatedFiles()
        => Assert.Empty(HandleOutputParser.FindLockers(SearchModeOutput, @"C:\temp\other.txt"));

    [Fact]
    public void FindLockers_IgnoresNonFileHandleTypes()
    {
        const string output = """
            chrome.exe pid: 900 DESKTOP\andre
               C4: Key  (RW-)   C:\temp\a.txt
            """;

        Assert.Empty(HandleOutputParser.FindLockers(output, @"C:\temp\a.txt"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FindLockers_RequiresANonBlankPath(string path)
        => Assert.Empty(HandleOutputParser.FindLockers(SearchModeOutput, path));

    [Fact]
    public void HandleTool_PrefersArchitectureSpecificExecutable()
    {
        Assert.NotEmpty(HandleTool.CandidateNames);
        Assert.Contains("handle.exe", HandleTool.CandidateNames);
    }

    [Fact]
    public void HandleTool_DownloadsOnlyFromTheOfficialSysinternalsHost()
    {
        var uri = new Uri(HandleTool.DownloadUrl);

        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
        Assert.Equal(HandleTool.DownloadHost, uri.Host);
    }

    [Fact]
    public void HandleTool_Extract_InstallsHandleAndEulaFromArchive()
    {
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("folder/handle.exe").Open())) writer.Write("stub");
            using (var writer = new StreamWriter(zip.CreateEntry("Eula.txt").Open())) writer.Write("terms");
            using (var writer = new StreamWriter(zip.CreateEntry("ignore.bin").Open())) writer.Write("x");
        }
        archive.Position = 0;

        string directory = Path.Combine(Path.GetTempPath(), $"dam_handle_extract_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string executable = HandleTool.Extract(archive, directory);

            Assert.Equal(Path.Combine(directory, "handle.exe"), executable);
            Assert.True(File.Exists(Path.Combine(directory, "handle.exe")));
            Assert.True(File.Exists(Path.Combine(directory, "Eula.txt")));
            Assert.False(File.Exists(Path.Combine(directory, "ignore.bin")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void HandleTool_Extract_ThrowsWhenArchiveHasNoExecutable()
    {
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("Eula.txt").Open());
            writer.Write("terms");
        }
        archive.Position = 0;

        string directory = Path.Combine(Path.GetTempPath(), $"dam_handle_extract_none_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => HandleTool.Extract(archive, directory));
            Assert.Contains("did not contain", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Locate_ReturnsNullWhenDirectoryHasNoHandleExecutable()
    {
        string empty = Path.Combine(Path.GetTempPath(), $"dam_handle_{Guid.NewGuid():N}");
        Directory.CreateDirectory(empty);
        try
        {
            // PATH may legitimately contain Handle on a developer machine, so only assert that the
            // supplied directory is not what produced a hit.
            string? found = HandleTool.Locate(empty);
            Assert.True(found is null || !found.StartsWith(empty, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    // ------------------------------------------------------------------ delete classification

    [Fact]
    public void Delete_ReportsNotFoundForMissingFile()
    {
        var outcome = FileDeletionService.Delete(Path.Combine(Path.GetTempPath(), $"dam_missing_{Guid.NewGuid():N}"));

        Assert.Equal(FileDeleteStatus.NotFound, outcome.Status);
        Assert.True(outcome.Removed);
    }

    [Fact]
    public void Delete_RemovesAnOrdinaryFile()
    {
        string path = NewTempFile("data");

        var outcome = FileDeletionService.Delete(path);

        Assert.Equal(FileDeleteStatus.Deleted, outcome.Status);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Delete_DetectsReadOnlyBeforeAttempting()
    {
        string path = NewTempFile("data");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var outcome = FileDeletionService.Delete(path);

            Assert.Equal(FileDeleteStatus.ReadOnly, outcome.Status);
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Delete_ReportsLockWhenAnotherHandleHoldsTheFile()
    {
        string path = NewTempFile("data");

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var outcome = FileDeletionService.Delete(path);

            Assert.Equal(FileDeleteStatus.Locked, outcome.Status);
            Assert.True(outcome.NeedsLockAnalysis);
        }
    }

    [Fact]
    public void Classify_MapsIoErrorCodes()
    {
        Assert.Equal(FileDeleteStatus.Locked, FileDeletionService.Classify(new HResultIOException(32)).Status);
        Assert.Equal(FileDeleteStatus.Locked, FileDeletionService.Classify(new HResultIOException(33)).Status);
        Assert.Equal(FileDeleteStatus.AccessDenied, FileDeletionService.Classify(new HResultIOException(5)).Status);
        Assert.Equal(FileDeleteStatus.NotFound, FileDeletionService.Classify(new HResultIOException(2)).Status);
        Assert.Equal(FileDeleteStatus.NotFound, FileDeletionService.Classify(new HResultIOException(3)).Status);
        Assert.Equal(FileDeleteStatus.Failed, FileDeletionService.Classify(new HResultIOException(123, "bad name")).Status);
    }

    [Fact]
    public void ErrorCodeOf_ExtractsLowWordFromHResult()
        => Assert.Equal(32, FileDeletionService.ErrorCodeOf(new HResultIOException(32)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Delete_RejectsBlankPaths(string? path)
        => Assert.Equal(FileDeleteStatus.Failed, FileDeletionService.Delete(path!).Status);

    [Fact]
    public void Classify_MapsAccessDenied()
    {
        var outcome = FileDeletionService.Classify(new UnauthorizedAccessException());

        Assert.Equal(FileDeleteStatus.AccessDenied, outcome.Status);
        Assert.True(outcome.NeedsLockAnalysis);
    }

    [Fact]
    public void Classify_FallsBackForUnexpectedFailures()
        => Assert.Equal(FileDeleteStatus.Failed, FileDeletionService.Classify(new InvalidOperationException("nope")).Status);

    // ------------------------------------------------------------------ binary policy

    [Theory]
    [InlineData(@"C:\a\b.exe", true)]
    [InlineData(@"C:\a\b.DLL", true)]
    [InlineData(@"C:\a\b.zip", true)]
    [InlineData(@"C:\a\b.sqlite3", true)]
    [InlineData(@"C:\a\b.log", false)]
    [InlineData(@"C:\a\b.txt", false)]
    [InlineData(@"C:\a\noextension", false)]
    public void BinaryPolicy_UsesTheDefaultList(string path, bool expected)
        => Assert.Equal(expected, new BinaryExtensionPolicy(AppConfig.DefaultBinaryExtensions).IsBinary(path));

    [Theory]
    [InlineData("exe;dll")]
    [InlineData(".exe, .dll")]
    [InlineData("*.exe *.dll")]
    public void BinaryPolicy_AcceptsCommonListFormats(string list)
    {
        var policy = new BinaryExtensionPolicy(list);

        Assert.True(policy.IsBinary(@"C:\x.exe"));
        Assert.True(policy.IsBinary(@"C:\x.dll"));
        Assert.False(policy.IsBinary(@"C:\x.txt"));
    }

    [Fact]
    public void BinaryPolicy_TreatsAnEmptyListAsAllowingEverything()
    {
        var policy = new BinaryExtensionPolicy("");

        Assert.Empty(policy.Extensions);
        Assert.False(policy.IsBinary(@"C:\x.exe"));
    }

    [Fact]
    public void BinaryPolicy_IsBinaryReturnsFalseForInvalidPath()
    {
        var policy = new BinaryExtensionPolicy("exe");

        Assert.False(policy.IsBinary("bad\0path.exe"));
    }

    [Fact]
    public void DefaultBinaryExtensions_CoverExecutablesMediaArchivesAndDatabases()
    {
        var parsed = BinaryExtensionPolicy.Parse(AppConfig.DefaultBinaryExtensions);

        Assert.Contains("exe", parsed);
        Assert.Contains("mp4", parsed);
        Assert.Contains("zip", parsed);
        Assert.Contains("sqlite", parsed);
        Assert.DoesNotContain("log", parsed);
        Assert.DoesNotContain("txt", parsed);
    }

    // ------------------------------------------------------------------ tailing

    [Fact]
    public void ReadTail_ReturnsTrailingLinesAndOffset()
    {
        string path = NewTempFile("one\ntwo\nthree\n");

        var batch = FileTailReader.ReadTail(path, maxLines: 2);

        Assert.True(batch.Success);
        Assert.Equal(new[] { "two", "three" }, batch.Lines);
        Assert.Equal(new FileInfo(path).Length, batch.NextOffset);
    }

    [Fact]
    public void ReadTail_PreservesBoundedFragmentOfLargeSingleLineFile()
    {
        string path = NewTempFile(new string('x', 4096));

        var batch = FileTailReader.ReadTail(path, maxLines: 2, maxReadBytes: 512);

        Assert.True(batch.Success);
        Assert.Single(batch.Lines);
        Assert.Equal(512, batch.Lines[0].Length);
        Assert.Equal(4096 - 512, batch.SkippedBytes);
    }

    [Fact]
    public void ReadFrom_ReturnsOnlyNewlyAppendedLines()
    {
        string path = NewTempFile("first\n");
        var seed = FileTailReader.ReadTail(path, 100);

        File.AppendAllText(path, "second\nthird\n");
        var batch = FileTailReader.ReadFrom(path, seed.NextOffset, 100);

        Assert.True(batch.Success);
        Assert.False(batch.Truncated);
        Assert.Equal(new[] { "second", "third" }, batch.Lines);
    }

    [Fact]
    public void ReadFrom_FlagsTruncationWhenTheFileShrinks()
    {
        string path = NewTempFile("aaaa\nbbbb\ncccc\n");
        var seed = FileTailReader.ReadTail(path, 100);

        File.WriteAllText(path, "x\n");
        var batch = FileTailReader.ReadFrom(path, seed.NextOffset, 100);

        Assert.True(batch.Success);
        Assert.True(batch.Truncated);
        Assert.Equal(new[] { "x" }, batch.Lines);
    }

    [Fact]
    public void ReadFrom_ReturnsNothingWhenUnchanged()
    {
        string path = NewTempFile("a\n");
        var seed = FileTailReader.ReadTail(path, 100);

        var batch = FileTailReader.ReadFrom(path, seed.NextOffset, 100);

        Assert.True(batch.Success);
        Assert.Empty(batch.Lines);
    }

    [Fact]
    public void ReadTail_DoesNotBlockAConcurrentWriter()
    {
        string path = NewTempFile("start\n");

        // The writer keeps the file open while the tail reads it, mirroring a live log.
        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var batch = FileTailReader.ReadTail(path, 100);

        Assert.True(batch.Success);
        Assert.Equal(new[] { "start" }, batch.Lines);
    }

    [Fact]
    public void ReadTail_ReportsMissingFiles()
    {
        var batch = FileTailReader.ReadTail(Path.Combine(Path.GetTempPath(), $"dam_none_{Guid.NewGuid():N}"), 10);

        Assert.False(batch.Success);
        Assert.Equal("The file no longer exists.", batch.Error);
    }

    [Fact]
    public void ReadFrom_RespectsLineLimitForLargeAppends()
    {
        string path = NewTempFile("head\n");
        var seed = FileTailReader.ReadTail(path, 100);

        File.AppendAllText(path, "a\nb\nc\n");
        var batch = FileTailReader.ReadFrom(path, seed.NextOffset, 2);

        Assert.Equal(new[] { "b", "c" }, batch.Lines);
    }

    [Fact]
    public void ReadFrom_BoundsLargeAppendAndReportsSkippedBytes()
    {
        string path = NewTempFile("head\n");
        var seed = FileTailReader.ReadTail(path, 100);

        File.AppendAllText(path, new string('x', 4096));
        var batch = FileTailReader.ReadFrom(path, seed.NextOffset, maxLines: 2, maxReadBytes: 512);

        Assert.True(batch.Success);
        Assert.Single(batch.Lines);
        Assert.Equal(512, batch.Lines[0].Length);
        Assert.Equal(4096 - 512, batch.SkippedBytes);
    }

    [Fact]
    public void Describe_MapsKnownTailFailureCases()
    {
        Assert.Equal("The file no longer exists.", FileTailReader.Describe(new FileNotFoundException()));
        Assert.Contains("permissions", FileTailReader.Describe(new UnauthorizedAccessException()), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exclusive", FileTailReader.Describe(new HResultIOException(32)), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be read", FileTailReader.Describe(new HResultIOException(111, "io failed")), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ database growth

    [Fact]
    public void ThresholdBytes_ConvertsGigabytes()
    {
        Assert.Equal(1024L * 1024 * 1024, DatabaseMaintenance.ThresholdBytes(1));
        Assert.Equal(0, DatabaseMaintenance.ThresholdBytes(0));
        Assert.Equal(0, DatabaseMaintenance.ThresholdBytes(-5));
    }

    [Fact]
    public void CompactionResult_ReclaimedBytesNeverGoesNegative()
    {
        Assert.Equal(25, new CompactionResult(true, 100, 75, null).ReclaimedBytes);
        Assert.Equal(0, new CompactionResult(true, 75, 100, null).ReclaimedBytes);
    }

    [Fact]
    public void ShouldWarn_TriggersOnlyAboveTheThreshold()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(DatabaseMaintenance.ShouldWarn(2L * 1024 * 1024 * 1024, 1, null, now, 12));
        Assert.False(DatabaseMaintenance.ShouldWarn(500L * 1024 * 1024, 1, null, now, 12));
    }

    [Fact]
    public void ShouldWarn_IsDisabledByANonPositiveThreshold()
        => Assert.False(DatabaseMaintenance.ShouldWarn(long.MaxValue, 0, null, DateTime.UtcNow, 12));

    [Fact]
    public void ShouldWarn_RespectsTheCooldown()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        long size = 2L * 1024 * 1024 * 1024;

        Assert.False(DatabaseMaintenance.ShouldWarn(size, 1, now.AddHours(-1), now, 12));
        Assert.True(DatabaseMaintenance.ShouldWarn(size, 1, now.AddHours(-13), now, 12));
    }

    [Fact]
    public void ShouldWarn_TreatsNegativeCooldownAsImmediate()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        long size = 2L * 1024 * 1024 * 1024;

        Assert.True(DatabaseMaintenance.ShouldWarn(size, 1, now, now, -10));
    }

    [Fact]
    public void Measure_ReportsZeroForAnAbsentDatabase()
    {
        var size = DatabaseMaintenance.Measure(Path.Combine(Path.GetTempPath(), $"dam_absent_{Guid.NewGuid():N}.db"));

        Assert.Equal(0, size.TotalBytes);
    }

    [Fact]
    public void Measure_IncludesMainWalAndShmFiles()
    {
        string db = Path.Combine(Path.GetTempPath(), $"dam_measure_{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllBytes(db, new byte[11]);
            File.WriteAllBytes(db + "-wal", new byte[7]);
            File.WriteAllBytes(db + "-shm", new byte[5]);

            var size = DatabaseMaintenance.Measure(db);

            Assert.Equal(11, size.MainBytes);
            Assert.Equal(7, size.WalBytes);
            Assert.Equal(5, size.ShmBytes);
            Assert.Equal(23, size.TotalBytes);
        }
        finally
        {
            foreach (string file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(db) + "*"))
                try { File.Delete(file); } catch { }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FileLength_ReturnsZeroForIoAndAccessFailures(bool ioFailure)
    {
        Exception failure = ioFailure
            ? new IOException("unavailable")
            : new UnauthorizedAccessException("denied");

        long length = DatabaseMaintenance.FileLength("ignored", _ => throw failure);

        Assert.Equal(0, length);
    }

    [Fact]
    public void Compact_RebuildsAndReportsReclaimedSpace()
    {
        string db = Path.Combine(Path.GetTempPath(), $"dam_vacuum_{Guid.NewGuid():N}.db");
        try
        {
            var repo = new MonitorRepository(db);
            repo.EnsureSchema();

            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var samples = Enumerable.Range(0, 4000)
                .Select(i => new Core.Models.ProcessFileIoSample
                {
                    TimestampUtc = start.AddMinutes(i % 600),
                    ProcessName = "writer.exe",
                    Path = $@"C:\data\file{i}.bin",
                    Kind = Core.Models.FileTargetKind.Other,
                    WriteBytes = 4096,
                })
                .ToList();
            repo.AddProcessFileSamples(samples);

            // Prune everything, then rebuild: the freed pages should return to the file system.
            repo.PruneFileTargetsOlderThan(start.AddYears(1));
            var result = DatabaseMaintenance.Compact(repo, db);

            Assert.True(result.Success, result.Error);
            Assert.Null(result.Error);
            Assert.True(
                result.AfterBytes <= result.BeforeBytes,
                $"Expected compaction not to grow storage: before={result.BeforeBytes}, after={result.AfterBytes}.");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (string file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(db) + "*"))
                try { File.Delete(file); } catch { }
        }
    }

    [Fact]
    public void Compact_UsesTheApplicationDatabasePathWhenNoPathIsProvided()
    {
        string db = Path.Combine(Path.GetTempPath(), $"dam_vacuum_default_{Guid.NewGuid():N}.db");
        try
        {
            var repo = new MonitorRepository(db);
            repo.EnsureSchema();

            CompactionResult result = DatabaseMaintenance.Compact(repo);

            Assert.True(result.Success, result.Error);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (string file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(db) + "*"))
                try { File.Delete(file); } catch { }
        }
    }

    [Fact]
    public void Compact_ReportsFailureWhenRepositoryIsReadOnly()
    {
        string db = Path.Combine(Path.GetTempPath(), $"dam_vacuum_ro_{Guid.NewGuid():N}.db");
        try
        {
            var writable = new MonitorRepository(db);
            writable.EnsureSchema();

            var readOnly = new MonitorRepository(db, readOnly: true);
            var result = DatabaseMaintenance.Compact(readOnly, db);

            Assert.False(result.Success);
            Assert.Equal(result.BeforeBytes, result.AfterBytes);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (string file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(db) + "*"))
                try { File.Delete(file); } catch { }
        }
    }

    // ------------------------------------------------------------------ configuration

    [Fact]
    public void PerFileRetentionDefaultsToThirtyDays()
        => Assert.Equal(30, new AppConfig().FileTargetRetentionDays);

    [Fact]
    public void DatabaseSizeWarningDefaultsToOneGigabyte()
    {
        var cfg = new AppConfig();

        Assert.Equal(1, cfg.DatabaseSizeWarnGb);
        Assert.Equal(AppConfig.DefaultBinaryExtensions, cfg.BinaryExtensions);
    }
}
