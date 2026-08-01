using System.Security.Cryptography;
using System.Text;

namespace DiskActivityMonitor.Core;

internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The destination must include a directory.", nameof(path));
        string fileName = Path.GetFileName(path);
        string? tempPath = null;

        try
        {
            FileStream? stream = null;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                string candidate = Path.Combine(directory, $".{fileName}.{RandomNumberGenerator.GetHexString(16)}.tmp");
                try
                {
                    stream = new FileStream(
                        candidate,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 4096,
                        FileOptions.WriteThrough);
                    tempPath = candidate;
                    break;
                }
                catch (IOException) when (File.Exists(candidate))
                {
                }
            }

            if (stream is null || tempPath is null)
                throw new IOException("Could not create a temporary file for the atomic write.");

            using (stream)
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
            tempPath = null;
        }
        finally
        {
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    public static string ReadAllText(string path, long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
            throw new InvalidDataException($"File exceeds the {maximumBytes}-byte limit.");

        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}