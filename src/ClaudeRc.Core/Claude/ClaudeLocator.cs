using System.Text.RegularExpressions;

namespace ClaudeRc.Core.Claude;

/// <summary>The few file-system reads <see cref="ClaudeLocator"/> needs, so tests can fake them.</summary>
public interface IFileProbe
{
    /// <summary>True when <paramref name="path"/> is an existing file (not a directory).</summary>
    bool FileExists(string path);

    /// <summary>Length of an existing file in bytes.</summary>
    long FileLength(string path);

    /// <summary>Reads a (small) text file.</summary>
    string ReadAllText(string path);
}

/// <summary><see cref="IFileProbe"/> over the real file system.</summary>
public sealed class RealFileProbe : IFileProbe
{
    /// <inheritdoc/>
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc/>
    public long FileLength(string path) => new FileInfo(path).Length;

    /// <inheritdoc/>
    public string ReadAllText(string path) => File.ReadAllText(path);
}

/// <summary>Where <see cref="ClaudeLocator"/> found claude.</summary>
public enum ClaudeSource
{
    /// <summary>The <c>claude_path</c> setting.</summary>
    Settings,
    /// <summary><c>%USERPROFILE%\.local\bin\claude.exe</c> (native installer).</summary>
    NativeInstall,
    /// <summary><c>claude.exe</c> on PATH.</summary>
    Path,
    /// <summary><c>claude.cmd</c> on PATH (npm install), resolved to what it launches.</summary>
    NpmShim,
}

/// <summary>
/// How to run claude. The session command line is <see cref="Program"/> followed by
/// <see cref="PrefixArguments"/> and then claude's own arguments (<c>rc --name …</c>), passed
/// as an argument list (never through <c>cmd.exe</c>; SPEC 6.3).
/// </summary>
/// <param name="Program">The executable to start: <c>claude.exe</c>, or <c>node.exe</c> for a legacy npm install.</param>
/// <param name="PrefixArguments">Arguments before claude's own: empty, or the resolved <c>cli.js</c> for a legacy npm install.</param>
/// <param name="Source">Which rule of SPEC 6.1 matched.</param>
/// <param name="FoundAt">The file that matched (the setting, <c>claude.exe</c> or <c>claude.cmd</c>); shown in the status bar.</param>
public sealed record ClaudeLaunch(string Program, IReadOnlyList<string> PrefixArguments, ClaudeSource Source, string FoundAt);

/// <summary>Result of <see cref="ClaudeLocator.Locate"/>: a launch, or an error for the rows.</summary>
/// <param name="Launch">How to run claude, when found.</param>
/// <param name="Error">Why claude cannot be run, when not found.</param>
public sealed record LocateResult(ClaudeLaunch? Launch, string? Error)
{
    /// <summary>True when <see cref="Launch"/> is set.</summary>
    public bool Found => Launch is not null;
}

/// <summary>
/// Finds claude (SPEC 6.1), in order: the settings override; the native install
/// <c>%USERPROFILE%\.local\bin\claude.exe</c>; <c>claude.exe</c> on PATH; <c>claude.cmd</c>
/// on PATH. A <c>claude.cmd</c> npm shim is resolved to the program it runs (m0-findings 7):
/// the native <c>claude.exe</c> under <c>node_modules</c>, or <c>node</c> plus <c>cli.js</c> for
/// old shims. A set override that does not exist is an error, not a fall-through.
/// </summary>
/// <param name="files">File-system access (tests pass a fake).</param>
/// <param name="userProfile">The user's profile folder, for the native-install location.</param>
public sealed partial class ClaudeLocator(IFileProbe files, string userProfile)
{
    /// <summary>The row error when no claude is found (SPEC 6.1).</summary>
    public const string NotFoundMessage = "claude not found: install Claude Code or set its path in Settings";

    /// <summary>The row error for an npm install whose postinstall did not run.</summary>
    public const string IncompleteNpmMessage = "npm install of Claude Code is incomplete (postinstall did not run)";

    // The npm placeholder bin/claude.exe is a tiny stub; the real binary is hundreds of MB.
    private const long StubMaxBytes = 4096;

    [GeneratedRegex("\"([^\"]*)\"")]
    private static partial Regex Quoted();

    /// <summary>
    /// True when <paramref name="path"/> may be the <c>claude_path</c> setting: an <c>.exe</c>,
    /// run directly, or a <c>.cmd</c> npm shim, resolved to the program it runs (SPEC 6.1, 6.3).
    /// </summary>
    public static bool IsAllowedOverride(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);

