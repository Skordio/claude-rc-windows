using System.Text;
using ClaudeRc.Core;
using ClaudeRc.Core.Ipc;
using ClaudeRc.Tray.Hosting;
using ClaudeRc.Tray.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace ClaudeRc.Tray.Tests;

/// <summary>The console viewer's bridge between the page and the host (SPEC 3.3).</summary>
public sealed class ConsoleViewModelTests : IAsyncDisposable
{
    private const string P = @"C:\src\app";

    private readonly FakeConnector _connector = new();
    private readonly HostConnection _connection;
    private readonly FakeViewerPage _page = new();
    private readonly List<string> _copied = [];

    public ConsoleViewModelTests()
    {
        _connection = new HostConnection(_connector, new FakeTimeProvider());
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private async Task<FakeHostClient> Connect(FolderStatus status, Func<IpcRequest, IpcResponse?>? handler = null)
    {
        var client = new FakeHostClient { Rows = [TestRows.Make(P, status)] };
        if (handler is not null)
        {
            client.Handler = r => handler(r) ?? r switch
            {
                ListRequest => new IpcResponse { Ok = true, Rows = client.Rows },
                SettingsGetRequest => new IpcResponse { Ok = true, Settings = client.Settings, Info = client.Info },
                _ => new IpcResponse { Ok = true },
            };
        }
        _connector.Enqueue(client);
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.Connected, "connected");
        return client;
    }

    private ConsoleViewModel Open()
    {
        var vm = new ConsoleViewModel(_connection, P, "app", _copied.Add);
        vm.ConnectPage(_page);
        return vm;
    }

    private static string Ready(int cols = 100, int rows = 30) => $$"""{"type":"ready","cols":{{cols}},"rows":{{rows}}}""";

    private static string Input(string data) => $$"""{"type":"input","data":{{System.Text.Json.JsonSerializer.Serialize(data)}}}""";

    private static void Push(FakeHostClient client, params Row[] rows) => client.Push(new RowsEvent(rows));

    [Fact]
    public async Task Attaches_with_the_page_size_once_the_page_is_ready()
    {
        var client = await Connect(FolderStatus.Running);
        using var vm = Open();
        Assert.Empty(client.Sent<AttachRequest>());

        vm.OnPageMessage(Ready(100, 30));

        var attach = Assert.Single(client.Sent<AttachRequest>());
        Assert.Equal(P, attach.Path);
        Assert.Equal(100, attach.Cols);
        Assert.Equal(30, attach.Rows);
        Assert.True(vm.IsAttached);
        Assert.Null(vm.StatusText);
        Assert.Null(_page.LastStatus);
    }

    [Fact]
    public async Task Output_is_passed_on_as_the_exact_bytes_also_when_a_utf8_sequence_is_split()
    {
        var client = await Connect(FolderStatus.Running);
        using var vm = Open();
        vm.OnPageMessage(Ready());
        var euro = Encoding.UTF8.GetBytes("€ ok"); // E2 82 AC ...

        client.Push(new OutputEvent(P, euro[..1]));
        client.Push(new OutputEvent(P, euro[1..]));

        await Eventually.True(() => _page.Outputs.Count == 2, "two output messages");
        Assert.Equal(euro[..1], _page.Outputs[0]);
        Assert.Equal(euro[1..], _page.Outputs[1]);
        // Never decoded to text on the way: the page's xterm joins the halves.
        Assert.Equal("€ ok", Encoding.UTF8.GetString([.. _page.Outputs[0], .. _page.Outputs[1]]));
    }

    [Fact]
    public async Task Output_of_other_folders_and_from_before_the_attach_is_ignored()
    {
        var client = await Connect(FolderStatus.Running);
        using var vm = Open();
        client.Push(new OutputEvent(P, [1]));
        Push(client, TestRows.Make(P, FolderStatus.Running) with { Pid = 1 });
        await Eventually.True(() => _connection.Rows[0].Pid == 1, "early output handled");
        vm.OnPageMessage(Ready());
        client.Push(new OutputEvent(@"C:\src\other", [2]));
        client.Push(new OutputEvent(P.ToUpperInvariant(), [3]));

        await Eventually.True(() => _page.Outputs.Count >= 1, "output");
        await Task.Delay(50);
        Assert.Equal([3], Assert.Single(_page.Outputs));
    }

    [Fact]
    public async Task Output_before_the_attach_answer_arrives_is_kept()
    {
        // The host sends the replay right after its answer; the two can come back in either order.
        var answer = new TaskCompletionSource<IpcResponse>();
        var client = await Connect(FolderStatus.Running);
        client.AsyncHandler = r => r is AttachRequest ? answer.Task : Task.FromResult(new IpcResponse { Ok = true });
        using var vm = Open();
        vm.OnPageMessage(Ready());

        client.Push(new OutputEvent(P, [7]));
        await Eventually.True(() => _page.Outputs.Count == 1, "replay before the answer");
        answer.SetResult(new IpcResponse { Ok = true });
        await Eventually.True(() => vm.IsAttached, "attached");
    }

