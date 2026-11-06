using System.Diagnostics;
using ClaudeRc.Core;
using ClaudeRc.Core.Ipc;
using ClaudeRc.Tray.Hosting;
using ClaudeRc.Tray.Services;

namespace ClaudeRc.Tray.Tests;

/// <summary>The installer's calls into ClaudeRc.exe (SPEC 14): <c>--install</c>, <c>--stop-for-update</c>, <c>--uninstall</c>.</summary>
public class TrayOptionsSetupTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string Local = @"C:\Users\me\AppData\Local";
    private const string Roaming = @"C:\Users\me\AppData\Roaming";
    private const string AppDir = @"C:\Users\me\AppData\Local\Programs\claude-rc-windows";

    private static TrayOptions Parse(params string[] args) => TrayOptions.Parse(args, Sid, Local, Roaming, AppDir);

    [Theory]
    [InlineData("--install", "Install")]
    [InlineData("--stop-for-update", "StopForUpdate")]
    [InlineData("--uninstall", "Uninstall")]
    public void Each_setup_action_is_parsed(string arg, string action)
    {
        var o = Parse(arg);
        Assert.Equal(Enum.Parse<SetupAction>(action), o.Setup);
        // Always the fixed task and the host next to ClaudeRc.exe, with no arguments.
        Assert.Equal(@"\claude-rc-windows\Host", o.TaskPath);
        Assert.Equal(AppDir + @"\ClaudeRcHost.exe", o.HostExe);
        Assert.Empty(o.HostArguments);
        Assert.Null(o.SetStartMode);
    }

    [Fact]
    public void No_setup_action_by_default()
    {
        Assert.Null(Parse().Setup);
    }

    [Fact]
    public void A_test_instance_sets_up_its_own_task_and_host_folders()
    {
        var o = Parse("--uninstall", "--instance", "t1");
        Assert.Equal(SetupAction.Uninstall, o.Setup);
        Assert.Equal(@"\claude-rc-windows-t1\Host", o.TaskPath);
        Assert.Equal(AppDir + @"\ClaudeRcHost.exe", o.HostExe);
        Assert.Equal("--instance", o.HostArguments[0]);
        Assert.Equal("t1", o.HostArguments[1]);
    }

    [Theory]
    [InlineData("--install", "--uninstall")]
    [InlineData("--install", "--install")]
    [InlineData("--stop-for-update", "--uninstall")]
    [InlineData("--install", "--host-exe", @"D:\other\ClaudeRcHost.exe")]
    [InlineData("--install", "--host-exe", AppDir + @"\ClaudeRcHost.exe")]
    [InlineData("--install", "--host-arg", "--data-dir")]
    [InlineData("--uninstall", "--set-start-mode", "logon", "--for-user", @"PC\me")]
    [InlineData("--install", "--for-user", @"PC\me")]
    [InlineData("--stop-for-update", "--restart-host")]
    [InlineData("--uninstall", "--unregister-notifications")]
    [InlineData("--install", "-ToastActivated")]
    [InlineData("--install", "--run-host")]
    public void A_setup_action_takes_nothing_but_the_instance(params string[] args)
    {
        // The setup actions act only on the fixed task and the exes in ClaudeRc.exe's own folder.
        Assert.Throws<ArgumentException>(() => Parse(args));
    }

    [Theory]
    [InlineData("--install")]
    [InlineData("--stop-for-update")]
    [InlineData("--uninstall")]
    public void Quiet_goes_with_a_setup_action(string arg)
    {
        Assert.True(Parse(arg, "--quiet").Quiet);
        Assert.True(Parse("--quiet", arg, "--instance", "t1").Quiet);
        Assert.False(Parse(arg).Quiet);
    }

    [Theory]
    [InlineData("--quiet")]
    [InlineData("--quiet", "--instance", "t1")]
    [InlineData("--quiet", "--set-start-mode", "logon", "--for-user", @"PC\me")]
    [InlineData("--quiet", "--run-host")]
    [InlineData("--quiet", "--unregister-notifications")]
    public void Quiet_is_refused_without_a_setup_action(params string[] args)
    {
        // Only the installer's own steps write their errors to a file instead of showing them.
        Assert.Throws<ArgumentException>(() => Parse(args));
    }

    [Fact]
    public void The_setup_log_is_in_the_instances_log_folder()
    {
        Assert.Equal(Local + @"\claude-rc-windows\logs\setup.log", Parse("--install", "--quiet").SetupLogPath);
        Assert.Equal(Local + @"\claude-rc-windows-t1\logs\setup.log", Parse("--install", "--instance", "t1").SetupLogPath);
    }

    [Fact]
    public void Run_host_is_parsed_with_the_fixed_task_and_host()
    {
        var o = Parse("--run-host");
        Assert.True(o.RunHost);
        Assert.Equal(@"\claude-rc-windows\Host", o.TaskPath);
        Assert.Equal(AppDir + @"\ClaudeRcHost.exe", o.HostExe);
        Assert.Null(o.Setup);
        Assert.Null(o.SetStartMode);
        Assert.Equal(@"\claude-rc-windows-t1\Host", Parse("--run-host", "--instance", "t1").TaskPath);
        Assert.False(Parse().RunHost);
    }

    [Theory]
    [InlineData("--run-host", "--host-exe", @"D:\other\ClaudeRcHost.exe")]
    [InlineData("--run-host", "--host-exe", AppDir + @"\ClaudeRcHost.exe")]
    [InlineData("--run-host", "--host-arg", "--evil")]
    [InlineData("--run-host", "--set-start-mode", "logon", "--for-user", @"PC\me")]
    [InlineData("--run-host", "--restart-host")]
    [InlineData("--run-host", "--unregister-notifications")]
    [InlineData("--run-host", "-ToastActivated")]
    [InlineData("--run-host", "--uninstall")]
    public void Run_host_takes_nothing_but_the_instance(params string[] args)
    {
        // It runs elevated with an untrusted command line: only the fixed task.
        Assert.Throws<ArgumentException>(() => Parse(args));
    }

    [Fact]
    public void Run_host_arguments_name_only_the_instance()
    {
        Assert.Equal(["--run-host"], Parse().RunHostArguments());
        Assert.Equal(["--run-host", "--instance", "t1"], Parse("--instance", "t1").RunHostArguments());
        // A debug build's host elsewhere: the helper would refuse its task, so it is not started.
        Assert.Null(Parse("--host-exe", @"D:\build\ClaudeRcHost.exe").RunHostArguments());
    }

    [Fact]
    public void Setup_exit_codes_are_stable()
    {
        // The installer script reads them (installer/claude-rc-windows.iss).
        Assert.Equal(0, SetupExitCodes.Ok);
        Assert.Equal(1, SetupExitCodes.Failed);
        Assert.Equal(2, SetupExitCodes.InvalidArguments);
        Assert.Equal(3, SetupExitCodes.OtherCopy);
    }

    [Fact]
    public void The_quit_event_is_per_user_and_instance()
    {
        Assert.Equal(@"Local\claude-rc-windows-tray-quit-" + Sid, Parse().QuitEventName);
        Assert.Equal(@"Local\claude-rc-windows-tray-quit-" + Sid + "-t1", Parse("--instance", "t1").QuitEventName);
    }
}

