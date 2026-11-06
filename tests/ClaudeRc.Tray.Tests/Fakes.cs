using System.Collections.Concurrent;
using System.Threading.Channels;
using ClaudeRc.Core;
using ClaudeRc.Core.Ipc;
using ClaudeRc.Tray.Hosting;
using ClaudeRc.Tray.Services;

namespace ClaudeRc.Tray.Tests;

/// <summary>A host connection in memory: answers requests with <see cref="Handler"/> and lets a test push events or close it.</summary>
internal sealed class FakeHostClient : IHostClient
{
    private readonly Channel<IpcEvent> _events = Channel.CreateUnbounded<IpcEvent>();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _reason;

    public ConcurrentQueue<IpcRequest> Requests { get; } = new();

    /// <summary>Answers each request; the default is a fake host with <see cref="Rows"/> and <see cref="Settings"/>.</summary>
    public Func<IpcRequest, IpcResponse>? Handler { get; set; }

    /// <summary>When set, answers instead of <see cref="Handler"/>, so a test can hold a request in flight.</summary>
    public Func<IpcRequest, Task<IpcResponse>>? AsyncHandler { get; set; }

    public List<Row> Rows { get; set; } = [];

    public IpcSettings Settings { get; set; } = new(null, true);

    public HostInfo Info { get; set; } = new("1.2.3", @"C:\data", @"C:\logs", @"C:\bin\claude.exe", "2.1.318 (Claude Code)");

    public bool Disposed { get; private set; }

    public ChannelReader<IpcEvent> Events => _events.Reader;

    public Task Closed => _closed.Task;

    public string? CloseReason => Volatile.Read(ref _reason);

    public Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken cancellationToken = default)
    {
        if (_closed.Task.IsCompleted)
            return Task.FromException<IpcResponse>(new IpcClosedException(_reason ?? "closed"));
        Requests.Enqueue(request);
        if (AsyncHandler is { } asyncHandler)
            return WithId(asyncHandler(request), request.Id);
        var response = (Handler ?? Default)(request);
        return Task.FromResult(response with { Id = request.Id });
    }

    private static async Task<IpcResponse> WithId(Task<IpcResponse> response, int id) => (await response) with { Id = id };

    private IpcResponse Default(IpcRequest request) => request switch
    {
        ListRequest => new IpcResponse { Ok = true, Rows = Rows },
        SettingsGetRequest => new IpcResponse { Ok = true, Settings = Settings, Info = Info },
        _ => new IpcResponse { Ok = true },
    };

    public void Push(IpcEvent e) => _events.Writer.TryWrite(e);

    public void Close(string reason = "the host closed the connection")
    {
        Volatile.Write(ref _reason, reason);
        _events.Writer.TryComplete();
        _closed.TrySetResult();
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        Close(_reason ?? "the connection was closed");
        return ValueTask.CompletedTask;
    }

    public IEnumerable<T> Sent<T>() where T : IpcRequest => Requests.OfType<T>();
}

/// <summary>Hands out the queued clients; with none queued the host is "not running" (a <see cref="TimeoutException"/>).</summary>
internal sealed class FakeConnector : IHostConnector
{
    private readonly ConcurrentQueue<FakeHostClient> _clients = new();
    private int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);

    public void Enqueue(FakeHostClient client) => _clients.Enqueue(client);

    public Task<IHostClient> ConnectAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);
        return _clients.TryDequeue(out var client)
            ? Task.FromResult<IHostClient>(client)
            : Task.FromException<IHostClient>(new TimeoutException("the host is not running"));
    }
}

internal sealed class FakeDialogs : IDialogs
{
    public List<(string Title, string Message)> Confirms { get; } = [];
    public List<(string Title, string Message)> Errors { get; } = [];
    public List<(string Title, string Message)> Infos { get; } = [];
    public bool ConfirmAnswer { get; set; } = true;
    public string? FolderToPick { get; set; }
    public string? FileToPick { get; set; }

    public bool Confirm(string title, string message)
    {
        Confirms.Add((title, message));
        return ConfirmAnswer;
    }