    /// <summary>The error for a <c>claude_path</c> setting that <see cref="IsAllowedOverride"/> refuses.</summary>
    public static string OverrideKindMessage(string path) => $"claude path from Settings must be claude.exe or claude.cmd: {path}";

    /// <summary>Finds claude.</summary>
    /// <param name="overridePath">The <c>claude_path</c> setting, or <see langword="null"/>.</param>
    /// <param name="pathVariable">The PATH to search (the host passes the freshly refreshed user + system PATH).</param>
    public LocateResult Locate(string? overridePath, string? pathVariable)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var full = Path.GetFullPath(overridePath.Trim().Trim('"'));
            // Anything but an .exe (a .bat, a script) would be run through a shell, which would
            // parse the folder name in --name (SPEC 6.3); a .cmd is resolved to what it runs.
            if (!IsAllowedOverride(full))
                return Fail(OverrideKindMessage(full));
            if (!files.FileExists(full))
                return Fail($"claude path from Settings not found: {full}");
            return full.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                ? ResolveShim(full, pathVariable, ClaudeSource.Settings)
                : Ok(full, [], ClaudeSource.Settings, full);
        }

        var native = Path.Combine(userProfile, ".local", "bin", "claude.exe");
        if (files.FileExists(native))
            return Ok(native, [], ClaudeSource.NativeInstall, native);

        var dirs = PathDirectories(pathVariable);
        if (FindOnPath(dirs, "claude.exe") is { } exe)
            return Ok(exe, [], ClaudeSource.Path, exe);
        if (FindOnPath(dirs, "claude.cmd") is { } cmd)
            return ResolveShim(cmd, pathVariable, ClaudeSource.NpmShim);

        return Fail(NotFoundMessage);
    }

    private LocateResult ResolveShim(string shim, string? pathVariable, ClaudeSource source)
    {
        string text;
        try { text = files.ReadAllText(shim); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail($"cannot read {shim}: {e.Message}");
        }

        var dp0 = Path.GetDirectoryName(shim) + "\\";
        var lines = text.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var targets = Quoted().Matches(lines[i])
                .Select(m => m.Groups[1].Value)
                .Where(v => v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || v.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                .Select(v => ExpandDp0(v, dp0))
                .Where(v => v is not null)
                .Select(v => v!)
                .ToList();
            if (targets.Count == 0)
                continue;

            if (targets.FirstOrDefault(t => t.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) is { } cliJs)
            {
                if (!files.FileExists(cliJs))
                    return Fail($"{shim} runs {cliJs}, which does not exist");
                var bundled = Path.Combine(dp0, "node.exe");
                var node = files.FileExists(bundled) ? bundled : FindOnPath(PathDirectories(pathVariable), "node.exe");
                if (node is null)
                    return Fail($"{shim} needs node.exe, which was not found next to it or on PATH");
                return Ok(node, [cliJs], source, shim);
            }

            var target = targets[^1];
            if (!files.FileExists(target))
                return Fail($"{shim} runs {target}, which does not exist");
            if (files.FileLength(target) < StubMaxBytes)
                return Fail(IncompleteNpmMessage);
            return Ok(target, [], source, shim);
        }
        return Fail($"unrecognised claude.cmd: {shim}");
    }

    // Shim paths are relative to the shim's folder through %dp0% (or %~dp0); anything else is
    // not a path we can resolve.
    private static string? ExpandDp0(string value, string dp0)
    {
        string rest;
        if (value.StartsWith("%dp0%", StringComparison.OrdinalIgnoreCase))
            rest = value[5..];
        else if (value.StartsWith("%~dp0", StringComparison.OrdinalIgnoreCase))
            rest = value[5..];
        else
            return Path.IsPathFullyQualified(value) ? Path.GetFullPath(value) : null;
        return Path.GetFullPath(dp0 + rest.TrimStart('\\', '/'));
    }

    private string? FindOnPath(IEnumerable<string> dirs, string fileName)
    {
        foreach (var dir in dirs)
        {
            var candidate = Path.Combine(dir, fileName);
            if (files.FileExists(candidate))
                return candidate;
        }
        return null;
    }

    private static List<string> PathDirectories(string? pathVariable)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(pathVariable))
            return result;
        foreach (var raw in pathVariable.Split(';'))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0 || !Path.IsPathFullyQualified(dir))
                continue;
            try { result.Add(Path.GetFullPath(dir)); }
            catch (ArgumentException) { }
        }
        return result;
    }

    private static LocateResult Ok(string program, string[] prefix, ClaudeSource source, string foundAt) =>
        new(new ClaudeLaunch(program, prefix, source, foundAt), null);

    private static LocateResult Fail(string error) => new(null, error);
}