public sealed class AppSetupTests
{
    private const string TaskPath = @"\claude-rc-windows\Host";
    private const string AppDir = @"C:\app";
    private const string HostExe = AppDir + @"\ClaudeRcHost.exe";
    private const string TrayExe = AppDir + @"\ClaudeRc.exe";
    private const string Account = @"PC\me";
    private const string OtherHost = @"D:\portable\ClaudeRcHost.exe";

    private readonly List<string> _journal = [];
    private readonly FakeTaskService _tasks = new();
    private readonly FakeSwitcher _switcher = new();
    private readonly FakeConnector _connector = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeElevation _elevation = new();
    private readonly FakeRunningTray _tray;
    private readonly FakeProcessStopper _processes;
    private int _unregistered;
    private int _delays;

    public AppSetupTests()
    {
        _tray = new FakeRunningTray(_journal);
        _processes = new FakeProcessStopper(_journal);
        _tasks.Observe = _journal.Add;
        // The switcher sets up the task as the real one would.
        _switcher.OnSwitch = mode => _tasks.Tasks[TaskPath] = new FakeTaskService.Entry
        {
            Xml = Xml(mode, HostExe),
            Account = Account,
            Logon = HostTaskDefinition.LogonFor(mode),
            State = _tasks.Tasks.GetValueOrDefault(TaskPath)?.State ?? TaskRunState.Ready,
        };
    }

    private static string Xml(StartMode mode, string hostExe) => HostTaskDefinition.BuildXml(new HostTaskSpec
    {
        TaskPath = TaskPath, Mode = mode, HostExe = hostExe, Account = Account,
    });

