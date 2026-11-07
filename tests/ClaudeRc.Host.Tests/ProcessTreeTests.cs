using ClaudeRc.Host.Adoption;

namespace ClaudeRc.Host.Tests;

/// <summary>
/// How take over finds the tree to terminate (SPEC 7 step 3), over a fake process table, so the
/// races with exited members and reused ids can be set up exactly. No real process is involved.
/// </summary>
public sealed class ProcessTreeTests
{
    private sealed class FakeProcess(int pid, int parent, long start) : ITreeProcess
    {
        public int Pid { get; } = pid;
        public long? StartTime { get; } = start;
        public int? ParentPid { get; } = parent;
        /// <summary>The parent id the snapshot shows (differs from <see cref="ParentPid"/> to model a race).</summary>
        public int SnapshotParent { get; set; } = parent;
        public long? ExitTime { get; set; }
        public bool HasExited => ExitTime is not null;
        public bool Terminated { get; private set; }
        public int Held { get; private set; }

        public bool Terminate()
        {
            if (HasExited)
                return false;
            Terminated = true;
            ExitTime = long.MaxValue;
            return true;
        }

        public FakeProcess Opened()
        {
            Held++;
            return this;
        }

        public void Dispose() => Held--;
    }

    private sealed class FakeTable : IProcessTable
    {
        /// <summary>The process each id names now (a later Add under the same id replaces it).</summary>
        public Dictionary<int, FakeProcess> ByPid { get; } = [];

        public FakeProcess Add(int pid, int parent, long start)
        {
            var p = new FakeProcess(pid, parent, start);
            ByPid[pid] = p;
            return p;
        }

        public IReadOnlyList<ProcessEntry> List() =>
            [.. ByPid.Values.Where(p => !p.HasExited).Select(p => new ProcessEntry(p.Pid, p.SnapshotParent, "x.exe"))];

        public ITreeProcess? Open(int pid) => ByPid.TryGetValue(pid, out var p) ? p.Opened() : null;
    }

    private static readonly IReadOnlySet<int> None = new HashSet<int>();

    private static (FakeTable Table, FakeProcess Root) WithRoot()
    {
        var table = new FakeTable();
        var root = table.Add(10, 1, 100);
        return (table, root);
    }

    [Fact]
    public void Finds_children_and_grandchildren_created_after_their_parent()
    {
        var (table, root) = WithRoot();
        table.Add(20, 10, 110);
        table.Add(30, 20, 120);
        table.Add(40, 99, 130); // not below the root

        using var tree = new ProcessTree(table, 10, root, None);
        tree.Expand();

        Assert.Equal([10, 20, 30], tree.Pids.Order());
    }

    [Fact]
    public void A_process_created_before_its_listed_parent_is_not_a_child()
    {
        var (table, root) = WithRoot();
        table.Add(20, 10, 90); // its parent id was an older process that 10 now reuses

        using var tree = new ProcessTree(table, 10, root, None);
        tree.Expand();

        Assert.Equal([10], tree.Pids);
    }

    [Fact]
    public void A_dead_members_reused_id_is_never_followed()
    {
        var (table, root) = WithRoot();
        var x = table.Add(20, 10, 110);
        using var tree = new ProcessTree(table, 10, root, None);
        tree.Expand();
        Assert.Contains(20, tree.Pids);

        // X exits; its id goes to an unrelated process U, which starts a child C.
        x.ExitTime = 150;
        var u = table.Add(20, 5, 160);
        var c = table.Add(40, 20, 200);
        tree.Expand();
        tree.TerminateAll();
        tree.Expand();
        tree.TerminateAll();

        Assert.DoesNotContain(40, tree.Pids);
        Assert.False(c.Terminated);
        Assert.False(u.Terminated);
    }

    [Fact]
    public void An_id_reused_between_the_snapshot_and_opening_it_is_not_followed()
    {
        var (table, root) = WithRoot();
        // The snapshot showed 30 as a child of 10; by the time it is opened, 30 is another process.
        var reused = table.Add(30, 77, 300);
        reused.SnapshotParent = 10;

        using var tree = new ProcessTree(table, 10, root, None);
        tree.Expand();
        tree.TerminateAll();

        Assert.Equal([10], tree.Pids);
        Assert.False(reused.Terminated);
        Assert.Equal(0, reused.Held);
    }

    [Fact]
    public void Excluded_processes_are_reported_never_returned_never_walked_and_never_terminated()
    {
        var (table, root) = WithRoot();
        var host = table.Add(20, 10, 110);
        var hostChild = table.Add(30, 20, 120);
        var sibling = table.Add(40, 10, 130);

        using var tree = new ProcessTree(table, 10, root, new HashSet<int> { 20 });
        tree.Expand();
        tree.TerminateAll();

        Assert.Equal([10, 40], tree.Pids.Order());
        Assert.Equal([20], tree.ExcludedFound);
        Assert.False(host.Terminated);
        Assert.False(hostChild.Terminated);
        Assert.True(sibling.Terminated);
        Assert.Equal(0, host.Held);
    }

    [Fact]
    public void An_excluded_id_that_is_not_below_the_root_is_not_reported()
    {
        var (table, root) = WithRoot();
        table.Add(20, 10, 90); // created before the root: the listed parent id is a reused one

        using var tree = new ProcessTree(table, 10, root, new HashSet<int> { 20 });
        tree.Expand();

        Assert.Empty(tree.ExcludedFound);
    }

    [Fact]
    public void Terminates_every_member_but_the_root_through_its_held_handle_and_releases_them()
    {
        var (table, root) = WithRoot();
        var a = table.Add(20, 10, 110);
        var b = table.Add(30, 20, 120);

        var tree = new ProcessTree(table, 10, root, None);
        tree.Expand();
        Assert.Equal(1, a.Held);
        Assert.Equal(2, tree.TerminateAll());
        Assert.True(a.Terminated && b.Terminated);
        Assert.False(root.Terminated);
        Assert.Equal(0, tree.TerminateAll()); // already gone
        tree.Dispose();

        Assert.Equal(0, a.Held);
        Assert.Equal(0, b.Held);
        Assert.Equal(0, root.Held); // the root is the caller's: never opened or closed here
    }

    [Fact]
    public void Children_started_after_a_first_walk_are_found_by_the_next()
    {
        var (table, root) = WithRoot();
        using var tree = new ProcessTree(table, 10, root, None);
        tree.Expand();
        table.Add(20, 10, 110);
        tree.Expand();

        Assert.Contains(20, tree.Pids);
    }
}
