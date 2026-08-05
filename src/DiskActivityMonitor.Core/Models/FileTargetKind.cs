namespace DiskActivityMonitor.Core.Models;

/// <summary>
/// Coarse classification of a file that received I/O. The raw path alone rarely explains why a
/// process - especially the kernel <c>System</c> process - is writing, so each target is tagged
/// with the kind of work it represents.
/// </summary>
public enum FileTargetKind
{
    /// <summary>Ordinary file with no more specific classification.</summary>
    Other = 0,

    /// <summary>NTFS internal metadata ($Mft, $LogFile, $Bitmap, $UsnJrnl, ...).</summary>
    NtfsMetadata = 1,

    /// <summary>Paging / swap backing store written by the memory manager.</summary>
    PagingFile = 2,

    /// <summary>Hibernation / fast-startup image.</summary>
    Hibernation = 3,

    /// <summary>Registry hives (SYSTEM, SOFTWARE, NTUSER.DAT, UsrClass.dat, ...).</summary>
    Registry = 4,

    /// <summary>Windows event log channels.</summary>
    EventLog = 5,

    /// <summary>Windows Update download and servicing payloads.</summary>
    WindowsUpdate = 6,

    /// <summary>Windows Search index storage.</summary>
    SearchIndex = 7,

    /// <summary>Microsoft Defender definitions, scan history and platform files.</summary>
    Defender = 8,

    /// <summary>Virtual disk images (.vhd, .vhdx, .vmdk - Hyper-V, WSL, sandboxes).</summary>
    VirtualDisk = 9,

    /// <summary>Volume Shadow Copy / System Restore storage.</summary>
    ShadowCopy = 10,

    /// <summary>Temporary files and scratch directories.</summary>
    Temporary = 11,

    /// <summary>Application or system log files.</summary>
    LogFile = 12,

    /// <summary>Database, index and journal files.</summary>
    Database = 13,

    /// <summary>A network (UNC / redirector) target rather than a local volume.</summary>
    Network = 14,
}
