using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeRc.Core.Logging;

/// <summary>
/// An append-only text log that rolls by size (SPEC 11). Each entry is one line,
/// <c>&lt;local ISO-8601 time&gt; &lt;message&gt;</c>. When the next entry would take the file
/// past <see cref="MaxBytes"/>, the file becomes <c>&lt;name&gt;.1</c> (older ones shift up)
/// and a new file starts; at most <see cref="KeepFiles"/> files exist, the current one
/// included. The optional header is the first line of every file. Thread-safe within one
/// instance; the file stays readable (and deletable) by other processes while it is written.
/// </summary>
public sealed class RollingLog
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly TimeProvider _time;
    private readonly string? _header;
    private readonly Lock _lock = new();

    /// <summary>Creates a log; nothing is written until <see cref="Write"/>.</summary>
    /// <param name="filePath">The current log file; its folder is created on first write.</param>
    /// <param name="maxBytes">Size at which the file is rolled.</param>
    /// <param name="keepFiles">Files kept in total, the current one included (at least 1).</param>
    /// <param name="time">Clock for the timestamps.</param>
    /// <param name="header">First line of every file (e.g. the folder path), or null.</param>
    public RollingLog(string filePath, long maxBytes, int keepFiles, TimeProvider time, string? header = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keepFiles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        FilePath = filePath;
        MaxBytes = maxBytes;
        KeepFiles = keepFiles;
        _time = time;
        _header = header is null ? null : OneLine(header);
    }

    /// <summary>The current log file.</summary>
    public string FilePath { get; }

    /// <summary>Size at which the file is rolled.</summary>
    public long MaxBytes { get; }

    /// <summary>Files kept in total, the current one included.</summary>
    public int KeepFiles { get; }

    /// <summary>Appends one timestamped line. Line breaks in <paramref name="message"/> become spaces.</summary>
    /// <exception cref="IOException">The log cannot be written.</exception>
    public void Write(string message)
    {
        var stamp = _time.GetLocalNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
        var entry = Utf8NoBom.GetBytes($"{stamp} {OneLine(message)}\n");
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            var info = new FileInfo(FilePath);
            var headerBytes = _header is null ? 0 : Utf8NoBom.GetByteCount(_header) + 1;
            if (info.Exists && info.Length > headerBytes && info.Length + entry.Length > MaxBytes)
                Roll();

            using var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length == 0 && _header is not null)
                fs.Write(Utf8NoBom.GetBytes(_header + "\n"));
            fs.Write(entry);
        }
    }

    /// <summary>
    /// The last <paramref name="count"/> entries, oldest first, read across the current and
    /// rolled files. Header lines are left out.
    /// </summary>
    public IReadOnlyList<string> ReadLastLines(int count)
    {
        var result = new List<string>();
        if (count <= 0)
            return result;
        lock (_lock)
        {
            for (var i = 0; i < KeepFiles && result.Count < count; i++)
            {
                var path = i == 0 ? FilePath : $"{FilePath}.{i}";
                string text;
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(fs, Utf8NoBom);
                    text = reader.ReadToEnd();
                }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { break; }

                var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var first = _header is not null && lines.Length > 0 && lines[0] == _header ? 1 : 0;
                for (var j = lines.Length - 1; j >= first && result.Count < count; j--)
                    result.Add(lines[j]);
            }
        }
        result.Reverse();
        return result;
    }

    private void Roll()
    {
        try
        {
            var oldest = $"{FilePath}.{KeepFiles - 1}";
            if (KeepFiles == 1)
            {
                File.Delete(FilePath);
                return;
            }
            File.Delete(oldest);
            for (var i = KeepFiles - 2; i >= 1; i--)
            {
                var from = $"{FilePath}.{i}";
                if (File.Exists(from))
                    File.Move(from, $"{FilePath}.{i + 1}", overwrite: true);
            }
            File.Move(FilePath, $"{FilePath}.1", overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A reader holds the file without delete sharing: keep appending; roll next time.
        }
    }

    private static string OneLine(string text) =>
        text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
}

/// <summary>The host's standard logs (SPEC 11).</summary>
public static class LogFiles
{
    /// <summary>host.log rolls at 5 MB.</summary>
    public const long HostLogMaxBytes = 5 * 1024 * 1024;

    /// <summary>host.log keeps 3 files.</summary>
    public const int HostLogKeepFiles = 3;

    /// <summary>Folder logs roll at 1 MB.</summary>
    public const long FolderLogMaxBytes = 1024 * 1024;

    /// <summary>Folder logs keep 2 files.</summary>
    public const int FolderLogKeepFiles = 2;

    /// <summary>
    /// The folder log's file name stem: the first 16 lower-case hex characters of the SHA-256 of
    /// the normalised path (UTF-8).
    /// </summary>
    public static string FolderHash(string normalizedPath) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)))[..16];

    /// <summary><c>&lt;logDir&gt;\host.log</c>: host events.</summary>
    public static RollingLog HostLog(string logDir, TimeProvider time) =>
        new(Path.Combine(logDir, "host.log"), HostLogMaxBytes, HostLogKeepFiles, time);

    /// <summary>
    /// <c>&lt;folderLogDir&gt;\&lt;hash&gt;.log</c>: starts, exits, exit codes, restarts, takeovers and
    /// prompt detections for one folder. Its first line is <c># folder: &lt;path&gt;</c>.
    /// </summary>
    public static RollingLog FolderLog(string folderLogDir, string normalizedPath, TimeProvider time) =>
        new(Path.Combine(folderLogDir, FolderHash(normalizedPath) + ".log"), FolderLogMaxBytes, FolderLogKeepFiles, time,
            header: "# folder: " + normalizedPath);
}
