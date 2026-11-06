using ClaudeRc.Core;
using ClaudeRc.Core.Ipc;
using ClaudeRc.Tray.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace ClaudeRc.Tray.Tests;

public sealed class HostConnectionTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeConnector _connector = new();
    private readonly HostConnection _connection;

    public HostConnectionTests()
    {
        _connection = new HostConnection(_connector, _time);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    /// <summary>Moves the fake clock until the loop has made <paramref name="attempts"/> connection attempts.</summary>
    private async Task AdvanceUntilAttempts(int attempts)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (_connector.Attempts < attempts)
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail($"only {_connector.Attempts} attempts, wanted {attempts}");
            _time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Connects_and_loads_rows_settings_and_info()
    {
        var client = new FakeHostClient { Rows = [TestRows.Make(@"C:\a", FolderStatus.Running)] };
        _connector.Enqueue(client);
        _connection.Start();

        await Eventually.True(() => _connection.State == HostState.Connected, "connected");
        Assert.Equal("Host connected", _connection.StateText);
        Assert.Single(_connection.Rows);
        Assert.Equal(@"C:\a", _connection.Rows[0].Path);
        Assert.Equal("1.2.3", _connection.Info?.HostVersion);
        Assert.True(_connection.Settings?.Notifications);
        Assert.Single(client.Sent<ListRequest>());
        Assert.Single(client.Sent<SettingsGetRequest>());
    }

    [Fact]
    public async Task Rows_events_replace_the_rows()
    {
        var client = new FakeHostClient { Rows = [TestRows.Make(@"C:\a", FolderStatus.Running)] };
        _connector.Enqueue(client);
        var rowsChanged = 0;
        _connection.RowsChanged += (_, _) => Interlocked.Increment(ref rowsChanged);
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.Connected, "connected");

        client.Push(new RowsEvent([TestRows.Make(@"C:\a", FolderStatus.Off, on: false), TestRows.Make(@"C:\b", FolderStatus.Starting)]));

        await Eventually.True(() => _connection.Rows.Count == 2, "two rows");
        Assert.Equal(FolderStatus.Off, _connection.Rows[0].Status);
        Assert.True(Volatile.Read(ref rowsChanged) >= 2);
    }

    [Fact]
    public async Task Toast_events_are_forwarded()
    {
        var client = new FakeHostClient();
        _connector.Enqueue(client);
        ToastEvent? toast = null;
        _connection.Toast += (_, e) => toast = e;
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.Connected, "connected");

        client.Push(new ToastEvent(@"C:\a", FolderStatus.Failing, "a: failing"));

        await Eventually.True(() => toast is not null, "toast");
        Assert.Equal("a: failing", toast!.Text);
    }

    [Fact]
    public async Task No_host_means_not_running_and_keeps_retrying_with_backoff()
    {
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.NotRunning, "not running");
        Assert.Equal("Host not running", _connection.StateText);

        await AdvanceUntilAttempts(3);

        _connector.Enqueue(new FakeHostClient());
        await AdvanceUntilAttempts(4);
        await Eventually.True(() => _connection.State == HostState.Connected, "connected after host appears");
    }

    [Fact]
    public async Task Reconnect_now_skips_the_backoff_wait()
    {
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.NotRunning, "not running");
        var before = _connector.Attempts;

        _connector.Enqueue(new FakeHostClient());
        _connection.ReconnectNow();

        // No clock advance: only ReconnectNow can have woken the loop.
        await Eventually.True(() => _connection.State == HostState.Connected, "connected");
        Assert.Equal(before + 1, _connector.Attempts);
    }

    [Fact]
    public async Task Host_closing_clears_rows_and_reconnects()
    {
        var first = new FakeHostClient { Rows = [TestRows.Make(@"C:\a", FolderStatus.Running)] };
        _connector.Enqueue(first);
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.Connected, "connected");

        first.Close();
        await Eventually.True(() => _connection.State == HostState.NotRunning, "not running after close");
        Assert.Empty(_connection.Rows);
        Assert.Null(_connection.Info);
        Assert.True(first.Disposed);

        var second = new FakeHostClient { Rows = [TestRows.Make(@"C:\b", FolderStatus.Running)] };
        _connector.Enqueue(second);
        await AdvanceUntilAttempts(2);
        await Eventually.True(() => _connection.State == HostState.Connected && _connection.Rows.Count == 1, "reconnected");
        Assert.Equal(@"C:\b", _connection.Rows[0].Path);
    }

    [Fact]
    public async Task Version_mismatch_is_shown_with_the_spec_message()
    {
        var client = new FakeHostClient();
        _connector.Enqueue(client);
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.Connected, "connected");

        client.Close(IpcCodec.VersionMismatchMessage);

        await Eventually.True(() => _connection.State == HostState.VersionMismatch, "mismatch");
        Assert.Equal("host and app versions differ: restart the host", _connection.StateText);
    }

    [Fact]
    public async Task Version_mismatch_on_the_first_request_is_shown_and_stays_while_retrying()
    {
        var client = new FakeHostClient();
        client.Handler = _ =>
        {
            client.Close(IpcCodec.VersionMismatchMessage);
            throw new IpcClosedException(IpcCodec.VersionMismatchMessage);
        };
        _connector.Enqueue(client);
        _connection.Start();

        await Eventually.True(() => _connection.State == HostState.VersionMismatch, "mismatch");
        // A retry that finds no host says so; one that meets the same host again keeps the mismatch.
        var again = new FakeHostClient();
        again.Handler = _ =>
        {
            again.Close(IpcCodec.VersionMismatchMessage);
            throw new IpcClosedException(IpcCodec.VersionMismatchMessage);
        };
        _connector.Enqueue(again);
        await AdvanceUntilAttempts(2);
        await Eventually.True(() => again.Disposed, "second attempt done");
        Assert.Equal(HostState.VersionMismatch, _connection.State);
    }

    [Fact]
    public async Task Send_when_not_connected_returns_an_error()
    {
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.NotRunning, "not running");

        var result = await _connection.SendAsync(new SetRequest(@"C:\a", false));

        Assert.False(result.Ok);
        Assert.Equal("Host not running", result.Error);
    }

    [Fact]
    public async Task Send_returns_the_hosts_error()
    {
        var client = new FakeHostClient();
        client.Handler = r => r is TakeoverRequest
            ? new IpcResponse { Ok = false, Error = "take over is not available yet" }
            : new IpcResponse { Ok = true, Rows = [] , Settings = client.Settings, Info = client.Info };
        _connector.Enqueue(client);
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.Connected, "connected");

        var result = await _connection.SendAsync(new TakeoverRequest(@"C:\a"));

        Assert.False(result.Ok);
        Assert.Equal("take over is not available yet", result.Error);
    }

    [Fact]
    public async Task Refresh_settings_reloads_settings_and_info()
    {
        var client = new FakeHostClient();
        _connector.Enqueue(client);
        var changed = 0;
        _connection.SettingsChanged += (_, _) => Interlocked.Increment(ref changed);
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.Connected, "connected");

        client.Settings = new IpcSettings(@"C:\x\claude.exe", false);
        await _connection.RefreshSettingsAsync();

        Assert.Equal(@"C:\x\claude.exe", _connection.Settings?.ClaudePath);
        Assert.False(_connection.Settings?.Notifications);
        Assert.True(Volatile.Read(ref changed) >= 2);
    }
}

public class ReconnectBackoffTests
{
    [Fact]
    public void Doubles_from_half_a_second_up_to_five_seconds_and_resets()
    {
        var b = new ReconnectBackoff();
        var seconds = Enumerable.Range(0, 7).Select(_ => b.Next().TotalSeconds).ToArray();
        Assert.Equal([0.5, 1, 2, 4, 5, 5, 5], seconds);
        b.Reset();
        Assert.Equal(0.5, b.Next().TotalSeconds);
    }
}