    [Fact]
    public async Task Keystrokes_go_to_the_session_in_order()
    {
        var client = await Connect(FolderStatus.Running);
        using var vm = Open();
        vm.OnPageMessage(Ready());

        vm.OnPageMessage(Input("a"));
        vm.OnPageMessage(Input("é"));
        vm.OnPageMessage(Input("\r"));

        await Eventually.True(() => string.Concat(client.Sent<InputRequest>().Select(i => i.Data)) == "aé\r", "input");
        Assert.All(client.Sent<InputRequest>(), i => Assert.Equal(P, i.Path));
    }

    [Fact]
    public async Task Keystrokes_typed_while_one_is_being_sent_are_sent_together_after_it()
    {
        var client = await Connect(FolderStatus.Running);
        var first = new TaskCompletionSource<IpcResponse>();
        var inputs = 0;
        client.AsyncHandler = r => r is InputRequest && Interlocked.Increment(ref inputs) == 1
            ? first.Task
            : Task.FromResult(new IpcResponse { Ok = true });
        using var vm = Open();
        vm.OnPageMessage(Ready());

        vm.OnPageMessage(Input("a"));
        vm.OnPageMessage(Input("b"));
        vm.OnPageMessage(Input("c"));
        Assert.Equal(["a"], client.Sent<InputRequest>().Select(i => i.Data));

        first.SetResult(new IpcResponse { Ok = true });
        await Eventually.True(() => client.Sent<InputRequest>().Count() == 2, "second send");
        Assert.Equal(["a", "bc"], client.Sent<InputRequest>().Select(i => i.Data));
    }

    [Fact]
    public async Task A_large_paste_is_split_so_no_request_is_too_long_for_the_host_and_no_character_is_cut()
    {
        var client = await Connect(FolderStatus.Running);
        using var vm = Open();
        vm.OnPageMessage(Ready());
        // An emoji is two UTF-16 units; a cut between them would send two invalid halves.
        var paste = string.Concat(Enumerable.Repeat("ab😀", 30_000));

        vm.OnPageMessage(Input(paste));

        await Eventually.True(() => string.Concat(client.Sent<InputRequest>().Select(i => i.Data)) == paste, "whole paste sent");
        var parts = client.Sent<InputRequest>().ToList();
        Assert.True(parts.Count > 1);
        Assert.All(parts, p =>
        {
            Assert.True(p.Data.Length <= ConsoleViewModel.MaxInputChars);
            Assert.False(char.IsHighSurrogate(p.Data[^1]));
            Assert.True(Encoding.UTF8.GetByteCount(IpcCodec.Encode(p)) < 1 << 20);
        });
    }

    [Fact]
    public async Task Keystrokes_are_dropped_while_no_session_is_attached()

    {
        var client = await Connect(FolderStatus.Off);
        using var vm = Open();
        vm.OnPageMessage(Ready());

        vm.OnPageMessage(Input("y"));

        Assert.Empty(client.Sent<InputRequest>());
    }

    [Fact]
    public async Task Resize_is_forwarded_and_kept_within_the_hosts_limits()
    {
        var client = await Connect(FolderStatus.Running);
        using var vm = Open();
        vm.OnPageMessage(Ready());

        vm.OnPageMessage("""{"type":"resize","cols":80,"rows":24}""");
        vm.OnPageMessage("""{"type":"resize","cols":5000,"rows":9000}""");
        vm.OnPageMessage("""{"type":"resize","cols":0,"rows":24}""");

        Assert.Equal([(80, 24), (1000, 500)], client.Sent<ResizeRequest>().Select(r => (r.Cols, r.Rows)));
    }

    [Fact]
    public async Task A_tiny_page_attaches_with_at_least_one_column_and_row()
    {
        var client = await Connect(FolderStatus.Running);
        using var vm = Open();

        vm.OnPageMessage(Ready(0, 0));

        var attach = Assert.Single(client.Sent<AttachRequest>());
        Assert.Equal((1, 1), (attach.Cols, attach.Rows));
    }

