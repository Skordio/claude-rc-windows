using ClaudeRc.Core.Ipc;
using ClaudeRc.Tray.Hosting;
using ClaudeRc.Tray.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace ClaudeRc.Tray.Tests;

public sealed class LogViewModelTests : IAsyncDisposable
{
    private readonly FakeConnector _connector = new();
    private readonly HostConnection _connection;

    public LogViewModelTests()
    {
        _connection = new HostConnection(_connector, new FakeTimeProvider());
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Loads_the_last_lines_of_the_folder_log()
    {
        var client = new FakeHostClient();
        client.Handler = r => r switch
        {
            LogRequest => new IpcResponse { Ok = true, Lines = ["# folder: C:\\a", "2026-11-06T10:00:00 started"] },
            SettingsGetRequest => new IpcResponse { Ok = true, Settings = client.Settings, Info = client.Info },
            _ => new IpcResponse { Ok = true, Rows = [] },
        };
        _connector.Enqueue(client);
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.Connected, "connected");
        var vm = new LogViewModel(_connection, @"C:\a", "a");

        await vm.LoadAsync();

        var request = Assert.Single(client.Sent<LogRequest>());
        Assert.Equal(@"C:\a", request.Path);
        Assert.Equal(LogViewModel.LineCount, request.Lines);
        Assert.Equal("# folder: C:\\a\n2026-11-06T10:00:00 started", vm.Text);
        Assert.Equal("Log: a", vm.Title);
    }

    [Fact]
    public async Task Shows_the_error_when_there_is_no_host()
    {
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.NotRunning, "not running");
        var vm = new LogViewModel(_connection, @"C:\a", "a");

        await vm.LoadAsync();

        Assert.Equal("Host not running", vm.Text);
    }
}
