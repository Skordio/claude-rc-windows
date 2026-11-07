namespace ClaudeRc.Host.Adoption;

/// <summary>A process opened and held by <see cref="ProcessTree"/>; its id cannot be reused while it is held.</summary>
internal interface ITreeProcess : IDisposable
{
    /// <summary>Creation time (FILETIME ticks), read when it was opened; null when it could not be read.</summary>
    long? StartTime { get; }

    /// <summary>The id of the process it was created from, read from the process itself when it was opened.</summary>
    int? ParentPid { get; }

    /// <summary>Exit time (FILETIME ticks) once it has exited, else null.</summary>
    long? ExitTime { get; }

    /// <summary>Whether it has exited.</summary>
    bool HasExited { get; }

    /// <summary>Terminates it; false when that failed (no access, or it has exited).</summary>
    bool Terminate();
}

/// <summary>The processes take over can see: a list, and a way to open one.</summary>
internal interface IProcessTable
{
    /// <summary>Every running process (id, parent id at creation, image name).</summary>
    IReadOnlyList<ProcessEntry> List();

    /// <summary>Opens the process with this id, or null when it cannot be opened (gone, or no access).</summary>
    ITreeProcess? Open(int pid);
}

/// <summary>
/// The tree take over terminates (SPEC 7 step 3): the root and every process below it, found by
/// parent id. Every member is opened when it is found and held until <see cref="Dispose"/>, so the
/// id of a member that exits cannot be reused meanwhile and the parent links stay true. A process
/// counts as a child of a member only if, read from the opened process itself, its parent id is that
/// member's and it was created after the member was and (if the member has exited) before the member
/// exited. Excluded ids (the host and its sessions) are never added, walked below or terminated.
/// </summary>
internal sealed class ProcessTree : IDisposable
{
    private readonly IProcessTable _table;
    private readonly int _rootPid;
    private readonly IReadOnlySet<int> _excluded;
    private readonly Dictionary<int, ITreeProcess> _members = [];
    private readonly HashSet<int> _excludedFound = [];

    /// <param name="table">Where processes are listed and opened.</param>
    /// <param name="rootPid">The root's id.</param>
    /// <param name="root">The root, opened and checked by the caller, who keeps owning it.</param>
    /// <param name="excluded">Ids never to add, walk below or terminate.</param>
    public ProcessTree(IProcessTable table, int rootPid, ITreeProcess root, IReadOnlySet<int> excluded)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(excluded);
        if (root.StartTime is null)
            throw new ArgumentException("the root's creation time is unknown", nameof(root));
        _table = table;
        _rootPid = rootPid;
        _excluded = excluded;
        _members[rootPid] = root;
    }

    /// <summary>The members found so far, the root included.</summary>
    public IReadOnlyCollection<int> Pids => _members.Keys;

    /// <summary>Excluded ids that were found below the root.</summary>
    public IReadOnlyCollection<int> ExcludedFound => _excludedFound;

    /// <summary>Lists the processes once and adds every new child of a member, down the tree.</summary>
    public void Expand()
    {
        var entries = _table.List();
        var rejected = new HashSet<int>();
        bool added;
        do
        {
            added = false;
            foreach (var e in entries)
            {
                if (e.Pid == e.ParentPid || _members.ContainsKey(e.Pid) || rejected.Contains(e.Pid)
                    || !_members.TryGetValue(e.ParentPid, out var parent))
                    continue;
                var child = _table.Open(e.Pid);
                if (child is null)
                {
                    // Excluded and not even openable: count it, so the caller errs on the safe side.
                    if (_excluded.Contains(e.Pid))
                        _excludedFound.Add(e.Pid);
                    rejected.Add(e.Pid);
                    continue;
                }
                var isChild = IsChildOf(child, e.ParentPid, parent);
                if (isChild && !_excluded.Contains(e.Pid))
                {
                    _members[e.Pid] = child;
                    added = true;
                    continue;
                }
                if (isChild)
                    _excludedFound.Add(e.Pid);
                child.Dispose();
                rejected.Add(e.Pid);
            }
        }
        while (added);
    }

    private static bool IsChildOf(ITreeProcess child, int parentPid, ITreeProcess parent) =>
        child.ParentPid == parentPid
        && child.StartTime is { } start && start >= parent.StartTime
        && (parent.ExitTime is not { } exit || start <= exit);

    /// <summary>Terminates every member but the root that is still running; returns how many were.</summary>
    public int TerminateAll()
    {
        var terminated = 0;
        foreach (var (pid, member) in _members)
        {
            if (pid != _rootPid && !member.HasExited && member.Terminate())
                terminated++;
        }
        return terminated;
    }

    /// <summary>Releases every member but the root.</summary>
    public void Dispose()
    {
        foreach (var (pid, member) in _members)
        {
            if (pid != _rootPid)
                member.Dispose();
        }
        _members.Clear();
    }
}
