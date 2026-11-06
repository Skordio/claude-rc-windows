using System.Diagnostics;
using System.Text;
using ClaudeRc.Core;
using ClaudeRc.Core.Ipc;
using ClaudeRc.Core.Paths;
using ClaudeRc.Tray.Hosting;
using ClaudeRc.Tray.Viewer;

namespace ClaudeRc.Tray.ViewModels;

/// <summary>Where the console viewer sends messages: the WebView2 page (a fake in tests).</summary>
internal interface IViewerPage
{
    /// <summary>Posts one JSON message (<see cref="ViewerProtocol.Encode"/>).</summary>
    void Post(string json);
}

/// <summary>
/// The console viewer of one folder (SPEC 3.3): the bridge between the xterm.js page and the host.
/// When the page is ready it sends <c>attach</c> with the page's size; the host answers with the
/// replay and then streams <c>output</c>, which goes to the page as the exact bytes. Keys become
/// <c>input</c>, a new fit becomes <c>resize</c>, closing sends <c>detach</c>. Nothing here ever
/// changes the session itself.
/// </summary>
/// <remarks>
/// While no session runs (OFF, restarting, failing, missing, external, no host) a banner says why
/// and the last screen stays. The host keeps an attached viewer across the folder's sessions and
/// replays each new one, so after an exit nothing needs to be sent again. A viewer that is not
/// attached (no session at open, a refused attach, a lost connection) attaches when the folder's
/// row next shows a running session.
/// </remarks>
internal sealed class ConsoleViewModel : IDisposable
{
    /// <summary>The host's largest pseudo-console (<c>Session.MaxColumns</c> × <c>Session.MaxRows</c>).</summary>
    public const int MaxColumns = 1000;

    /// <inheritdoc cref="MaxColumns"/>
    public const int MaxRows = 500;

    /// <summary>
    /// Characters per <c>input</c> request. The host disconnects a client whose request line is over
    /// 1 MiB; 32 Ki characters stay under it even when every one is escaped as <c>\uXXXX</c>.
    /// </summary>
    public const int MaxInputChars = 32 * 1024;

    private const string Info = "info";
    private const string Error = "error";

    private readonly HostConnection _connection;
    private readonly Action<string> _copyToClipboard;
    private readonly Lock _lock = new();
    private readonly StringBuilder _pendingInput = new();
    private IViewerPage? _page;
    private bool _ready;
    private int _cols = 120;
    private int _rows = 40;
    private bool _attached;
    private bool _attaching;
    private bool _evaluateAgain;
    private bool _sendingInput;
    private bool _disposed;
    private string? _status;
    private string _level = Info;

    /// <summary>Creates the view-model and follows the connection.</summary>
    /// <param name="connection">The host connection.</param>
    /// <param name="path">The folder (the row's path).</param>
    /// <param name="name">The row's display name.</param>
    /// <param name="copyToClipboard">Puts text on the clipboard (Ctrl+C with a selection).</param>
    public ConsoleViewModel(HostConnection connection, string path, string name, Action<string> copyToClipboard)
    {
        _connection = connection;
        _copyToClipboard = copyToClipboard;
        Path = path;
        Title = name + " - console";
        _connection.RowsChanged += OnRowsChanged;
        _connection.StateChanged += OnStateChanged;
        _connection.Output += OnOutput;
    }

    /// <summary>The folder.</summary>
    public string Path { get; }

    /// <summary>Window title.</summary>
    public string Title { get; }

    /// <summary>The banner text, or null when the console is live.</summary>
    public string? StatusText
    {
        get
        {
            lock (_lock)
                return _status;
        }
    }

    /// <summary>The host streams this folder's console to the viewer.</summary>
    public bool IsAttached
    {
        get
        {
            lock (_lock)
                return _attached;
        }
    }

    /// <summary>The page exists; it sends <c>ready</c> once its terminal is open.</summary>
    public void ConnectPage(IViewerPage page)
    {
        lock (_lock)
            _page = page;
    }

