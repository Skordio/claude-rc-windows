using System.Diagnostics;
using ClaudeRc.Core;
using ClaudeRc.Core.Ipc;
using ClaudeRc.Core.Paths;
using ClaudeRc.Core.Storage;

namespace ClaudeRc.Host.Tests;

/// <summary>
/// Adoption of a claude rc started outside the app (SPEC 7), end to end: an in-process host and
/// FakeClaude processes the test starts itself, hidden, in its own temp folders (<see cref="ExternalFake"/>).
/// </summary>
/// <remarks>
/// Safety: the test host counts only FakeClaude.exe as claude (<see cref="TestHost.Config"/>), so it
/// never lists the user's own claude rc sessions, and every take over here names a folder of the
/// test. The tests also pin that nothing but a take over of that exact folder stops an external
/// session.
/// </remarks>
public sealed class AdoptionTests
{
    private static Row? RowOf(IEnumerable<Row> rows, string path) => rows.FirstOrDefault(r => PathNormalizer.Comparer.Equals(r.Path, path));

    /// <summary>Lets the (scaled, 100 ms) process scan run several times.</summary>
    private static Task SeveralScans() => Task.Delay(800);

    [Fact]
    public async Task An_external_claude_rc_in_an_unlisted_folder_is_an_unsaved_off_row_and_turning_it_on_saves_it_and_leaves_it_running()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.NewFolder(new { record = "record.json" });
        using var external = ExternalFake.Start(folder);

        var row = await host.WaitForStatus(folder, FolderStatus.External);
        Assert.False(row.On);
        Assert.False(row.Saved);
        Assert.Equal(external.Pid, row.Pid);
        Assert.Equal(PathNormalizer.Normalize(folder), row.Path);
        Assert.False(File.Exists(Path.Combine(host.DataDir, "folders.json")));

        await host.SendOk(new SetRequest(folder, true));

