using System.Text;

namespace DiskActivityMonitor.Core.Files;

/// <summary>A batch of lines read from a tailed file plus the offset to resume from.</summary>
/// <param name="Lines">Newly observed complete lines.</param>
/// <param name="NextOffset">Byte offset the next read should start at.</param>
/// <param name="Truncated">True when the file shrank, meaning it was rotated or rewritten.</param>
/// <param name="Error">Failure reason when the file could not be read, else null.</param>
/// <param name="SkippedBytes">Bytes intentionally skipped to keep the read bounded.</param>
public sealed record TailBatch(
    IReadOnlyList<string> Lines,
    long NextOffset,
    bool Truncated,
    string? Error,
    long SkippedBytes = 0)
{
    /// <summary>True when the read succeeded.</summary>
    public bool Success => Error is null;
}

/// <summary>
/// Reads the tail of a file without preventing the writer from continuing.
/// </summary>
/// <remarks>
/// Every open uses <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/> so tailing a
/// file that the monitored process is actively writing never blocks that process, and so a file
/// that gets deleted while being watched does not keep a handle alive.
/// </remarks>
public static class FileTailReader
{
    private const FileShare TailShare = FileShare.ReadWrite | FileShare.Delete;

    /// <summary>Default maximum bytes decoded by one tail read.</summary>
    public const int DefaultMaxReadBytes = 512 * 1024;

    /// <summary>
    /// Reads up to <paramref name="maxLines"/> trailing lines and returns the offset to resume from.
    /// </summary>
    public static TailBatch ReadTail(string path, int maxLines, int maxReadBytes = DefaultMaxReadBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, TailShare);

            long length = stream.Length;
            long start = Math.Max(0, length - Math.Max(1, maxReadBytes));
            stream.Position = start;

            var text = ReadText(stream);

            // A mid-file start almost certainly lands inside a line; drop that partial fragment.
            var lines = SplitLines(text);
            if (start > 0 && lines.Count > 1) lines.RemoveAt(0);

            if (lines.Count > maxLines) lines.RemoveRange(0, lines.Count - maxLines);

            return new TailBatch(lines, length, Truncated: false, Error: null, SkippedBytes: start);
        }
        catch (Exception ex)
        {
            return new TailBatch(Array.Empty<string>(), 0, false, Describe(ex));
        }
    }

    /// <summary>
    /// Reads whatever was appended after <paramref name="offset"/>. When the file has shrunk the
    /// batch is flagged as truncated and reading restarts from the beginning.
    /// </summary>
    public static TailBatch ReadFrom(
        string path,
        long offset,
        int maxLines,
        int maxReadBytes = DefaultMaxReadBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, TailShare);

            long length = stream.Length;
            if (length == offset)
                return new TailBatch(Array.Empty<string>(), offset, false, null);

            bool truncated = length < offset;
            long requestedStart = truncated ? 0 : offset;
            long start = Math.Max(requestedStart, length - Math.Max(1, maxReadBytes));
            long skippedBytes = start - requestedStart;
            stream.Position = start;

            var lines = SplitLines(ReadText(stream));
            if (skippedBytes > 0 && lines.Count > 1) lines.RemoveAt(0);
            if (lines.Count > maxLines) lines.RemoveRange(0, lines.Count - maxLines);

            return new TailBatch(lines, length, truncated, null, skippedBytes);
        }
        catch (Exception ex)
        {
            return new TailBatch(Array.Empty<string>(), offset, false, Describe(ex));
        }
    }

    private static string ReadText(Stream stream)
    {
        // detectEncodingFromByteOrderMarks handles UTF-8/UTF-16 logs; invalid bytes degrade to
        // replacement characters rather than throwing, which suits partially written files.
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        if (text.Length == 0) return lines;

        foreach (var line in text.Split('\n'))
            lines.Add(line.TrimEnd('\r'));

        // A trailing newline produces an empty final element that is not a real line.
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        return lines;
    }

    /// <summary>Turns a read failure into a message that explains what the user can do.</summary>
    internal static string Describe(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => "The file no longer exists.",
        UnauthorizedAccessException => "Access denied. Reading this file requires additional permissions.",
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 =>
            "The file is locked by another process with exclusive access, so it cannot be read.",
        IOException io => $"The file could not be read: {io.Message}",
        _ => $"The file could not be read: {ex.Message}",
    };
}
