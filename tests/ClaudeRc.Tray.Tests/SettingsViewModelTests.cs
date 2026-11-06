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
    private readonly HostConnection _connection;

    public SettingsViewModelTests()
    {
        _connection = new HostConnection(_connector, _time);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private async Task<(FakeHostClient Client, SettingsViewModel Vm)> Open(IpcSettings? settings = null)
    {
        var client = new FakeHostClient { Settings = settings ?? new IpcSettings(null, true) };
        _connector.Enqueue(client);
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.Connected, "connected");
        var vm = new SettingsViewModel(_connection, _task, _dialogs);
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

    [Fact]
    public async Task Start_mode_is_shown_but_cannot_be_changed_until_m4()
    {
        var (_, vm) = await Open();
        Assert.Equal("unknown", vm.StartModeText);
        Assert.False(vm.CanChangeStartMode);
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
        var vm = new SettingsViewModel(_connection, _task, _dialogs);
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
        using var vm = new SettingsViewModel(_connection, _task, _dialogs);
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
