using DiskActivityMonitor.Core.Collection;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Models;
using DiskActivityMonitor.Service;

namespace DiskActivityMonitor.Tests;

public sealed class FileTargetTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dam_files_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_db) + "*"))
            try { File.Delete(file); } catch { }
    }

    private static readonly Dictionary<string, string> VolumeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"\Device\HarddiskVolume3"] = "C:",
        [@"\Device\HarddiskVolume7"] = "D:",
    };

    [Theory]
    [InlineData(@"\Device\HarddiskVolume3\Windows\System32\config\SOFTWARE", @"C:\Windows\System32\config\SOFTWARE")]
    [InlineData(@"\Device\HarddiskVolume7\data\big.vhdx", @"D:\data\big.vhdx")]
    [InlineData(@"\Device\HarddiskVolume3\$Mft", @"C:\$Mft")]
    [InlineData(@"\??\C:\temp\file.txt", @"C:\temp\file.txt")]
    [InlineData(@"\Device\Mup\server\share\file.bin", @"\\server\share\file.bin")]
    [InlineData(@"\Device\HarddiskVolume9\unmapped\file", @"\Device\HarddiskVolume9\unmapped\file")]
    [InlineData(@"C:\already\resolved.txt", @"C:\already\resolved.txt")]
    public void Normalize_ResolvesKernelPaths(string raw, string expected)
        => Assert.Equal(expected, FileTargetNormalizer.Normalize(raw, VolumeMap));

    [Fact]
    public void Normalize_HandlesMissingInput()
    {
        Assert.Equal("", FileTargetNormalizer.Normalize(null, VolumeMap));
        Assert.Equal("", FileTargetNormalizer.Normalize("   ", VolumeMap));
        Assert.Equal(@"\Device\HarddiskVolume3\x", FileTargetNormalizer.Normalize(@"\Device\HarddiskVolume3\x", null));
        Assert.Equal("C:\\", FileTargetNormalizer.Normalize(@"\Device\HarddiskVolume3", VolumeMap));
    }

    [Theory]
    [InlineData(@"C:\$Mft", FileTargetKind.NtfsMetadata)]
    [InlineData(@"C:\$Extend\$UsnJrnl:$J", FileTargetKind.NtfsMetadata)]
    [InlineData(@"C:\pagefile.sys", FileTargetKind.PagingFile)]
    [InlineData(@"C:\hiberfil.sys", FileTargetKind.Hibernation)]
    [InlineData(@"C:\Windows\System32\config\SOFTWARE", FileTargetKind.Registry)]
    [InlineData(@"C:\Users\a\NTUSER.DAT{guid}.TM.blf", FileTargetKind.Registry)]
    [InlineData(@"C:\Windows\System32\winevt\Logs\System.evtx", FileTargetKind.EventLog)]
    [InlineData(@"C:\Windows\SoftwareDistribution\Download\x.cab", FileTargetKind.WindowsUpdate)]
    [InlineData(@"C:\ProgramData\Search\Data\Applications\Windows\Windows.edb", FileTargetKind.SearchIndex)]
    [InlineData(@"C:\ProgramData\Microsoft\Windows Defender\Definitions\x.vdm", FileTargetKind.Defender)]
    [InlineData(@"D:\wsl\ext4.vhdx", FileTargetKind.VirtualDisk)]
    [InlineData(@"C:\System Volume Information\{guid}{3808876b}", FileTargetKind.ShadowCopy)]
    [InlineData(@"C:\Users\a\AppData\Local\Temp\build.tmp", FileTargetKind.Temporary)]
    [InlineData(@"C:\logs\service.log", FileTargetKind.LogFile)]
    [InlineData(@"C:\app\store.sqlite", FileTargetKind.Database)]
    [InlineData(@"\\server\share\report.docx", FileTargetKind.Network)]
    [InlineData(@"C:\Users\a\Documents\notes.docx", FileTargetKind.Other)]
    [InlineData("", FileTargetKind.Other)]
    public void Classify_IdentifiesTheWorkBehindAWrite(string path, FileTargetKind expected)
        => Assert.Equal(expected, FileTargetNormalizer.Classify(path));

    [Fact]
    public void EveryKind_HasALabelAndExplanation()
    {
        foreach (FileTargetKind kind in Enum.GetValues<FileTargetKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(FileTargetNormalizer.Label(kind)));
            Assert.False(string.IsNullOrWhiteSpace(FileTargetNormalizer.Explain(kind)));
        }
    }

    [Fact]
    public void ExplainProcess_CoversKernelWriters()
    {
        Assert.Contains("kernel", FileTargetNormalizer.ExplainProcess("System"), StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(FileTargetNormalizer.ExplainProcess("Registry"));
        Assert.NotNull(FileTargetNormalizer.ExplainProcess("MemCompression"));
        Assert.Null(FileTargetNormalizer.ExplainProcess("chrome"));
        Assert.Null(FileTargetNormalizer.ExplainProcess(null));
    }

    [Fact]
    public void SelectFileTargets_KeepsBusiestFilesPerProcessAboveTheFloor()
    {
        var minute = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        var cfg = new AppConfig { FileTargetsPerProcessPerMinute = 2, FileTargetMinKbPerMinute = 100 };
        var accumulated = new Dictionary<(string Process, string Path), (long Read, long Write)>
        {
            [("System", @"C:\$Mft")] = (0, 900 * 1024),
            [("System", @"C:\pagefile.sys")] = (0, 500 * 1024),
            [("System", @"C:\small.txt")] = (0, 300 * 1024),
            [("System", @"C:\noise.txt")] = (0, 4 * 1024),
            [("chrome", @"C:\cache.db")] = (0, 200 * 1024),
            [("reader", @"C:\big.iso")] = (700 * 1024, 0),
        };

        var samples = CollectorWorker.SelectFileTargets(accumulated, minute, cfg);

        var system = samples.Where(s => s.ProcessName == "System").ToList();
        Assert.Equal(3, system.Count);   // two listed files plus the aggregate remainder
        Assert.Contains(system, s => s.Path == @"C:\$Mft" && s.Kind == FileTargetKind.NtfsMetadata);
        Assert.Contains(system, s => s.Path == @"C:\pagefile.sys" && s.Kind == FileTargetKind.PagingFile);
        Assert.DoesNotContain(samples, s => s.Path == @"C:\small.txt");
        Assert.DoesNotContain(samples, s => s.Path == @"C:\noise.txt");

        // Nothing is lost: files below the floor or beyond the cutoff land in the remainder row.
        var other = Assert.Single(system, s => s.Path == FileTargetNormalizer.OtherFilesPath);
        Assert.Equal((300 + 4) * 1024, other.WriteBytes);
        Assert.Equal(
            accumulated.Where(kv => kv.Key.Process == "System").Sum(kv => kv.Value.Write),
            system.Sum(s => s.WriteBytes));

        Assert.Contains(samples, s => s.ProcessName == "chrome");

        // Read-only traffic above the floor still counts as a target worth recording.
        Assert.Contains(samples, s => s.ProcessName == "reader" && s.ReadBytes == 700 * 1024);
        Assert.All(samples, s => Assert.Equal(minute, s.TimestampUtc));
    }

    [Fact]
    public void SelectFileTargets_ClampsDegenerateConfiguration()
    {
        var minute = DateTime.UtcNow;
        var cfg = new AppConfig { FileTargetsPerProcessPerMinute = 0, FileTargetMinKbPerMinute = -5 };
        var accumulated = new Dictionary<(string Process, string Path), (long Read, long Write)>
        {
            [("System", @"C:\a")] = (0, 10),
            [("System", @"C:\b")] = (0, 20),
        };

        var samples = CollectorWorker.SelectFileTargets(accumulated, minute, cfg);

        Assert.Equal(2, samples.Count);
        Assert.Equal(@"C:\b", samples[0].Path);
        Assert.Equal(FileTargetNormalizer.OtherFilesPath, samples[1].Path);
        Assert.Equal(10, samples[1].WriteBytes);
    }

    [Fact]
    public void FileTargetKeys_MergeCaseVariantsBeforeRanking()
    {
        var minute = DateTime.UtcNow;
        var cfg = new AppConfig { FileTargetsPerProcessPerMinute = 1, FileTargetMinKbPerMinute = 0 };
        var accumulated = new Dictionary<(string Process, string Path), (long Read, long Write)>(
            FileTargetKeyComparer.Instance);

        foreach (var (process, path, write) in new[]
        {
            ("System", @"C:\Logs\App.log", 600L),
            ("system", @"c:\logs\app.log", 500L),
            ("System", @"C:\pagefile.sys", 900L),
        })
        {
            accumulated.TryGetValue((process, path), out var cur);
            accumulated[(process, path)] = (cur.Read, cur.Write + write);
        }

        Assert.Equal(2, accumulated.Count);

        var samples = CollectorWorker.SelectFileTargets(accumulated, minute, cfg);

        // Merged 1100 bytes outrank the paging file rather than losing to it as two 600/500 entries.
        var listed = Assert.Single(samples, s => s.Path != FileTargetNormalizer.OtherFilesPath);
        Assert.Equal(@"C:\Logs\App.log", listed.Path);
        Assert.Equal(1100, listed.WriteBytes);
        Assert.Equal(900, Assert.Single(samples, s => s.Path == FileTargetNormalizer.OtherFilesPath).WriteBytes);
    }

    [Fact]
    public void ExplainTarget_DistinguishesTheAggregateRow()
    {
        Assert.Contains(
            "spread thinly",
            FileTargetNormalizer.ExplainTarget(FileTargetNormalizer.OtherFilesPath, FileTargetKind.Other));
        Assert.Equal(
            FileTargetNormalizer.Explain(FileTargetKind.PagingFile),
            FileTargetNormalizer.ExplainTarget(@"C:\pagefile.sys", FileTargetKind.PagingFile));
    }

    [Fact]
    public void Repository_RanksFilesAndPrunesSeparately()
    {
        var repo = new MonitorRepository(_db);
        repo.EnsureSchema();
        var minute = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

        repo.AddProcessFileSamples(
        [
            Sample(minute, "System", @"C:\$Mft", FileTargetKind.NtfsMetadata, 100, 10),
            Sample(minute, "System", @"C:\pagefile.sys", FileTargetKind.PagingFile, 500, 0),
            Sample(minute.AddMinutes(1), "System", @"C:\$mft", FileTargetKind.NtfsMetadata, 450, 0),
            Sample(minute, "chrome", @"C:\cache.db", FileTargetKind.Database, 900, 0),
        ]);

        var top = repo.GetTopFileTargets("System", minute, minute.AddMinutes(5), 10);
        Assert.Equal(2, top.Count);
        // Case-insensitive grouping: 100 + 450 outranks the paging file's 500.
        Assert.Equal(550, top[0].WriteBytes);
        Assert.Equal(FileTargetKind.NtfsMetadata, top[0].Kind);
        Assert.Equal(FileTargetKind.PagingFile, top[1].Kind);
        Assert.Equal(1050, repo.GetFileTargetWriteTotal("System", minute, minute.AddMinutes(5)));

        // Repeated inserts for the same minute accumulate rather than replace.
        repo.AddProcessFileSamples([Sample(minute, "chrome", @"C:\cache.db", FileTargetKind.Database, 100, 0)]);
        Assert.Equal(1000, repo.GetFileTargetWriteTotal("chrome", minute, minute.AddMinutes(5)));

        Assert.Equal(0, repo.PruneOlderThan(minute.AddMinutes(5)));   // per-file rows have their own retention
        Assert.Equal(4, repo.PruneFileTargetsOlderThan(minute.AddMinutes(5)));
        Assert.Empty(repo.GetTopFileTargets("System", minute, minute.AddMinutes(5), 10));
    }

    private static ProcessFileIoSample Sample(
        DateTime minute, string process, string path, FileTargetKind kind, long write, long read) => new()
        {
            TimestampUtc = minute,
            ProcessName = process,
            Path = path,
            Kind = kind,
            WriteBytes = write,
            ReadBytes = read,
        };
}
