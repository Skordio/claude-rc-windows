using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using ClaudeRc.Core.Ipc;

namespace ClaudeRc.Host.Ipc;

/// <summary>
/// One connected pipe client (SPEC 10): reads newline-delimited UTF-8 lines and hands each to the
/// host, and writes the host's responses and events through a bounded queue, so a slow client
/// never blocks the host. A client that falls too far behind, or sends a line over
/// <see cref="MaxLineBytes"/>, is disconnected.
/// </summary>
internal sealed class ClientConnection
{
    /// <summary>Longest accepted request line.</summary>
    public const int MaxLineBytes = 1 << 20;

    /// <summary>Messages queued for a client before it counts as stuck and is disconnected.</summary>
    public const int MaxQueuedMessages = 20_000;

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly NamedPipeServerStream _pipe;
    private readonly Channel<string> _outgoing = Channel.CreateBounded<string>(
        new BoundedChannelOptions(MaxQueuedMessages) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _abort = new();
    private volatile bool _closing;

    public ClientConnection(NamedPipeServerStream pipe, int id)
    {
        _pipe = pipe;
        Id = id;
    }

    /// <summary>A number for the host log.</summary>
    public int Id { get; }

    /// <summary>The host's per-client state (attached viewers); only touched under the host's lock.</summary>
    public Dictionary<string, Viewer> Viewers { get; } = new(Core.Paths.PathNormalizer.Comparer);

    /// <summary>Queues a response.</summary>
    public void Send(IpcResponse response) => SendLine(IpcCodec.Encode(response));

    /// <summary>Queues an event.</summary>
    public void Send(IpcEvent ipcEvent) => SendLine(IpcCodec.Encode(ipcEvent));

    /// <summary>Queues an encoded message line (without the newline). A full queue disconnects the client.</summary>
    public void SendLine(string line)
    {
        if (_closing)
            return;
        if (!_outgoing.Writer.TryWrite(line))
            Abort();
    }

    /// <summary>Sends what is queued, then closes the connection (e.g. after a version mismatch).</summary>
    public void CloseAfterFlush()
    {
        _closing = true;
        _outgoing.Writer.TryComplete();
    }

    /// <summary>Closes the connection at once.</summary>
    public void Abort()
    {
        _closing = true;
        _outgoing.Writer.TryComplete();
        try { _abort.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Serves the connection until it ends: <paramref name="onLine"/> gets every non-empty line and
    /// returns false to stop reading (the connection then closes once the queue is written).
    /// </summary>
    public async Task RunAsync(Func<ClientConnection, string, bool> onLine)
    {
        var writer = WriteLoop();
        try
        {
            await ReadLoop(onLine).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The client went away.
        }
        finally
        {
            // No more requests; flush what is queued (unless aborted), then close.
            CloseAfterFlush();
            await writer.ConfigureAwait(false);
            await _pipe.DisposeAsync().ConfigureAwait(false);
            _abort.Dispose();
        }
    }

    private async Task ReadLoop(Func<ClientConnection, string, bool> onLine)
    {
        var buffer = new byte[16384];
        var line = new MemoryStream();
        var token = _abort.Token;
        while (true)
        {
            var n = await _pipe.ReadAsync(buffer, token).ConfigureAwait(false);
            if (n == 0)
                return;
            var start = 0;
            for (var i = 0; i < n; i++)
            {
                if (buffer[i] != (byte)'\n')
                    continue;
                line.Write(buffer, start, i - start);
                start = i + 1;
                var text = Utf8NoBom.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
                line.SetLength(0);
                if (text.Length > 0 && !onLine(this, text))
                    return;
            }
            line.Write(buffer, start, n - start);
            if (line.Length > MaxLineBytes)
            {
                Send(new IpcResponse { Ok = false, Error = $"request line longer than {MaxLineBytes} bytes" });
                return;
            }
        }
    }

    private async Task WriteLoop()
    {
        try
        {
            await foreach (var line in _outgoing.Reader.ReadAllAsync(_abort.Token).ConfigureAwait(false))
            {
                await _pipe.WriteAsync(Utf8NoBom.GetBytes(line + "\n"), _abort.Token).ConfigureAwait(false);
                if (_outgoing.Reader.Count == 0)
                    await _pipe.FlushAsync(_abort.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // Broken pipe or aborted: stop writing; the read side ends too.
        }
        finally
        {
            Abort();
        }
    }
}

/// <summary>
/// A client attached to a folder's console (SPEC 10 <c>attach</c>). It outlives the session: when
/// the folder's next session starts, the host attaches it again and sends a fresh replay.
/// </summary>
internal sealed class Viewer(string path, int columns, int rows)
{
    public string Path { get; } = path;
    public int Columns { get; set; } = columns;
    public int Rows { get; set; } = rows;
    public IDisposable? Subscription { get; set; }
}
