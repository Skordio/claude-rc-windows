using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading.Channels;

namespace ClaudeRc.Core.Ipc;

/// <summary>The connection to the host ended (closed by either side, or broken).</summary>
/// <param name="message">Why; the host's own error text when it closed the connection with one (e.g. a version mismatch).</param>
public sealed class IpcClosedException(string message) : IOException(message);

/// <summary>
/// A client of the host's pipe (SPEC 10): sends requests and matches responses by <c>id</c>, and
/// hands every event (<c>rows</c>, <c>output</c>, <c>toast</c>) to <see cref="Events"/>. Used by the
/// debug CLI, the tests and the tray app. Thread-safe.
/// </summary>
public sealed class IpcClient : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly NamedPipeClientStream _pipe;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<IpcResponse>> _pending = new();
    private readonly Channel<IpcEvent> _events = Channel.CreateUnbounded<IpcEvent>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _readLoop;
    private int _nextId;
    private string? _closeReason;

    private IpcClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _readLoop = Task.Run(ReadLoop);
    }

    /// <summary>Events from the host, in order. Completes when the connection ends.</summary>
    public ChannelReader<IpcEvent> Events => _events.Reader;

    /// <summary>Completes when the connection has ended.</summary>
    public Task Closed => _readLoop;

    /// <summary>Why the connection ended, once it has.</summary>
    public string? CloseReason => Volatile.Read(ref _closeReason);

    /// <summary>
    /// Connects to the host's pipe, retrying until <paramref name="timeout"/> while it does not exist
    /// yet or all its instances are busy. The pipe must be owned by the current user's SID.
    /// </summary>
    /// <param name="pipeName">The pipe name without <c>\\.\pipe\</c> (<c>HostOptions.PipeName</c>).</param>
    /// <param name="timeout">How long to keep trying.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <exception cref="TimeoutException">No host answered in time.</exception>
    /// <exception cref="UnauthorizedAccessException">The pipe is not owned by the current user.</exception>
    public static Task<IpcClient> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("the current user has no SID");
        return ConnectAsync(pipeName, timeout, user, cancellationToken);
    }

    /// <summary>
    /// <see cref="ConnectAsync(string, TimeSpan, CancellationToken)"/> with the owner the pipe must have.
    /// Not <c>PipeOptions.CurrentUserOnly</c>: that compares the pipe's owner with the token's
    /// default owner, which in an elevated process is BUILTIN\Administrators, so an elevated client
    /// refused the host's pipe (whose owner is always the user's SID, SPEC 12).
    /// </summary>
    internal static async Task<IpcClient> ConnectAsync(string pipeName, TimeSpan timeout, SecurityIdentifier user,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                var left = deadline - DateTime.UtcNow;
                await pipe.ConnectAsync((int)Math.Clamp(left.TotalMilliseconds, 1, 1000), cancellationToken).ConfigureAwait(false);
                // Refuse a pipe of the same name that another account created. Only the user (or an
                // account with the restore privilege, i.e. an administrator) can make the user its owner.
                EnsureOwnedBy(pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier, user);
                return new IpcClient(pipe);
            }
            catch (Exception e) when (e is TimeoutException or IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException($"the host is not running (no answer on pipe {pipeName})", e);
                // A missing pipe fails at once (IOException/FileNotFound): wait a little before retrying.
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>Throws unless the pipe's <paramref name="owner"/> is <paramref name="user"/>.</summary>
    /// <exception cref="UnauthorizedAccessException">Another owner, or none.</exception>
    internal static void EnsureOwnedBy(SecurityIdentifier? owner, SecurityIdentifier user)
    {
        if (owner != user)
            throw new UnauthorizedAccessException(
                $"the pipe is owned by {owner?.Value ?? "nobody"}, not by the current user {user.Value}");
    }

    /// <summary>Sends a request (its <c>Id</c> is replaced by a fresh one) and waits for the host's answer.</summary>
    /// <exception cref="IpcClosedException">The connection ended before the answer came.</exception>
    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        if (_readLoop.IsCompleted)
            Fail(tcs);
        try
        {
            await WriteLineAsync(IpcCodec.Encode(request with { Id = id }), cancellationToken).ConfigureAwait(false);
            using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
                return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Writes one raw line (without the newline) to the pipe. For tests of the host's input handling.</summary>
    public async Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        var bytes = Utf8NoBom.GetBytes(line + "\n");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            throw new IpcClosedException(CloseReason ?? "the connection to the host was closed");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoop()
    {
        string reason = "the host closed the connection";
        try
        {
            using var reader = new StreamReader(_pipe, Utf8NoBom, false, 65536, leaveOpen: true);
            while (await reader.ReadLineAsync(_closing.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                    continue;
                IpcMessage message;
                try
                {
                    message = IpcCodec.DecodeHostMessage(line);
                }
                catch (IpcVersionException e)
                {
                    reason = e.Message;
                    break;
                }
                catch (IpcFormatException)
                {
                    continue; // not something this client understands; skip it
                }
                switch (message)
                {
                    case IpcResponse { Id: { } id } response when _pending.TryGetValue(id, out var tcs):
                        tcs.TrySetResult(response);
                        break;
                    case IpcResponse { Id: null, Error: { } error }:
                        reason = error; // connection-level error; the host closes next
                        break;
                    case IpcEvent e:
                        _events.Writer.TryWrite(e);
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            if (!_closing.IsCancellationRequested)
                reason = "the connection to the host broke: " + e.Message;
            else
                reason = "the connection was closed";
        }
        finally
        {
            Volatile.Write(ref _closeReason, reason);
            _events.Writer.TryComplete();
            foreach (var tcs in _pending.Values)
                Fail(tcs);
        }
    }

    private void Fail(TaskCompletionSource<IpcResponse> tcs) =>
        tcs.TrySetException(new IpcClosedException(CloseReason ?? "the connection to the host was closed"));

    /// <summary>Closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_closing.IsCancellationRequested)
            return;
        await _closing.CancelAsync().ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        _closing.Dispose();
        _writeLock.Dispose();
    }
}
