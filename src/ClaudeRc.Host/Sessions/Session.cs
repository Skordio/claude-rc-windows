using System.Text;
using ClaudeRc.Core.Prompts;
using ClaudeRc.Host.Terminal;

namespace ClaudeRc.Host.Sessions;

/// <summary>How to start a <see cref="Session"/>.</summary>
public sealed record SessionStartInfo
{
    /// <summary>Full path of the program (claude.exe, or node.exe for an old npm install).</summary>
    public required string Program { get; init; }

    /// <summary>
    /// Arguments, passed as a list (SPEC 6.3): <c>[prefix arguments…, "rc", "--name", name]</c>.
    /// No shell ever sees them.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Working directory: the folder's normalised path, exactly as the <c>.claude.json</c> key is built from it.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The whole environment (see <see cref="SessionEnvironment.FromRegistry"/>); nothing is inherited.</summary>
    public required IReadOnlyDictionary<string, string> Environment { get; init; }

    /// <summary>Initial width (SPEC 6.3: 120).</summary>
    public int Columns { get; init; } = Session.DefaultColumns;

    /// <summary>Initial height (SPEC 6.3: 40).</summary>
    public int Rows { get; init; } = Session.DefaultRows;

    /// <summary>Lines of scrollback the emulator keeps (SPEC 3.3: 5,000).</summary>
    public int Scrollback { get; init; } = TerminalEmulator.DefaultScrollback;
}

/// <summary>A session could not be started; the message is meant for the row's error field.</summary>
/// <param name="message">What failed.</param>
public sealed class SessionStartException(string message) : Exception(message);

/// <summary>How a stop ended.</summary>
/// <param name="ExitCode">The process's exit code (<see cref="Session.KilledExitCode"/> when killed).</param>
/// <param name="Killed">True when it did not exit within the grace period and the job was terminated.</param>
public readonly record struct StopResult(int ExitCode, bool Killed);

/// <summary>Something whose current screen can be read (a session, or a fake in tests).</summary>
public interface IScreenSource
{
    /// <summary>The current screen rows, top to bottom.</summary>
    IReadOnlyList<ScreenRow> GetScreen();
}

/// <summary>
/// One <c>claude rc</c> process and its children (SPEC 2, 6): a pseudo-console with a headless
/// terminal emulator fed from its output, a Job Object that takes every child process down with
/// it, input, resize, output streaming to attached viewers, and the stop sequence of SPEC 6.4.
/// </summary>
/// <remarks>
/// When the main process exits (on its own or stopped), whatever is left in its job is killed,
/// the pseudo-console is closed, and the output is read to EOF before <see cref="Exited"/>
/// completes. Thread-safe.
/// </remarks>
public sealed class Session : IDisposable, IScreenSource
{
    /// <summary>SPEC 6.3: 120×40.</summary>
    public const int DefaultColumns = 120;

    /// <summary>SPEC 6.3: 120×40.</summary>
    public const int DefaultRows = 40;

    /// <summary>Largest accepted width.</summary>
    public const int MaxColumns = 1000;

    /// <summary>Largest accepted height.</summary>
    public const int MaxRows = 500;

    /// <summary>Exit code of a session whose job was terminated.</summary>
    public const int KilledExitCode = -1;

    /// <summary>SPEC 6.4: wait 5 s after Ctrl+C before terminating the job.</summary>
    public static readonly TimeSpan DefaultStopGrace = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(3);

    private readonly ConPtyProcess _pty;
    private readonly object _screenLock = new();
    private readonly TerminalEmulator _terminal;
    private readonly List<Action<ReadOnlyMemory<byte>>> _subscribers = [];
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _outputDone = new();
    private readonly object _stopLock = new();
    private Task<StopResult>? _stop;
    private volatile bool _killed;
    private int _disposed;

