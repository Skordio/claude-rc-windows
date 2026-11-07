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

    [Fact]
    public async Task A_throwing_subscriber_reaches_neither_the_loop_nor_the_other_subscribers()
    {
        var client = new FakeHostClient { Rows = [TestRows.Make(@"C:\a", FolderStatus.Running)] };
        _connector.Enqueue(client);
        _connection.RowsChanged += (_, _) =>
        {
            if (_connection.Rows.Count == 2)
                throw new ArgumentException("a subscriber's bug");
        };
        _connection.Toast += (_, _) => throw new InvalidOperationException("a subscriber's bug");
        var seen = 0;
        var toasts = 0;
        _connection.RowsChanged += (_, _) => Interlocked.Increment(ref seen);
        _connection.Toast += (_, _) => Interlocked.Increment(ref toasts);
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.Connected, "connected");

        client.Push(new RowsEvent([TestRows.Make(@"C:\a", FolderStatus.Running), TestRows.Make(@"C:\b", FolderStatus.Off)]));
        client.Push(new ToastEvent(@"C:\a", FolderStatus.Failing, "a: failing"));
        client.Push(new RowsEvent([TestRows.Make(@"C:\c", FolderStatus.Running)]));

        await Eventually.True(() => _connection.Rows.Count == 1 && _connection.Rows[0].Path == @"C:\c", "later events handled");
        Assert.True(Volatile.Read(ref seen) >= 3);
        Assert.Equal(1, Volatile.Read(ref toasts));
        Assert.Equal(HostState.Connected, _connection.State);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task An_unexpected_error_ends_only_that_connection_and_the_loop_goes_on()
    {
        var broken = new FakeHostClient { Handler = _ => throw new InvalidOperationException("unexpected") };
        _connector.Enqueue(broken);
        _connection.Start();
        await Eventually.True(() => broken.Disposed, "broken connection closed");
        await Eventually.True(() => _connection.State == HostState.NotRunning, "not running");

        _connector.Enqueue(new FakeHostClient());
        await AdvanceUntilAttempts(2);
        await Eventually.True(() => _connection.State == HostState.Connected, "connected on the next attempt");
    }

    [Fact]
    public async Task An_unexpected_connect_error_is_retried()
    {
        var connector = new ThrowingOnceConnector(new InvalidOperationException("unexpected"), _connector);
        await using var connection = new HostConnection(connector, _time);
        _connector.Enqueue(new FakeHostClient());
        connection.Start();
        await Eventually.True(() => connector.Attempts >= 1, "first attempt");

        var deadline = Environment.TickCount64 + 5000;
        while (connection.State != HostState.Connected)
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail("not connected after the failed attempt");
            _time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(5);
        }
    }

    [Theory]
    [InlineData("disposed")]
    [InlineData("io")]
    [InlineData("canceled")]
    [InlineData("other")]
    public async Task Send_never_throws_when_the_connection_breaks(string kind)
    {
        var client = new FakeHostClient();
        client.Handler = r => r switch
        {
            ListRequest => new IpcResponse { Ok = true, Rows = [] },
            SettingsGetRequest => new IpcResponse { Ok = true, Settings = client.Settings, Info = client.Info },
            _ => throw kind switch
            {
                "disposed" => new ObjectDisposedException("IpcClient"),
                "io" => new IOException("pipe is broken"),
                "canceled" => new OperationCanceledException(),
                _ => new InvalidOperationException("odd"),
            },
        };
        _connector.Enqueue(client);
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.Connected, "connected");

        var result = await _connection.SendAsync(new InputRequest(@"C:\a", "x"));

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public async Task Send_cancelled_by_the_caller_still_throws()
    {
        var client = new FakeHostClient();
        client.AsyncHandler = async r => r switch
        {
            ListRequest => new IpcResponse { Ok = true, Rows = [] },
            SettingsGetRequest => new IpcResponse { Ok = true, Settings = client.Settings, Info = client.Info },
            _ => await Task.FromCanceled<IpcResponse>(new CancellationToken(true)),
        };
        _connector.Enqueue(client);
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.Connected, "connected");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _connection.SendAsync(new InputRequest(@"C:\a", "x"), cts.Token));
    }

    [Fact]
    public async Task Each_connection_has_a_new_generation()
    {
        var first = new FakeHostClient();
        _connector.Enqueue(first);
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.Connected, "connected");
        var g1 = _connection.Generation;
        Assert.True(_connection.IsCurrent(g1));

        first.Close();
        await Eventually.True(() => _connection.State == HostState.NotRunning, "not running");
        Assert.Equal(g1, _connection.Generation);
        Assert.False(_connection.IsCurrent(g1));

        _connector.Enqueue(new FakeHostClient());
        _connection.ReconnectNow();
        await Eventually.True(() => _connection.State == HostState.Connected, "reconnected");
        Assert.True(_connection.Generation > g1);
        Assert.False(_connection.IsCurrent(g1));
        Assert.True(_connection.IsCurrent(_connection.Generation));
    }

    [Fact]
    public async Task A_flood_of_events_yields_to_the_ui_now_and_then()
    {
        var yields = 0;
        await using var connection = new HostConnection(_connector, _time, () =>
        {
            Interlocked.Increment(ref yields);
            return Task.CompletedTask;
        });
        var client = new FakeHostClient();
        _connector.Enqueue(client);
        var outputs = 0;
        connection.Output += (_, _) => Interlocked.Increment(ref outputs);
        connection.Start();
        await Eventually.True(() => connection.State == HostState.Connected, "connected");

        for (var i = 0; i < 1000; i++)
            client.Push(new OutputEvent(@"C:\a", [1, 2, 3]));

        await Eventually.True(() => Volatile.Read(ref outputs) == 1000, "all events handled");
        Assert.True(Volatile.Read(ref yields) >= 1000 / HostConnection.EventsPerYield - 1,
            $"only {Volatile.Read(ref yields)} yields for 1000 events");
    }

    [Fact]
    public async Task Dispose_finishes_even_after_errors()
    {
        var client = new FakeHostClient { Rows = [TestRows.Make(@"C:\a", FolderStatus.Running)] };
        _connector.Enqueue(client);
        _connection.StateChanged += (_, _) => throw new InvalidOperationException("a subscriber's bug");
        _connection.Start();
        await Eventually.True(() => _connection.Rows.Count == 1, "connected");

        await _connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Throws once, then hands over to <paramref name="next"/>.</summary>
    private sealed class ThrowingOnceConnector(Exception error, IHostConnector next) : IHostConnector
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public Task<IHostClient> ConnectAsync(CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _attempts) == 1 ? Task.FromException<IHostClient>(error) : next.ConnectAsync(cancellationToken);
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