    private void AddTask(StartMode mode, string hostExe = HostExe, TaskRunState state = TaskRunState.Ready) =>
        _tasks.Tasks[TaskPath] = new FakeTaskService.Entry
        {
            Xml = Xml(mode, hostExe),
            Account = Account,
            Logon = HostTaskDefinition.LogonFor(mode),
            State = state,
        };

    private AppSetup Setup(IStartModeSwitcher? switcher = null, TimeSpan? turnOffTimeout = null) => new(_tasks, switcher ?? _switcher,
        new HostTaskManager(_tasks, TaskPath, new ElevatedHostRun(_elevation, TaskPath, ["--run-host"])), _connector, _tray, _processes,
        () =>
        {
            _unregistered++;
            _journal.Add("unregister");
        },
        _dialogs, new StartModeSetup(TaskPath, HostExe, [], Account), TrayExe,
        (_, _) =>
        {
            _delays++;
            return Task.CompletedTask;
        })
    {
        TurnOffTimeout = turnOffTimeout ?? AppSetup.DefaultTurnOffTimeout,
    };

    private int Index(string entry)
    {
        var i = _journal.IndexOf(entry);
        Assert.True(i >= 0, $"'{entry}' did not happen: {string.Join(", ", _journal)}");
        return i;
    }

    // ---- --install ----

    [Fact]
    public async Task Install_with_no_task_sets_up_logon_then_starts_the_host()
    {
        var code = await Setup().RunAsync(SetupAction.Install);

        Assert.Equal(SetupExitCodes.Ok, code);
        Assert.Empty(_dialogs.Errors);
        Assert.Equal([(StartMode.Logon, false)], _switcher.Calls);
        Assert.Equal(TaskRunState.Running, _tasks.Tasks[TaskPath].State);
        // The tray first (a running one would only show its window again), then the host, then the run.
        Assert.True(Index("quit tray") < Index($"end {HostExe}"));
        Assert.True(Index($"end {HostExe}") < Index($"run {TaskPath}"));
    }

    [Fact]
    public async Task Install_registers_the_logon_task_through_the_real_switcher()
    {
        var setup = new StartModeSetup(TaskPath, HostExe, [], Account);
        var switcher = new StartModeSwitcher(_tasks, new FakeElevation(), setup, (_, _) => null, _ => true, (_, _) => Task.CompletedTask);

        var code = await Setup(switcher).RunAsync(SetupAction.Install);

        Assert.Equal(SetupExitCodes.Ok, code);
        var task = _tasks.Tasks[TaskPath];
        Assert.Equal(StartMode.Logon, HostTaskDefinition.ReadMode(task.Xml));
        Assert.True(HostTaskDefinition.RunsHost(task.Xml, HostExe, []));
        Assert.Equal(TaskLogon.InteractiveToken, task.Logon);
        Assert.Equal(Account, task.Account);
        Assert.Equal(TaskRunState.Running, task.State);
    }

    [Fact]
    public async Task Install_over_a_logon_task_registers_it_again()
    {
        // An upgrade gets this version's task definition.
        AddTask(StartMode.Logon, OtherHost);

        Assert.Equal(SetupExitCodes.Ok, await Setup().RunAsync(SetupAction.Install));

        Assert.Equal([(StartMode.Logon, false)], _switcher.Calls);
        Assert.True(HostTaskDefinition.RunsHost(_tasks.Tasks[TaskPath].Xml, HostExe, []));
    }

    [Fact]
    public async Task Install_keeps_a_startup_task_that_already_runs_this_host()
    {
        // Registering it again would need administrator rights and the password: an upgrade keeps it.
        AddTask(StartMode.Startup);

        Assert.Equal(SetupExitCodes.Ok, await Setup().RunAsync(SetupAction.Install));

        Assert.Empty(_switcher.Calls);
        Assert.DoesNotContain(_tasks.Calls, c => c.StartsWith("register", StringComparison.Ordinal));
        Assert.Equal(TaskRunState.Running, _tasks.Tasks[TaskPath].State);
    }

