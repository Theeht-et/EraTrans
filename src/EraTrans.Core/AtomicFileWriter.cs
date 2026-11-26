using System.Text;

namespace EraTrans.Core;

/// <summary>
/// Atomically persists files by writing to a same-directory temporary file with
/// write-through flushing before replacing the target path.
/// </summary>
internal static class AtomicFileWriter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllText(
        string path,
        string content,
        Encoding? encoding = null,
        int bufferSize = 4096)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        Encoding effectiveEncoding = encoding ?? Utf8WithoutBom;
        byte[] preamble = effectiveEncoding.GetPreamble();
        byte[] contentBytes = effectiveEncoding.GetBytes(content);
        byte[] bytes = preamble.Length == 0
            ? contentBytes
            : [.. preamble, .. contentBytes];

        WriteAllBytes(path, bytes, bufferSize);
    }

    public static void WriteAllBytes(
        string path,
        byte[] bytes,
        int bufferSize = 4096,
        Action? beforeReplace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = Path.Combine(
            string.IsNullOrEmpty(directory) ? "." : directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: bufferSize,
                       options: FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            beforeReplace?.Invoke();

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temporaryPath, path, destinationBackupFileName: null);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(temporaryPath, path, overwrite: true);
                }
            }
            else
            {
                File.Move(temporaryPath, path, overwrite: true);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temporary file.
            }
        }
    }
}
