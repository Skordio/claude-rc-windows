using ClaudeRc.Core.Paths;

namespace ClaudeRc.Core.Adoption;

/// <summary>
/// What the process scan read about one running process (SPEC 7). Everything except the ids and
/// the image name may be unknown (null): the process could not be opened (another user, an
/// elevated process, a protected one) or had exited while it was read.
/// </summary>
public sealed record ProcessFacts
{
    /// <summary>Process id.</summary>
    public required int Pid { get; init; }

    /// <summary>The parent's process id when this process was created (it may since have exited, and its id be reused).</summary>
    public int ParentPid { get; init; }

    /// <summary>The image's file name, e.g. <c>claude.exe</c>.</summary>
    public required string ImageName { get; init; }

    /// <summary>Creation time (FILETIME ticks), which tells a process from a later one with the same id.</summary>
    public long? StartTime { get; init; }

    /// <summary>The arguments after the program (<c>argv[1..]</c> as <c>CommandLineToArgvW</c> splits them).</summary>
    public IReadOnlyList<string>? Arguments { get; init; }

    /// <summary>The current directory, as read from the process (often with a trailing <c>\</c>).</summary>
    public string? CurrentDirectory { get; init; }

    /// <summary>The string SID of the process token's user.</summary>
    public string? OwnerSid { get; init; }
}

/// <summary>A <c>claude rc</c> started outside the app: the top process of its tree and the folder it runs in.</summary>
/// <param name="Path">Its current directory, normalised (SPEC 3.2).</param>
/// <param name="Pid">Process id.</param>
/// <param name="StartTime">Creation time (FILETIME ticks), to recognise the same process later.</param>
public sealed record ExternalSession(string Path, int Pid, long StartTime);

/// <summary>
/// Decides which running processes are <c>claude rc</c> sessions started outside the app (SPEC 7),
/// over facts the Windows enumerator read. Pure: no process is opened here.
/// </summary>
/// <remarks>
/// A process matches when:
/// <list type="bullet">
/// <item>its image is <c>claude.exe</c> (or another configured claude image name) and its arguments
/// start with <c>rc</c> or <c>remote-control</c>, or its image is <c>node.exe</c> running Claude's
/// <c>cli.js</c> (a path ending in <c>claude-code\cli.js</c>, after any node options) with those
/// arguments after it;</item>
/// <item>its token user is the host's user;</item>
/// <item>its current directory is known;</item>
/// <item>it is not one of the host's own processes (its sessions' job members), nor a descendant of one;</item>
/// <item>no ancestor of it matches too: claude rc's own children count as part of it.</item>
/// </list>
/// Ancestors are followed by parent id only while the parent is older than the child, so a parent
/// id that was reused by a later process is not taken for the parent.
/// </remarks>
public sealed class ExternalSessionMatcher
{
    /// <summary>The native claude image (installer, and current npm installs: m0-findings 7).</summary>
    public const string ClaudeImage = "claude.exe";

    /// <summary>Old npm installs ran <c>cli.js</c> under node.</summary>
    public const string NodeImage = "node.exe";

    private static readonly string[] RemoteControlCommands = ["rc", "remote-control"];

    private readonly string _userSid;
    private readonly HashSet<string> _claudeImages;
    private readonly bool _matchNodeCli;
    private readonly Func<string, string?> _normalize;