    /// <summary>A message from the page (<see cref="ViewerProtocol"/>); anything invalid is ignored.</summary>
    public void OnPageMessage(string json)
    {
        switch (ViewerProtocol.Parse(json))
        {
            case PageReady ready:
                OnReady(ready);
                break;
            case PageInput input:
                OnInput(input.Data);
                break;
            case PageResize resize:
                OnResize(resize);
                break;
            case PageCopy copy:
                _copyToClipboard(copy.Text);
                break;
        }
    }

    /// <summary>Gives the terminal the keyboard focus (the window was activated).</summary>
    public void Focus() => Post(new ViewerFocus());

    /// <summary>Why nothing runs, for a row whose session is not alive.</summary>
    public static string DescribeNotRunning(Row row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var exit = row.ExitCode is { } code ? $" (exit code {FolderRowViewModel.FormatExitCode(code)})" : "";
        return row.Status switch
        {
            FolderStatus.Off => "This folder is OFF, so claude rc is not running. Turn it ON in the main window; the console appears when it starts.",
            FolderStatus.Restarting when row.Error is { Length: > 0 } error =>
                $"claude rc could not start: {error}. It is restarting; the console comes back when it runs.",
            FolderStatus.Restarting => $"claude rc exited{exit} and is restarting. The console comes back when it starts again.",
            FolderStatus.Failing => $"claude rc kept exiting{exit}, so it is not restarted automatically. Use Restart in the main window; the console comes back when it starts.",
            FolderStatus.Missing => "The folder does not exist (is its drive there?). The console appears when it is back and claude rc starts.",
            FolderStatus.External => "This claude rc was started outside the app, so there is no console to show here.",
            _ => $"claude rc is not running ({row.Status.ToString().ToLowerInvariant()}).",
        };
    }

    private static bool IsAlive(Row row) => row.Status is FolderStatus.Starting or FolderStatus.Running or FolderStatus.Waiting;

    private void OnReady(PageReady ready)
    {
        string? status;
        string level;
        lock (_lock)
        {
            if (_disposed)
                return;
            _ready = true;
            _cols = Math.Clamp(ready.Cols, 1, MaxColumns);
            _rows = Math.Clamp(ready.Rows, 1, MaxRows);
            status = _status;
            level = _level;
        }
        Post(new ViewerStatus(status, level));
        Evaluate();
    }

