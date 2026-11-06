using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeRc.Core;
using ClaudeRc.Core.Claude;
using ClaudeRc.Core.Ipc;
using ClaudeRc.Core.Paths;
using ClaudeRc.Core.Storage;
using ClaudeRc.Host.Terminal;

namespace ClaudeRc.Host.Tests;

/// <summary>
/// The host end to end (SPEC 5, 6, 10): an in-process <see cref="HostApp"/> driven through its real
/// named pipe, running FakeClaude sessions. See <see cref="TestHost"/> for the isolation.
/// </summary>
public sealed class HostIntegrationTests
{
    private static string Fixture(string name) => FakeClaudeHarness.Fixture(name);

    [Fact]
    public async Task Add_starts_claude_rc_in_the_folder_saves_it_and_presets_claude_json()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.NewFolder(new { record = "record.json", replay = Fixture("claude-rc-ready.raw") }, "my app & co");

        await host.SendOk(new AddRequest(folder));

        var row = await host.WaitForStatus(folder, FolderStatus.Running);
        Assert.True(row.On);
        Assert.True(row.Saved);
        Assert.Equal("my app & co", row.Name);
        Assert.NotNull(row.Pid);
        Assert.NotNull(row.StartedAt);
        var record = TestHost.Record(folder);
        Assert.Equal(["rc", "--name", "my app & co"], record["args"]!.AsArray().Select(a => (string)a!));
        Assert.Equal(folder, (string)record["cwd"]!);
        Assert.Equal(row.Pid, (int)record["pid"]!);

