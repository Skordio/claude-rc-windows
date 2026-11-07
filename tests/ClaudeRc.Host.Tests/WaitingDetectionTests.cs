using ClaudeRc.Core;
using ClaudeRc.Core.Paths;
using ClaudeRc.Core.Supervision;
using ClaudeRc.Host.Sessions;

namespace ClaudeRc.Host.Tests;

/// <summary>A FakeClaude prompt, seen by the prompt scan, turns the supervisor's folder to waiting (SPEC 8).</summary>
public sealed class WaitingDetectionTests
{
    [Fact]
    public void Prompt_on_the_session_screen_makes_the_folder_waiting_until_answered()
    {
        using var h = new FakeClaudeHarness();
        h.Configure(new { replay = FakeClaudeHarness.Fixture("prompt-trust.raw"), input_file = "input.txt" });
        var path = PathNormalizer.Normalize(h.Folder);
        var supervisor = new Supervisor(TimeProvider.System, Directory.Exists);
        supervisor.Load([]);
        Assert.Contains(new StartSession(path), supervisor.Add(path).Effects);

        using var session = h.Start();
        supervisor.SessionStarted(path, session.ProcessId);
        using var scanner = new PromptScanner(TimeProvider.System,
            p => { lock (supervisor) supervisor.ReadySeen(p); },
            (p, prompt) => { lock (supervisor) supervisor.PromptChanged(p, prompt); },
            TimeSpan.FromMilliseconds(100));
        scanner.Add(path, session);

        FolderSnapshot Folder()
        {
            lock (supervisor)
                return supervisor.Get(path)!;
        }

        FakeClaudeHarness.WaitUntil(() => Folder().Status == FolderStatus.Waiting, "folder waiting");
        Assert.EndsWith(@"\gitA? [y/N]", Folder().Prompt);

        session.WriteInput("y\r");
        FakeClaudeHarness.WaitUntil(() => Folder().Status != FolderStatus.Waiting, "prompt answered");
        Assert.Null(Folder().Prompt);
        // The echo changes the screen before FakeClaude has appended the line, so wait for the file.
        FakeClaudeHarness.WaitUntil(() =>
        {
            try
            {
                return h.ReadText("input.txt") == "y\n";
            }
            catch (IOException)
            {
                return false; // FakeClaude is writing it
            }
        }, "input recorded");
    }

    [Fact]
    public void Ready_output_on_the_session_screen_makes_the_folder_running()
    {
        using var h = new FakeClaudeHarness();
        h.Configure(new { replay = FakeClaudeHarness.Fixture("claude-rc-ready.raw") });
        var path = PathNormalizer.Normalize(h.Folder);
        var supervisor = new Supervisor(TimeProvider.System, Directory.Exists);
        supervisor.Load([]);
        supervisor.Add(path);

        using var session = h.Start();
        supervisor.SessionStarted(path, session.ProcessId);
        Assert.Equal(FolderStatus.Starting, supervisor.Get(path)!.Status);
        using var scanner = new PromptScanner(TimeProvider.System,
            p => { lock (supervisor) supervisor.ReadySeen(p); },
            (p, prompt) => { lock (supervisor) supervisor.PromptChanged(p, prompt); },
            TimeSpan.FromMilliseconds(100));
        scanner.Add(path, session);

        FakeClaudeHarness.WaitUntil(() => { lock (supervisor) return supervisor.Get(path)!.Status == FolderStatus.Running; }, "folder running");
    }
}
