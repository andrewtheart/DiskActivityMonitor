using System.Runtime.InteropServices;
using System.Text;
using DiskActivityMonitor.Core.Models;

namespace DiskActivityMonitor.Core.Collection;

/// <summary>
/// Turns raw kernel file paths into something a person can act on: a drive-letter path plus a
/// classification of the work the write represents.
///
/// This is what makes writes attributed to the kernel <c>System</c> process explainable. System
/// (PID 4) issues writes on behalf of the whole machine - the cache manager flushing pages other
/// applications dirtied, NTFS metadata and journal updates, the memory manager writing the paging
/// file, and kernel-mode drivers - so the target file, not the process name, identifies the work.
/// </summary>
public static class FileTargetNormalizer
{
    /// <summary>
    /// Stand-in path for the bytes a process wrote to files that were too small or too numerous to
    /// list individually. Without it a breakdown of a scattered writer would silently lose most of
    /// its bytes.
    /// </summary>
    public const string OtherFilesPath = "(all other files)";
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int QueryDosDeviceW(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);

    /// <summary>
    /// Maps NT device prefixes (<c>\Device\HarddiskVolume3</c>) to their drive letter (<c>C:</c>).
    /// Rebuilt by callers occasionally so newly mounted volumes are picked up.
    /// </summary>
    public static Dictionary<string, string> BuildVolumeMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new StringBuilder(1024);
        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            string dosName = $"{letter}:";
            try
            {
                if (QueryDosDeviceW(dosName, buffer, buffer.Capacity) == 0)
                    continue;
            }
            catch (DllNotFoundException)
            {
                return map;
            }

