using System.Diagnostics;
using ClaudeRc.Core.Ipc;

namespace ClaudeRc.Tray.Hosting;

/// <summary>Where the tray stands with the host.</summary>
internal enum HostState
{
    /// <summary>The first attempt has not finished yet.</summary>
    Connecting,

    /// <summary>Connected; rows and settings are loaded.</summary>
    Connected,

    /// <summary>No host answers on the pipe (SPEC 10: "Host not running").</summary>
    NotRunning,

    /// <summary>The host speaks another protocol version (SPEC 10).</summary>
    VersionMismatch,
}

/// <summary>The outcome of a request: <see cref="Error"/> is the text to show when <see cref="Ok"/> is false.</summary>
/// <param name="Ok">The host did it.</param>
/// <param name="Error">Why not: the host's own message, or that there is no host.</param>
/// <param name="Response">The host's answer, if one came.</param>
internal sealed record HostResult(bool Ok, string? Error, IpcResponse? Response);

/// <summary>
/// Keeps the tray connected to the host (SPEC 10): connects, loads the rows (<c>list</c>) and
/// the settings and host facts (<c>settings.get</c>), follows the <c>rows</c> events, and when the
/// connection ends tries again with <see cref="ReconnectBackoff"/>. A version mismatch is kept as
/// <see cref="HostState.VersionMismatch"/> with the spec's message.
/// </summary>
/// <remarks>
/// Start it on the UI thread: the loop's continuations come back to the caller's
/// <see cref="SynchronizationContext"/>, so every property changes and every event is raised there.
/// Each subscriber is called on its own: one that throws is traced and skipped, and stops neither
/// the loop nor the other subscribers. An unexpected error from a connection ends only that
/// connection; the loop waits out the backoff and connects again.
/// </remarks>
internal sealed class HostConnection : IAsyncDisposable
{
    /// <summary>Shown in the status bar, and as the error of a request made without a host.</summary>
    public const string NotRunningText = "Host not running";

    private readonly IHostConnector _connector;
    private readonly TimeProvider _time;
    private readonly ReconnectBackoff _backoff = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private CancellationTokenSource? _wait;
    private bool _kick;
    private Task? _loop;
    private volatile IHostClient? _client;
    private volatile HostState _state = HostState.Connecting;
    private volatile IReadOnlyList<Row> _rows = [];
    private volatile IpcSettings? _settings;
    private volatile HostInfo? _info;
    private readonly Func<Task> _yield;
    private int _generation;

    /// <summary>Creates the connection; nothing happens until <see cref="Start"/>.</summary>
    /// <param name="connector">Opens connections to the host.</param>
    /// <param name="time">Clock for the reconnect waits.</param>
    /// <param name="yieldToCaller">
    /// Lets the caller's thread do other work while many events are queued. The app passes a
    /// background-priority dispatcher yield, so input and rendering go first. Default: <see cref="Task.Yield"/>.
    /// </param>
    public HostConnection(IHostConnector connector, TimeProvider time, Func<Task>? yieldToCaller = null)
    {
        _connector = connector;
        _time = time;
        _yield = yieldToCaller ?? (async () => await Task.Yield());
    }

    /// <summary>Events handled in a row before the loop yields (<see cref="YieldInterval"/> also ends a run).</summary>
    public const int EventsPerYield = 64;

    /// <summary>The longest run of events handled before the loop yields.</summary>
    public static readonly TimeSpan YieldInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// Counts the connections: it goes up each time a connection is made, before the state becomes
    /// <see cref="HostState.Connected"/>. An answer that came on an older connection is stale.
    /// </summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// The connection of <paramref name="generation"/> is still the open one. True already while
    /// that connection loads its rows (before <see cref="HostState.Connected"/>), false as soon as
    /// it starts closing.
    /// </summary>
    public bool IsCurrent(int generation) => _client is not null && generation == Generation;

    /// <summary>The current state.</summary>
    public HostState State => _state;

    /// <summary>The state for the status bar.</summary>
    public string StateText => _state switch
    {
        HostState.Connecting => "Connecting to the host…",
        HostState.Connected => "Host connected",
        HostState.VersionMismatch => IpcCodec.VersionMismatchMessage,
        _ => NotRunningText,
    };

    /// <summary>The host's rows, in its order; empty while not connected.</summary>
    public IReadOnlyList<Row> Rows => _rows;

    /// <summary>The settings, while connected.</summary>
    public IpcSettings? Settings => _settings;

    /// <summary>The host's read-only facts, while connected.</summary>
    public HostInfo? Info => _info;

    /// <summary><see cref="State"/> changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary><see cref="Rows"/> changed.</summary>
    public event EventHandler? RowsChanged;

    /// <summary><see cref="Settings"/> or <see cref="Info"/> changed.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>The host asked for a notification (SPEC 3.5).</summary>
    public event EventHandler<ToastEvent>? Toast;

    /// <summary>Console output for an attached viewer.</summary>
    public event EventHandler<OutputEvent>? Output;

    /// <summary>Starts the connection loop (once).</summary>
    public void Start()
    {
        if (_loop is null)
            _loop = RunAsync(_stop.Token);
    }

