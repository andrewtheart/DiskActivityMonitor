namespace DiskActivityMonitor.Core.Files;

/// <summary>Why a delete attempt ended the way it did.</summary>
public enum FileDeleteStatus
{
    /// <summary>The file was removed.</summary>
    Deleted,

    /// <summary>The file no longer exists, so there was nothing to remove.</summary>
    NotFound,

    /// <summary>Windows refused the operation for permission reasons.</summary>
    AccessDenied,

    /// <summary>The file carries the read-only attribute.</summary>
    ReadOnly,

    /// <summary>Another process holds the file open with a conflicting share mode.</summary>
    Locked,

    /// <summary>Any other failure.</summary>
    Failed,
}

/// <summary>Result of a delete attempt, including a message suitable for display.</summary>
public sealed record FileDeleteOutcome(FileDeleteStatus Status, string Message)
{
    /// <summary>True when the file is no longer on disk.</summary>
    public bool Removed => Status is FileDeleteStatus.Deleted or FileDeleteStatus.NotFound;

    /// <summary>True when identifying the holding process would help the user.</summary>
    public bool NeedsLockAnalysis => Status is FileDeleteStatus.Locked or FileDeleteStatus.AccessDenied;
}

/// <summary>
/// Deletes files on behalf of the UI and classifies failures precisely enough that the user is
/// told whether the problem is permissions, a read-only attribute or another process holding the
/// file open. Windows reports all three as exceptions that look alike without inspecting the
/// underlying error code.
/// </summary>
public static class FileDeletionService
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    /// <summary>Attempts to delete <paramref name="path"/> and classifies the outcome.</summary>
    public static FileDeleteOutcome Delete(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new FileDeleteOutcome(FileDeleteStatus.Failed, "No file path was supplied.");

        try
        {
            if (!File.Exists(path))
                return new FileDeleteOutcome(FileDeleteStatus.NotFound, "The file no longer exists.");

            // A read-only file throws UnauthorizedAccessException that is indistinguishable from a
            // genuine permission failure, so detect the attribute before attempting the delete.
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                return new FileDeleteOutcome(
                    FileDeleteStatus.ReadOnly,
                    "The file is marked read-only. Clear the read-only attribute and try again.");
            }

            File.Delete(path);
            return new FileDeleteOutcome(FileDeleteStatus.Deleted, "File deleted.");
        }
        catch (Exception ex)
        {
            return Classify(ex);
        }
    }

    /// <summary>Maps a delete exception onto a status and a message the user can act on.</summary>
    internal static FileDeleteOutcome Classify(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException =>
            new FileDeleteOutcome(FileDeleteStatus.NotFound, "The file no longer exists."),

        UnauthorizedAccessException =>
            new FileDeleteOutcome(
                FileDeleteStatus.AccessDenied,
                "Access denied. The file may be protected, owned by another account, or require administrator rights."),

        IOException io => ClassifyIo(io),

        _ => new FileDeleteOutcome(FileDeleteStatus.Failed, $"The file could not be deleted: {ex.Message}"),
    };

    private static FileDeleteOutcome ClassifyIo(IOException io) => ErrorCodeOf(io) switch
    {
        ErrorSharingViolation or ErrorLockViolation =>
            new FileDeleteOutcome(
                FileDeleteStatus.Locked,
                "The file is in use by another process."),

        ErrorAccessDenied =>
            new FileDeleteOutcome(
                FileDeleteStatus.AccessDenied,
                "Access denied. The file may be protected, owned by another account, or require administrator rights."),

        ErrorFileNotFound or ErrorPathNotFound =>
            new FileDeleteOutcome(FileDeleteStatus.NotFound, "The file no longer exists."),

        _ => new FileDeleteOutcome(FileDeleteStatus.Failed, $"The file could not be deleted: {io.Message}"),
    };

    /// <summary>Extracts the Win32 error code embedded in an <see cref="IOException"/> HRESULT.</summary>
    internal static int ErrorCodeOf(IOException io) => io.HResult & 0xFFFF;
}