            string device = buffer.ToString();
            if (device.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
                map[device] = dosName;
        }
        return map;
    }

    /// <summary>
    /// Rewrites a raw kernel path into a readable one: NT device prefixes become drive letters,
    /// <c>\??\</c> prefixes are stripped and the multiple-UNC-provider device becomes <c>\\</c>.
    /// Unknown device paths are returned unchanged rather than discarded.
    /// </summary>
    public static string Normalize(string? rawPath, IReadOnlyDictionary<string, string>? volumeMap = null)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return "";

        string path = rawPath.Trim();

        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
            path = path[4..];

        if (path.StartsWith(@"\Device\Mup\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[@"\Device\Mup\".Length..];

        if (path.StartsWith(@"\Device\LanmanRedirector\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[@"\Device\LanmanRedirector\".Length..];

        if (volumeMap is { Count: > 0 } && path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            // The device name ends at the fourth backslash: \Device\HarddiskVolume3\Windows\...
            int split = path.IndexOf('\\', @"\Device\".Length);
            string device = split < 0 ? path : path[..split];
            if (volumeMap.TryGetValue(device, out var drive))
                return split < 0 ? drive + "\\" : drive + path[split..];
        }

        return path;
    }

    /// <summary>Classifies a normalized path by the kind of work its writes represent.</summary>
    public static FileTargetKind Classify(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return FileTargetKind.Other;

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return FileTargetKind.Network;

        string lower = path.ToLowerInvariant();
        string file = FileNameOf(lower);

        // NTFS keeps its bookkeeping in reserved $-prefixed files at the volume root.
        if (file.StartsWith('$') || lower.Contains(@"\$extend\", StringComparison.Ordinal))
            return FileTargetKind.NtfsMetadata;

        if (file is "pagefile.sys" or "swapfile.sys")
            return FileTargetKind.PagingFile;
        if (file == "hiberfil.sys")
            return FileTargetKind.Hibernation;

        if (lower.Contains(@"\system volume information\", StringComparison.Ordinal))
            return FileTargetKind.ShadowCopy;

        if (file is "ntuser.dat" or "usrclass.dat"
            || file.StartsWith("ntuser.dat", StringComparison.Ordinal)
            || file.StartsWith("usrclass.dat", StringComparison.Ordinal)
            || lower.Contains(@"\system32\config\", StringComparison.Ordinal))
            return FileTargetKind.Registry;

        if (lower.Contains(@"\winevt\logs\", StringComparison.Ordinal) || file.EndsWith(".evtx", StringComparison.Ordinal))
            return FileTargetKind.EventLog;

        if (lower.Contains(@"\softwaredistribution\", StringComparison.Ordinal)
            || lower.Contains(@"\windows\servicing\", StringComparison.Ordinal)
            || lower.Contains(@"\windows\winsxs\", StringComparison.Ordinal))
            return FileTargetKind.WindowsUpdate;

        if (file == "windows.edb" || lower.Contains(@"\search\data\", StringComparison.Ordinal))
            return FileTargetKind.SearchIndex;

        if (lower.Contains(@"\windows defender\", StringComparison.Ordinal)
            || lower.Contains(@"\windows\defender\", StringComparison.Ordinal))
            return FileTargetKind.Defender;

        if (file.EndsWith(".vhd", StringComparison.Ordinal) || file.EndsWith(".vhdx", StringComparison.Ordinal)
            || file.EndsWith(".avhdx", StringComparison.Ordinal) || file.EndsWith(".vmdk", StringComparison.Ordinal)
            || file.EndsWith(".vdi", StringComparison.Ordinal))
            return FileTargetKind.VirtualDisk;

        if (lower.Contains(@"\temp\", StringComparison.Ordinal) || lower.Contains(@"\tmp\", StringComparison.Ordinal)
            || file.EndsWith(".tmp", StringComparison.Ordinal))
            return FileTargetKind.Temporary;

        if (file.EndsWith(".log", StringComparison.Ordinal) || file.EndsWith(".etl", StringComparison.Ordinal)
            || file.EndsWith(".dmp", StringComparison.Ordinal))
            return FileTargetKind.LogFile;

        if (file.EndsWith(".db", StringComparison.Ordinal) || file.EndsWith(".sqlite", StringComparison.Ordinal)
            || file.EndsWith(".edb", StringComparison.Ordinal) || file.EndsWith(".mdf", StringComparison.Ordinal)
            || file.EndsWith(".ldf", StringComparison.Ordinal) || file.EndsWith("-wal", StringComparison.Ordinal)
            || file.EndsWith(".jdb", StringComparison.Ordinal))
            return FileTargetKind.Database;

        return FileTargetKind.Other;
    }

    /// <summary>Short label shown next to a file target.</summary>
    public static string Label(FileTargetKind kind) => kind switch
    {
        FileTargetKind.NtfsMetadata => "NTFS metadata",
        FileTargetKind.PagingFile => "Paging file",
        FileTargetKind.Hibernation => "Hibernation",
        FileTargetKind.Registry => "Registry hive",
        FileTargetKind.EventLog => "Event log",
        FileTargetKind.WindowsUpdate => "Windows Update",
        FileTargetKind.SearchIndex => "Search index",
        FileTargetKind.Defender => "Defender",
        FileTargetKind.VirtualDisk => "Virtual disk",
        FileTargetKind.ShadowCopy => "Shadow copy",
        FileTargetKind.Temporary => "Temporary",
        FileTargetKind.LogFile => "Log file",
        FileTargetKind.Database => "Database",
        FileTargetKind.Network => "Network path",
        _ => "File",
    };

    /// <summary>One sentence explaining what causes writes to this kind of target.</summary>
    public static string Explain(FileTargetKind kind) => kind switch
    {
        FileTargetKind.NtfsMetadata =>
            "NTFS bookkeeping written by the filesystem itself - the master file table, transaction log, allocation bitmap and change journal. Heavy traffic here means many files are being created, renamed, extended or deleted.",
        FileTargetKind.PagingFile =>
            "The memory manager writing pages out of RAM. This grows when the machine is short of memory rather than because an application asked to write a file.",
        FileTargetKind.Hibernation =>
            "The hibernation image written when the machine hibernates or fast-startup shuts down.",
        FileTargetKind.Registry =>
            "Registry hives flushed to disk. Applications that write settings constantly show up here through the kernel rather than under their own name.",
        FileTargetKind.EventLog => "Windows event log channels being appended to.",
        FileTargetKind.WindowsUpdate => "Windows Update payloads and component servicing.",
        FileTargetKind.SearchIndex => "The Windows Search index being rebuilt or updated.",
        FileTargetKind.Defender => "Microsoft Defender definition updates and scan bookkeeping.",
        FileTargetKind.VirtualDisk =>
            "A virtual disk image. Everything written inside the virtual machine, container or WSL distribution lands in this one host file.",
        FileTargetKind.ShadowCopy => "Volume Shadow Copy / System Restore data, typically written during backups or restore-point creation.",
        FileTargetKind.Temporary => "Scratch files that are usually deleted again shortly afterwards.",
        FileTargetKind.LogFile => "Log, trace or crash-dump output.",
        FileTargetKind.Database => "A database, index or journal file being updated in place.",
        FileTargetKind.Network => "A network path, so the bytes leave over the network instead of hitting a local disk.",
        _ => "An ordinary file write.",
    };

    /// <summary>Explanation for one listed target, including the aggregate remainder row.</summary>
    public static string ExplainTarget(string? path, FileTargetKind kind)
        => string.Equals(path, OtherFilesPath, StringComparison.Ordinal)
            ? "Everything else this process wrote, combined. A large share here means the writes are spread thinly over many files rather than concentrated in a few - typical of the kernel flushing cached pages for the whole machine."
            : Explain(kind);

    /// <summary>
    /// Explains a process whose name alone does not identify the work, or null when the process
    /// name already says who is responsible.
    /// </summary>
    public static string? ExplainProcess(string? processName) => processName switch
    {
        "System" =>
            "System is the Windows kernel itself (PID 4), not an application. Its writes are issued by kernel components on behalf of the whole machine: the cache manager flushing pages that other applications dirtied, NTFS metadata and journal updates, the memory manager writing the paging file, and kernel-mode drivers. The files below identify which of those activities is responsible.",
        "Registry" =>
            "Registry is the kernel process that owns registry hive memory; its writes are hive flushes performed for whichever applications changed settings.",
        "MemCompression" =>
            "Memory Compression is a kernel process that manages compressed memory pages; its file writes relate to paging rather than application data.",
        _ => null,
    };

    private static string FileNameOf(string path)
    {
        int slash = path.LastIndexOf('\\');
        string name = slash >= 0 ? path[(slash + 1)..] : path;

        // Alternate data streams (e.g. $UsnJrnl:$J) keep their base name for classification.
        int colon = name.IndexOf(':');
        return colon > 0 ? name[..colon] : name;
    }
}