    [Fact]
    public async Task A_folder_that_is_not_running_shows_why_and_attaches_when_it_starts()
    {
        var client = await Connect(FolderStatus.Off);
        using var vm = Open();
        vm.OnPageMessage(Ready());

        Assert.Empty(client.Sent<AttachRequest>());
        Assert.False(vm.IsAttached);
        Assert.Contains("OFF", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal(vm.StatusText, _page.LastStatus);

        Push(client, TestRows.Make(P, FolderStatus.Starting));

        await Eventually.True(() => vm.IsAttached, "attached once it starts");
        Assert.Single(client.Sent<AttachRequest>());
        Assert.Null(vm.StatusText);
        Assert.Null(_page.LastStatus);
    }

    [Theory]
    [InlineData(FolderStatus.Restarting, "restarting")]
    [InlineData(FolderStatus.Failing, "Restart")]
    [InlineData(FolderStatus.Missing, "does not exist")]
    [InlineData(FolderStatus.External, "outside the app")]
    public async Task The_banner_says_why_nothing_runs(FolderStatus status, string expected)
    {
        await Connect(status);
        using var vm = Open();

        vm.OnPageMessage(Ready());

        Assert.Contains(expected, vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_attach_is_shown_and_tried_again_on_the_next_row_change()
    {
        var refuse = true;
        var client = await Connect(FolderStatus.Running, r => r is AttachRequest && refuse
            ? new IpcResponse { Ok = false, Error = @"no claude rc session is running in C:\src\app" }
            : null);
        using var vm = Open();
        vm.OnPageMessage(Ready());

        Assert.False(vm.IsAttached);
        Assert.Contains("no claude rc session is running", vm.StatusText, StringComparison.Ordinal);

        refuse = false;
        Push(client, TestRows.Make(P, FolderStatus.Running) with { Pid = 42 });

        await Eventually.True(() => vm.IsAttached, "attached on retry");
        Assert.Equal(2, client.Sent<AttachRequest>().Count());
    }

    [Fact]
    public async Task While_attached_an_exit_shows_the_banner_and_the_next_session_clears_it_without_a_new_attach()
    {
        var client = await Connect(FolderStatus.Running);
        using var vm = Open();
        vm.OnPageMessage(Ready());

        Push(client, TestRows.Make(P, FolderStatus.Restarting, restartInS: 5, exitCode: 1));
        await Eventually.True(() => vm.StatusText is not null, "banner");
        Assert.Contains("restarting", vm.StatusText, StringComparison.Ordinal);
        Assert.True(vm.IsAttached); // the host keeps the viewer and replays the next session itself

        Push(client, TestRows.Make(P, FolderStatus.Running));
        await Eventually.True(() => vm.StatusText is null, "banner cleared");
        Assert.Single(client.Sent<AttachRequest>());
    }

    [Fact]
    public async Task A_removed_folder_says_so()
    {
        var client = await Connect(FolderStatus.Running);
        using var vm = Open();
        vm.OnPageMessage(Ready());

        Push(client);

        await Eventually.True(() => vm.StatusText is not null, "banner");
        Assert.Contains("no longer in the list", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_gone_shows_it_and_the_viewer_attaches_again_after_reconnecting()
    {
        var client = await Connect(FolderStatus.Running);
        using var vm = Open();
        vm.OnPageMessage(Ready(90, 25));

        client.Close();
        await Eventually.True(() => _connection.State == HostState.NotRunning, "not running");
        Assert.False(vm.IsAttached);
        Assert.Contains("Host not running", vm.StatusText, StringComparison.Ordinal);

        var second = new FakeHostClient { Rows = [TestRows.Make(P, FolderStatus.Running)] };
        _connector.Enqueue(second);
        _connection.ReconnectNow();

        await Eventually.True(() => vm.IsAttached && vm.StatusText is null, "attached again");
        var attach = Assert.Single(second.Sent<AttachRequest>());
        Assert.Equal((90, 25), (attach.Cols, attach.Rows));
    }

    [Fact]
    public async Task An_attach_answer_from_a_lost_connection_does_not_count_after_the_reconnect()
    {
        var client = new FakeHostClient { Rows = [TestRows.Make(P, FolderStatus.Running)] };
        var answer = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.AsyncHandler = r => r switch
        {
            ListRequest => Task.FromResult(new IpcResponse { Ok = true, Rows = client.Rows }),
            SettingsGetRequest => Task.FromResult(new IpcResponse { Ok = true, Settings = client.Settings, Info = client.Info }),
            AttachRequest => answer.Task,
            _ => Task.FromResult(new IpcResponse { Ok = true }),
        };
        _connector.Enqueue(client);
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.Connected, "connected");
        using var vm = Open();
        vm.OnPageMessage(Ready());
        Assert.Single(client.Sent<AttachRequest>());

        // The host answered, then dropped the pipe; the answer is handled only after the reconnect.
        client.Close();
        await Eventually.True(() => _connection.State == HostState.NotRunning, "not running");
        var second = new FakeHostClient { Rows = [TestRows.Make(P, FolderStatus.Running)] };
        _connector.Enqueue(second);
        _connection.ReconnectNow();
        await Eventually.True(() => _connection.State == HostState.Connected, "reconnected");
        answer.SetResult(new IpcResponse { Ok = true });

        await Eventually.True(() => second.Sent<AttachRequest>().Any(), "attached on the new connection");
        await Eventually.True(() => vm.IsAttached, "attached");
    }

    [Fact]
    public async Task An_attach_answer_that_lands_while_the_host_is_gone_does_not_count()
    {
        var client = new FakeHostClient { Rows = [TestRows.Make(P, FolderStatus.Running)] };
        var answer = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.AsyncHandler = r => r switch
        {
            ListRequest => Task.FromResult(new IpcResponse { Ok = true, Rows = client.Rows }),
            SettingsGetRequest => Task.FromResult(new IpcResponse { Ok = true, Settings = client.Settings, Info = client.Info }),
            AttachRequest => answer.Task,
            _ => Task.FromResult(new IpcResponse { Ok = true }),
        };
        _connector.Enqueue(client);
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.Connected, "connected");
        using var vm = Open();
        vm.OnPageMessage(Ready());

        client.Close();
        await Eventually.True(() => _connection.State == HostState.NotRunning, "not running");
        answer.SetResult(new IpcResponse { Ok = true });
        await Eventually.True(() => vm.StatusText?.Contains("Host not running", StringComparison.Ordinal) == true, "banner");
        Assert.False(vm.IsAttached);

        var second = new FakeHostClient { Rows = [TestRows.Make(P, FolderStatus.Running)] };
        _connector.Enqueue(second);
        _connection.ReconnectNow();
        await Eventually.True(() => vm.IsAttached && second.Sent<AttachRequest>().Any(), "attached on the new connection");
    }

    [Fact]
    public async Task A_send_that_fails_unexpectedly_does_not_stop_the_keys()
    {
        var failed = 0;
        var client = await Connect(FolderStatus.Running, r => r is InputRequest && Interlocked.Increment(ref failed) == 1
            ? throw new ObjectDisposedException("IpcClient")
            : null);
        using var vm = Open();
        vm.OnPageMessage(Ready());

        vm.OnPageMessage(Input("a"));
        vm.OnPageMessage(Input("b"));

        await Eventually.True(() => client.Sent<InputRequest>().Count() == 2, "second key sent");
        Assert.Equal(["a", "b"], client.Sent<InputRequest>().Select(i => i.Data));
    }

    [Fact]
    public async Task Closing_detaches_and_never_touches_the_session()
    {
        var client = await Connect(FolderStatus.Running);
        var vm = Open();
        vm.OnPageMessage(Ready());

        vm.Dispose();

        Assert.Equal(P, Assert.Single(client.Sent<DetachRequest>()).Path);
        Assert.Empty(client.Sent<SetRequest>());
        Assert.Empty(client.Sent<RestartRequest>());
        client.Push(new OutputEvent(P, [1]));
        Push(client, TestRows.Make(P, FolderStatus.Off));
        await Task.Delay(50);
        Assert.Empty(_page.Outputs);
        Assert.Single(client.Sent<AttachRequest>());
    }

    [Fact]
    public async Task Closing_without_an_attach_sends_nothing()
    {
        var client = await Connect(FolderStatus.Off);
        var vm = Open();
        vm.OnPageMessage(Ready());

        vm.Dispose();

        Assert.Empty(client.Sent<DetachRequest>());
    }

    [Fact]
    public async Task Copy_puts_the_selection_on_the_clipboard()
    {
        await Connect(FolderStatus.Running);
        using var vm = Open();

        vm.OnPageMessage("""{"type":"copy","text":"line 1\nline 2"}""");

        Assert.Equal(["line 1\nline 2"], _copied);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"type":"bogus"}""")]
    [InlineData("""{"type":"input"}""")]
    public async Task Bad_page_messages_are_ignored(string json)
    {
        var client = await Connect(FolderStatus.Running);
        using var vm = Open();

        vm.OnPageMessage(json);

        Assert.Empty(client.Sent<AttachRequest>());
        Assert.Empty(client.Sent<InputRequest>());
    }

    [Fact]
    public async Task Focus_asks_the_page_to_focus_the_terminal()
    {
        await Connect(FolderStatus.Running);
        using var vm = Open();
        vm.OnPageMessage(Ready());

        vm.Focus();

        Assert.Single(_page.OfType("focus"));
    }

    [Fact]
    public async Task Title_names_the_folder()
    {
        await Connect(FolderStatus.Running);
        using var vm = Open();

        Assert.Equal("app - console", vm.Title);
    }
}
