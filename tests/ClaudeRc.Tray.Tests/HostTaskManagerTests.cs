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

    [Fact]
    public async Task A_run_task_scheduler_refuses_goes_through_the_elevated_helper()
    {
        // An administrator-made startup task: the user may not run it unelevated (SPEC 9.4).
        var elevation = new FakeElevation { OnRun = _ => _tasks.Tasks[Path].State = TaskRunState.Running };
        var manager = new HostTaskManager(_tasks, Path, new ElevatedHostRun(elevation, Path, ["--run-host"]));
        _tasks.Add(Path, StartMode.Startup);
        _tasks.FailNext("run", new UnauthorizedAccessException("Access is denied."));

        var result = await manager.RunAsync();

        Assert.True(result.Ok, result.Error);
        Assert.Equal(["--run-host"], Assert.Single(elevation.Runs));
    }

    [Fact]
    public async Task Other_run_errors_do_not_go_through_the_elevated_helper()
    {
        var elevation = new FakeElevation();
        var manager = new HostTaskManager(_tasks, Path, new ElevatedHostRun(elevation, Path, ["--run-host"]));
        _tasks.Add(Path, StartMode.Logon);
        _tasks.FailNext("run", new COMException("The service is not available", unchecked((int)0x80070426)));

        var result = await manager.RunAsync();

        Assert.False(result.Ok);
        Assert.Contains("The service is not available", result.Error, StringComparison.Ordinal);
        Assert.Empty(elevation.Runs);
    }
}

/// <summary>Running the host's task through the elevated helper (<c>ClaudeRc.exe --run-host</c>, SPEC 9.4).</summary>
public class ElevatedHostRunTests
{
    private const string Path = @"\claude-rc-windows\Host";
    private readonly FakeElevation _elevation = new();

    [Fact]
    public async Task The_helper_is_run_with_its_arguments()
    {
        var result = await new ElevatedHostRun(_elevation, Path, ["--run-host", "--instance", "t1"]).RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(["--run-host", "--instance", "t1"], Assert.Single(_elevation.Runs));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(HelperExitCodes.Cancelled)]
    public async Task No_at_uac_is_cancelled(int? code)
    {
        _elevation.ExitCode = code;

        var result = await new ElevatedHostRun(_elevation, Path, ["--run-host"]).RunAsync();

        Assert.False(result.Ok);
        Assert.Contains("Cancelled", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_helper_says_it_showed_why()
    {
        _elevation.ExitCode = HelperExitCodes.Failed;

        var result = await new ElevatedHostRun(_elevation, Path, ["--run-host"]).RunAsync();

        Assert.False(result.Ok);
        Assert.Contains("code 1", result.Error, StringComparison.Ordinal);
        Assert.Contains(Path, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_helper_arguments_is_refused_without_uac()
    {
        var result = await new ElevatedHostRun(_elevation, Path, null).RunAsync();

        Assert.False(result.Ok);
        Assert.Contains("administrator", result.Error, StringComparison.Ordinal);
        Assert.Empty(_elevation.Runs);
    }
}