    public void ShowError(string title, string message) => Errors.Add((title, message));

    public void ShowInfo(string title, string message) => Infos.Add((title, message));

    public string? PickFolder() => FolderToPick;

    public string? PickClaudeExecutable(string? current) => FileToPick;
}

internal sealed class FakeHostTask : IHostTask
{
    public StartHostResult RunResult { get; set; } = StartHostResult.Missing(@"\claude-rc-windows\Host");
    public int Runs { get; private set; }
    public HostTaskInfo Info { get; set; } = new(HostTaskState.Missing, StartMode.Unknown, @"\claude-rc-windows\Host");

    public Task<HostTaskInfo> GetInfoAsync(CancellationToken cancellationToken = default) => Task.FromResult(Info);

    public Task<StartHostResult> RunAsync(CancellationToken cancellationToken = default)
    {
        Runs++;
        return Task.FromResult(RunResult);
    }
}

internal sealed class FakeShell : IShell
{
    public List<string> Explorer { get; } = [];
    public List<string> Terminal { get; } = [];

    public void OpenInExplorer(string path) => Explorer.Add(path);

    public void OpenInTerminal(string path) => Terminal.Add(path);
}

internal sealed class FakeWindows : IWindows
{
    public int SettingsOpened { get; private set; }
    public List<string> Logs { get; } = [];
    public List<string> Consoles { get; } = [];

    public void OpenSettings() => SettingsOpened++;

    public void OpenLog(string path, string name) => Logs.Add(path);

    public void OpenConsole(string path, string name) => Consoles.Add(path);
}

internal static class TestRows
{
    public static Row Make(string path, FolderStatus status, bool on = true, bool saved = true, string? name = null,
        string? prompt = null, int? exitCode = null, int? restartInS = null, string? error = null,
        IReadOnlyList<string>? lastLines = null) => new()
        {
            Path = path,
            Name = name ?? Path.GetFileName(path),
            On = on,
            Saved = saved,
            Status = status,
            Prompt = prompt,
            ExitCode = exitCode,
            RestartInS = restartInS,
            Crashes10m = 0,
            Error = error,
            LastLines = lastLines,
        };
}

internal static class Eventually
{
    /// <summary>Waits until <paramref name="condition"/> holds (the connection loop runs on the thread pool in tests).</summary>
    public static async Task True(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail("timed out waiting for: " + what);
            await Task.Delay(5);
        }
    }
}

/// <summary>The console viewer page: records what the view-model posts to it.</summary>
internal sealed class FakeViewerPage : ClaudeRc.Tray.ViewModels.IViewerPage
{
    private readonly ConcurrentQueue<string> _posted = new();

    public IReadOnlyList<string> Posted => _posted.ToArray();

    public void Post(string json) => _posted.Enqueue(json);

    /// <summary>The posted messages of one type, parsed.</summary>
    public List<System.Text.Json.JsonElement> OfType(string type) =>
        Posted.Select(j => System.Text.Json.JsonDocument.Parse(j).RootElement)
            .Where(e => e.GetProperty("type").GetString() == type).ToList();

    /// <summary>The bytes of every output message, in order.</summary>
    public List<byte[]> Outputs => OfType("output").Select(e => e.GetProperty("data").GetBytesFromBase64()).ToList();

    /// <summary>The last banner text posted (null = hidden), or "(none)" when no status was posted.</summary>
    public string? LastStatus => OfType("status") is { Count: > 0 } s
        ? s[^1].GetProperty("text").GetString()
        : "(none)";
}

internal sealed class FakeNotifier : ClaudeRc.Tray.Notifications.IToastNotifier
{
    public List<ClaudeRc.Tray.Notifications.ToastRequest> Shown { get; } = [];

    public event EventHandler<string>? Activated;

    public void Show(ClaudeRc.Tray.Notifications.ToastRequest toast)
    {
        lock (Shown)
            Shown.Add(toast);
    }

    public int Count
    {
        get
        {
            lock (Shown)
                return Shown.Count;
        }
    }

    public void Click(string path) => Activated?.Invoke(this, path);
}
