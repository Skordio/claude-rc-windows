using System.Text;

namespace ClaudeRc.Core.Storage;

/// <summary>
/// Writes a file so that readers only ever see the old or the new contents: the new
/// contents go to a temp file in the same folder, which then replaces the target with
/// <see cref="File.Replace(string, string, string?, bool)"/> (or is moved into place with
/// <see cref="File.Move(string, string)"/> when the target does not exist yet).
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Atomically writes <paramref name="contents"/> as UTF-8 without a BOM.</summary>
    /// <param name="path">Target file.</param>
    /// <param name="contents">New contents.</param>
    /// <param name="createDirectory">Create the target's folder if it does not exist.</param>
    /// <exception cref="IOException">The file could not be written; the target is unchanged and no temp file is left.</exception>
    public static void WriteAllText(string path, string contents, bool createDirectory = false) =>
        WriteAllBytes(path, Utf8NoBom.GetBytes(contents), createDirectory);

    /// <summary>Atomically writes <paramref name="bytes"/>.</summary>
    /// <param name="path">Target file.</param>
    /// <param name="bytes">New contents.</param>
    /// <param name="createDirectory">Create the target's folder if it does not exist.</param>
    /// <exception cref="IOException">The file could not be written; the target is unchanged and no temp file is left.</exception>
    public static void WriteAllBytes(string path, byte[] bytes, bool createDirectory = false)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full) ?? throw new IOException($"No folder for {full}");
        if (createDirectory)
            Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(full))
            {
                try
                {
                    File.Replace(temp, full, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    return;
                }
                catch (FileNotFoundException) when (!File.Exists(full))
                {
                    // The target vanished in between: fall through to a plain move.
                }
            }
            File.Move(temp, full);
        }
        catch (UnauthorizedAccessException e)
        {
            TryDelete(temp);
            throw new IOException(e.Message, e);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