        var project = JsonNode.Parse(File.ReadAllText(host.ClaudeJson))!["projects"]![ClaudeJsonPresets.ProjectKey(folder)]!;
        Assert.True((bool)project["hasTrustDialogAccepted"]!);
        Assert.Equal("same-dir", (string)project["remoteControlSpawnMode"]!);
        Assert.Equal([new FolderEntry(folder, true)], new FoldersStore(host.DataDir).Load());
    }

    [Fact]
    public async Task Add_normalises_the_path_and_adding_it_again_just_turns_it_on()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.NewFolder(new { });

        await host.SendOk(new AddRequest(folder.ToLowerInvariant() + "\\"));
        var row = await host.WaitForStatus(folder, FolderStatus.Running);
        Assert.Equal(folder, row.Path);

        await host.SendOk(new SetRequest(folder, false));
        await host.WaitForStatus(folder, FolderStatus.Off);
        await host.SendOk(new AddRequest(folder));
        await host.WaitForStatus(folder, FolderStatus.Running);
        Assert.Single(await host.List());
    }

    [Theory]
    [InlineData("does-not-exist")]
    [InlineData("relative")]
    [InlineData("")]
    public async Task Add_refuses_a_path_that_is_not_an_existing_absolute_directory(string kind)
    {
        await using var host = await TestHost.StartAsync();
        var path = kind switch
        {
            "does-not-exist" => Path.Combine(host.Root, "nope"),
            "relative" => "f",
            _ => "",
        };

        var r = await host.Send(new AddRequest(path));

        Assert.False(r.Ok);
        Assert.False(string.IsNullOrEmpty(r.Error));
        Assert.Empty(await host.List());
        Assert.False(File.Exists(Path.Combine(host.DataDir, "folders.json")));
    }

    [Fact]
    public async Task Set_off_stops_the_session_with_ctrl_c_and_saves_off()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.NewFolder(new { ctrl_c_file = "ctrlc.txt" });
        await host.SendOk(new AddRequest(folder));
        var pid = (await host.WaitForStatus(folder, FolderStatus.Running)).Pid!.Value;

        await host.SendOk(new SetRequest(folder, false));

        var row = await host.WaitForRow(folder, r => r is { Status: FolderStatus.Off, Pid: null }, "off with no pid");
        Assert.False(row!.On);
        FakeClaudeHarness.WaitUntil(() => !FakeClaudeHarness.IsRunning(pid), "session process gone");
        Assert.Contains("ctrl-c", TestHost.ReadText(folder, "ctrlc.txt"));
        Assert.Equal([new FolderEntry(folder, false)], new FoldersStore(host.DataDir).Load());
        Assert.Null(row.ExitCode); // a requested stop is not an exit
        Assert.Equal(0, row.Crashes10m);
    }

    [Fact]
    public async Task Exits_restart_with_backoff_then_failing_after_5_with_toast_and_last_lines_and_restart_clears_it()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.NewFolder(new { exit_after_ms = 20, exit_code = 7, exit_text = "boom seven\r\n" });

        await host.SendOk(new AddRequest(folder));

        await host.WaitForRowEvent(folder, r => r is { Status: FolderStatus.Restarting, ExitCode: 7, RestartInS: not null, Crashes10m: 1 },
            "restarting after the first exit");
        var failing = await host.WaitForStatus(folder, FolderStatus.Failing);
        Assert.Equal(7, failing.ExitCode);
        Assert.Equal(5, failing.Crashes10m);
        Assert.NotNull(failing.LastLines);
        Assert.Contains("boom seven", failing.LastLines!);
        Assert.True(failing.LastLines!.Count <= 20);
        Assert.Null(failing.Pid);
        var toast = await host.WaitForEvent<ToastEvent>(t => t.Status == FolderStatus.Failing, "failing toast");
        Assert.Equal(folder, toast.Path);
        var log = (await host.SendOk(new LogRequest(folder, 100))).Lines!;
        Assert.Equal(5, log.Count(l => l.Contains("exited with code 7", StringComparison.Ordinal)));
        Assert.Contains(log, l => l.Contains("failing", StringComparison.Ordinal));

        // It stays failing: no more starts.
        await Task.Delay(500);
        Assert.Equal(FolderStatus.Failing, (await host.WaitForRow(folder, _ => true, "row"))!.Status);

        TestHost.ConfigureFolder(folder, new { });
        await host.SendOk(new RestartRequest(folder));
        var running = await host.WaitForStatus(folder, FolderStatus.Running);
        Assert.Equal(0, running.Crashes10m);
        Assert.Null(running.ExitCode);
        Assert.Null(running.LastLines);
    }

    [Fact]
    public async Task Backoff_grows_between_restarts()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.NewFolder(new { exit_after_ms = 10, exit_code = 1 });

        await host.SendOk(new AddRequest(folder));
        await host.WaitForStatus(folder, FolderStatus.Failing);

        // Each start is logged; the gaps between them follow 5, 10, 20, 40 s × the test scale.
        var starts = (await host.SendOk(new LogRequest(folder, 100))).Lines!
            .Where(l => l.Contains(" started (pid ", StringComparison.Ordinal))
            .Select(l => DateTimeOffset.Parse(l[..l.IndexOf(' ', StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        Assert.Equal(5, starts.Count);
        var gaps = starts.Zip(starts.Skip(1), (a, b) => (b - a).TotalSeconds).ToList();
        double[] backoff = [5, 10, 20, 40];
        for (var i = 0; i < gaps.Count; i++)
            Assert.True(gaps[i] >= backoff[i] * TestHost.TimeScale * 0.9, $"gap {i} was {gaps[i]:0.000} s");
        Assert.True(gaps[3] > gaps[0], "backoff grows");
    }

    [Fact]
    public async Task Missing_folder_shows_missing_and_starts_as_soon_as_it_appears()
    {
        string later = "";
        await using var host = await TestHost.StartAsync(before: h =>
        {
            later = PathNormalizer.Normalize(Path.Combine(h.Root, "f", "later"));
            new FoldersStore(h.DataDir).Save([new FolderEntry(later, true)]);
        });

        // The toast for this went out before any client was connected; only the row shows it.
        await host.WaitForStatus(later, FolderStatus.Missing);
        await Task.Delay(300); // several re-checks (10 s × the test scale)
        Assert.Equal(FolderStatus.Missing, (await host.List()).Single().Status);

        Directory.CreateDirectory(later);
        TestHost.ConfigureFolder(later, new { record = "record.json" });

        var row = await host.WaitForStatus(later, FolderStatus.Running);
        Assert.Equal(0, row.Crashes10m);
        Assert.Equal(later, (string)TestHost.Record(later)["cwd"]!);
    }

    [Fact]
    public async Task Attach_replays_the_screen_then_streams_and_input_reaches_the_session()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.NewFolder(new { print = "hello from fake\r\n", prompt = "Answer? [y/N] ", input_file = "input.txt", size_file = "size.txt" });
        await host.SendOk(new AddRequest(folder));
        var waiting = await host.WaitForStatus(folder, FolderStatus.Waiting);
        Assert.Equal("Answer? [y/N]", waiting.Prompt);
        await host.WaitForEvent<ToastEvent>(t => t.Status == FolderStatus.Waiting, "waiting toast");

        var viewer = await host.ConnectAsync();
        var attach = await viewer.SendAsync(new AttachRequest(folder, 100, 30));
        Assert.True(attach.Ok, attach.Error);

        var terminal = new TerminalEmulator(100, 30);
        var replay = await NextOutput(viewer, folder);
        terminal.Feed(replay.Data);
        Assert.Contains(terminal.GetScreen(), r => r.Text.Contains("hello from fake", StringComparison.Ordinal));
        Assert.Contains(terminal.GetScreen(), r => r.Text.TrimEnd().EndsWith("Answer? [y/N]", StringComparison.Ordinal));
        FakeClaudeHarness.WaitUntil(() => TestHost.ReadText(folder, "size.txt").Contains("100x30", StringComparison.Ordinal), "pseudo-console resized to the viewer");

        await host.SendOk(new InputRequest(folder, "y\r"));
        FakeClaudeHarness.WaitUntil(() => TestHost.ReadText(folder, "input.txt") == "y\n", "input line reached FakeClaude");
        // The echo comes back as live output.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!terminal.GetScreen().Any(r => r.Text.Contains("[y/N] y", StringComparison.Ordinal)))
        {
            Assert.True(DateTime.UtcNow < deadline, "echo of the input streamed to the viewer");
            terminal.Feed((await NextOutput(viewer, folder)).Data);
        }
        await host.WaitForRow(folder, r => r?.Status == FolderStatus.Running, "prompt answered");

        await host.SendOk(new ResizeRequest(folder, 90, 25));
        FakeClaudeHarness.WaitUntil(() => TestHost.ReadText(folder, "size.txt").Contains("90x25", StringComparison.Ordinal), "resize reached the pseudo-console");

        Assert.True((await viewer.SendAsync(new DetachRequest(folder))).Ok);
    }

    private static async Task<OutputEvent> NextOutput(IpcClient client, string path)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var e = await client.Events.ReadAsync(cts.Token);
            if (e is OutputEvent o && o.Path == path)
                return o;
        }
    }

    [Fact]
    public async Task Attach_input_and_resize_need_a_known_folder_with_a_running_session()
    {
        await using var host = await TestHost.StartAsync();
        var unknown = host.NewFolder();
        Assert.False((await host.Send(new AttachRequest(unknown, 80, 24))).Ok);
        Assert.False((await host.Send(new InputRequest(unknown, "x"))).Ok);
        Assert.False((await host.Send(new ResizeRequest(unknown, 80, 24))).Ok);

        var folder = host.NewFolder(new { });
        await host.SendOk(new AddRequest(folder));
        await host.WaitForStatus(folder, FolderStatus.Running);
        Assert.False((await host.Send(new AttachRequest(folder, 0, 24))).Ok);
        Assert.False((await host.Send(new ResizeRequest(folder, 80, 100_000))).Ok);
        await host.SendOk(new SetRequest(folder, false));
        await host.WaitForRow(folder, r => r is { Status: FolderStatus.Off, Pid: null }, "stopped");
        Assert.False((await host.Send(new InputRequest(folder, "x"))).Ok);
    }

    [Fact]
    public async Task A_version_mismatch_gets_an_error_and_the_connection_is_closed()
    {
        await using var host = await TestHost.StartAsync();
        await using var pipe = new NamedPipeClientStream(".", host.Options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true };

        // A line that is not JSON is answered, and the connection stays open.
        await writer.WriteLineAsync("this is not json");
        var bad = (IpcResponse)IpcCodec.DecodeHostMessage((await reader.ReadLineAsync())!);
        Assert.False(bad.Ok);
        await writer.WriteLineAsync("""{"v":1,"id":3,"op":"list"}""");
        var list = (IpcResponse)IpcCodec.DecodeHostMessage((await reader.ReadLineAsync())!);
        Assert.True(list.Ok);
        Assert.Equal(3, list.Id);

        await writer.WriteLineAsync("""{"v":2,"id":4,"op":"list"}""");
        var reply = (IpcResponse)IpcCodec.DecodeHostMessage((await reader.ReadLineAsync())!);
        Assert.False(reply.Ok);
        Assert.Equal(IpcCodec.VersionMismatchMessage, reply.Error);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Null(await reader.ReadLineAsync(cts.Token));
    }

    [Fact]
    public async Task Several_clients_are_served_at_once_and_all_get_rows_events()
    {
        await using var host = await TestHost.StartAsync();
        var other = await host.ConnectAsync();
        var folder = host.NewFolder(new { });

        var lists = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            (i % 2 == 0 ? host.Client : other).SendAsync(new ListRequest())));
        Assert.All(lists, r => Assert.True(r.Ok));

        await host.SendOk(new AddRequest(folder));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var e = await other.Events.ReadAsync(cts.Token);
            if (e is RowsEvent rows && rows.Rows.Any(r => r.Path == folder && r.Status == FolderStatus.Running))
                break;
        }
    }

    [Fact]
    public async Task Remove_only_when_off_and_takeover_is_not_available_yet()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.NewFolder(new { });
        await host.SendOk(new AddRequest(folder));
        await host.WaitForStatus(folder, FolderStatus.Running);

        var refused = await host.Send(new RemoveRequest(folder));
        Assert.False(refused.Ok);
        Assert.Contains("OFF", refused.Error, StringComparison.Ordinal);

        var takeover = await host.Send(new TakeoverRequest(folder));
        Assert.False(takeover.Ok);
        Assert.Contains("M3", takeover.Error, StringComparison.Ordinal);

        await host.SendOk(new SetRequest(folder, false));
        await host.WaitForStatus(folder, FolderStatus.Off);
        await host.SendOk(new RemoveRequest(folder));
        Assert.Empty(await host.List());
        Assert.Empty(new FoldersStore(host.DataDir).Load());
        Assert.False((await host.Send(new SetRequest(folder, true))).Ok);
    }

    [Fact]
    public async Task Restart_of_a_running_folder_stops_it_and_starts_a_new_process()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.NewFolder(new { ctrl_c_file = "ctrlc.txt" });
        await host.SendOk(new AddRequest(folder));
        var first = (await host.WaitForStatus(folder, FolderStatus.Running)).Pid!.Value;

        await host.SendOk(new RestartRequest(folder));

        var row = await host.WaitForRow(folder, r => r is { Status: FolderStatus.Running } && r.Pid != first, "running with a new pid");
        Assert.Equal(0, row!.Crashes10m);
        Assert.False(FakeClaudeHarness.IsRunning(first));
        Assert.Contains("ctrl-c", TestHost.ReadText(folder, "ctrlc.txt"));
    }

    [Fact]
    public async Task Claude_not_found_shows_the_error_on_the_row_and_is_retried_after_the_setting_is_fixed()
    {
        await using var host = await TestHost.StartAsync(before: h => h.WriteSettings(Path.Combine(h.Root, "no-such-claude.exe")));
        var folder = host.NewFolder(new { });

        await host.SendOk(new AddRequest(folder));

        var row = await host.WaitForRow(folder, r => r?.Error is not null, "error on the row");
        Assert.Equal(FolderStatus.Restarting, row!.Status);
        Assert.StartsWith("claude path from Settings not found:", row.Error, StringComparison.Ordinal);
        Assert.Equal(0, row.Crashes10m);
        var info = (await host.SendOk(new SettingsGetRequest())).Info!;
        Assert.Null(info.ClaudePath);
        Assert.StartsWith("claude path from Settings not found:", info.ClaudeError, StringComparison.Ordinal);

        await host.SendOk(new SettingsSetRequest(FakeClaudeHarness.FakeClaudeExe, true));

        var running = await host.WaitForStatus(folder, FolderStatus.Running);
        Assert.Null(running.Error);
        Assert.Equal(FakeClaudeHarness.FakeClaudeExe, new SettingsStore(host.DataDir).Load().ClaudePath);
    }

    [Fact]
    public async Task Invalid_claude_json_is_not_touched_and_shows_on_the_row()
    {
        await using var host = await TestHost.StartAsync(before: h => File.WriteAllText(h.ClaudeJson, "{ not json"));
        var folder = host.NewFolder(new { record = "record.json" });

        await host.SendOk(new AddRequest(folder));

        var row = await host.WaitForRow(folder, r => r?.Error is not null, "error on the row");
        Assert.Equal(FolderStatus.Restarting, row!.Status);
        Assert.Contains("not valid JSON", row.Error, StringComparison.Ordinal);
        Assert.Equal("{ not json", File.ReadAllText(host.ClaudeJson));
        Assert.False(File.Exists(Path.Combine(folder, "record.json")));
    }

    [Fact]
    public async Task Settings_get_and_set()
    {
        await using var host = await TestHost.StartAsync();

        var get = await host.SendOk(new SettingsGetRequest());
        Assert.Equal(new IpcSettings(FakeClaudeHarness.FakeClaudeExe, true), get.Settings);
        Assert.Equal(host.DataDir, get.Info!.DataDir);
        Assert.Equal(host.LogDir, get.Info.LogDir);
        Assert.Equal(FakeClaudeHarness.FakeClaudeExe, get.Info.ClaudePath);
        Assert.Equal("0.0.0-fake (Claude Code)", get.Info.ClaudeVersion);
        Assert.False(string.IsNullOrEmpty(get.Info.HostVersion));

        await host.SendOk(new SettingsSetRequest(FakeClaudeHarness.FakeClaudeExe, false));
        Assert.Equal(new AppSettings(FakeClaudeHarness.FakeClaudeExe, false), new SettingsStore(host.DataDir).Load());
        Assert.False((await host.SendOk(new SettingsGetRequest())).Settings!.Notifications);

        var relative = await host.Send(new SettingsSetRequest(@"bin\claude.exe", true));
        Assert.False(relative.Ok);
        Assert.Equal(new AppSettings(FakeClaudeHarness.FakeClaudeExe, false), new SettingsStore(host.DataDir).Load());
    }

    [Fact]
    public async Task An_attached_viewer_gets_a_fresh_replay_when_the_folder_restarts()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.NewFolder(new { print = "first run\r\n" });
        await host.SendOk(new AddRequest(folder));
        await host.WaitForStatus(folder, FolderStatus.Running);
        var viewer = await host.ConnectAsync();
        Assert.True((await viewer.SendAsync(new AttachRequest(folder, 80, 24))).Ok);
        Assert.Contains("first run", Encoding.UTF8.GetString((await NextOutput(viewer, folder)).Data), StringComparison.Ordinal);

        TestHost.ConfigureFolder(folder, new { print = "second run\r\n" });
        await host.SendOk(new RestartRequest(folder));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!Encoding.UTF8.GetString((await NextOutput(viewer, folder)).Data).Contains("second run", StringComparison.Ordinal))
            Assert.True(DateTime.UtcNow < deadline, "replay of the new session");
    }

    [Fact]
    public async Task No_toast_when_notifications_are_off()
    {
        await using var host = await TestHost.StartAsync(before: h => h.WriteSettings(FakeClaudeHarness.FakeClaudeExe, notifications: false));
        var folder = host.NewFolder(new { prompt = "Go on? [y/N] " });
        await host.SendOk(new AddRequest(folder));
        await host.WaitForStatus(folder, FolderStatus.Waiting);
        await Task.Delay(200);
        Assert.Empty(host.Events.OfType<ToastEvent>());
    }

    [Fact]
    public async Task Host_quit_stops_sessions_and_the_next_host_starts_every_on_folder_staggered()
    {
        var stagger = TimeSpan.FromMilliseconds(600);
        await using var host = await TestHost.StartAsync(configure: c => c with { Supervisor = c.Supervisor with { StartStagger = stagger } });
        var a = host.NewFolder(new { ctrl_c_file = "ctrlc.txt" }, "a");
        var b = host.NewFolder(new { ctrl_c_file = "ctrlc.txt" }, "b");
        var off = host.NewFolder(new { record = "record.json" }, "off");
        await host.SendOk(new AddRequest(a));
        await host.SendOk(new AddRequest(b));
        await host.SendOk(new AddRequest(off));
        await host.WaitForStatus(a, FolderStatus.Running);
        await host.WaitForStatus(b, FolderStatus.Running);
        await host.WaitForStatus(off, FolderStatus.Running);
        await host.SendOk(new SetRequest(off, false));
        await host.WaitForStatus(off, FolderStatus.Off);
        File.Delete(Path.Combine(off, "record.json"));

        await host.StopHostAsync();
        Assert.Empty(FakeClaudeHarness.RunningFakeClaudes());
        Assert.Contains("ctrl-c", TestHost.ReadText(a, "ctrlc.txt"));
        Assert.Contains("ctrl-c", TestHost.ReadText(b, "ctrlc.txt"));
        Assert.Equal([new FolderEntry(a, true), new FolderEntry(b, true), new FolderEntry(off, false)], new FoldersStore(host.DataDir).Load());

        await host.StartHostAsync();
        var ra = await host.WaitForStatus(a, FolderStatus.Running);
        var rb = await host.WaitForStatus(b, FolderStatus.Running);
        Assert.True(rb.StartedAt - ra.StartedAt >= stagger - TimeSpan.FromMilliseconds(200),
            $"second start {(rb.StartedAt - ra.StartedAt)?.TotalMilliseconds} ms after the first");
        Assert.Equal(FolderStatus.Off, (await host.List()).Single(r => r.Path == off).Status);
        Assert.False(File.Exists(Path.Combine(off, "record.json")));
    }

    [Fact]
    public async Task A_corrupt_folders_json_is_never_overwritten()
    {
        await using var host = await TestHost.StartAsync(before: h =>
        {
            Directory.CreateDirectory(h.DataDir);
            File.WriteAllText(Path.Combine(h.DataDir, "folders.json"), "garbage");
        });
        var folder = host.NewFolder(new { });

        var r = await host.Send(new AddRequest(folder));

        Assert.False(r.Ok);
        Assert.Contains("folders.json", r.Error, StringComparison.Ordinal);
        Assert.Equal("garbage", File.ReadAllText(Path.Combine(host.DataDir, "folders.json")));
        Assert.Empty(await host.List());
    }

    [Fact]
    public async Task Host_and_folder_logs_are_written_in_the_log_folder()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.NewFolder(new { });
        await host.SendOk(new AddRequest(folder));
        await host.WaitForStatus(folder, FolderStatus.Running);

        var lines = (await host.SendOk(new LogRequest(folder, 10))).Lines!;
        Assert.Contains(lines, l => l.EndsWith(" added (ON)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(" started (pid ", StringComparison.Ordinal));
        var folderLog = Path.Combine(host.Options.FolderLogDir, Core.Logging.LogFiles.FolderHash(folder) + ".log");
        Assert.StartsWith("# folder: " + folder + "\n", File.ReadAllText(folderLog), StringComparison.Ordinal);
        Assert.Contains("host started", File.ReadAllText(host.Options.HostLogPath), StringComparison.Ordinal);
    }
}