    /// <summary>Tries to connect now instead of after the current wait (after Start host).</summary>
    public void ReconnectNow()
    {
        CancellationTokenSource? wait;
        lock (_gate)
        {
            wait = _wait;
            if (wait is null)
                _kick = true;
        }
        try
        {
            wait?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Sends a request to the host.</summary>
    public async Task<HostResult> SendAsync(IpcRequest request, CancellationToken cancellationToken = default)
    {
        var client = _client;
        if (client is null)
            return new HostResult(false, NotRunningText, null);
        try
        {
            var response = await client.SendAsync(request, cancellationToken);
            return response.Ok
                ? new HostResult(true, null, response)
                : new HostResult(false, response.Error ?? "the host refused the request", response);
        }
        catch (IpcClosedException e)
        {
            return new HostResult(false, e.Message, null);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The connection broke under the request (a disposed client, a broken pipe, a
            // teardown race): for the caller that is the same as the host being gone.
            Trace.TraceWarning("claude-rc-windows: {0} failed: {1}", request.GetType().Name, e);
            return new HostResult(false, $"{NotRunningText} ({e.Message})", null);
        }
    }

    /// <summary>Reads the settings and host facts again (after <c>settings.set</c>).</summary>
    public async Task RefreshSettingsAsync()
    {
        var result = await SendAsync(new SettingsGetRequest());
        if (result.Response is { Ok: true } r)
            SetSettings(r.Settings, r.Info);
    }

    private async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                IHostClient client;
                try
                {
                    client = await _connector.ConnectAsync(stop);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e) when (!stop.IsCancellationRequested)
                {
                    if (e is not (TimeoutException or IOException or UnauthorizedAccessException))
                        Trace.TraceError("claude-rc-windows: connecting to the host failed: {0}", e);
                    SetState(HostState.NotRunning);
                    await WaitAsync(_backoff.Next(), stop);
                    continue;
                }

                await ServeAsync(client, stop);
            }
            catch (Exception e) when (!stop.IsCancellationRequested)
            {
                // Nothing above should throw; if something does, the tray still reconnects.
                Trace.TraceError("claude-rc-windows: host connection loop: {0}", e);
                SetState(HostState.NotRunning);
            }
            if (!stop.IsCancellationRequested)
                await WaitAsync(_backoff.Next(), stop);
        }
    }

    private async Task ServeAsync(IHostClient client, CancellationToken stop)
    {
        string? reason = null;
        try
        {
            // A rows event the host pushed while it answered may be read after the answer; the
            // next status change replaces it (rows events always carry every row).
            var list = await client.SendAsync(new ListRequest(), stop);
            var settings = await client.SendAsync(new SettingsGetRequest(), stop);
            Interlocked.Increment(ref _generation);
            _client = client;
            _backoff.Reset();
            lock (_gate)
                _kick = false;
            SetRows(list.Rows ?? []);
            SetSettings(settings.Settings, settings.Info);
            SetState(HostState.Connected);

            // ReadAllAsync completes at once while events are queued, so without a yield a flood
            // of output would hold the UI thread until the queue is empty.
            var run = 0;
            var runStart = _time.GetTimestamp();
            await foreach (var e in client.Events.ReadAllAsync(stop))
            {
                Dispatch(e);
                if (++run >= EventsPerYield || _time.GetElapsedTime(runStart) >= YieldInterval)
                {
                    await _yield();
                    run = 0;
                    runStart = _time.GetTimestamp();
                }
            }
        }
        catch (IpcClosedException e)
        {
            reason = e.Message;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception e) when (!stop.IsCancellationRequested)
        {
            // Unexpected: end this connection only; the loop connects again.
            Trace.TraceError("claude-rc-windows: host connection ended by an error: {0}", e);
            reason = e.Message;
        }
        finally
        {
            _client = null;
            reason = client.CloseReason ?? reason;
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                Trace.TraceWarning("claude-rc-windows: closing the host connection: {0}", e);
            }
            SetRows([]);
            SetSettings(null, null);
            if (!stop.IsCancellationRequested)
                SetState(reason == IpcCodec.VersionMismatchMessage ? HostState.VersionMismatch : HostState.NotRunning);
        }
    }

    private void Dispatch(IpcEvent e)
    {
        switch (e)
        {
            case RowsEvent rows:
                SetRows(rows.Rows);
                break;
            case ToastEvent toast:
                Raise(Toast, toast);
                break;
            case OutputEvent output:
                Raise(Output, output);
                break;
        }
    }

    /// <summary>Calls each subscriber on its own, so one that throws reaches neither the loop nor the others.</summary>
    private void Raise(EventHandler? handlers)
    {
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception e)
            {
                Trace.TraceError("claude-rc-windows: a host connection subscriber failed: {0}", e);
            }
        }
    }

    /// <inheritdoc cref="Raise(EventHandler?)"/>
    private void Raise<T>(EventHandler<T>? handlers, T args)
    {
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception e)
            {
                Trace.TraceError("claude-rc-windows: a host connection subscriber failed: {0}", e);
            }
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken stop)
    {
        var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
        bool skip;
        lock (_gate)
        {
            skip = _kick;
            _kick = false;
            _wait = skip ? null : wait;
        }
        try
        {
            if (!skip)
                await Task.Delay(delay, _time, wait.Token);
        }
        catch (OperationCanceledException)
        {
            // Woken by ReconnectNow, or stopping.
        }
        finally
        {
            lock (_gate)
                _wait = null;
            wait.Dispose();
        }
    }

    private void SetState(HostState state)
    {
        if (_state == state)
            return;
        _state = state;
        Raise(StateChanged);
    }

    private void SetRows(IReadOnlyList<Row> rows)
    {
        if (rows.Count == 0 && _rows.Count == 0)
            return;
        _rows = rows;
        Raise(RowsChanged);
    }

    private void SetSettings(IpcSettings? settings, HostInfo? info)
    {
        _settings = settings;
        _info = info;
        Raise(SettingsChanged);
    }

    /// <summary>Stops the loop and closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested)
            return;
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                // Stopping must always finish: Quit waits for it.
                Trace.TraceError("claude-rc-windows: host connection loop failed: {0}", e);
            }
        }
        _stop.Dispose();
    }
}