    private Session(ConPtyProcess pty, SessionStartInfo info)
    {
        _pty = pty;
        _terminal = new TerminalEmulator(info.Columns, info.Rows, info.Scrollback);
        StartedAt = DateTimeOffset.Now;
        new Thread(ReadOutput) { IsBackground = true, Name = $"session {pty.ProcessId} output" }.Start();
        new Thread(WaitForExit) { IsBackground = true, Name = $"session {pty.ProcessId} exit" }.Start();
    }

    /// <summary>The main process's id.</summary>
    public int ProcessId => _pty.ProcessId;

    /// <summary>When the process was started.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Completes with the exit code once the process has exited and its output has been read.</summary>
    public Task<int> Exited => _exited.Task;

    /// <summary>True once <see cref="Exited"/> has completed.</summary>
    public bool HasExited => _exited.Task.IsCompleted;

    /// <summary>Starts a session.</summary>
    /// <exception cref="SessionStartException">The folder does not exist, or the program could not be started.</exception>
    /// <exception cref="ArgumentException">Invalid size, program path or environment.</exception>
    public static Session Start(SessionStartInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        CheckSize(info.Columns, info.Rows);
        if (!Path.IsPathFullyQualified(info.Program))
            throw new ArgumentException($"program must be a full path: {info.Program}", nameof(info));
        if (!Directory.Exists(info.WorkingDirectory))
            throw new SessionStartException($"folder not found: {info.WorkingDirectory}");
        if (!File.Exists(info.Program))
            throw new SessionStartException($"program not found: {info.Program}");

        var commandLine = CommandLine.Build(info.Program, info.Arguments);
        var environment = SessionEnvironment.ToBlock(info.Environment);
        var pty = ConPtyProcess.Start(info.Program, commandLine, info.WorkingDirectory, environment,
            (short)info.Columns, (short)info.Rows);
        return new Session(pty, info);
    }

