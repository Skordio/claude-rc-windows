using ClaudeRc.Core;
using ClaudeRc.Core.Ipc;
using ClaudeRc.Tray.Hosting;
using ClaudeRc.Tray.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace ClaudeRc.Tray.Tests;

public sealed class MainViewModelTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeConnector _connector = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeHostTask _task = new();
    private readonly FakeSwitcher _switcher = new();
    private readonly FakeShell _shell = new();
    private readonly FakeWindows _windows = new();
    private readonly HostConnection _connection;
    private readonly HashSet<string> _networkPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly FakeStartupRegistry _startup = new();
    private readonly SignInStart _signIn;
    private readonly MainViewModel _vm;

    private const string TrayExe = @"D:\portable\ClaudeRc.exe";

    public MainViewModelTests()
    {
        _connection = new HostConnection(_connector, _time);
        _signIn = new SignInStart(_startup, TrayExe, null, _ => true);
        _vm = new MainViewModel(_connection, _task, _switcher, _dialogs, _shell, _windows, _time, _networkPaths.Contains, _signIn);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private async Task<FakeHostClient> Connect(params Row[] rows)
    {
        var client = new FakeHostClient { Rows = [.. rows] };
        _connector.Enqueue(client);
        _connection.Start();
        await Eventually.True(() => _vm.IsHostConnected && _vm.Rows.Count == rows.Length, "connected with rows");
        return client;
    }

    private FolderRowViewModel Row(string path) => _vm.Rows.Single(r => r.Path == path);

    [Fact]
    public async Task Rows_follow_the_host_in_its_order_and_keep_their_view_models()
    {
        var client = await Connect(TestRows.Make(@"C:\a", FolderStatus.Running), TestRows.Make(@"C:\b", FolderStatus.Off, on: false));
        var a = Row(@"C:\a");

        client.Push(new RowsEvent([
            TestRows.Make(@"C:\c", FolderStatus.Starting),
            TestRows.Make(@"C:\a", FolderStatus.Waiting, prompt: "Trust C:\\a? [y/N]"),
        ]));

        await Eventually.True(() => _vm.Rows.Count == 2 && _vm.Rows[0].Path == @"C:\c", "rows updated");
        Assert.Equal([@"C:\c", @"C:\a"], _vm.Rows.Select(r => r.Path));
        Assert.Same(a, Row(@"C:\a"));
        Assert.Equal("waiting for input", a.StatusText);
        Assert.Equal("Trust C:\\a? [y/N]", a.PromptText);
    }

    [Fact]
    public async Task Rows_match_paths_case_insensitively()
    {
        var client = await Connect(TestRows.Make(@"C:\Src\App", FolderStatus.Running));
        var row = _vm.Rows[0];
        client.Push(new RowsEvent([TestRows.Make(@"c:\src\app", FolderStatus.Off, on: false)]));
        await Eventually.True(() => row.Status == FolderStatus.Off, "updated");
        Assert.Same(row, _vm.Rows.Single());
    }

    [Fact]
    public async Task Host_gone_clears_rows_and_offers_start_host()
    {
        var client = await Connect(TestRows.Make(@"C:\a", FolderStatus.Running));
        Assert.Equal("Host connected", _vm.HostStatusText);
        Assert.False(_vm.ShowStartHost);

        client.Close();

        await Eventually.True(() => !_vm.IsHostConnected, "disconnected");
        await Eventually.True(() => _vm.Rows.Count == 0, "rows cleared");
        Assert.Equal("Host not running", _vm.HostStatusText);
        Assert.True(_vm.ShowStartHost);
    }

    [Fact]
    public async Task Version_mismatch_is_shown_in_the_status_bar()
    {
        var client = await Connect();
        client.Close(IpcCodec.VersionMismatchMessage);
        await Eventually.True(() => _vm.HostStatusText == IpcCodec.VersionMismatchMessage, "mismatch shown");
        Assert.True(_vm.ShowStartHost);
    }

    [Fact]
    public async Task Status_bar_shows_claude_path_and_version()
    {
        await Connect();
        await Eventually.True(() => _vm.ClaudeText.Contains("2.1.318", StringComparison.Ordinal), "claude text");
        // Version first, so trimming the end of the status bar never hides it.
        Assert.Equal(@"claude: 2.1.318 (Claude Code) · C:\bin\claude.exe", _vm.ClaudeText);
    }

    [Fact]
    public async Task Status_bar_shows_claude_error()
    {
        var client = new FakeHostClient { Info = new HostInfo("1", "d", "l", ClaudeError: "claude not found: install Claude Code or set its path in Settings") };
        _connector.Enqueue(client);
        _connection.Start();
        await Eventually.True(() => _vm.IsHostConnected, "connected");
        await Eventually.True(() => _vm.ClaudeText.Contains("not found", StringComparison.Ordinal), "claude error");
        Assert.Equal("claude: claude not found: install Claude Code or set its path in Settings", _vm.ClaudeText);
    }

    private const string TaskPath = @"\claude-rc-windows\Host";

    private static HostTaskInfo Exists(StartMode mode, int? lastResult = 0, bool running = false, bool enabled = true) =>
        new(HostTaskState.Exists, mode, TaskPath) { LastResult = lastResult, Running = running, Enabled = enabled };

    [Fact]
    public async Task Status_bar_shows_the_start_mode_and_task_health()
    {
        Assert.Equal("Start mode: unknown", _vm.StartModeText);

        await _vm.LoadTaskInfoAsync();
        Assert.Equal("Start mode: no scheduled task", _vm.StartModeText);

        _task.Info = Exists(StartMode.Logon, running: true);
        await _vm.LoadTaskInfoAsync();
        Assert.Equal("Start mode: at logon · running", _vm.StartModeText);

        _task.Info = Exists(StartMode.Startup);
        await _vm.LoadTaskInfoAsync();
        Assert.Equal("Start mode: at startup · ready", _vm.StartModeText);

        _task.Info = Exists(StartMode.Logon, enabled: false);
        await _vm.LoadTaskInfoAsync();
        Assert.Equal("Start mode: at logon · disabled", _vm.StartModeText);

        _task.Info = new HostTaskInfo(HostTaskState.Unknown, StartMode.Unknown, TaskPath) { Error = "The service is not available" };
        await _vm.LoadTaskInfoAsync();
        Assert.Equal("Start mode: unknown (the task could not be read: The service is not available)", _vm.StartModeText);
    }

    [Fact]
    public async Task A_changed_password_shows_re_enter_and_its_button()
    {
        _task.Info = Exists(StartMode.Startup, HostTaskDefinition.LogonFailure);
        await _vm.LoadTaskInfoAsync();

        Assert.Equal("Start mode: at startup · re-enter your password", _vm.StartModeText);
        Assert.True(_vm.ShowReenterPassword);

        _task.Info = Exists(StartMode.Startup);
        await _vm.LoadTaskInfoAsync();
        Assert.False(_vm.ShowReenterPassword);
    }

    [Fact]
    public async Task Re_enter_password_reruns_the_startup_flow_and_starts_the_host_that_could_not_start()
    {
        _task.Info = Exists(StartMode.Startup, HostTaskDefinition.LogonFailure);
        await _vm.LoadTaskInfoAsync();
        _switcher.OnSwitch = _ => _task.Info = Exists(StartMode.Startup, running: true);

        await _vm.ReenterPasswordAsync();

        // The host could not start, so it is not connected: the task runs again at once.
        Assert.Equal([(StartMode.Startup, true)], _switcher.Calls);
        Assert.False(_vm.ShowReenterPassword);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public async Task Re_enter_password_failures_are_shown_and_a_cancel_is_quiet()
    {
        _switcher.Result = new SwitchResult(SwitchOutcome.Failed, "it broke");
        await _vm.ReenterPasswordAsync();
        Assert.Equal("it broke", Assert.Single(_dialogs.Errors).Message);

        _switcher.Result = new SwitchResult(SwitchOutcome.Cancelled, "Cancelled");
        await _vm.ReenterPasswordAsync();
        Assert.Single(_dialogs.Errors);
    }

    [Fact]
    public async Task First_run_without_a_task_offers_to_create_the_logon_task()
    {
        _switcher.OnSwitch = _ => _task.Info = Exists(StartMode.Logon, running: true);

        await _vm.StartupCheckAsync();

        var (title, message) = Assert.Single(_dialogs.Confirms);
        Assert.Contains("logon", title + message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no administrator", message, StringComparison.OrdinalIgnoreCase);
        // No host answers yet, so the new task is run straight away.
        Assert.Equal([(StartMode.Logon, true)], _switcher.Calls);
        Assert.Equal("Start mode: at logon · running", _vm.StartModeText);
    }

    [Fact]
    public async Task First_run_yes_also_starts_this_app_at_sign_in()
    {
        await _vm.StartupCheckAsync();

        var (_, message) = Assert.Single(_dialogs.Confirms);
        Assert.Contains("this app", message, StringComparison.Ordinal);
        Assert.Contains("Settings", message, StringComparison.Ordinal);
        Assert.Equal("\"" + TrayExe + "\" --background", _startup.Run["claude-rc-windows"]);
    }

    [Fact]
    public async Task First_run_no_does_not_start_the_app_at_sign_in()
    {
        _dialogs.ConfirmAnswer = false;
        await _vm.StartupCheckAsync();
        Assert.Empty(_startup.Run);
    }

    [Fact]
    public async Task First_run_sign_in_start_that_fails_is_shown_and_the_task_is_still_created()
    {
        _startup.Error = new UnauthorizedAccessException("Access to the registry key is denied.");

        await _vm.StartupCheckAsync();

        Assert.Contains("Access to the registry key is denied.", Assert.Single(_dialogs.Errors).Message, StringComparison.Ordinal);
        Assert.Equal(StartMode.Logon, Assert.Single(_switcher.Calls).Mode);
    }

    [Fact]
    public async Task First_run_with_a_host_already_running_creates_the_task_without_restarting_it()
    {
        await Connect();
        await _vm.StartupCheckAsync();
        Assert.Equal([(StartMode.Logon, false)], _switcher.Calls);
    }

    [Fact]
    public async Task First_run_offer_declined_creates_nothing_and_is_not_repeated_in_this_run()
    {
        _dialogs.ConfirmAnswer = false;

        await _vm.StartupCheckAsync();
        await _vm.StartupCheckAsync();

        Assert.Single(_dialogs.Confirms);
        Assert.Empty(_switcher.Calls);
        Assert.Equal("Start mode: no scheduled task", _vm.StartModeText);
    }

    [Fact]
    public async Task First_run_offer_failure_is_shown()
    {
        _switcher.Result = new SwitchResult(SwitchOutcome.Failed, "ClaudeRcHost.exe was not found");
        await _vm.StartupCheckAsync();
        Assert.Contains("ClaudeRcHost.exe was not found", Assert.Single(_dialogs.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_existing_or_unreadable_task_is_not_offered()
    {
        _task.Info = Exists(StartMode.Startup);
        await _vm.StartupCheckAsync();
        _task.Info = new HostTaskInfo(HostTaskState.Unknown, StartMode.Unknown, TaskPath);
        await _vm.StartupCheckAsync();

        Assert.Empty(_dialogs.Confirms);
        Assert.Empty(_switcher.Calls);
    }

    [Fact]
    public async Task Start_host_with_a_missing_task_says_so_and_does_not_start_anything_else()
    {
        _task.RunResult = StartHostResult.Missing(@"\claude-rc-windows\Host");

        await _vm.StartHostAsync();

        Assert.Equal(1, _task.Runs);
        var error = Assert.Single(_dialogs.Errors);
        Assert.Contains(@"\claude-rc-windows\Host", error.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_host_that_ran_the_task_reconnects_at_once()
    {
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.NotRunning, "not running");
        _task.RunResult = StartHostResult.Started;
        _connector.Enqueue(new FakeHostClient());

        await _vm.StartHostAsync();

        Assert.Empty(_dialogs.Errors);
        await Eventually.True(() => _vm.IsHostConnected, "connected without advancing the clock");
    }

    [Fact]
    public async Task Turning_off_a_running_folder_asks_and_does_nothing_when_declined()
    {
        var client = await Connect(TestRows.Make(@"C:\a", FolderStatus.Running));
        _dialogs.ConfirmAnswer = false;

        await _vm.ToggleAsync(Row(@"C:\a"));

        var confirm = Assert.Single(_dialogs.Confirms);
        Assert.Contains("stops the running claude rc session; it won't come back at logon/boot", confirm.Message, StringComparison.Ordinal);
        Assert.Empty(client.Sent<SetRequest>());
    }

    [Fact]
    public async Task Turning_off_a_running_folder_sends_set_off_when_confirmed()
    {
        var client = await Connect(TestRows.Make(@"C:\a", FolderStatus.Running));

        await _vm.ToggleAsync(Row(@"C:\a"));

        var set = Assert.Single(client.Sent<SetRequest>());
        Assert.Equal(@"C:\a", set.Path);
        Assert.False(set.On);
    }

    [Fact]
    public async Task Turning_off_a_restarting_folder_does_not_ask()
    {
        var client = await Connect(TestRows.Make(@"C:\a", FolderStatus.Restarting, exitCode: 1, restartInS: 5));
        await _vm.ToggleAsync(Row(@"C:\a"));
        Assert.Empty(_dialogs.Confirms);
        Assert.False(Assert.Single(client.Sent<SetRequest>()).On);
    }

    [Fact]
    public async Task Turning_on_does_not_ask()
    {
        var client = await Connect(TestRows.Make(@"C:\a", FolderStatus.Off, on: false));
        await _vm.ToggleAsync(Row(@"C:\a"));
        Assert.Empty(_dialogs.Confirms);
        Assert.True(Assert.Single(client.Sent<SetRequest>()).On);
    }

    [Fact]
    public async Task A_refused_request_shows_the_hosts_error()
    {
        var client = await Connect(TestRows.Make(@"C:\a", FolderStatus.Off, on: false));
        client.Handler = _ => new IpcResponse { Ok = false, Error = "folders.json is corrupt" };
        await _vm.ToggleAsync(Row(@"C:\a"));
        Assert.Equal("folders.json is corrupt", Assert.Single(_dialogs.Errors).Message);
    }

    [Fact]
    public async Task Take_over_confirms_and_shows_the_hosts_error()
    {
        var client = await Connect(TestRows.Make(@"C:\a", FolderStatus.External, on: false, saved: false));
        client.Handler = _ => new IpcResponse { Ok = false, Error = "no claude rc started outside the app is running in this folder" };

        await _vm.TakeOverAsync(Row(@"C:\a"));

        Assert.Contains("stops the claude rc you started yourself and restarts it inside the app", Assert.Single(_dialogs.Confirms).Message, StringComparison.Ordinal);
        Assert.Equal(@"C:\a", Assert.Single(client.Sent<TakeoverRequest>()).Path);
        Assert.Contains("started outside the app", Assert.Single(_dialogs.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Take_over_accepted_shows_no_error()
    {
        var client = await Connect(TestRows.Make(@"C:\a", FolderStatus.External, on: false, saved: false));
        await _vm.TakeOverAsync(Row(@"C:\a"));
        Assert.Single(client.Sent<TakeoverRequest>());
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public async Task Turning_on_an_unsaved_external_row_saves_it_without_asking()
    {
        var client = await Connect(TestRows.Make(@"C:\a", FolderStatus.External, on: false, saved: false));

        await _vm.ToggleAsync(Row(@"C:\a"));

        Assert.Empty(_dialogs.Confirms);
        Assert.Equal(new SetRequest(@"C:\a", true), Assert.Single(client.Sent<SetRequest>()) with { Id = 0 });
    }

    [Fact]
    public async Task Turning_off_an_on_external_row_asks_nothing_because_nothing_is_stopped()
    {
        var client = await Connect(TestRows.Make(@"C:\a", FolderStatus.External, on: true, saved: true));

        await _vm.ToggleAsync(Row(@"C:\a"));

        Assert.Empty(_dialogs.Confirms);
        Assert.False(Assert.Single(client.Sent<SetRequest>()).On);
    }

    [Fact]
    public async Task Take_over_declined_sends_nothing()
    {
        var client = await Connect(TestRows.Make(@"C:\a", FolderStatus.External));
        _dialogs.ConfirmAnswer = false;
        await _vm.TakeOverAsync(Row(@"C:\a"));
        Assert.Empty(client.Sent<TakeoverRequest>());
    }

    [Fact]
    public async Task Remove_confirms_and_sends_remove()
    {
        var client = await Connect(TestRows.Make(@"C:\a", FolderStatus.Off, on: false));
        await _vm.RemoveAsync(Row(@"C:\a"));
        Assert.Single(_dialogs.Confirms);
        Assert.Equal(@"C:\a", Assert.Single(client.Sent<RemoveRequest>()).Path);
    }

    [Fact]
    public async Task Remove_is_refused_locally_while_on()
    {
        var client = await Connect(TestRows.Make(@"C:\a", FolderStatus.Running));
        await _vm.RemoveAsync(Row(@"C:\a"));
        Assert.Empty(_dialogs.Confirms);
        Assert.Empty(client.Sent<RemoveRequest>());
    }

    [Fact]
    public async Task Restart_sends_restart()
    {
        var client = await Connect(TestRows.Make(@"C:\a", FolderStatus.Failing, exitCode: 1));
        await _vm.RestartAsync(Row(@"C:\a"));
        Assert.Equal(@"C:\a", Assert.Single(client.Sent<RestartRequest>()).Path);
    }

    [Fact]
    public async Task Row_menu_actions_go_to_the_shell_and_windows()
    {
        await Connect(TestRows.Make(@"C:\a", FolderStatus.Running));
        var row = Row(@"C:\a");
        _vm.OpenInExplorer(row);
        _vm.OpenInTerminal(row);
        _vm.ViewLog(row);
        _vm.Show(row);
        Assert.Equal([@"C:\a"], _shell.Explorer);
        Assert.Equal([@"C:\a"], _shell.Terminal);
        Assert.Equal([@"C:\a"], _windows.Logs);
        Assert.Equal([@"C:\a"], _windows.Consoles);
    }

    [Fact]
    public async Task Show_does_nothing_for_an_external_row()
    {
        await Connect(TestRows.Make(@"C:\a", FolderStatus.External));
        _vm.Show(Row(@"C:\a"));
        Assert.Empty(_windows.Consoles);
    }

    [Fact]
    public async Task Row_commands_call_the_main_view_model()
    {
        var client = await Connect(TestRows.Make(@"C:\a", FolderStatus.Off, on: false));
        await Row(@"C:\a").ToggleCommand.ExecuteAsync(null);
        Assert.True(Assert.Single(client.Sent<SetRequest>()).On);
    }

    [Fact]
    public async Task Add_folder_sends_add()
    {
        var client = await Connect();
        _dialogs.FolderToPick = @"C:\new";
        await _vm.AddFolderAsync();
        Assert.Empty(_dialogs.Confirms);
        Assert.Equal(@"C:\new", Assert.Single(client.Sent<AddRequest>()).Path);
    }

    [Fact]
    public async Task Add_folder_cancelled_sends_nothing()
    {
        var client = await Connect();
        _dialogs.FolderToPick = null;
        await _vm.AddFolderAsync();
        Assert.Empty(client.Sent<AddRequest>());
    }

    [Fact]
    public async Task Add_network_folder_warns_it_may_not_exist_at_boot()
    {
        var client = await Connect();
        _dialogs.FolderToPick = @"Z:\share";
        _networkPaths.Add(@"Z:\share");
        _dialogs.ConfirmAnswer = false;

        await _vm.AddFolderAsync();

        Assert.Contains("may not exist yet at boot", Assert.Single(_dialogs.Confirms).Message, StringComparison.Ordinal);
        Assert.Empty(client.Sent<AddRequest>());

        _dialogs.ConfirmAnswer = true;
        await _vm.AddFolderAsync();
        Assert.Single(client.Sent<AddRequest>());
    }

    [Fact]
    public async Task Add_folder_without_a_host_shows_an_error()
    {
        _dialogs.FolderToPick = @"C:\new";
        await _vm.AddFolderAsync();
        Assert.Equal("Host not running", Assert.Single(_dialogs.Errors).Message);
    }

    [Fact]
    public async Task Tick_runs_the_restarting_countdown()
    {
        await Connect(TestRows.Make(@"C:\a", FolderStatus.Restarting, exitCode: 1, restartInS: 5));
        _time.Advance(TimeSpan.FromSeconds(2));
        _vm.Tick();
        Assert.Equal("restarting in 3s (exit code 1)", Row(@"C:\a").StatusText);
    }

    [Fact]
    public async Task Empty_list_hint_shows_only_when_connected_without_rows()
    {
        Assert.False(_vm.ShowEmptyHint);
        var client = await Connect();
        Assert.True(_vm.ShowEmptyHint);
        client.Push(new RowsEvent([TestRows.Make(@"C:\a", FolderStatus.Running)]));
        await Eventually.True(() => _vm.Rows.Count == 1, "row");
        Assert.False(_vm.ShowEmptyHint);
    }

    [Fact]
    public async Task Host_down_hint_explains_the_empty_list()
    {
        Assert.Null(_vm.HostDownHint);
        var client = await Connect(TestRows.Make(@"C:\a", FolderStatus.Running));
        Assert.Null(_vm.HostDownHint);

        client.Close();
        await Eventually.True(() => _vm.HostDownHint is not null, "hint");
        Assert.Contains("Start host", _vm.HostDownHint, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_command_opens_settings()
    {
        _vm.SettingsCommand.Execute(null);
        Assert.Equal(1, _windows.SettingsOpened);
    }

    [Fact]
    public async Task Focus_row_highlights_it_and_asks_the_window_to_show_it()
    {
        await Connect(TestRows.Make(@"C:\a", FolderStatus.Running), TestRows.Make(@"C:\b", FolderStatus.Failing));
        var asked = new List<FolderRowViewModel>();
        _vm.RowFocusRequested += (_, r) => asked.Add(r);

        _vm.FocusRow(@"c:\B");

        Assert.Equal([Row(@"C:\b")], asked);
        Assert.True(Row(@"C:\b").IsHighlighted);
        Assert.False(Row(@"C:\a").IsHighlighted);

        _vm.FocusRow(@"C:\a");
        Assert.True(Row(@"C:\a").IsHighlighted);
        Assert.False(Row(@"C:\b").IsHighlighted);
    }

    [Fact]
    public async Task Focus_row_of_an_unknown_folder_does_nothing()
    {
        await Connect(TestRows.Make(@"C:\a", FolderStatus.Running));
        var asked = 0;
        _vm.RowFocusRequested += (_, _) => asked++;

        _vm.FocusRow(@"C:\gone");

        Assert.Equal(0, asked);
        Assert.False(Row(@"C:\a").IsHighlighted);
    }

    [Fact]
    public async Task Focus_row_before_the_rows_have_loaded_focuses_it_once_they_arrive()
    {
        var asked = new List<FolderRowViewModel>();
        _vm.RowFocusRequested += (_, r) => asked.Add(r);

        // A toast click that started the tray: the click comes before the first list answer.
        _vm.FocusRow(@"c:\B");
        Assert.Empty(asked);

        await Connect(TestRows.Make(@"C:\a", FolderStatus.Running), TestRows.Make(@"C:\b", FolderStatus.Failing));

        await Eventually.True(() => asked.Count == 1, "focus requested");
        Assert.Same(Row(@"C:\b"), asked[0]);
        Assert.True(Row(@"C:\b").IsHighlighted);
        Assert.False(Row(@"C:\a").IsHighlighted);
    }

    [Fact]
    public async Task A_pending_focus_runs_only_once()
    {
        var asked = 0;
        _vm.RowFocusRequested += (_, _) => Interlocked.Increment(ref asked);
        _vm.FocusRow(@"C:\b");
        var client = await Connect(TestRows.Make(@"C:\b", FolderStatus.Failing));
        await Eventually.True(() => Volatile.Read(ref asked) == 1, "focus requested");

        client.Push(new RowsEvent([TestRows.Make(@"C:\b", FolderStatus.Running)]));
        await Eventually.True(() => Row(@"C:\b").Status == FolderStatus.Running, "updated");
        Assert.Equal(1, Volatile.Read(ref asked));
    }

    [Fact]
    public async Task A_pending_focus_expires()
    {
        var asked = 0;
        _vm.RowFocusRequested += (_, _) => Interlocked.Increment(ref asked);
        _vm.FocusRow(@"C:\b");
        _time.Advance(MainViewModel.PendingFocusTimeout + TimeSpan.FromSeconds(1));

        await Connect(TestRows.Make(@"C:\b", FolderStatus.Failing));

        Assert.Equal(0, Volatile.Read(ref asked));
        Assert.False(Row(@"C:\b").IsHighlighted);
    }

    [Fact]
    public async Task Rows_whose_paths_differ_only_in_case_do_not_break_the_list()
    {
        var client = await Connect(TestRows.Make(@"C:\a", FolderStatus.Running));

        client.Push(new RowsEvent([TestRows.Make(@"C:\a", FolderStatus.Running), TestRows.Make(@"c:\A", FolderStatus.Off, on: false)]));
        client.Push(new RowsEvent([TestRows.Make(@"C:\a", FolderStatus.Waiting), TestRows.Make(@"C:\b", FolderStatus.Running)]));

        await Eventually.True(() => _vm.Rows.Count == 2 && _vm.Rows[1].Path == @"C:\b", "later rows applied");
        Assert.Equal(FolderStatus.Waiting, Row(@"C:\a").Status);
        Assert.Equal(HostState.Connected, _connection.State);
    }
}
