using ClaudeRc.Core;
using ClaudeRc.Core.Ipc;
using ClaudeRc.Tray.Hosting;
using ClaudeRc.Tray.Notifications;
using Microsoft.Extensions.Time.Testing;

namespace ClaudeRc.Tray.Tests;

/// <summary>Toasts (SPEC 3.5): once per transition, the setting, and the click.</summary>
public sealed class ToastRouterTests : IAsyncDisposable
{
    private const string P = @"C:\src\app";

    private readonly FakeConnector _connector = new();
    private readonly HostConnection _connection;
    private readonly FakeNotifier _notifier = new();
    private readonly List<string> _opened = [];

    public ToastRouterTests()
    {
        _connection = new HostConnection(_connector, new FakeTimeProvider());
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private async Task<FakeHostClient> Connect(FolderStatus status, bool notifications = true)
    {
        var client = new FakeHostClient
        {
            Rows = [TestRows.Make(P, status, name: "app")],
            Settings = new IpcSettings(null, notifications),
        };
        _connector.Enqueue(client);
        _connection.Start();
        await Eventually.True(() => _connection.State == HostState.Connected, "connected");
        return client;
    }

    private ToastRouter Router() => new(_connection, _notifier, _opened.Add);

    /// <summary>Pushes events and waits until the loop has handled them (a rows event marks the end).</summary>
    private async Task Send(FakeHostClient client, IpcEvent e, FolderStatus statusAfter)
    {
        var marker = TestRows.Make(@"C:\marker" + Guid.NewGuid().ToString("N"), FolderStatus.Off);
        client.Push(e);
        client.Push(new RowsEvent([TestRows.Make(P, statusAfter, name: "app"), marker]));
        await Eventually.True(() => _connection.Rows.Any(r => r.Path == marker.Path), "events handled");
    }

    private static ToastEvent Toast(FolderStatus status, string text = "app: something") => new(P, status, text);

    [Fact]
    public async Task A_toast_event_shows_a_toast_for_the_row()
    {
        var client = await Connect(FolderStatus.Failing);
        using var router = Router();

        await Send(client, Toast(FolderStatus.Failing, "app: claude rc keeps exiting"), FolderStatus.Failing);

        var shown = Assert.Single(_notifier.Shown);
        Assert.Equal(new ToastRequest(P, ToastRouter.TitleFor(FolderStatus.Failing), "app: claude rc keeps exiting"), shown);
    }

    [Fact]
    public async Task The_same_status_is_shown_only_once_until_the_folder_leaves_it()
    {
        var client = await Connect(FolderStatus.Waiting);
        using var router = Router();

        await Send(client, Toast(FolderStatus.Waiting), FolderStatus.Waiting);
        await Send(client, Toast(FolderStatus.Waiting), FolderStatus.Waiting);
        Assert.Equal(1, _notifier.Count);

        // Answered, then at a prompt again: a new transition.
        await Send(client, new RowsEvent([TestRows.Make(P, FolderStatus.Running)]), FolderStatus.Running);
        await Send(client, Toast(FolderStatus.Waiting), FolderStatus.Waiting);
        Assert.Equal(2, _notifier.Count);
    }

    [Fact]
    public async Task A_different_status_is_a_new_transition()
    {
        var client = await Connect(FolderStatus.Waiting);
        using var router = Router();

        await Send(client, Toast(FolderStatus.Waiting), FolderStatus.Waiting);
        await Send(client, Toast(FolderStatus.Missing), FolderStatus.Missing);

        Assert.Equal([FolderStatus.Waiting, FolderStatus.Missing],
            _notifier.Shown.Select(t => t.Title == ToastRouter.TitleFor(FolderStatus.Waiting) ? FolderStatus.Waiting : FolderStatus.Missing));
    }

    [Fact]
    public async Task Nothing_is_shown_when_notifications_are_off()
    {
        var client = await Connect(FolderStatus.Failing, notifications: false);
        using var router = Router();

        await Send(client, Toast(FolderStatus.Failing), FolderStatus.Failing);

        Assert.Equal(0, _notifier.Count);
    }

    [Fact]
    public async Task Clicking_a_toast_opens_the_main_window_at_its_row()
    {
        await Connect(FolderStatus.Failing);
        using var router = Router();

        _notifier.Click(P);

        Assert.Equal([P], _opened);
    }

    [Fact]
    public async Task After_dispose_nothing_happens()
    {
        var client = await Connect(FolderStatus.Failing);
        var router = Router();
        router.Dispose();

        await Send(client, Toast(FolderStatus.Failing), FolderStatus.Failing);
        _notifier.Click(P);

        Assert.Equal(0, _notifier.Count);
        Assert.Empty(_opened);
    }

    [Theory]
    [InlineData(FolderStatus.Failing, "claude rc is failing")]
    [InlineData(FolderStatus.Waiting, "Waiting for input")]
    [InlineData(FolderStatus.Missing, "Folder missing")]
    [InlineData(FolderStatus.Running, "claude-rc-windows")]
    public void Titles(FolderStatus status, string title)
    {
        Assert.Equal(title, ToastRouter.TitleFor(status));
    }
}

public class ToastArgsTests
{
    [Theory]
    [InlineData(@"C:\src\app", null)]
    [InlineData(@"C:\a;b=c&d e\%x%", "t1")]
    public void The_folder_comes_back_from_the_click(string path, string? instance)
    {
        Assert.Equal(path, ToastArgs.TryGetPath(ToastArgs.Build(path, instance), instance));
    }

    [Theory]
    [InlineData(null, "t1")]
    [InlineData("t1", null)]
    [InlineData("t1", "t2")]
    public void A_toast_of_another_instance_is_ignored(string? shownBy, string? clickedIn)
    {
        Assert.Null(ToastArgs.TryGetPath(ToastArgs.Build(@"C:\src\app", shownBy), clickedIn));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("action=other;path=C:\a")]
    public void Other_arguments_are_ignored(string arguments)
    {
        Assert.Null(ToastArgs.TryGetPath(arguments, null));
    }
}
