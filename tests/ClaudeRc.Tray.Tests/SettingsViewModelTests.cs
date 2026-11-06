using ClaudeRc.Core.Ipc;
using ClaudeRc.Tray.Hosting;
using ClaudeRc.Tray.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace ClaudeRc.Tray.Tests;

public sealed class SettingsViewModelTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeConnector _connector = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeHostTask _task = new();
    private readonly FakeSwitcher _switcher = new();
    private readonly FakeStartupRegistry _startup = new();
    private readonly SignInStart _signIn;
    private readonly HostConnection _connection;

    private const string TrayExe = @"C:\app\ClaudeRc.exe";
    private const string SignInCommand = "\"" + TrayExe + "\" --background";

    public SettingsViewModelTests()
    {
        _connection = new HostConnection(_connector, _time);
        _signIn = new SignInStart(_startup, TrayExe, null, path => path == TrayExe);
    }

    private async Task<SettingsViewModel> OpenWithoutHost()
    {
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.NotRunning, "not running");
        var vm = new SettingsViewModel(_connection, _task, _switcher, _dialogs, _signIn);
        await vm.LoadAsync();
        return vm;
    }

    // ---- start the app at sign-in (a tray setting: the Run value, with or without a host) ----

    [Fact]
    public async Task The_sign_in_checkbox_shows_the_run_value()
    {
        _startup.Run["claude-rc-windows"] = SignInCommand;
        var (_, vm) = await Open();

        Assert.True(vm.StartAtSignIn);
        Assert.Null(vm.SignInNote);
        Assert.False(vm.SignInChanged);
    }

    [Fact]
    public async Task Ticking_sign_in_start_and_saving_writes_this_exe()
    {
        var (client, vm) = await Open();
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        Assert.False(vm.StartAtSignIn);

        vm.StartAtSignIn = true;
        Assert.True(vm.SignInChanged);
        Assert.True(await vm.SaveAsync());

        Assert.Equal(SignInCommand, _startup.Run["claude-rc-windows"]);
        Assert.Single(client.Sent<SettingsSetRequest>());
        Assert.True(closed);
    }

    [Fact]
    public async Task Unticking_sign_in_start_and_saving_deletes_the_value()
    {
        _startup.Run["claude-rc-windows"] = SignInCommand;
        var (_, vm) = await Open();

        vm.StartAtSignIn = false;
        Assert.True(await vm.SaveAsync());

        Assert.Empty(_startup.Run);
    }

    [Fact]
    public async Task Sign_in_start_can_be_changed_without_a_host()
    {
        using var vm = await OpenWithoutHost();
        Assert.False(vm.CanEdit);
        Assert.True(vm.CanChangeSignIn);
        Assert.False(vm.CanSave);

        vm.StartAtSignIn = true;
        Assert.True(vm.CanSave);
        Assert.True(await vm.SaveAsync());

        Assert.Equal(SignInCommand, _startup.Run["claude-rc-windows"]);
        Assert.Empty(_switcher.Calls);
    }

    [Fact]
    public async Task A_value_for_a_missing_exe_is_shown_and_saving_fixes_it()
    {
        // A portable copy that was moved.
        _startup.Run["claude-rc-windows"] = "\"D:\\old\\ClaudeRc.exe\" --background";
        using var vm = await OpenWithoutHost();

        Assert.True(vm.StartAtSignIn);
        Assert.Contains(@"D:\old\ClaudeRc.exe", vm.SignInNote, StringComparison.Ordinal);
        // Untouched, Save still has something to do.
        Assert.True(vm.SignInChanged);
        Assert.True(vm.CanSave);

        Assert.True(await vm.SaveAsync());

        Assert.Equal(SignInCommand, _startup.Run["claude-rc-windows"]);
        Assert.Null(vm.SignInNote);
        Assert.False(vm.SignInChanged);
    }

    [Fact]
    public async Task Turned_off_in_windows_startup_apps_shows_unticked_with_a_note()
    {
        _startup.Run["claude-rc-windows"] = SignInCommand;
        _startup.TurnedOff.Add("claude-rc-windows");
        var (_, vm) = await Open();

        Assert.False(vm.StartAtSignIn);
        Assert.Contains("Startup apps", vm.SignInNote, StringComparison.Ordinal);
        Assert.False(vm.SignInChanged);

        vm.StartAtSignIn = true;
        Assert.True(await vm.SaveAsync());

        Assert.Empty(_startup.TurnedOff);
        Assert.True(vm.StartAtSignIn);
    }

    [Fact]
    public async Task A_registry_error_on_save_is_shown_and_the_window_stays_open()
    {
        var (_, vm) = await Open();
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.StartAtSignIn = true;
        _startup.Error = new UnauthorizedAccessException("Access to the registry key is denied.");

        Assert.False(await vm.SaveAsync());

        Assert.Contains("Access to the registry key is denied.", vm.ValidationError, StringComparison.Ordinal);
        Assert.False(closed);
    }

    [Fact]
    public async Task A_run_key_that_cannot_be_read_says_so()
    {
        _startup.Error = new UnauthorizedAccessException("Access to the registry key is denied.");
        var (_, vm) = await Open();

        Assert.False(vm.StartAtSignIn);
        Assert.Contains("Access to the registry key is denied.", vm.SignInNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_in_start_is_saved_before_a_start_mode_switch()
    {
        // A switch can fail (UAC declined); the sign-in start is kept anyway.
        _task.Info = Exists(StartMode.Logon);
        _switcher.Result = new SwitchResult(SwitchOutcome.Cancelled, "Cancelled.");
        var (_, vm) = await Open();
        vm.StartAtSignIn = true;
        vm.StartModeIsStartup = true;

        Assert.False(await vm.SaveAsync());

        Assert.Equal(SignInCommand, _startup.Run["claude-rc-windows"]);
        Assert.False(vm.SignInChanged);
    }

    [Fact]
    public async Task A_reconnect_keeps_the_ticked_sign_in_box()
    {
        var (client, vm) = await Open();
        using var owned = vm;
        vm.StartAtSignIn = true;

        client.Close();
        await Eventually.True(() => !vm.CanEdit, "read-only");
        _connector.Enqueue(new FakeHostClient());
        _connection.ReconnectNow();
        await Eventually.True(() => vm.CanEdit, "editable again");

        Assert.True(vm.StartAtSignIn);
        Assert.True(vm.SignInChanged);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private async Task<(FakeHostClient Client, SettingsViewModel Vm)> Open(IpcSettings? settings = null)
    {
        var client = new FakeHostClient { Settings = settings ?? new IpcSettings(null, true) };
        _connector.Enqueue(client);
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.Connected, "connected");
        var vm = new SettingsViewModel(_connection, _task, _switcher, _dialogs, _signIn);
        await vm.LoadAsync();
        return (client, vm);
    }

    [Fact]
    public async Task Loads_settings_and_read_only_info()
    {
        var (_, vm) = await Open(new IpcSettings(@"C:\tools\claude.exe", false));

        Assert.True(vm.CanEdit);
        Assert.Equal(@"C:\tools\claude.exe", vm.ClaudePathOverride);
        Assert.False(vm.Notifications);
        Assert.Equal(@"C:\data", vm.DataDir);
        Assert.Equal(@"C:\logs", vm.LogDir);
        Assert.Equal("1.2.3", vm.HostVersion);
        Assert.Equal(@"C:\bin\claude.exe · 2.1.318 (Claude Code)", vm.DetectedClaude);
        Assert.Equal(@"missing (\claude-rc-windows\Host)", vm.TaskStatusText);
    }

    private const string TaskPath = @"\claude-rc-windows\Host";

    private static HostTaskInfo Exists(StartMode mode, int? lastResult = 0) =>
        new(HostTaskState.Exists, mode, TaskPath) { LastResult = lastResult };

    [Fact]
    public async Task The_current_start_mode_is_selected_and_can_be_changed()
    {
        _task.Info = Exists(StartMode.Logon);
        var (_, vm) = await Open();

        Assert.Equal("at logon", vm.StartModeText);
        Assert.True(vm.StartModeIsLogon);
        Assert.False(vm.StartModeIsStartup);
        Assert.True(vm.CanChangeStartMode);
        Assert.False(vm.StartModeChanged);
    }

    [Fact]
    public async Task An_unreadable_task_cannot_be_switched()
    {
        _task.Info = new HostTaskInfo(HostTaskState.Unknown, StartMode.Unknown, TaskPath);
        var (_, vm) = await Open();
        Assert.False(vm.CanChangeStartMode);
    }

    [Fact]
    public async Task Save_with_an_unchanged_mode_does_not_touch_the_task()
    {
        _task.Info = Exists(StartMode.Logon);
        var (client, vm) = await Open();

        Assert.True(await vm.SaveAsync());

        Assert.Empty(_switcher.Calls);
        Assert.Single(client.Sent<SettingsSetRequest>());
    }

    [Fact]
    public async Task Switching_to_startup_asks_whether_to_restart_the_host_and_switches()
    {
        _task.Info = Exists(StartMode.Logon);
        _switcher.OnSwitch = m => _task.Info = Exists(m);
        var (client, vm) = await Open();
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.StartModeIsStartup = true;
        Assert.True(vm.StartModeChanged);
        Assert.False(vm.StartModeIsLogon);
        Assert.True(await vm.SaveAsync());

        var (_, question) = Assert.Single(_dialogs.Confirms);
        Assert.Contains("Restart the host now", question, StringComparison.Ordinal);
        Assert.Equal([(StartMode.Startup, true)], _switcher.Calls);
        // The settings went to the host before the host was restarted.
        Assert.Single(client.Sent<SettingsSetRequest>());
        Assert.True(closed);
        Assert.Equal("at startup", vm.StartModeText);
    }

    [Fact]
    public async Task Not_restarting_switches_for_the_next_start_only()
    {
        _task.Info = Exists(StartMode.Startup);
        _dialogs.ConfirmAnswer = false;
        var (_, vm) = await Open();

        vm.StartModeIsLogon = true;
        Assert.True(await vm.SaveAsync());

        Assert.Equal([(StartMode.Logon, false)], _switcher.Calls);
    }

    [Fact]
    public async Task A_cancelled_switch_keeps_the_window_open_with_the_reason()
    {
        _task.Info = Exists(StartMode.Logon);
        _switcher.Result = new SwitchResult(SwitchOutcome.Cancelled, "Cancelled: the start mode was not changed.");
        var (_, vm) = await Open();
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.StartModeIsStartup = true;
        Assert.False(await vm.SaveAsync());

        Assert.False(closed);
        Assert.Equal("Cancelled: the start mode was not changed.", vm.ValidationError);
        Assert.True(vm.StartModeIsStartup); // the choice stays, so Save can try again
    }

    [Fact]
    public async Task A_failed_switch_shows_the_reason()
    {
        _task.Info = Exists(StartMode.Logon);
        _switcher.Result = new SwitchResult(SwitchOutcome.Failed, "it broke");
        var (_, vm) = await Open();
        vm.StartModeIsStartup = true;

        Assert.False(await vm.SaveAsync());
        Assert.Equal("it broke", vm.ValidationError);
    }

    [Fact]
    public async Task A_note_from_a_done_switch_is_shown()
    {
        _task.Info = Exists(StartMode.Logon);
        _switcher.Result = new SwitchResult(SwitchOutcome.Done, "could not restart");
        var (_, vm) = await Open();
        vm.StartModeIsStartup = true;

        Assert.True(await vm.SaveAsync());
        Assert.Equal("could not restart", Assert.Single(_dialogs.Infos).Message);
    }

    [Fact]
    public async Task An_invalid_claude_path_stops_the_save_before_the_switch()
    {
        _task.Info = Exists(StartMode.Logon);
        var (_, vm) = await Open();
        vm.StartModeIsStartup = true;
        vm.ClaudePathOverride = "claude.bat";

        Assert.False(await vm.SaveAsync());
        Assert.Empty(_switcher.Calls);
    }

    [Fact]
    public async Task Without_a_host_the_start_mode_can_still_be_switched_and_the_task_is_run()
    {
        _task.Info = Exists(StartMode.Logon);
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.NotRunning, "not running");
        using var vm = new SettingsViewModel(_connection, _task, _switcher, _dialogs, _signIn);
        await vm.LoadAsync();
        Assert.False(vm.CanEdit);
        Assert.True(vm.CanChangeStartMode);
        Assert.False(vm.CanSave);

        vm.StartModeIsStartup = true;
        Assert.True(vm.CanSave);
        Assert.True(await vm.SaveAsync());

        // No host to restart: nothing is asked, and the task runs at once.
        Assert.Empty(_dialogs.Confirms);
        Assert.Equal([(StartMode.Startup, true)], _switcher.Calls);
    }

    [Fact]
    public async Task With_no_task_choosing_a_mode_creates_it()
    {
        var (_, vm) = await Open();
        Assert.False(vm.StartModeIsLogon);
        Assert.False(vm.StartModeIsStartup);
        Assert.True(vm.CanChangeStartMode);

        vm.StartModeIsLogon = true;
        Assert.True(vm.StartModeChanged);
        Assert.True(await vm.SaveAsync());
        Assert.Equal(StartMode.Logon, Assert.Single(_switcher.Calls).Mode);
    }

    [Fact]
    public async Task A_changed_password_shows_re_enter_which_reruns_the_startup_flow()
    {
        _task.Info = Exists(StartMode.Startup, HostTaskDefinition.LogonFailure);
        _switcher.OnSwitch = m => _task.Info = Exists(m);
        var (_, vm) = await Open();
        Assert.True(vm.NeedsPassword);

        await vm.ReenterPasswordAsync();

        Assert.Equal(StartMode.Startup, Assert.Single(_switcher.Calls).Mode);
        Assert.False(vm.NeedsPassword);
    }

    [Fact]
    public async Task Empty_override_saves_null_for_auto_detect()
    {
        var (client, vm) = await Open(new IpcSettings(@"C:\tools\claude.exe", true));
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.ClaudePathOverride = "  ";
        vm.Notifications = false;

        Assert.True(await vm.SaveAsync());

        var set = Assert.Single(client.Sent<SettingsSetRequest>());
        Assert.Null(set.ClaudePath);
        Assert.False(set.Notifications);
        Assert.True(closed);
    }

    [Theory]
    [InlineData(@"C:\tools\claude.exe", @"C:\tools\claude.exe")]
    [InlineData(@"  C:\npm\claude.CMD ", @"C:\npm\claude.CMD")]
    [InlineData("\"C:\\Program Files\\claude\\claude.exe\"", @"C:\Program Files\claude\claude.exe")]
    public async Task A_full_exe_or_cmd_path_is_saved(string text, string saved)
    {
        var (client, vm) = await Open();
        vm.ClaudePathOverride = text;
        Assert.True(await vm.SaveAsync());
        Assert.Equal(saved, Assert.Single(client.Sent<SettingsSetRequest>()).ClaudePath);
    }

    [Theory]
    [InlineData("claude.exe", "full path")]
    [InlineData(@"C:\tools\claude.bat", "claude.exe or claude.cmd")]
    [InlineData(@"C:\tools\claude", "claude.exe or claude.cmd")]
    public async Task An_invalid_override_is_refused_before_sending(string text, string error)
    {
        var (client, vm) = await Open();
        vm.ClaudePathOverride = text;

        Assert.False(await vm.SaveAsync());

        Assert.Contains(error, vm.ValidationError, StringComparison.Ordinal);
        Assert.Empty(client.Sent<SettingsSetRequest>());
    }

    [Fact]
    public async Task The_hosts_refusal_is_shown_and_the_window_stays_open()
    {
        var (client, vm) = await Open();
        client.Handler = r => r is SettingsSetRequest
            ? new IpcResponse { Ok = false, Error = "claude path from Settings must be claude.exe or claude.cmd" }
            : new IpcResponse { Ok = true, Settings = client.Settings, Info = client.Info, Rows = [] };
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.ClaudePathOverride = @"C:\x\claude.exe";

        Assert.False(await vm.SaveAsync());

        Assert.Equal("claude path from Settings must be claude.exe or claude.cmd", vm.ValidationError);
        Assert.False(closed);
    }

    [Fact]
    public async Task Browse_fills_in_the_picked_file()
    {
        var (_, vm) = await Open();
        _dialogs.FileToPick = @"C:\picked\claude.exe";
        vm.BrowseCommand.Execute(null);
        Assert.Equal(@"C:\picked\claude.exe", vm.ClaudePathOverride);
    }

    [Fact]
    public async Task Without_a_host_settings_cannot_be_edited()
    {
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.NotRunning, "not running");
        var vm = new SettingsViewModel(_connection, _task, _switcher, _dialogs, _signIn);
        await vm.LoadAsync();

        Assert.False(vm.CanEdit);
        Assert.Contains("Host not running", vm.ValidationError, StringComparison.Ordinal);
        Assert.False(await vm.SaveAsync());
    }

    [Fact]
    public async Task Opened_without_a_host_it_loads_once_the_host_connects()
    {
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.NotRunning, "not running");
        using var vm = new SettingsViewModel(_connection, _task, _switcher, _dialogs, _signIn);
        await vm.LoadAsync();
        Assert.False(vm.CanEdit);

        _connector.Enqueue(new FakeHostClient { Settings = new IpcSettings(@"C:\tools\claude.exe", false) });
        _connection.ReconnectNow();

        await Eventually.True(() => vm.CanEdit, "editable after the host connected");
        Assert.Equal(@"C:\tools\claude.exe", vm.ClaudePathOverride);
        Assert.False(vm.Notifications);
        Assert.Null(vm.ValidationError);
        Assert.Equal("1.2.3", vm.HostVersion);
    }

    [Fact]
    public async Task When_the_host_goes_the_window_becomes_read_only_and_says_so()
    {
        var (client, vm) = await Open();
        using var owned = vm;
        Assert.True(vm.CanEdit);

        client.Close();

        await Eventually.True(() => !vm.CanEdit, "read-only");
        Assert.Contains("Host not running", vm.ValidationError, StringComparison.Ordinal);
        Assert.False(await vm.SaveAsync());
    }

    [Fact]
    public async Task A_reconnect_keeps_what_the_user_typed()
    {
        var (client, vm) = await Open(new IpcSettings(@"C:\tools\claude.exe", true));
        using var owned = vm;
        vm.ClaudePathOverride = @"C:\typed\claude.exe";
        vm.Notifications = false;

        client.Close();
        await Eventually.True(() => !vm.CanEdit, "read-only");
        _connector.Enqueue(new FakeHostClient { Settings = new IpcSettings(@"C:\tools\claude.exe", true) });
        _connection.ReconnectNow();

        await Eventually.True(() => vm.CanEdit, "editable again");
        Assert.Equal(@"C:\typed\claude.exe", vm.ClaudePathOverride);
        Assert.False(vm.Notifications);
        Assert.Null(vm.ValidationError);
    }

    [Fact]
    public async Task After_dispose_the_connection_is_not_followed()
    {
        var (client, vm) = await Open();
        vm.Dispose();

        client.Close();
        await Eventually.True(() => _connection.State == HostState.NotRunning, "not running");

        Assert.True(vm.CanEdit);
    }
}
