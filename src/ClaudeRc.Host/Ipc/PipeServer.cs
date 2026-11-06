using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ClaudeRc.Host.Ipc;

/// <summary>
/// The host's named pipe (SPEC 10, 12): <c>\\.\pipe\claude-rc-windows-&lt;SID&gt;[-instance]</c>, owned
/// by the user and open only to the user and SYSTEM. Serves any number of clients at once; each
/// one is a <see cref="ClientConnection"/>.
/// </summary>
internal sealed class PipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly PipeSecurity _security;
    private readonly Func<ClientConnection, string, bool> _onLine;
    private readonly Action<ClientConnection> _onConnected;
    private readonly Action<ClientConnection> _onClosed;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly Dictionary<ClientConnection, Task> _clients = [];
    private NamedPipeServerStream? _listening;
    private Task? _acceptLoop;
    private int _nextId;

    /// <param name="pipeName">Pipe name without <c>\\.\pipe\</c>.</param>
    /// <param name="user">The only account (besides SYSTEM) allowed to connect.</param>
    /// <param name="onLine">Gets every request line; returns false to stop reading from that client.</param>
    /// <param name="onConnected">A client connected (before its first line).</param>
    /// <param name="onClosed">A client's connection ended.</param>
    /// <param name="log">Host log.</param>
    public PipeServer(string pipeName, SecurityIdentifier user, Func<ClientConnection, string, bool> onLine,
        Action<ClientConnection> onConnected, Action<ClientConnection> onClosed, Action<string> log)
    {
        _pipeName = pipeName;
        _security = CreateSecurity(user);
        _onLine = onLine;
        _onConnected = onConnected;
        _onClosed = onClosed;
        _log = log;
    }

    /// <summary>
    /// The pipe's security: owner = the user; access for the user and SYSTEM only, nothing inherited.
    /// </summary>
    public static PipeSecurity CreateSecurity(SecurityIdentifier user)
    {
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Creates the pipe and starts accepting clients. The first instance is created here, so
    /// clients can connect as soon as this returns.
    /// </summary>
    /// <exception cref="IOException">The pipe already exists (another process holds the name).</exception>
    public void Start()
    {
        // FirstPipeInstance: fail rather than join a pipe someone else created under our name.
        _listening = Create(PipeOptions.FirstPipeInstance);
        _acceptLoop = AcceptLoop();
    }

    private NamedPipeServerStream Create(PipeOptions extra) =>
        NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | extra, 0, 0, _security);

    private async Task AcceptLoop()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            var pipe = _listening!;
            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException e)
            {
                // A client that connected and vanished at once; make a fresh instance.
                _log("pipe: connection failed: " + e.Message);
                await pipe.DisposeAsync().ConfigureAwait(false);
                if (!TryCreateNext())
                    break;
                continue;
            }

            if (!TryCreateNext())
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            var client = new ClientConnection(pipe, Interlocked.Increment(ref _nextId));
            lock (_lock)
                _clients[client] = Serve(client);
        }
    }

    private bool TryCreateNext()
    {
        try
        {
            _listening = Create(PipeOptions.None);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log("pipe: cannot create another pipe instance: " + e.Message);
            return false;
        }
    }

    private async Task Serve(ClientConnection client)
    {
        await Task.Yield();
        try
        {
            _onConnected(client);
            await client.RunAsync(_onLine).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A failing client must never take the server down.
        catch (Exception e)
#pragma warning restore CA1031
        {
            _log($"pipe: client {client.Id} failed: {e}");
            client.Abort();
        }
        finally
        {
            lock (_lock)
                _clients.Remove(client);
            _onClosed(client);
        }
    }

    /// <summary>Stops accepting, disconnects every client and waits for them to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested)
            return;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_acceptLoop is not null)
            await _acceptLoop.ConfigureAwait(false);
        if (_listening is not null)
            await _listening.DisposeAsync().ConfigureAwait(false);
        Task[] running;
        lock (_lock)
        {
            foreach (var c in _clients.Keys)
                c.Abort();
            running = [.. _clients.Values];
        }
        await Task.WhenAll(running).ConfigureAwait(false);
        _stop.Dispose();
    }
}