    private void OnRowsChanged(object? sender, EventArgs e) => Evaluate();

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // A new connection has no viewers: the host forgot this one with the old connection.
        if (_connection.State != HostState.Connected)
        {
            lock (_lock)
                _attached = false;
        }
        Evaluate();
    }

    /// <summary>Attaches if a session runs and nothing is attached; otherwise updates the banner.</summary>
    private void Evaluate()
    {
        var row = _connection.Rows.FirstOrDefault(r => PathNormalizer.Comparer.Equals(r.Path, Path));
        bool attach;
        lock (_lock)
        {
            if (_disposed || !_ready)
                return;
            if (_attaching)
            {
                _evaluateAgain = true;
                return;
            }
            attach = !_attached && row is not null && IsAlive(row);
            if (attach)
                _attaching = true;
        }

        if (attach)
            _ = AttachAsync();
        else if (row is null)
            SetStatus(_connection.State == HostState.Connected ? "This folder is no longer in the list." : HostDownText(), Info);
        else if (IsAlive(row))
            SetStatus(null, Info);
        else
            SetStatus(DescribeNotRunning(row), Info);
    }

    private string HostDownText() => _connection.State switch
    {
        HostState.Connecting => "Connecting to the host…",
        HostState.VersionMismatch => IpcCodec.VersionMismatchMessage,
        _ => HostConnection.NotRunningText + ". The console comes back when the host runs again.",
    };

    private async Task AttachAsync()
    {
        int cols, rows;
        lock (_lock)
        {
            _evaluateAgain = false;
            cols = _cols;
            rows = _rows;
        }

        // The answer counts only for the connection it was asked on: one that has ended since
        // took its viewer with it, and a new connection has none.
        var generation = _connection.Generation;
        HostResult result;
        try
        {
            result = await _connection.SendAsync(new AttachRequest(Path, cols, rows));
        }
        catch (Exception e)
        {
            // SendAsync does not throw; if it ever does, the viewer must not stay "attaching".
            Trace.TraceError("claude-rc-windows: attach failed: {0}", e);
            result = new HostResult(false, e.Message, null);
        }
        // Not State == Connected: a reconnect raises the rows (and so this attach) before it says Connected.
        var current = _connection.IsCurrent(generation);

        bool again, detach;
        lock (_lock)
        {
            _attaching = false;
            detach = result.Ok && current && _disposed;
            _attached = result.Ok && current && !_disposed;
            // A stale answer: evaluate again on the connection there is now.
            again = _evaluateAgain || (result.Ok && !current);
            _evaluateAgain = false;
        }
        if (detach)
        {
            _ = _connection.SendAsync(new DetachRequest(Path));
            return;
        }
        if (result.Ok || again)
            Evaluate();
        else
            SetStatus("The console could not be opened: " + (result.Error ?? "the host refused it") + ". It is tried again when the folder's status changes.", Error);
    }

    private void OnOutput(object? sender, OutputEvent e)
    {
        if (!PathNormalizer.Comparer.Equals(e.Path, Path))
            return;
        lock (_lock)
        {
            // The replay can arrive before the attach answer is read.
            if (_disposed || !(_attached || _attaching))
                return;
        }
        Post(new ViewerOutput(e.Data));
    }

    private void OnInput(string data)
    {
        lock (_lock)
        {
            if (_disposed || !_attached || data.Length == 0)
                return;
            _pendingInput.Append(data);
            if (_sendingInput)
                return;
            _sendingInput = true;
        }
        _ = PumpInputAsync();
    }

    /// <summary>Sends the typed text one request at a time, in order; text typed meanwhile goes in the next one.</summary>
    private async Task PumpInputAsync()
    {
        while (true)
        {
            string data;
            lock (_lock)
            {
                if (_disposed || _pendingInput.Length == 0)
                {
                    _pendingInput.Clear();
                    _sendingInput = false;
                    return;
                }
                var take = Math.Min(_pendingInput.Length, MaxInputChars);
                // Never cut a surrogate pair in two.
                if (take < _pendingInput.Length && char.IsHighSurrogate(_pendingInput[take - 1]))
                    take--;
                data = _pendingInput.ToString(0, take);
                _pendingInput.Remove(0, take);
            }
            try
            {
                await _connection.SendAsync(new InputRequest(Path, data));
            }
            catch (Exception e)
            {
                // SendAsync does not throw; if it ever does, the keys after this one still go.
                Trace.TraceError("claude-rc-windows: input failed: {0}", e);
            }
        }
    }

    private void OnResize(PageResize resize)
    {
        if (resize.Cols < 1 || resize.Rows < 1)
            return;
        int cols = Math.Min(resize.Cols, MaxColumns), rows = Math.Min(resize.Rows, MaxRows);
        bool send;
        lock (_lock)
        {
            if (_disposed)
                return;
            _cols = cols;
            _rows = rows;
            send = _attached;
        }
        if (send)
            _ = _connection.SendAsync(new ResizeRequest(Path, cols, rows));
    }

    private void SetStatus(string? text, string level)
    {
        lock (_lock)
        {
            if (_status == text && _level == level)
                return;
            _status = text;
            _level = level;
        }
        Post(new ViewerStatus(text, level));
    }

    private void Post(ViewerMessage message)
    {
        IViewerPage? page;
        lock (_lock)
        {
            if (_disposed || !_ready)
                return;
            page = _page;
        }
        page?.Post(ViewerProtocol.Encode(message));
    }

    /// <summary>The window closed: stop following the host and detach. The session keeps running.</summary>
    public void Dispose()
    {
        bool detach;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            detach = _attached;
            _attached = false;
        }
        _connection.RowsChanged -= OnRowsChanged;
        _connection.StateChanged -= OnStateChanged;
        _connection.Output -= OnOutput;
        if (detach)
            _ = _connection.SendAsync(new DetachRequest(Path));
    }
}
