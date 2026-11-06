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
    private readonly FakeShell _shell = new();
    private readonly FakeWindows _windows = new();
    private readonly HostConnection _connection;
    private readonly HashSet<string> _networkPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly MainViewModel _vm;

    public MainViewModelTests()
    {
        _connection = new HostConnection(_connector, _time);
        _vm = new MainViewModel(_connection, _task, _dialogs, _shell, _windows, _time, _networkPaths.Contains);
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
        Assert.Equal(@"claude: C:\bin\claude.exe · 2.1.318 (Claude Code)", _vm.ClaudeText);
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

    [Fact]
    public async Task Start_mode_comes_from_the_task_and_is_unknown_until_m4()
    {
        await _vm.LoadTaskInfoAsync();
        Assert.Equal("Start mode: unknown", _vm.StartModeText);

        _task.Info = _task.Info with { StartMode = StartMode.Logon };
        await _vm.LoadTaskInfoAsync();
        Assert.Equal("Start mode: at logon", _vm.StartModeText);
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
    public async Task Take_over_confirms_and_shows_the_not_implemented_error()
    {
        var client = await Connect(TestRows.Make(@"C:\a", FolderStatus.External, on: false, saved: false));
        client.Handler = _ => new IpcResponse { Ok = false, Error = "take over is not available yet: it comes with adoption of external sessions in M3" };

        await _vm.TakeOverAsync(Row(@"C:\a"));

        Assert.Contains("stops the claude rc you started yourself and restarts it inside the app", Assert.Single(_dialogs.Confirms).Message, StringComparison.Ordinal);
        Assert.Single(client.Sent<TakeoverRequest>());
        Assert.Contains("not available yet", Assert.Single(_dialogs.Errors).Message, StringComparison.Ordinal);
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
}