    /// <summary>Creates a matcher.</summary>
    /// <param name="userSid">The host's user (string SID); only that user's processes match.</param>
    /// <param name="claudeImageNames">Image file names that count as claude (case-insensitive).</param>
    /// <param name="matchNodeCli">Whether <c>node.exe … claude-code\cli.js rc</c> matches.</param>
    /// <param name="normalize">Turns a process's current directory into a folder path; null result or an
    /// exception means "no folder". Default: <see cref="PathNormalizer.Normalize"/>.</param>
    public ExternalSessionMatcher(string userSid, IEnumerable<string> claudeImageNames, bool matchNodeCli,
        Func<string, string?>? normalize = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        ArgumentNullException.ThrowIfNull(claudeImageNames);
        _userSid = userSid;
        _claudeImages = new HashSet<string>(claudeImageNames.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
        _matchNodeCli = matchNodeCli;
        _normalize = normalize ?? PathNormalizer.Normalize;
    }

    /// <summary>
    /// The matcher the host uses: <c>claude.exe</c>, the node rule, and the file name of the
    /// settings' claude path when it is an <c>.exe</c> (so a renamed or test claude matches too).
    /// </summary>
    public static ExternalSessionMatcher ForHost(string userSid, string? claudePathSetting)
    {
        var names = new List<string> { ClaudeImage };
        if (!string.IsNullOrWhiteSpace(claudePathSetting)
            && Path.GetFileName(claudePathSetting.Trim().Trim('"')) is { Length: > 0 } name
            && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !name.Equals(NodeImage, StringComparison.OrdinalIgnoreCase)
            && !name.Equals(ClaudeImage, StringComparison.OrdinalIgnoreCase))
            names.Add(name);
        return new ExternalSessionMatcher(userSid, names, matchNodeCli: true);
    }

    /// <summary>The image names that count as claude.</summary>
    public IReadOnlyCollection<string> ClaudeImageNames => _claudeImages;

    /// <summary>Whether a process with this image could match, so the enumerator reads its details.</summary>
    public bool IsCandidateImage(string imageName) =>
        _claudeImages.Contains(imageName) || (_matchNodeCli && imageName.Equals(NodeImage, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the process is a claude rc of the host's user (image, arguments, owner); its folder is not checked.</summary>
    public bool IsRemoteControl(ProcessFacts process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.Arguments is not { } args || process.OwnerSid is null
            || !process.OwnerSid.Equals(_userSid, StringComparison.OrdinalIgnoreCase))
            return false;
        if (_claudeImages.Contains(process.ImageName))
            return args.Count > 0 && IsRemoteControlCommand(args[0]);
        if (_matchNodeCli && process.ImageName.Equals(NodeImage, StringComparison.OrdinalIgnoreCase))
            return IsNodeCliRemoteControl(args);
        return false;
    }

    private static bool IsRemoteControlCommand(string arg) => RemoteControlCommands.Contains(arg, StringComparer.Ordinal);

    // node [node options…] <…\claude-code\cli.js> rc|remote-control …
    private static bool IsNodeCliRemoteControl(IReadOnlyList<string> args)
    {
        var i = 0;
        while (i < args.Count && args[i].StartsWith('-'))
            i++;
        if (i + 1 >= args.Count)
            return false;
        var script = args[i].Replace('/', '\\');
        return script.EndsWith(@"\claude-code\cli.js", StringComparison.OrdinalIgnoreCase)
            && IsRemoteControlCommand(args[i + 1]);
    }

    /// <summary>The normalised folder the process runs in, or null when unknown.</summary>
    public string? FolderOf(ProcessFacts process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (string.IsNullOrWhiteSpace(process.CurrentDirectory))
            return null;
        try
        {
            return _normalize(process.CurrentDirectory) is { Length: > 0 } path ? path : null;
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The external claude rc sessions among <paramref name="processes"/> (all processes of one
    /// snapshot, so ancestors can be followed), leaving out <paramref name="excludedPids"/> (the
    /// host's own processes) and their descendants, and counting each tree once, at its top.
    /// </summary>
    public IReadOnlyList<ExternalSession> Match(IReadOnlyCollection<ProcessFacts> processes, IReadOnlySet<int> excludedPids)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(excludedPids);
        var byPid = new Dictionary<int, ProcessFacts>();
        foreach (var p in processes)
            byPid.TryAdd(p.Pid, p);
        var rc = new HashSet<int>(processes.Where(p => p.StartTime is not null && IsRemoteControl(p)).Select(p => p.Pid));

        var found = new List<ExternalSession>();
        foreach (var p in byPid.Values)
        {
            if (!rc.Contains(p.Pid) || excludedPids.Contains(p.Pid))
                continue;
            if (HasAncestor(p, byPid, a => excludedPids.Contains(a.Pid) || rc.Contains(a.Pid)))
                continue;
            if (FolderOf(p) is { } folder)
                found.Add(new ExternalSession(folder, p.Pid, p.StartTime!.Value));
        }
        return found;
    }

    // Follows parent ids while each parent is known to be older than its child (else the id was reused).
    private static bool HasAncestor(ProcessFacts process, Dictionary<int, ProcessFacts> byPid, Func<ProcessFacts, bool> predicate)
    {
        var seen = new HashSet<int> { process.Pid };
        var child = process;
        while (byPid.TryGetValue(child.ParentPid, out var parent) && seen.Add(parent.Pid))
        {
            if (child.StartTime is not { } childStart || parent.StartTime is not { } parentStart || parentStart > childStart)
                return false;
            if (predicate(parent))
                return true;
            child = parent;
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="fresh"/>, read again just before take over, is still the process the
    /// scan found: the same creation time, still a claude rc of this user, in the same folder.
    /// </summary>
    public bool StillMatches(ProcessFacts fresh, ExternalSession expected)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        ArgumentNullException.ThrowIfNull(expected);
        return fresh.Pid == expected.Pid
            && fresh.StartTime == expected.StartTime
            && IsRemoteControl(fresh)
            && FolderOf(fresh) is { } folder
            && PathNormalizer.Comparer.Equals(folder, expected.Path);
    }

    /// <summary>Groups sessions by folder (case-insensitive), oldest first in each folder.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ExternalSession>> ByFolder(IEnumerable<ExternalSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        return sessions
            .GroupBy(s => s.Path, PathNormalizer.Comparer)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ExternalSession>)[.. g.OrderBy(s => s.StartTime).ThenBy(s => s.Pid)],
                PathNormalizer.Comparer);
    }
}