        row = await host.WaitForRow(folder, r => r is { On: true, Saved: true }, "saved and ON");
        Assert.Equal(FolderStatus.External, row!.Status);
        Assert.Equal([new FolderEntry(row.Path, true)], new FoldersStore(host.DataDir).Load());
        await SeveralScans();
        Assert.True(external.IsRunning);
        Assert.False(File.Exists(Path.Combine(folder, "record.json")), "the host started a second session");
        Assert.Equal(FolderStatus.External, RowOf(await host.List(), folder)!.Status);
    }

    [Fact]
    public async Task Adding_a_folder_with_an_external_session_starts_no_second_one()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.NewFolder(new { record = "record.json" });
        using var external = ExternalFake.Start(folder);
        // Added at once, before the periodic scan had a chance: add scans first.
        await host.SendOk(new AddRequest(folder));

        var row = await host.WaitForRow(folder, r => r is { On: true, Saved: true }, "saved");
        Assert.Equal(FolderStatus.External, row!.Status);
        await SeveralScans();
        Assert.False(File.Exists(Path.Combine(folder, "record.json")), "the host started a second session");
        Assert.True(external.IsRunning);
    }

    [Fact]
    public async Task At_host_start_a_folder_with_an_external_session_is_not_started()
    {
        ExternalFake? started = null;
        string folder = "";
        TestHost host;
        try
        {
            host = await TestHost.StartAsync(before: h =>
            {
                folder = h.NewFolder(new { record = "record.json" });
                started = ExternalFake.Start(folder);
                new FoldersStore(h.DataDir).Save([new FolderEntry(folder, true)]);
            });
        }
        catch
        {
            started?.Dispose();
            throw;
        }
        await using var _ = host;
        using var external = started!;

        // The first scan runs before the saved list is loaded (SPEC 5.4): the very first list shows it.
        var row = RowOf(await host.List(), folder);
        Assert.NotNull(row);
        Assert.Equal(FolderStatus.External, row.Status);
        Assert.True(row.On);
        Assert.True(row.Saved);
        Assert.Equal(external.Pid, row.Pid);
        await SeveralScans();
        Assert.False(File.Exists(Path.Combine(folder, "record.json")), "the host started a second session");
    }

    [Fact]
    public async Task When_an_on_folders_external_session_exits_the_host_starts_its_own()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.NewFolder(new { record = "record.json" });
        using var external = ExternalFake.Start(folder, new { exit_after_ms = 1500 });
        await host.WaitForStatus(folder, FolderStatus.External);
        await host.SendOk(new SetRequest(folder, true));

        var row = await host.WaitForRow(folder, r => r is { Status: FolderStatus.Running } && r.Pid != external.Pid, "hosted session running");

        var record = TestHost.Record(folder);
        Assert.Equal(row!.Pid, (int)record["pid"]!);
        Assert.Equal("rc", (string)record["args"]![0]!);
        Assert.Contains(await LogLines(host, folder), l => l.Contains("external claude rc ended", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unsaved_rows_external_session_exiting_removes_the_row()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.NewFolder();
        using var external = ExternalFake.Start(folder, new { exit_after_ms = 1000 });
        await host.WaitForStatus(folder, FolderStatus.External);

        await host.WaitForRow(folder, r => r is null, "row gone");
        Assert.False(File.Exists(Path.Combine(folder, "record.json")));
    }

    [Fact]
    public async Task Take_over_stops_the_external_tree_and_starts_a_hosted_session()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.NewFolder(new { record = "record.json" });
        using var external = ExternalFake.Start(folder, new { spawn_child = true });
        var child = external.ChildPid!.Value;
        Assert.True(FakeClaudeHarness.IsRunning(child));
        await host.WaitForStatus(folder, FolderStatus.External);

        await host.SendOk(new TakeoverRequest(folder));

        var row = await host.WaitForRow(folder, r => r is { Status: FolderStatus.Running or FolderStatus.Starting } && r.Pid != external.Pid,
            "hosted session after take over");
        Assert.True(row!.On);
        Assert.True(row.Saved);
        FakeClaudeHarness.WaitUntil(() => !external.IsRunning && !FakeClaudeHarness.IsRunning(child), "external tree stopped");
        Assert.Equal((int)TestHost.Record(folder)["pid"]!, row.Pid);
        Assert.Equal([new FolderEntry(row.Path, true)], new FoldersStore(host.DataDir).Load());
        var log = await LogLines(host, folder);
        Assert.Contains(log, l => l.Contains(FormattableString.Invariant($"taking over the external claude rc (pid {external.Pid})"), StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains(FormattableString.Invariant($"took over: stopped the external claude rc (pid {external.Pid})"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_rc_child_of_an_external_claude_rc_is_counted_once_and_stopped_with_it()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.NewFolder(new { });
        using var external = ExternalFake.Start(folder, new { spawn_child = true, spawn_child_args = new[] { "rc" } });
        var child = external.ChildPid!.Value;

        var row = await host.WaitForStatus(folder, FolderStatus.External);
        Assert.Equal(external.Pid, row.Pid);
        await SeveralScans();
        var rows = await host.List();
        Assert.Single(rows, r => PathNormalizer.Comparer.Equals(r.Path, folder));
        Assert.Equal(external.Pid, RowOf(rows, folder)!.Pid);
        Assert.DoesNotContain(host.Events.OfType<RowsEvent>().SelectMany(e => e.Rows), r => r.Pid == child);

        await host.SendOk(new TakeoverRequest(folder));
        FakeClaudeHarness.WaitUntil(() => !external.IsRunning && !FakeClaudeHarness.IsRunning(child), "external tree stopped");
        await host.WaitForRow(folder, r => r is { Status: FolderStatus.Running } && r.Pid != external.Pid && r.Pid != child, "hosted");
    }

    [Fact]
    public async Task A_fake_claude_without_rc_arguments_is_not_adopted()
    {
        await using var host = await TestHost.StartAsync();
        var a = host.NewFolder();
        var b = host.NewFolder();
        using var chat = ExternalFake.Start(a, null, "chat");
        using var flagFirst = ExternalFake.Start(b, null, "--name", "rc");

        await SeveralScans();

        Assert.Empty(await host.List());
    }

    [Fact]
    public async Task The_hosts_own_sessions_are_never_external_even_with_an_rc_child()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.NewFolder(new { record = "record.json", spawn_child = true, spawn_child_args = new[] { "rc" } });
        await host.SendOk(new AddRequest(folder));
        var pid = (await host.WaitForStatus(folder, FolderStatus.Running)).Pid;

        await SeveralScans();

        var row = RowOf(await host.List(), folder)!;
        Assert.Equal(FolderStatus.Running, row.Status);
        Assert.Equal(pid, row.Pid);
        Assert.DoesNotContain(host.Events.OfType<RowsEvent>().SelectMany(e => e.Rows), r => r.Status == FolderStatus.External);
        Assert.Single(await host.List());
    }

    [Fact]
    public async Task Turning_off_removing_restarting_the_host_and_another_folders_take_over_never_stop_an_external_session()
    {
        await using var host = await TestHost.StartAsync();
        var a = host.NewFolder(new { });
        var b = host.NewFolder(new { });
        var c = host.NewFolder(new { });
        using var extA = ExternalFake.Start(a);
        using var extB = ExternalFake.Start(b);
        using var extC = ExternalFake.Start(c);
        await host.WaitForStatus(a, FolderStatus.External);
        await host.WaitForStatus(b, FolderStatus.External);
        await host.WaitForStatus(c, FolderStatus.External);

        await host.SendOk(new SetRequest(a, true));
        await host.SendOk(new SetRequest(a, false));
        await host.SendOk(new RemoveRequest(a));
        Assert.False((await host.Send(new RestartRequest(b))).Ok);
        await host.SendOk(new SetRequest(b, true));
        await host.RestartHostAsync();
        await host.WaitForStatus(b, FolderStatus.External);
        await host.SendOk(new TakeoverRequest(c));
        await host.WaitForRow(c, r => r is { Status: FolderStatus.Running }, "c hosted");
        await SeveralScans();

        Assert.True(extA.IsRunning);
        Assert.True(extB.IsRunning);
        Assert.False(extC.IsRunning);
        var rows = await host.List();
        Assert.Equal(FolderStatus.External, RowOf(rows, a)!.Status);
        Assert.False(RowOf(rows, a)!.Saved);
        Assert.Equal(FolderStatus.External, RowOf(rows, b)!.Status);
        Assert.True(RowOf(rows, b)!.On);
    }

    [Fact]
    public async Task Take_over_names_the_folder_and_is_refused_without_an_external_session()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.NewFolder(new { });
        var other = host.NewFolder(new { });

        var r = await host.Send(new TakeoverRequest(folder));
        Assert.False(r.Ok);
        Assert.Contains("started outside the app", r.Error, StringComparison.Ordinal);

        using var external = ExternalFake.Start(other);
        await host.WaitForStatus(other, FolderStatus.External);
        Assert.False((await host.Send(new TakeoverRequest(folder))).Ok);
        Assert.True(external.IsRunning);
    }

    [Fact]
    public async Task A_test_host_lists_only_its_own_temp_folders()
    {
        // The user may have their own claude rc sessions running: a test host must not see them.
        await using var host = await TestHost.StartAsync();
        var folder = host.NewFolder(new { });
        using var external = ExternalFake.Start(folder);
        await host.WaitForStatus(folder, FolderStatus.External);
        await SeveralScans();

        var root = PathNormalizer.Normalize(host.Root) + "\\";
        Assert.All(await host.List(), r => Assert.StartsWith(root, r.Path, StringComparison.OrdinalIgnoreCase));
        Assert.Equal([Path.GetFileName(FakeClaudeHarness.FakeClaudeExe)], host.Config().ExternalImageNames);
    }

    [Fact]
    public async Task At_the_real_5_second_interval_an_external_session_is_seen_within_about_5_seconds()
    {
        await using var host = await TestHost.StartAsync(configure: c => c with { ExternalScanInterval = TimeSpan.FromSeconds(5) });
        var folder = host.NewFolder(new { });
        var sw = Stopwatch.StartNew();
        using var external = ExternalFake.Start(folder);

        await host.WaitForStatus(folder, FolderStatus.External, timeoutMs: 7_000);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(7), $"seen after {sw.Elapsed}");
    }

    private static async Task<IReadOnlyList<string>> LogLines(TestHost host, string folder) =>
        (await host.SendOk(new LogRequest(folder, 200))).Lines!;
}
