using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClaudeRc.Core.Paths;

/// <summary>
/// Normalises folder paths (SPEC 3.2): full path, symlinks and junctions resolved, case as
/// on disk, no trailing separator except on a drive root (<c>C:\</c>).
/// </summary>
/// <remarks>
/// The normalised string is what the host stores, passes to claude as the working directory
/// and turns into the <c>.claude.json</c> project key (<see cref="Claude.ClaudeJsonPresets.ProjectKey"/>),
/// so it must be produced by this one function everywhere. A path that does not exist is
/// normalised as far as possible: its deepest existing ancestor is resolved and the rest is
/// kept as typed.
/// </remarks>
public static partial class PathNormalizer
{
    /// <summary>
    /// Comparer for normalised paths: ordinal, case-insensitive (two paths that differ only in
    /// case are the same folder on NTFS). Use it for folder-list lookups and adoption matching,
    /// never for <c>.claude.json</c> keys, which claude compares case-sensitively.
    /// </summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>Normalises <paramref name="path"/> against the file system.</summary>
    /// <exception cref="ArgumentException">The path is empty or not a valid path.</exception>
    public static string Normalize(string path)
    {
        var full = Lexical(path);

        // Resolve the deepest existing ancestor, then append the missing tail as typed.
        var tail = new Stack<string>();
        var current = full;
        while (true)
        {
            var resolved = TryGetFinalPath(current);
            if (resolved is not null)
            {
                foreach (var part in tail)
                    resolved = Path.Join(resolved, part);
                return resolved;
            }
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent))
                return full;
            tail.Push(Path.GetFileName(current));
            current = parent;
        }
    }

    /// <summary>
    /// Purely textual normalisation: strips a <c>\\?\</c> prefix, makes the path absolute,
    /// resolves <c>.</c> and <c>..</c>, upper-cases the drive letter and removes a trailing
    /// separator (except on a drive root).
    /// </summary>
    internal static string Lexical(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var p = path.Trim();
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
            p = p[4..];

        // "C:" alone is drive-relative to Windows; for a folder list it can only mean the root.
        if (p.Length == 2 && p[1] == ':' && char.IsAsciiLetter(p[0]))
            p += '\\';

        var full = Path.GetFullPath(p);
        if (full.Length >= 2 && full[1] == ':' && char.IsAsciiLetterLower(full[0]))
            full = char.ToUpperInvariant(full[0]) + full[1..];
        return TrimTrailingSeparator(full);
    }

    private static string TrimTrailingSeparator(string full)
    {
        var end = full.Length;
        while (end > 0 && (full[end - 1] == '\\' || full[end - 1] == '/'))
            end--;
        if (end == 2 && full[1] == ':')
            return full[..2] + '\\'; // drive root keeps its separator
        return end == full.Length ? full : full[..end];
    }

    private static string? TryGetFinalPath(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
            return null;
        using var handle = CreateFileW(path, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;
        var buffer = new char[1024];
        while (true)
        {
            uint len;
            unsafe
            {
                fixed (char* p = buffer)
                    len = GetFinalPathNameByHandleW(handle, p, (uint)buffer.Length, 0);
            }
            if (len == 0)
                return null;
            if (len < buffer.Length)
                return Lexical(new string(buffer, 0, (int)len));
            buffer = new char[len + 1];
        }
    }

    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle file, char* buffer, uint bufferLength, uint flags);
}
