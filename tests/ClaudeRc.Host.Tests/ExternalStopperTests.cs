using System.Security.Principal;
using ClaudeRc.Core.Adoption;
using ClaudeRc.Core.Paths;
using ClaudeRc.Host.Adoption;

namespace ClaudeRc.Host.Tests;

/// <summary>
/// Take over's stop (SPEC 7) on FakeClaude processes this test starts itself. It must touch nothing
/// unless the process is still exactly the claude rc the scan found.
/// </summary>
public sealed class ExternalStopperTests : IDisposable
{
    private readonly FakeClaudeHarness _dir = new();
    private readonly ExternalSessionMatcher _matcher =
        new(WindowsIdentity.GetCurrent().User!.Value, [Path.GetFileName(FakeClaudeHarness.FakeClaudeExe)], matchNodeCli: false);

    public void Dispose() => _dir.Dispose();

    private ExternalSession Found(ExternalFake external)
    {
        var facts = WindowsProcesses.Read(external.Pid)!;
        return new ExternalSession(_matcher.FolderOf(facts)!, external.Pid, facts.StartTime!.Value);
    }

    [Fact]
    public void Stops_the_process_and_its_child()
    {
        using var external = ExternalFake.Start(_dir.Folder, new { spawn_child = true });
        var child = external.ChildPid!.Value;
        var target = Found(external);
        Assert.Contains(child, ExternalStopper.Descendants(new() { [target.Pid] = target.StartTime }).Keys);

        var outcome = ExternalStopper.Stop(target, _matcher, TimeSpan.FromSeconds(1));

        Assert.True(outcome.Stopped, outcome.Error);
        Assert.False(outcome.ClosedWindow); // hidden: no window of its own
        Assert.True(outcome.Terminated >= 2);
        FakeClaudeHarness.WaitUntil(() => !FakeClaudeHarness.IsRunning(external.Pid) && !FakeClaudeHarness.IsRunning(child), "tree gone");
    }

    [Fact]
    public void Another_process_under_the_same_id_is_not_touched()
    {
        using var external = ExternalFake.Start(_dir.Folder);
        var target = Found(external) with { StartTime = Found(external).StartTime - 1 };

        var outcome = ExternalStopper.Stop(target, _matcher, TimeSpan.FromSeconds(1));

        Assert.True(outcome.Stopped); // the scanned process is gone; the id now names another one
        Assert.Equal(0, outcome.Terminated);
        Thread.Sleep(300);
        Assert.True(external.IsRunning);
    }

    [Fact]
    public void A_process_that_is_no_longer_in_that_folder_is_not_touched()
    {
        using var external = ExternalFake.Start(_dir.Folder);
        var target = Found(external) with { Path = PathNormalizer.Normalize(Path.GetTempPath()) };

        var outcome = ExternalStopper.Stop(target, _matcher, TimeSpan.FromSeconds(1));

        Assert.False(outcome.Stopped);
        Assert.Contains("no longer the claude rc", outcome.Error, StringComparison.Ordinal);
        Thread.Sleep(300);
        Assert.True(external.IsRunning);
    }

    [Fact]
    public void A_process_without_rc_arguments_is_not_touched()
    {
        using var external = ExternalFake.Start(_dir.Folder, null, "chat");
        var target = Found(external);

        var outcome = ExternalStopper.Stop(target, _matcher, TimeSpan.FromSeconds(1));

        Assert.False(outcome.Stopped);
        Thread.Sleep(300);
        Assert.True(external.IsRunning);
    }

    [Fact]
    public void A_process_that_has_gone_counts_as_stopped()
    {
        var external = ExternalFake.Start(_dir.Folder);
        var target = Found(external);
        external.Dispose();

        var outcome = ExternalStopper.Stop(target, _matcher, TimeSpan.FromSeconds(1));

        Assert.True(outcome.Stopped);
        Assert.Equal(0, outcome.Terminated);
    }
}