    [Fact]
    public async Task Install_over_another_copys_startup_task_keeps_startup_for_this_one()
    {
        AddTask(StartMode.Startup, OtherHost);

        Assert.Equal(SetupExitCodes.Ok, await Setup().RunAsync(SetupAction.Install));

        Assert.Equal([(StartMode.Startup, false)], _switcher.Calls);
        Assert.True(HostTaskDefinition.RunsHost(_tasks.Tasks[TaskPath].Xml, HostExe, []));
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    public async Task A_task_that_was_not_set_up_is_shown_and_no_host_is_started(string outcome)
    {
        _switcher.Result = new SwitchResult(Enum.Parse<SwitchOutcome>(outcome), "Task Scheduler said no.");
        _switcher.OnSwitch = null;

        Assert.Equal(SetupExitCodes.Failed, await Setup().RunAsync(SetupAction.Install));

        var error = Assert.Single(_dialogs.Errors);
        Assert.Contains("Task Scheduler said no.", error.Message, StringComparison.Ordinal);
        Assert.Contains("Settings", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_tasks.Calls, c => c.StartsWith("run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Install_stops_a_running_host_before_running_the_task()
    {
        // E.g. the portable copy's host: the new one would meet its single-instance mutex and exit.
        AddTask(StartMode.Logon, OtherHost, TaskRunState.Running);

        Assert.Equal(SetupExitCodes.Ok, await Setup().RunAsync(SetupAction.Install));

        Assert.True(Index($"stop {TaskPath}") < Index($"run {TaskPath}"));
    }

    [Fact]
    public async Task A_host_that_does_not_end_is_shown_and_the_task_is_not_run()
    {
        _processes.Left[HostExe] = 1;

        Assert.Equal(SetupExitCodes.Failed, await Setup().RunAsync(SetupAction.Install));

        Assert.Contains(_dialogs.Errors, e => e.Message.Contains(HostExe, StringComparison.Ordinal));
        Assert.DoesNotContain(_tasks.Calls, c => c.StartsWith("run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stop_refused_by_task_scheduler_still_ends_this_host()
    {
        AddTask(StartMode.Startup, HostExe, TaskRunState.Running);
        _tasks.FailNext("stop", new UnauthorizedAccessException("Access is denied."));

        Assert.Equal(SetupExitCodes.Ok, await Setup().RunAsync(SetupAction.Install));

        Assert.Contains(_processes.Stops, s => s.Exe == HostExe);
        Assert.Contains($"run {TaskPath}", _tasks.Calls);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public async Task An_upgrade_in_startup_mode_runs_the_kept_task_elevated_when_task_scheduler_refuses()
    {
        // The administrator-made startup task: Task Scheduler refuses both the stop and the run.
        AddTask(StartMode.Startup, HostExe, TaskRunState.Running);
        _tasks.FailNext("stop", new UnauthorizedAccessException("Access is denied."));
        _tasks.FailNext("run", new UnauthorizedAccessException("Access is denied."));
        _elevation.OnRun = _ =>
        {
            _journal.Add("elevated run");
            _tasks.Tasks[TaskPath].State = TaskRunState.Running;
        };

        Assert.Equal(SetupExitCodes.Ok, await Setup().RunAsync(SetupAction.Install));

        Assert.Empty(_dialogs.Errors);
        Assert.Empty(_switcher.Calls); // no password prompt: the task is kept
        Assert.Equal(["--run-host"], Assert.Single(_elevation.Runs));
        Assert.True(Index($"end {HostExe}") < Index("elevated run"));
        Assert.Equal(TaskRunState.Running, _tasks.Tasks[TaskPath].State);
    }

    [Fact]
    public async Task A_refused_run_cancelled_at_uac_says_when_the_host_starts()
    {
        AddTask(StartMode.Startup);
        _tasks.FailNext("run", new UnauthorizedAccessException("Access is denied."));
        _elevation.ExitCode = null;

        Assert.Equal(SetupExitCodes.Failed, await Setup().RunAsync(SetupAction.Install));

        var error = Assert.Single(_dialogs.Errors);
        Assert.Contains("Cancelled", error.Message, StringComparison.Ordinal);
        Assert.Contains("next time Windows starts", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tray_that_does_not_quit_is_shown()
    {
        _tray.QuitResult = false;

        Assert.Equal(SetupExitCodes.Failed, await Setup().RunAsync(SetupAction.Install));

        Assert.Contains(_dialogs.Errors, e => e.Message.Contains("tray", StringComparison.OrdinalIgnoreCase));
        // The host is still set up and started.
        Assert.Contains($"run {TaskPath}", _tasks.Calls);
    }

    [Fact]
    public async Task This_trays_exe_is_ended_after_it_was_asked_to_quit()
    {
        Assert.Equal(SetupExitCodes.Ok, await Setup().RunAsync(SetupAction.Install));

        Assert.True(Index("quit tray") < Index($"end {TrayExe}"));
    }

    [Fact]
    public async Task A_tray_exe_that_still_runs_is_shown()
    {
        _processes.Left[TrayExe] = 1;

        Assert.Equal(SetupExitCodes.Failed, await Setup().RunAsync(SetupAction.Install));

        Assert.Contains(_dialogs.Errors, e => e.Message.Contains(TrayExe, StringComparison.Ordinal));
    }

    // ---- --stop-for-update ----

    [Fact]
    public async Task Stop_for_update_ends_the_tray_and_the_host_and_changes_nothing()
    {
        AddTask(StartMode.Logon, HostExe, TaskRunState.Running);

        Assert.Equal(SetupExitCodes.Ok, await Setup().RunAsync(SetupAction.StopForUpdate));

        Assert.Equal(TaskRunState.Ready, _tasks.Tasks[TaskPath].State);
        Assert.Equal(1, _tray.Quits);
        Assert.Contains(_processes.Stops, s => s.Exe == HostExe);
        Assert.Contains(_processes.Stops, s => s.Exe == TrayExe);
        Assert.Empty(_switcher.Calls);
        Assert.DoesNotContain(_tasks.Calls, c => c.StartsWith("register", StringComparison.Ordinal)
            || c.StartsWith("run", StringComparison.Ordinal) || c.StartsWith("delete", StringComparison.Ordinal));
        Assert.Equal(0, _connector.Attempts);
        Assert.Equal(0, _unregistered);
    }

    [Fact]
    public async Task Stop_for_update_without_a_task_still_ends_the_host()
    {
        Assert.Equal(SetupExitCodes.Ok, await Setup().RunAsync(SetupAction.StopForUpdate));

        Assert.Contains(_processes.Stops, s => s.Exe == HostExe);
        Assert.Empty(_dialogs.Errors);
    }

    // ---- --uninstall ----

    private FakeHostClient HostWith(params Row[] rows)
    {
        var current = rows.ToList();
        var client = new FakeHostClient();
        client.Handler = request =>
        {
            switch (request)
            {
                case ListRequest:
                    return new IpcResponse { Ok = true, Rows = current.ToList() };
                case SetRequest { On: false } set:
                    _journal.Add($"off {set.Path}");
                    var i = current.FindIndex(r => r.Path == set.Path);
                    current[i] = current[i] with { On = false, Status = current[i].Status == FolderStatus.External ? FolderStatus.External : FolderStatus.Off };
                    return new IpcResponse { Ok = true };
                default:
                    return new IpcResponse { Ok = false, Error = "not expected here" };
            }
        };
        _connector.Enqueue(client);
        return client;
    }

    [Fact]
    public async Task Uninstall_turns_every_folder_off_then_stops_the_host_and_deletes_the_task()
    {
        AddTask(StartMode.Logon, HostExe, TaskRunState.Running);
        var client = HostWith(
            TestRows.Make(@"C:\src\a", FolderStatus.Running),
            TestRows.Make(@"C:\src\b", FolderStatus.Off, on: false),
            TestRows.Make(@"C:\src\c", FolderStatus.External, on: false, saved: false),
            TestRows.Make(@"C:\src\d", FolderStatus.Restarting),
            TestRows.Make(@"C:\src\e", FolderStatus.External));

        Assert.Equal(SetupExitCodes.Ok, await Setup().RunAsync(SetupAction.Uninstall));

        Assert.Empty(_dialogs.Errors);
        // Only the saved ON folders, only ever OFF: nothing is started, removed or taken over.
        Assert.Equal([@"C:\src\a", @"C:\src\d", @"C:\src\e"], client.Sent<SetRequest>().Select(s => s.Path));
        Assert.All(client.Sent<SetRequest>(), s => Assert.False(s.On));
        Assert.All(client.Requests, r => Assert.True(r is ListRequest or SetRequest));
        Assert.True(client.Disposed);
        // Folders OFF while the host runs; then the tray, the host and the task; then the toasts.
        Assert.True(Index(@"off C:\src\d") < Index("quit tray"));
        Assert.True(Index("quit tray") < Index($"stop {TaskPath}"));
        Assert.True(Index($"stop {TaskPath}") < Index($"delete {TaskPath}"));
        Assert.True(Index($"delete {TaskPath}") < Index("unregister"));
        Assert.False(_tasks.Tasks.ContainsKey(TaskPath));
        Assert.Contains(_processes.Stops, s => s.Exe == HostExe);
        Assert.Equal(1, _unregistered);
    }

    [Fact]
    public async Task Uninstall_waits_for_the_sessions_to_stop()
    {
        AddTask(StartMode.Logon, HostExe, TaskRunState.Running);
        var lists = 0;
        var client = new FakeHostClient();
        client.Handler = request => request switch
        {
            // The session takes a few polls to stop after the set.
            ListRequest => new IpcResponse
            {
                Ok = true,
                Rows = [TestRows.Make(@"C:\src\a", ++lists <= 3 ? FolderStatus.Running : FolderStatus.Off, on: lists <= 1)],
            },
            _ => new IpcResponse { Ok = true },
        };
        client.Handler = WithJournal(client.Handler);
        _connector.Enqueue(client);

        Assert.Equal(SetupExitCodes.Ok, await Setup().RunAsync(SetupAction.Uninstall));

        Assert.Equal(4, lists);
        Assert.True(_delays >= 2);
        Assert.True(Index("list 4") < Index("quit tray"));
    }

    private Func<IpcRequest, IpcResponse> WithJournal(Func<IpcRequest, IpcResponse> inner)
    {
        var n = 0;
        return r =>
        {
            if (r is ListRequest)
                _journal.Add($"list {++n}");
            return inner(r);
        };
    }

    [Fact]
    public async Task Sessions_that_do_not_stop_in_time_end_with_the_host()
    {
        AddTask(StartMode.Logon, HostExe, TaskRunState.Running);
        var client = new FakeHostClient();
        client.Handler = request => request switch
        {
            ListRequest => new IpcResponse { Ok = true, Rows = [TestRows.Make(@"C:\src\a", FolderStatus.Running)] },
            _ => new IpcResponse { Ok = true },
        };
        _connector.Enqueue(client);

        Assert.Equal(SetupExitCodes.Ok, await Setup().RunAsync(SetupAction.Uninstall));

        Assert.Equal((int)(AppSetup.SessionsStopTimeout / AppSetup.Poll), _delays);
        Assert.Contains($"stop {TaskPath}", _tasks.Calls);
    }

    [Fact]
    public async Task Uninstall_without_a_host_still_deletes_the_task()
    {
        AddTask(StartMode.Logon);

        Assert.Equal(SetupExitCodes.Ok, await Setup().RunAsync(SetupAction.Uninstall));

        Assert.True(_connector.Attempts >= 1);
        Assert.Empty(_dialogs.Errors);
        Assert.False(_tasks.Tasks.ContainsKey(TaskPath));
        Assert.Equal(1, _unregistered);
    }

    [Fact]
    public async Task Uninstall_without_a_task_cleans_up_the_rest()
    {
        HostWith(TestRows.Make(@"C:\src\a", FolderStatus.Running));

        Assert.Equal(SetupExitCodes.Ok, await Setup().RunAsync(SetupAction.Uninstall));

        Assert.Contains(@"off C:\src\a", _journal);
        Assert.Contains(_processes.Stops, s => s.Exe == HostExe);
        Assert.Equal(1, _unregistered);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public async Task Uninstall_leaves_another_copys_task_and_host_alone()
    {
        // The task runs the portable copy now: its host and its folders keep running.
        AddTask(StartMode.Logon, OtherHost, TaskRunState.Running);
        HostWith(TestRows.Make(@"C:\src\a", FolderStatus.Running));

        // A code of its own, so the uninstaller keeps that copy's data without asking.
        Assert.Equal(SetupExitCodes.OtherCopy, await Setup().RunAsync(SetupAction.Uninstall));

        Assert.Empty(_dialogs.Errors);
        Assert.Equal(0, _connector.Attempts);
        Assert.DoesNotContain(_tasks.Calls, c => c.StartsWith("stop", StringComparison.Ordinal) || c.StartsWith("delete", StringComparison.Ordinal));
        Assert.Equal(TaskRunState.Running, _tasks.Tasks[TaskPath].State);
        // This copy's own processes still end, and its toast registration goes.
        Assert.Contains(_processes.Stops, s => s.Exe == HostExe);
        Assert.Contains(_processes.Stops, s => s.Exe == TrayExe);
        Assert.Equal(1, _unregistered);
    }

    [Fact]
    public async Task Another_copys_task_is_reported_even_when_something_else_failed()
    {
        AddTask(StartMode.Logon, OtherHost, TaskRunState.Running);
        _tray.QuitResult = false;

        Assert.Equal(SetupExitCodes.OtherCopy, await Setup().RunAsync(SetupAction.Uninstall));

        Assert.Contains(_dialogs.Errors, e => e.Message.Contains("tray", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_host_that_never_answers_does_not_hold_up_the_uninstall()
    {
        AddTask(StartMode.Logon, HostExe, TaskRunState.Running);
        var client = new FakeHostClient { AsyncHandler = _ => new TaskCompletionSource<IpcResponse>().Task };
        _connector.Enqueue(client);

        var run = Setup(turnOffTimeout: TimeSpan.FromMilliseconds(200)).RunAsync(SetupAction.Uninstall);
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10))));

        Assert.Equal(SetupExitCodes.Failed, await run);
        var error = Assert.Single(_dialogs.Errors);
        Assert.Contains("did not answer", error.Message, StringComparison.Ordinal);
        // The rest still runs.
        Assert.True(client.Disposed);
        Assert.Contains("quit tray", _journal);
        Assert.Contains(_processes.Stops, s => s.Exe == HostExe);
        Assert.False(_tasks.Tasks.ContainsKey(TaskPath));
        Assert.Equal(1, _unregistered);
    }

    [Fact]
    public async Task A_pipe_that_refuses_the_connection_does_not_skip_the_rest()
    {
        // E.g. the pipe-owner check: the pipe is not this user's host.
        AddTask(StartMode.Logon, HostExe, TaskRunState.Running);
        _connector.Error = new UnauthorizedAccessException("The pipe is not owned by the host's user.");

        Assert.Equal(SetupExitCodes.Failed, await Setup().RunAsync(SetupAction.Uninstall));

        var error = Assert.Single(_dialogs.Errors);
        Assert.Contains("The pipe is not owned by the host's user.", error.Message, StringComparison.Ordinal);
        Assert.Contains(_processes.Stops, s => s.Exe == HostExe);
        Assert.False(_tasks.Tasks.ContainsKey(TaskPath));
        Assert.Equal(1, _unregistered);
    }

    [Fact]
    public async Task A_task_that_cannot_be_deleted_is_shown_and_the_rest_still_runs()
    {
        AddTask(StartMode.Startup);
        _tasks.FailNext("delete", new UnauthorizedAccessException("Access is denied."));

        Assert.Equal(SetupExitCodes.Failed, await Setup().RunAsync(SetupAction.Uninstall));

        var error = Assert.Single(_dialogs.Errors);
        Assert.Contains(TaskPath, error.Message, StringComparison.Ordinal);
        Assert.Contains("Task Scheduler", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, _unregistered);
    }

    [Fact]
    public async Task A_folder_the_host_would_not_turn_off_is_shown()
    {
        AddTask(StartMode.Logon);
        var client = new FakeHostClient();
        client.Handler = request => request switch
        {
            ListRequest => new IpcResponse { Ok = true, Rows = [TestRows.Make(@"C:\src\a", FolderStatus.Missing)] },
            _ => new IpcResponse { Ok = false, Error = "disk on fire" },
        };
        _connector.Enqueue(client);

        Assert.Equal(SetupExitCodes.Failed, await Setup().RunAsync(SetupAction.Uninstall));

        Assert.Contains(_dialogs.Errors, e => e.Message.Contains("disk on fire", StringComparison.Ordinal));
        Assert.False(_tasks.Tasks.ContainsKey(TaskPath));
    }

    [Fact]
    public async Task Errors_are_shown_once_together()
    {
        AddTask(StartMode.Logon);
        _tray.QuitResult = false;
        _tasks.FailNext("delete", new UnauthorizedAccessException("Access is denied."));

        Assert.Equal(SetupExitCodes.Failed, await Setup().RunAsync(SetupAction.Uninstall));

        var error = Assert.Single(_dialogs.Errors);
        Assert.Contains("tray", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Access is denied.", error.Message, StringComparison.Ordinal);
    }
}

/// <summary>The quit signal to a running tray (a named event), checked with a stand-in tray on another thread.</summary>
public sealed class WindowsRunningTrayTests
{
    private readonly string _mutex = @"Local\claude-rc-windows-test-tray-" + Guid.NewGuid().ToString("N");
    private readonly string _event = @"Local\claude-rc-windows-test-tray-quit-" + Guid.NewGuid().ToString("N");

    /// <summary>A stand-in tray: holds the mutex until told to quit (or forever, if it ignores the event).</summary>
    private Thread StartTray(bool obeys, ManualResetEventSlim ready, ManualResetEventSlim? end = null, bool abandon = false)
    {
        var t = new Thread(() =>
        {
            using var quit = new EventWaitHandle(false, EventResetMode.AutoReset, _event);
            var mutex = new Mutex(true, _mutex, out _);
            ready.Set();
            if (obeys)
                quit.WaitOne(TimeSpan.FromSeconds(10));
            else
                end!.Wait(TimeSpan.FromSeconds(10));
            if (!abandon)
                mutex.ReleaseMutex();
            mutex.Dispose();
        })
        { IsBackground = true };
        t.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
        return t;
    }

    [Fact]
    public void No_running_tray_counts_as_quit()
    {
        var sw = Stopwatch.StartNew();
        Assert.True(new WindowsRunningTray(_mutex, _event).Quit(TimeSpan.FromSeconds(5)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void A_running_tray_is_asked_to_quit_and_waited_for()
    {
        using var ready = new ManualResetEventSlim();
        var tray = StartTray(obeys: true, ready);

        Assert.True(new WindowsRunningTray(_mutex, _event).Quit(TimeSpan.FromSeconds(5)));

        Assert.True(tray.Join(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_tray_that_ends_without_releasing_its_mutex_counts_as_quit()
    {
        using var ready = new ManualResetEventSlim();
        var tray = StartTray(obeys: true, ready, abandon: true);

        Assert.True(new WindowsRunningTray(_mutex, _event).Quit(TimeSpan.FromSeconds(5)));

        Assert.True(tray.Join(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_tray_that_does_not_quit_times_out()
    {
        using var ready = new ManualResetEventSlim();
        using var end = new ManualResetEventSlim();
        var tray = StartTray(obeys: false, ready, end);
        try
        {
            Assert.False(new WindowsRunningTray(_mutex, _event).Quit(TimeSpan.FromMilliseconds(300)));
        }
        finally
        {
            end.Set();
            Assert.True(tray.Join(TimeSpan.FromSeconds(5)));
        }
    }
}

/// <summary>Ending the processes of one exe path, with copies of cmd.exe that wait on their input (no window).</summary>
public sealed class WindowsProcessStopperTests : IDisposable
{
    private const string ExeName = "ClaudeRcStopperTest.exe";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-rc-windows-test-stopper-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _started = [];

    private string CopyTo(string folder)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, ExeName);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe);
        return exe;
    }

    private Process Start(string exe)
    {
        var p = Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        })!;
        _started.Add(p);
        return p;
    }

    [Fact]
    public void Only_the_processes_of_that_path_are_ended()
    {
        var a = CopyTo("a");
        var b = CopyTo("b");
        var pa1 = Start(a);
        var pa2 = Start(a);
        var pb = Start(b);
        Thread.Sleep(300);

        var left = new WindowsProcessStopper().StopAll(a.ToUpperInvariant(), TimeSpan.Zero);

        Assert.Equal(0, left);
        Assert.True(pa1.WaitForExit(5000));
        Assert.True(pa2.WaitForExit(5000));
        Assert.False(pb.HasExited);
    }

    [Fact]
    public void A_process_gets_the_grace_time_before_it_is_ended()
    {
        var a = CopyTo("a");
        var p = Start(a);
        Thread.Sleep(300);
        var sw = Stopwatch.StartNew();

        Assert.Equal(0, new WindowsProcessStopper().StopAll(a, TimeSpan.FromMilliseconds(700)));

        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(650), $"ended after {sw.Elapsed}");
        Assert.True(p.WaitForExit(5000));
    }

    [Fact]
    public void No_process_of_that_path_is_nothing_to_do()
    {
        Assert.Equal(0, new WindowsProcessStopper().StopAll(Path.Combine(_root, "none", ExeName), TimeSpan.FromSeconds(5)));
    }

    public void Dispose()
    {
        foreach (var p in _started)
        {
            try
            {
                if (!p.HasExited)
                    p.Kill();
                p.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
            }
            p.Dispose();
        }
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A silent install's messages (<c>--quiet</c>) go to the setup log instead of a message box.</summary>
public sealed class SetupLogDialogsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "claude-rc-windows-test-setuplog-" + Guid.NewGuid().ToString("N"));

    private string LogPath => Path.Combine(_dir, "logs", "setup.log");

    [Fact]
    public void Errors_are_appended_one_line_each_and_the_folder_is_created()
    {
        var log = new SetupLogDialogs(LogPath, TimeProvider.System);

        log.ShowError(AppSetup.Title, "first problem\n\nsecond problem");
        log.ShowInfo("Start mode", "a note");

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(2, lines.Length);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d", lines[0]);
        Assert.EndsWith("claude-rc-windows setup: first problem second problem", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("Start mode: a note", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void It_never_asks_or_picks()
    {
        var log = new SetupLogDialogs(LogPath, TimeProvider.System);

        Assert.False(log.Confirm("t", "Continue?"));
        Assert.Null(log.PickFolder());
        Assert.Null(log.PickClaudeExecutable(null));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
