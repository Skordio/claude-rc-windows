namespace ClaudeRc.Host.Sessions;

/// <summary>
/// Writes a pseudo-console's input on its own thread, from a queue, so callers never block: a
/// console that stops draining its input pipe (a hung conhost, a large paste while it is busy)
/// must not hold up the host's lock or the stop sequence of SPEC 6.4. Chunks are written in the
/// order they were queued. The thread owns the stream and disposes it when it ends.
/// </summary>
internal sealed class InputWriter : IDisposable
{
    /// <summary>Bytes that may wait in the queue (including the chunk being written) before input is refused.</summary>
    public const int DefaultMaxQueuedBytes = 4 << 20;

    private readonly Stream _stream;
    private readonly int _maxQueuedBytes;
    private readonly object _gate = new();
    private readonly Queue<byte[]> _queue = new();
    private int _queuedBytes;
    private bool _closed;

    /// <param name="stream">The pseudo-console's input; owned from now on.</param>
    /// <param name="name">For the thread's name.</param>
    /// <param name="maxQueuedBytes">See <see cref="DefaultMaxQueuedBytes"/>.</param>
    public InputWriter(Stream stream, string name, int maxQueuedBytes = DefaultMaxQueuedBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxQueuedBytes, 1);
        _stream = stream;
        _maxQueuedBytes = maxQueuedBytes;
        new Thread(Run) { IsBackground = true, Name = name + " input" }.Start();
    }

    /// <summary>
    /// Queues <paramref name="bytes"/> and returns at once. False when the writer is closed (disposed,
    /// or the pipe broke) or the queue is full because the console is not reading.
    /// </summary>
    public bool TryWrite(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (_closed || _queuedBytes + (long)bytes.Length > _maxQueuedBytes)
                return false;
            if (bytes.IsEmpty)
                return true;
            _queue.Enqueue(bytes.ToArray());
            _queuedBytes += bytes.Length;
            Monitor.Pulse(_gate);
            return true;
        }
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                byte[] chunk;
                lock (_gate)
                {
                    while (_queue.Count == 0 && !_closed)
                        Monitor.Wait(_gate);
                    if (_closed)
                        return;
                    chunk = _queue.Dequeue();
                }
                try
                {
                    _stream.Write(chunk);
                    _stream.Flush();
                }
#pragma warning disable CA1031 // A failing write ends the input, never the host.
                catch (Exception)
#pragma warning restore CA1031
                {
                    Close();
                    return;
                }
                lock (_gate)
                    _queuedBytes -= chunk.Length;
            }
        }
        finally
        {
            _stream.Dispose();
        }
    }

    private void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _queue.Clear();
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Refuses further input and drops what is queued. Does not wait: a write in progress ends when
    /// the pipe breaks (the pseudo-console closing), and the thread then releases the stream.
    /// </summary>
    public void Dispose() => Close();
}