    private static void CheckSize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, MaxColumns);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, MaxRows);
    }

    // ------------------------------------------------------------------ threads

    private void ReadOutput()
    {
        var buffer = new byte[16384];
        try
        {
            int n;
            while ((n = _pty.Output.Read(buffer, 0, buffer.Length)) > 0)
                OnOutput(buffer.AsSpan(0, n).ToArray());
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The pipe broke or was closed: the same as EOF.
        }
        finally
        {
            _outputDone.Set();
        }
    }

    private void OnOutput(byte[] chunk)
    {
        lock (_screenLock)
        {
            _terminal.Feed(chunk);
            for (var i = _subscribers.Count - 1; i >= 0; i--)
            {
                try
                {
                    _subscribers[i](chunk);
                }
#pragma warning disable CA1031 // A failing viewer must not stop the session's output.
                catch (Exception)
#pragma warning restore CA1031
                {
                    _subscribers.RemoveAt(i);
                }
            }
        }
    }

    private void WaitForExit()
    {
        var code = _pty.WaitForExit();
        // The session is over: nothing it started may outlive it (SPEC 6.3).
        _pty.TerminateJob(code);
        // conhost flushes its last output, exits, and the output pipe reaches EOF
        // (m0-findings 1: output can arrive after the process has exited).
        _pty.ClosePseudoConsole();
        _outputDone.Wait(OutputDrainTimeout);
        _exited.TrySetResult(code);
    }

    // ------------------------------------------------------------------ control

    /// <summary>Sends text to the pseudo-console as UTF-8 (viewer keystrokes, SPEC 10 <c>input</c>). Ignored after exit.</summary>
    public void WriteInput(string text) => WriteInput(Encoding.UTF8.GetBytes(text));

    /// <summary>Sends raw bytes to the pseudo-console. Ignored after exit.</summary>
    public void WriteInput(ReadOnlySpan<byte> bytes)
    {
        if (!HasExited)
            _pty.WriteInput(bytes);
    }

    /// <summary>Resizes the pseudo-console and the emulator. Ignored after exit.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 1–<see cref="MaxColumns"/> × 1–<see cref="MaxRows"/>.</exception>
    public void Resize(int columns, int rows)
    {
        CheckSize(columns, rows);
        if (HasExited)
            return;
        lock (_screenLock)
            _terminal.Resize(columns, rows);
        // conhost repaints the whole screen at the new size right after this.
        _pty.Resize((short)columns, (short)rows);
    }

    /// <summary>
    /// SPEC 6.4: sends Ctrl+C (0x03), waits up to <paramref name="grace"/> (default 5 s) for the
    /// process to exit, then terminates the job. Calling it again returns the same stop.
    /// </summary>
    public Task<StopResult> StopAsync(TimeSpan? grace = null)
    {
        lock (_stopLock)
            return _stop ??= StopCore(grace ?? DefaultStopGrace);
    }

    private async Task<StopResult> StopCore(TimeSpan grace)
    {
        if (!HasExited)
        {
            WriteInput([0x03]);
            var first = await Task.WhenAny(_exited.Task, Task.Delay(grace)).ConfigureAwait(false);
            if (first != _exited.Task && !HasExited)
            {
                _killed = true;
                _pty.TerminateJob(KilledExitCode);
            }
        }
        var code = await _exited.Task.ConfigureAwait(false);
        return new StopResult(code, _killed);
    }

    /// <summary>Process ids in the session's job right now (the main process and its children).</summary>
    public IReadOnlyList<int> JobProcessIds() => _pty.JobProcessIds();

    // ------------------------------------------------------------------ screen

    /// <summary>The emulator's current screen.</summary>
    public IReadOnlyList<ScreenRow> GetScreen()
    {
        lock (_screenLock)
            return _terminal.GetScreen();
    }

    /// <summary>
    /// The last <paramref name="count"/> non-blank lines of scrollback and screen, soft-wrapped
    /// rows joined (SPEC 5.2: the failing row shows the last 20 console lines).
    /// </summary>
    public IReadOnlyList<string> GetLastLines(int count)
    {
        IReadOnlyList<ScreenRow> back, screen;
        lock (_screenLock)
        {
            back = _terminal.GetScrollback();
            screen = _terminal.GetScreen();
        }
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var row in back.Concat(screen))
        {
            current.Append(row.Text);
            if (row.Wrapped)
                continue;
            var line = current.ToString().TrimEnd();
            if (line.Length > 0)
                lines.Add(line);
            current.Clear();
        }
        if (current.ToString().TrimEnd() is { Length: > 0 } rest)
            lines.Add(rest);
        return lines.Count > count ? lines[^count..] : lines;
    }

    /// <summary>
    /// Starts streaming output to <paramref name="onOutput"/> and returns, in
    /// <paramref name="replay"/>, the VT bytes that rebuild the current screen and scrollback
    /// (SPEC 3.3). Nothing is lost or repeated between the two. The callback runs on the
    /// session's output thread under its screen lock: it must only queue the chunk, never block.
    /// Dispose the result to detach.
    /// </summary>
    public IDisposable Attach(Action<ReadOnlyMemory<byte>> onOutput, out byte[] replay)
    {
        ArgumentNullException.ThrowIfNull(onOutput);
        lock (_screenLock)
        {
            replay = _terminal.Serialize();
            _subscribers.Add(onOutput);
        }
        return new Subscription(this, onOutput);
    }

    private sealed class Subscription(Session session, Action<ReadOnlyMemory<byte>> callback) : IDisposable
    {
        public void Dispose()
        {
            lock (session._screenLock)
                session._subscribers.Remove(callback);
        }
    }

    /// <summary>Kills the session if it is still running, waits for it to end, and releases everything.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (!HasExited)
        {
            _killed = true;
            _pty.TerminateJob(KilledExitCode);
            _exited.Task.Wait(TimeSpan.FromSeconds(10));
        }
        _pty.Dispose();
    }
}
