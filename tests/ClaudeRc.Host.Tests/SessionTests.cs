using System.Diagnostics;
using System.Text;
using ClaudeRc.Core.Prompts;
using ClaudeRc.Host.Sessions;
using ClaudeRc.Host.Terminal;

namespace ClaudeRc.Host.Tests;

/// <summary>Sessions in a real ConPTY with FakeClaude.exe (SPEC 6, 17).</summary>
public sealed class SessionTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(15);

    private static string ScreenText(Session s) => string.Join("\n", s.GetScreen().Select(r => r.Text));

    [Fact]
    public void Arguments_with_spaces_quotes_ampersand_and_percent_arrive_unchanged()
    {
        using var h = new FakeClaudeHarness();
        h.Configure(new { record = "record.json" });
        string[] args = ["rc", "--name", "m0 \"q\" & %PATH% 50%", @"C:\dir with space\", "a\\\\\"b", "", "ünï ✔"];
        using var s = h.Start(args);

        var record = h.Record();
        Assert.Equal(args, record["args"]!.AsArray().Select(a => (string)a!).ToArray());
        Assert.Equal(h.Folder, (string)record["cwd"]!, ignoreCase: true);
        Assert.Equal(s.ProcessId, (int)record["pid"]!);
    }

    [Fact]
    public void Environment_comes_from_the_registry_not_from_the_host_process()
    {
        const string Name = "CLAUDE_RC_TEST_HOST_ONLY";
        Environment.SetEnvironmentVariable(Name, "leaked");
        try
        {
            using var h = new FakeClaudeHarness();
            h.Configure(new { record = "record.json", record_env = new[] { Name, "SystemRoot" } });
            using var s = h.Start();
            var env = h.Record()["env"]!.AsObject();
            Assert.Null((string?)env[Name]);
            Assert.False(string.IsNullOrEmpty((string?)env["SystemRoot"]));
            Assert.Equal(FakeClaudeHarness.Environment()["PATH"], (string?)env["PATH"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Name, null);
        }
    }

    [Fact]
    public async Task Stop_sends_ctrl_c_and_the_process_exits_with_0()
    {
        using var h = new FakeClaudeHarness();
        h.Configure(new { record = "record.json", ctrl_c_file = "ctrlc.txt" });
        using var s = h.Start();
        h.Record();

        var sw = Stopwatch.StartNew();
        var result = await s.StopAsync().WaitAsync(Long);
        Assert.False(result.Killed);
        Assert.Equal(0, result.ExitCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"took {sw.Elapsed}");
        Assert.Contains("ctrl-c", h.ReadText("ctrlc.txt"));
        Assert.True(s.HasExited);
        Assert.False(FakeClaudeHarness.IsRunning(s.ProcessId));
    }

    [Fact]
    public async Task Ctrl_c_reaches_the_session_even_when_the_host_process_ignores_ctrl_c()
    {
        // Task Scheduler and test runners start the host in a new process group, which sets the
        // inherited "ignore Ctrl+C" flag. The session must not inherit it.
        Assert.True(NativeTestMethods.SetConsoleCtrlHandler(0, 1));
        using var h = new FakeClaudeHarness();
        h.Configure(new { record = "record.json", ctrl_c_file = "ctrlc.txt" });
        using var s = h.Start();
        h.Record();

        var result = await s.StopAsync().WaitAsync(Long);
        Assert.False(result.Killed);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ctrl-c", h.ReadText("ctrlc.txt"));
    }

    [Fact]
    public async Task Stop_kills_the_job_5_s_after_an_ignored_ctrl_c()
    {
        using var h = new FakeClaudeHarness();
        h.Configure(new { record = "record.json", ctrl_c_file = "ctrlc.txt", ignore_ctrl_c = true });
        using var s = h.Start();
        h.Record();

        var sw = Stopwatch.StartNew();
        var result = await s.StopAsync().WaitAsync(Long);
        Assert.True(result.Killed);
        Assert.Equal(Session.KilledExitCode, result.ExitCode);
        Assert.InRange(sw.Elapsed.TotalSeconds, 4.8, 8);
        Assert.Contains("ctrl-c", h.ReadText("ctrlc.txt"));
        Assert.False(FakeClaudeHarness.IsRunning(s.ProcessId));
    }

    [Fact]
    public async Task Child_processes_die_with_the_job_when_the_session_is_stopped()
    {
        using var h = new FakeClaudeHarness();
        h.Configure(new { record = "record.json", spawn_child = true, ignore_ctrl_c = true });
        using var s = h.Start();
        var child = (int)h.Record()["child_pid"]!;
        Assert.True(FakeClaudeHarness.IsRunning(child));
        Assert.Contains(child, s.JobProcessIds());
        Assert.Contains(s.ProcessId, s.JobProcessIds());

        var result = await s.StopAsync(TimeSpan.FromMilliseconds(200)).WaitAsync(Long);
        Assert.True(result.Killed);
        FakeClaudeHarness.WaitUntil(() => !FakeClaudeHarness.IsRunning(child), "child gone");
    }

    [Fact]
    public async Task Child_processes_die_when_the_session_exits_on_its_own()
    {
        using var h = new FakeClaudeHarness();
        h.Configure(new { record = "record.json", spawn_child = true, exit_after_ms = 1000, exit_code = 3 });
        using var s = h.Start();
        var child = (int)h.Record()["child_pid"]!;
        Assert.True(FakeClaudeHarness.IsRunning(child));

        Assert.Equal(3, await s.Exited.WaitAsync(Long));
        FakeClaudeHarness.WaitUntil(() => !FakeClaudeHarness.IsRunning(child), "child gone");
    }

    [Fact]
    public async Task Exit_code_is_reported_and_output_written_just_before_exit_is_kept()
    {
        using var h = new FakeClaudeHarness();
        h.Configure(new { print = "hello\r\n", exit_after_ms = 300, exit_code = 7, exit_text = "bye now\r\n" });
        using var s = h.Start();

        Assert.Equal(7, await s.Exited.WaitAsync(Long));
        Assert.True(s.HasExited);
        var screen = ScreenText(s);
        Assert.Contains("hello", screen);
        Assert.Contains("bye now", screen);
    }

    [Fact]
    public void Input_reaches_the_process()
    {
        using var h = new FakeClaudeHarness();
        h.Configure(new { prompt = "Continue? [y/N] ", input_file = "input.txt" });
        using var s = h.Start();
        FakeClaudeHarness.WaitUntil(() => ScreenText(s).Contains("Continue? [y/N]"), "prompt shown");

        s.WriteInput("y\r");
        FakeClaudeHarness.WaitUntil(() => h.ReadText("input.txt") == "y\n", "input recorded");
        s.WriteInput(Encoding.UTF8.GetBytes("ünï ✔\r"));
        FakeClaudeHarness.WaitUntil(() => h.ReadText("input.txt") == "y\nünï ✔\n", "UTF-8 input recorded");
    }

    [Fact]
    public void Resize_reaches_the_pseudo_console_and_the_emulator()
    {
        using var h = new FakeClaudeHarness();
        h.Configure(new { size_file = "size.txt" });
        using var s = h.Start();
        FakeClaudeHarness.WaitUntil(() => h.ReadText("size.txt").StartsWith("120x40\n", StringComparison.Ordinal), "initial size 120x40");

        s.Resize(100, 30);
        FakeClaudeHarness.WaitUntil(() => h.ReadText("size.txt").Contains("100x30\n"), "resized to 100x30");
        Assert.Equal(30, s.GetScreen().Count);
    }

    [Fact]
    public void Ready_output_is_rendered_and_not_a_prompt()
    {
        using var h = new FakeClaudeHarness();
        h.Configure(new { replay = FakeClaudeHarness.Fixture("claude-rc-ready.raw") });
        using var s = h.Start();

        FakeClaudeHarness.WaitUntil(() => ReadyDetector.IsReady(s.GetScreen()), "ready marker on screen");
        FakeClaudeHarness.WaitUntil(() => ScreenText(s).Contains("space to show QR code"), "last line drawn");
        Assert.Null(PromptDetector.Detect(s.GetScreen()));
    }

    [Fact]
    public void Trust_prompt_is_detected_on_the_session_screen()
    {
        using var h = new FakeClaudeHarness();
        h.Configure(new { replay = FakeClaudeHarness.Fixture("prompt-trust.raw"), input_file = "input.txt" });
        using var s = h.Start();

        FakeClaudeHarness.WaitUntil(() => PromptDetector.Detect(s.GetScreen()) is not null, "trust prompt detected");
        Assert.EndsWith(@"\gitA? [y/N]", PromptDetector.Detect(s.GetScreen()));
    }

    [Fact]
    public void Attach_replays_the_screen_and_then_streams_live_output()
    {
        using var h = new FakeClaudeHarness();
        h.Configure(new { replay = FakeClaudeHarness.Fixture("claude-rc-ready.raw"), prompt = "type> ", input_file = "input.txt" });
        using var s = h.Start();
        FakeClaudeHarness.WaitUntil(() => ScreenText(s).Contains("type>"), "prompt drawn");

        var viewer = new TerminalEmulator(120, 40);
        var gate = new object();
        using (s.Attach(chunk => { lock (gate) viewer.Feed(chunk.Span); }, out var replay))
        {
            lock (gate)
                viewer.Feed(replay);
            Assert.Equal(s.GetScreen(), viewer.GetScreen());

            s.WriteInput("echoed text");
            FakeClaudeHarness.WaitUntil(() => { lock (gate) return string.Join("\n", viewer.GetScreen().Select(r => r.Text)).Contains("type> echoed text"); },
                "live echo reaches the viewer");
            FakeClaudeHarness.WaitUntil(() => { lock (gate) return s.GetScreen().SequenceEqual(viewer.GetScreen()); }, "viewer matches the session");
        }

        // Detached: no more output to the viewer.
        string before;
        lock (gate)
            before = string.Join("\n", viewer.GetScreen().Select(r => r.Text));
        s.WriteInput(" more");
        FakeClaudeHarness.WaitUntil(() => ScreenText(s).Contains("echoed text more"), "session got more");
        lock (gate)
            Assert.Equal(before, string.Join("\n", viewer.GetScreen().Select(r => r.Text)));
    }

    [Fact]
    public void Starting_a_missing_program_throws_and_leaves_nothing_behind()
    {
        using var h = new FakeClaudeHarness();
        var e = Assert.Throws<SessionStartException>(() => Session.Start(new SessionStartInfo
        {
            Program = Path.Combine(h.Folder, "does-not-exist.exe"),
            Arguments = ["rc"],
            WorkingDirectory = h.Folder,
            Environment = FakeClaudeHarness.Environment(),
        }));
        Assert.Contains("does-not-exist.exe", e.Message);
    }

    [Fact]
    public void Starting_in_a_missing_folder_throws()
    {
        using var h = new FakeClaudeHarness();
        Assert.Throws<SessionStartException>(() => Session.Start(new SessionStartInfo
        {
            Program = FakeClaudeHarness.FakeClaudeExe,
            Arguments = ["rc"],
            WorkingDirectory = Path.Combine(h.Folder, "missing"),
            Environment = FakeClaudeHarness.Environment(),
        }));
    }

    [Fact]
    public void Dispose_kills_a_running_session()
    {
        using var h = new FakeClaudeHarness();
        h.Configure(new { record = "record.json", ignore_ctrl_c = true, spawn_child = true });
        var s = h.Start();
        var child = (int)h.Record()["child_pid"]!;
        s.Dispose();
        Assert.True(s.HasExited);
        FakeClaudeHarness.WaitUntil(() => !FakeClaudeHarness.IsRunning(child) && !FakeClaudeHarness.IsRunning(s.ProcessId), "all gone");
    }

    [Fact]
    public async Task Input_and_resize_after_exit_are_ignored()
    {
        using var h = new FakeClaudeHarness();
        h.Configure(new { exit_after_ms = 100 });
        using var s = h.Start();
        await s.Exited.WaitAsync(Long);
        s.WriteInput("x");
        s.Resize(80, 24);
        Assert.False((await s.StopAsync()).Killed);
    }

    [Fact]
    public void Last_lines_skip_blank_rows()
    {
        using var h = new FakeClaudeHarness();
        h.Configure(new { print = "one\r\n\r\ntwo\r\nthree\r\n" });
        using var s = h.Start();
        FakeClaudeHarness.WaitUntil(() => ScreenText(s).Contains("three"), "printed");
        Assert.Equal(["one", "two", "three"], s.GetLastLines(20));
        Assert.Equal(["two", "three"], s.GetLastLines(2));
    }
}
