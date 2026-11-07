using System.Threading.Channels;
using ClaudeRc.Core.Ipc;

namespace ClaudeRc.Tray.Hosting;

/// <summary>One open connection to the host's pipe (SPEC 10). <see cref="IpcClient"/> in the app, a fake in tests.</summary>
internal interface IHostClient : IAsyncDisposable
{
    /// <summary>Events from the host, in order. Completes when the connection ends.</summary>
    ChannelReader<IpcEvent> Events { get; }

    /// <summary>Completes when the connection has ended.</summary>
    Task Closed { get; }

    /// <summary>Why the connection ended, once it has (e.g. <see cref="IpcCodec.VersionMismatchMessage"/>).</summary>
    string? CloseReason { get; }

    /// <summary>Sends a request and waits for the answer.</summary>
    /// <exception cref="IpcClosedException">The connection ended first.</exception>
    Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Opens connections to the host.</summary>
internal interface IHostConnector
{
    /// <summary>Connects once.</summary>
    /// <exception cref="TimeoutException">No host answered (it is not running).</exception>
    Task<IHostClient> ConnectAsync(CancellationToken cancellationToken);
}

/// <summary>Connects to the host's named pipe with <see cref="IpcClient"/>.</summary>
/// <param name="pipeName">The pipe name (<see cref="TrayOptions.PipeName"/>).</param>
internal sealed class PipeHostConnector(string pipeName) : IHostConnector
{
    /// <summary>How long one attempt waits for the pipe.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromMilliseconds(500);

    /// <inheritdoc/>
    public async Task<IHostClient> ConnectAsync(CancellationToken cancellationToken)
    {
        var client = await IpcClient.ConnectAsync(pipeName, AttemptTimeout, cancellationToken).ConfigureAwait(false);
        return new PipeHostClient(client);
    }

    private sealed class PipeHostClient(IpcClient client) : IHostClient
    {
        public ChannelReader<IpcEvent> Events => client.Events;

        public Task Closed => client.Closed;

        public string? CloseReason => client.CloseReason;

        public Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken cancellationToken = default) =>
            client.SendAsync(request, cancellationToken);

        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
}

/// <summary>Grows the wait between connection attempts: 0.5, 1, 2, 4, then 5 s; reset after a connection.</summary>
internal sealed class ReconnectBackoff
{
    private static readonly TimeSpan First = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(5);
    private TimeSpan _next = First;

    /// <summary>The next wait.</summary>
    public TimeSpan Next()
    {
        var d = _next;
        _next = TimeSpan.FromTicks(Math.Min(_next.Ticks * 2, Max.Ticks));
        return d;
    }

    /// <summary>Starts again from the shortest wait.</summary>
    public void Reset() => _next = First;
}
