using System.Runtime.InteropServices;
using ClaudeRc.Tray.Hosting;

namespace ClaudeRc.Tray.Tests;

/// <summary>The host's scheduled task as the tray reads and runs it (SPEC 9, 9.4), over a fake Task Scheduler.</summary>
public class HostTaskManagerTests
{
    private const string Path = @"\claude-rc-windows\Host";
    private readonly FakeTaskService _tasks = new();
    private readonly HostTaskManager _manager;

    public HostTaskManagerTests() => _manager = new HostTaskManager(_tasks, Path);

    [Fact]
    public async Task No_task_reads_as_missing()
    {
        var info = await _manager.GetInfoAsync();
        Assert.Equal(HostTaskState.Missing, info.State);
        Assert.Equal(StartMode.Unknown, info.StartMode);
        Assert.Equal(Path, info.TaskPath);
        Assert.Equal(@"missing (\claude-rc-windows\Host)", info.StatusText);
    }

    [Fact]
    public async Task A_logon_task_reads_its_mode_and_state()
    {
        _tasks.Add(Path, StartMode.Logon, TaskRunState.Running, HostTaskDefinition.TaskIsRunning);
        _tasks.Tasks[Path].LastRunTime = new DateTime(2026, 11, 6, 9, 30, 0);

        var info = await _manager.GetInfoAsync();

        Assert.Equal(HostTaskState.Exists, info.State);
        Assert.Equal(StartMode.Logon, info.StartMode);
        Assert.True(info.Running);
        Assert.True(info.Enabled);
        Assert.Equal(HostTaskDefinition.TaskIsRunning, info.LastResult);
        Assert.Equal(new DateTime(2026, 11, 6, 9, 30, 0), info.LastRunTime);
        Assert.False(info.NeedsPassword);
        Assert.Equal("running", info.Health);
        Assert.StartsWith(@"running (\claude-rc-windows\Host), last run ", info.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_startup_task_whose_logon_failed_needs_the_password_again()
    {
        _tasks.Add(Path, StartMode.Startup, TaskRunState.Ready, HostTaskDefinition.LogonFailure);

        var info = await _manager.GetInfoAsync();

        Assert.Equal(StartMode.Startup, info.StartMode);
        Assert.True(info.NeedsPassword);
        Assert.Equal("re-enter your password", info.Health);
    }

    [Fact]
    public async Task A_logon_failure_of_a_logon_task_is_just_a_failed_run()
    {
        _tasks.Add(Path, StartMode.Logon, TaskRunState.Ready, HostTaskDefinition.LogonFailure);
        var info = await _manager.GetInfoAsync();
        Assert.False(info.NeedsPassword);
        Assert.Equal("last run failed (0x8007052E)", info.Health);
    }

    [Theory]
    [InlineData(0, "ready")]
    [InlineData(HostTaskDefinition.HasNotRun, "ready")]
    [InlineData(0x41306, "ready")] // stopped by the user or by a restart
    [InlineData(3, "last run ended with exit code 3: another host was already running")]
    [InlineData(1, "last run ended with exit code 1")]
    [InlineData(unchecked((int)0x80070002), "last run failed (0x80070002)")]
    public async Task Health_says_how_the_last_run_ended(int lastResult, string health)
    {
        _tasks.Add(Path, StartMode.Logon, TaskRunState.Ready, lastResult);
        Assert.Equal(health, (await _manager.GetInfoAsync()).Health);
    }

    [Fact]
    public async Task A_disabled_task_says_so()
    {
        _tasks.Add(Path, StartMode.Logon, TaskRunState.Disabled);
        var info = await _manager.GetInfoAsync();
        Assert.False(info.Enabled);
        Assert.Equal("disabled", info.Health);
    }

    [Fact]
    public async Task A_task_changed_by_hand_has_an_unknown_mode()
    {
        _tasks.Add(Path, StartMode.Logon);
        _tasks.Tasks[Path].Xml = "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Triggers><TimeTrigger /></Triggers></Task>";
        var info = await _manager.GetInfoAsync();
        Assert.Equal(HostTaskState.Exists, info.State);
        Assert.Equal(StartMode.Unknown, info.StartMode);
    }

    [Fact]
    public async Task A_task_scheduler_error_reads_as_unknown_with_the_reason()
    {
        _tasks.FailNext("find", new COMException("The service is not available", unchecked((int)0x80070426)));
        var info = await _manager.GetInfoAsync();
        Assert.Equal(HostTaskState.Unknown, info.State);
        Assert.Contains("The service is not available", info.Error, StringComparison.Ordinal);
        Assert.StartsWith(@"unknown (\claude-rc-windows\Host)", info.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_runs_the_task()
    {
        _tasks.Add(Path, StartMode.Logon);
        var result = await _manager.RunAsync();
        Assert.True(result.Ok);
        Assert.Contains($"run {Path}", _tasks.Calls);
    }

    [Fact]
    public async Task Run_with_no_task_says_so_and_runs_nothing()
    {
        var result = await _manager.RunAsync();
        Assert.False(result.Ok);
        Assert.Contains("does not exist", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(_tasks.Calls, c => c.StartsWith("run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_of_a_disabled_task_says_so()
    {
        _tasks.Add(Path, StartMode.Logon, TaskRunState.Disabled);
        var result = await _manager.RunAsync();
        Assert.False(result.Ok);
        Assert.Contains("disabled", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_errors_are_reported()
    {
        _tasks.Add(Path, StartMode.Logon);
        _tasks.FailNext("run", new UnauthorizedAccessException("Access is denied."));
        var result = await _manager.RunAsync();
        Assert.False(result.Ok);
        Assert.Contains(Path, result.Error, StringComparison.Ordinal);
        Assert.Contains("Access is denied.", result.Error, StringComparison.Ordinal);
    }
}
