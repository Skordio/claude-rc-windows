using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using ClaudeRc.Core;
using ClaudeRc.Core.Claude;
using ClaudeRc.Core.Hosting;
using ClaudeRc.Core.Ipc;
using ClaudeRc.Core.Logging;
using ClaudeRc.Core.Paths;
using ClaudeRc.Core.Storage;
using ClaudeRc.Core.Supervision;
using ClaudeRc.Host.Ipc;
using ClaudeRc.Host.Sessions;

namespace ClaudeRc.Host;

/// <summary>
/// Everything the host is given from outside. <see cref="Program"/> builds it from the command
/// line; tests build it directly to run a host in-process (see <see cref="WithTimeScale"/>).
/// </summary>
internal sealed record HostConfig
{
    /// <summary>Paths and names (data, logs, <c>.claude.json</c>, pipe, mutex).</summary>
    public required HostOptions Options { get; init; }

    /// <summary>The user's profile folder, where claude's native install is looked for (SPEC 6.1).</summary>
    public required string UserProfile { get; init; }

    /// <summary>Clock for the supervisor, the tick loop, the prompt scan and the logs.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Supervision timings (SPEC 4, 5).</summary>
    public SupervisorOptions Supervisor { get; init; } = new();

    /// <summary>How often the screens are scanned for prompts (SPEC 8: 2 s).</summary>
    public TimeSpan PromptScanInterval { get; init; } = PromptScanner.DefaultInterval;

    /// <summary>Wait after Ctrl+C before the job is terminated (SPEC 6.4: 5 s). Real time: it waits for a real process.</summary>
    public TimeSpan StopGrace { get; init; } = Session.DefaultStopGrace;

    /// <summary>The environment for each start, also searched for claude (SPEC 6.3: fresh from the registry).</summary>
    public Func<IReadOnlyDictionary<string, string>> Environment { get; init; } = SessionEnvironment.FromRegistry;

    /// <summary>
    /// The test seam for time: multiplies every supervision timing (backoff, failing window, stable
    /// uptime, missing-folder re-checks, starting timeout, start stagger, start-error retry) and the
    /// prompt-scan interval by <paramref name="factor"/>, so integration tests see the whole
    /// schedule in real processes without waiting minutes. <see cref="StopGrace"/> is not scaled.
    /// </summary>
    public HostConfig WithTimeScale(double factor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        TimeSpan S(TimeSpan t) => TimeSpan.FromTicks((long)(t.Ticks * factor));
        var s = Supervisor;
        return this with
        {
            Supervisor = s with
            {
                Backoff = [.. s.Backoff.Select(S)],
                StableUptime = S(s.StableUptime),
                FailingWindow = S(s.FailingWindow),
                MissingFastInterval = S(s.MissingFastInterval),
                MissingFastPeriod = S(s.MissingFastPeriod),
                MissingSlowInterval = S(s.MissingSlowInterval),
                StartingTimeout = S(s.StartingTimeout),
                StartStagger = S(s.StartStagger),
                StartErrorRetry = S(s.StartErrorRetry),
            },
            PromptScanInterval = S(PromptScanInterval),
        };
    }
}

/// <summary>ClaudeRcHost.exe exit codes.</summary>
internal static class HostExitCodes
{
    /// <summary>Stopped normally.</summary>
    public const int Ok = 0;

    /// <summary>Could not start, or failed while running (see host.log).</summary>
    public const int Failed = 1;

    /// <summary>Unknown option or bad value.</summary>
    public const int InvalidArguments = 2;

    /// <summary>Another host holds the single-instance mutex (SPEC 9.4).</summary>
    public const int AlreadyRunning = 3;
}

/// <summary>
/// The host: carries out the <see cref="Supervisor"/>'s decisions with real sessions, and serves
/// the pipe (SPEC 5, 6, 10).
/// </summary>
/// <remarks>
/// <para>
/// One lock guards the supervisor, the sessions and the clients' viewer state. Every input (pipe
/// request, session exit, prompt scan, clock tick) takes it, calls the supervisor and carries out
/// the effects in order; a start's own result (started / failed) is fed back in the same pass.
/// Nothing slow runs under the lock: stops wait for the exit on their own task, and
/// <c>claude --version</c> runs outside it.
/// </para>
/// <para>
/// The tick loop calls <see cref="Supervisor.Tick"/> once a second, or sooner at
/// <see cref="Supervisor.NextDueAt"/>, and is woken by every other input.
/// </para>
/// </remarks>
internal sealed class HostApp : IAsyncDisposable
{
    /// <summary>SPEC 5.2: the failing row shows the last 20 console lines.</summary>
    public const int LastLinesCount = 20;

    private const int MaxLogLines = 10_000;
    private static readonly TimeSpan MinTickDelay = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan MaxTickDelay = TimeSpan.FromSeconds(1);

    private readonly HostConfig _config;
    private readonly HostOptions _o;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Supervisor _supervisor;
    private readonly Dictionary<string, Session> _sessions = new(PathNormalizer.Comparer);
    private readonly Dictionary<string, IReadOnlyList<string>> _lastLines = new(PathNormalizer.Comparer);
    private readonly Dictionary<string, RollingLog> _folderLogs = new(PathNormalizer.Comparer);
    private readonly List<ClientConnection> _clients = [];
    private readonly List<Task> _background = [];
    private readonly RollingLog _hostLog;
    private readonly FoldersStore _foldersStore;
    private readonly SettingsStore _settingsStore;
    private readonly ClaudeVersionProbe _versionProbe = new();
    private readonly PromptScanner _scanner;
    private readonly PipeServer _server;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AppSettings _settings;
    private string? _foldersError;
    private Task? _tickLoop;
    private bool _rowsDirty;
    private readonly List<ToastEvent> _toasts = [];
    private int _disposed;

    private HostApp(HostConfig config)
    {
        _config = config;
        _o = config.Options;
        _time = config.Time;
        _hostLog = LogFiles.HostLog(_o.LogDir, _time);
        _foldersStore = new FoldersStore(_o.DataDir);
        _settingsStore = new SettingsStore(_o.DataDir);
        _settings = new AppSettings();
        _supervisor = new Supervisor(_time, Directory.Exists, config.Supervisor);
        _scanner = new PromptScanner(_time,
            onReady: p => Run(s => s.ReadySeen(p)),
            onPrompt: (p, prompt) => Run(s => s.PromptChanged(p, prompt)),
            config.PromptScanInterval);
        _server = new PipeServer(_o.PipeName, new SecurityIdentifier(_o.UserSid), OnLine, OnConnected, OnClosed, HostLog);
    }

    /// <summary>The host's version (informational version of the assembly).</summary>
    public static string Version { get; } =
        typeof(HostApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(HostApp).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>Completes when the host can no longer serve (its tick loop failed); the host should then exit.</summary>
    public Task Completion => _completion.Task;

    /// <summary>
    /// Starts the host: loads settings and folders, starts every ON folder (staggered, SPEC 5.4),
    /// opens the pipe and starts the clock. The pipe accepts clients when this returns.
    /// </summary>
    /// <exception cref="IOException">The pipe could not be created.</exception>
    public static HostApp Start(HostConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var app = new HostApp(config);
        try
        {
            app.StartCore();
        }
        catch
        {
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
        return app;
    }

    private void StartCore()
    {
        HostLog(Inv($"host started: version {Version}, pid {Environment.ProcessId}, instance {_o.Instance ?? "(none)"}, data {_o.DataDir}, logs {_o.LogDir}, claude.json {_o.ClaudeJsonPath}"));
        try
        {
            _settings = _settingsStore.Load();
        }
        catch (DataFileException e)
        {
            HostLog("settings.json cannot be read, using the defaults until settings are saved: " + e.Message);
        }

        IReadOnlyList<FolderEntry> folders = [];
        try
        {
            folders = _foldersStore.Load();
        }
        catch (Exception e) when (e is DataFileException or IOException or UnauthorizedAccessException)
        {
            _foldersError = $"{_foldersStore.FilePath} cannot be read ({e.Message}); the host will not change it. Fix or delete the file, then restart the host.";
            HostLog(_foldersError);
        }
        HostLog(Inv($"{folders.Count} folder(s) loaded, {folders.Count(f => f.On)} ON"));

        // The pipe is created first, so a second host fails before it starts anything; clients
        // that connect at once wait on the lock until the saved list is loaded (a request served
        // before Load could save a list without the loaded folders).
        lock (_lock)
        {
            _server.Start();
            // M3 adds the process scan, whose first result goes to ExternalChanged before Load.
            Run(s => s.Load(folders));
        }
        _tickLoop = Task.Run(TickLoop);
    }

    // ------------------------------------------------------------------ supervisor plumbing

    /// <summary>Calls the supervisor under the lock and carries out the effects.</summary>
    private SupervisorResult Run(Func<Supervisor, SupervisorResult> call)
    {
        SupervisorResult result;
        lock (_lock)
        {
            result = call(_supervisor);
            Execute(result.Effects);
            FlushEvents();
        }
        Volatile.Read(ref _wake).TrySetResult();
        return result;
    }

    private void Execute(IReadOnlyList<SupervisorEffect> effects)
    {
        var queue = new Queue<SupervisorEffect>(effects);
        while (queue.TryDequeue(out var effect))
        {
            switch (effect)
            {
                case StartSession start:
                    foreach (var next in StartSession(start.Path).Effects)
                        queue.Enqueue(next);
                    break;
                case StopSession stop:
                    StopSession(stop.Path);
                    break;
                case StopExternal external:
                    HostLog(Inv($"take over of pid {external.Pid} in {external.Path} is not available until M3"));
                    break;
                case FolderChanged or FolderRemoved:
                    _rowsDirty = true;
                    break;
                case FoldersChanged changed:
                    SaveFolders(changed.Folders);
                    break;
                case FolderLog log:
                    FolderLog(log.Path, log.Message);
                    break;
                case Notify notify:
                    if (_settings.Notifications)
                        _toasts.Add(new ToastEvent(notify.Path, notify.Status, notify.Text));
                    break;
            }
        }
    }

    // Rows first, so a toast never names a row the tray app has not seen yet.
    private void FlushEvents()
    {
        if (_rowsDirty)
        {
            _rowsDirty = false;
            Broadcast(new RowsEvent(Rows()));
        }
        foreach (var toast in _toasts)
            Broadcast(toast);
        _toasts.Clear();
    }

    private List<Row> Rows() =>
        _supervisor.GetRows().Select(r =>
            r.Status == FolderStatus.Failing && _lastLines.TryGetValue(r.Path, out var lines) && lines.Count > 0
                ? r with { LastLines = lines }
                : r).ToList();

    private void Broadcast(IpcEvent e)
    {
        var line = IpcCodec.Encode(e);
        foreach (var client in _clients)
            client.SendLine(line);
    }

    private void SaveFolders(IReadOnlyList<FolderEntry> folders)
    {
        if (_foldersError is not null)
            return;
        try
        {
            _foldersStore.Save(folders);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            HostLog("could not save folders.json: " + e.Message);
        }
    }

    private void FolderLog(string path, string message)
    {
        try
        {
            if (!_folderLogs.TryGetValue(path, out var log))
                _folderLogs[path] = log = LogFiles.FolderLog(_o.FolderLogDir, path, _time);
            log.Write(message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            HostLog($"could not write the folder log of {path}: {e.Message}");
        }
    }

    private RollingLog FolderLogFor(string path) =>
        _folderLogs.TryGetValue(path, out var log) ? log : _folderLogs[path] = LogFiles.FolderLog(_o.FolderLogDir, path, _time);

    private void HostLog(string message)
    {
        try
        {
            _hostLog.Write(message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine("host log: " + e.Message);
        }
    }

    // ------------------------------------------------------------------ sessions

    /// <summary>SPEC 6.2, 6.1, 6.3 in that order: presets, find claude, launch. Returns the supervisor's answer to the outcome.</summary>
    private SupervisorResult StartSession(string path)
    {
        Session session;
        try
        {
            if (Launch(path, out var error) is not { } started)
                return _supervisor.StartFailed(path, error!);
            session = started;
        }
#pragma warning disable CA1031 // Every failed start must reach the supervisor, or the folder stays "launching".
        catch (Exception e)
#pragma warning restore CA1031
        {
            HostLog($"start of {path} failed: {e}");
            return _supervisor.StartFailed(path, "could not start claude: " + e.Message);
        }

        _sessions[path] = session;
        _lastLines.Remove(path);
        _scanner.Add(path, session);
        foreach (var client in _clients)
        {
            if (client.Viewers.TryGetValue(path, out var viewer))
                Subscribe(client, viewer, session);
        }
        _background.RemoveAll(t => t.IsCompleted);
        _background.Add(WatchExit(path, session));
        return _supervisor.SessionStarted(path, session.ProcessId);
    }

    private Session? Launch(string path, out string? error)
    {
        var preset = new ClaudeJsonPresets(_o.ClaudeJsonPath).Ensure(path);
        if (preset.Outcome == PresetOutcome.Error)
        {
            error = preset.Error;
            return null;
        }
        if (preset.Outcome == PresetOutcome.Written)
            FolderLog(path, "presets written to " + _o.ClaudeJsonPath);

        IReadOnlyDictionary<string, string> environment;
        try
        {
            environment = _config.Environment();
        }
        catch (Win32Exception e)
        {
            error = "could not read the user environment: " + e.Message;
            return null;
        }
        var located = Locate(environment);
        if (located.Launch is not { } launch)
        {
            error = located.Error;
            return null;
        }

        var name = _supervisor.GetRows().First(r => PathNormalizer.Comparer.Equals(r.Path, path)).Name;
        try
        {
            error = null;
            return Session.Start(new SessionStartInfo
            {
                Program = launch.Program,
                Arguments = [.. launch.PrefixArguments, "rc", "--name", name],
                WorkingDirectory = path,
                Environment = environment,
            });
        }
        catch (Exception e) when (e is SessionStartException or ArgumentException or Win32Exception or IOException)
        {
            error = "could not start claude: " + e.Message;
            return null;
        }
    }

    private LocateResult Locate(IReadOnlyDictionary<string, string> environment) =>
        new ClaudeLocator(new RealFileProbe(), _config.UserProfile)
            .Locate(_settings.ClaudePath, environment.TryGetValue("PATH", out var p) ? p : null);

    private async Task WatchExit(string path, Session session)
    {
        var code = await session.Exited.ConfigureAwait(false);
        Run(s =>
        {
            if (!_sessions.TryGetValue(path, out var current) || !ReferenceEquals(current, session))
                return new SupervisorResult([]);
            _sessions.Remove(path);
            _scanner.Remove(path);
            _lastLines[path] = session.GetLastLines(LastLinesCount);
            foreach (var client in _clients)
            {
                if (client.Viewers.TryGetValue(path, out var viewer))
                {
                    viewer.Subscription?.Dispose();
                    viewer.Subscription = null;
                }
            }
            return s.SessionExited(path, code);
        });
        session.Dispose();
    }

    private void StopSession(string path)
    {
        if (!_sessions.TryGetValue(path, out var session))
        {
            HostLog($"stop requested for {path}, which has no session");
            return;
        }
        _background.Add(StopAndLog(path, session));
    }

    private async Task StopAndLog(string path, Session session)
    {
        var result = await session.StopAsync(_config.StopGrace).ConfigureAwait(false);
        if (result.Killed)
        {
            lock (_lock)
                FolderLog(path, Inv($"did not exit within {_config.StopGrace.TotalSeconds:0.#} s of Ctrl+C; its job was terminated"));
        }
    }

    // ------------------------------------------------------------------ viewers

    /// <summary>
    /// Resizes the session to the viewer, then sends the replay and streams the output. Output that
    /// arrives while the replay is being queued waits behind it, so the order is always right.
    /// </summary>
    private static void Subscribe(ClientConnection client, Viewer viewer, Session session)
    {
        viewer.Subscription?.Dispose();
        session.Resize(viewer.Columns, viewer.Rows);
        var gate = new Lock();
        var replaySent = false;
        var early = new List<byte[]>();
        var path = viewer.Path;
        viewer.Subscription = session.Attach(chunk =>
        {
            lock (gate)
            {
                if (replaySent)
                    client.Send(new OutputEvent(path, chunk.ToArray()));
                else
                    early.Add(chunk.ToArray());
            }
        }, out var replay);
        lock (gate)
        {
            client.Send(new OutputEvent(path, replay));
            foreach (var chunk in early)
                client.Send(new OutputEvent(path, chunk));
            replaySent = true;
        }
    }

    // ------------------------------------------------------------------ clock

    private async Task TickLoop()
    {
        var token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _wake, wake);
                DateTimeOffset? due;
                lock (_lock)
                {
                    Execute(_supervisor.Tick().Effects);
                    FlushEvents();
                    due = _supervisor.NextDueAt;
                }
                var delay = MaxTickDelay;
                if (due is { } at)
                {
                    var until = at - _time.GetUtcNow();
                    if (until < delay)
                        delay = until < MinTickDelay ? MinTickDelay : until;
                }
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                var sleep = Task.Delay(delay, _time, cts.Token);
                await Task.WhenAny(sleep, wake.Task).ConfigureAwait(false);
                await cts.CancelAsync().ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // The host must log why it stops serving.
        catch (Exception e)
#pragma warning restore CA1031
        {
            HostLog("tick loop failed: " + e);
            _completion.TrySetException(e);
        }
    }

    // ------------------------------------------------------------------ pipe

    private void OnConnected(ClientConnection client)
    {
        lock (_lock)
            _clients.Add(client);
    }

    private void OnClosed(ClientConnection client)
    {
        lock (_lock)
        {
            _clients.Remove(client);
            foreach (var viewer in client.Viewers.Values)
                viewer.Subscription?.Dispose();
            client.Viewers.Clear();
        }
    }

    private bool OnLine(ClientConnection client, string line)
    {
        IpcRequest request;
        try
        {
            request = IpcCodec.DecodeRequest(line);
        }
        catch (IpcVersionException e)
        {
            client.Send(new IpcResponse { Ok = false, Error = e.Message });
            client.CloseAfterFlush();
            return false;
        }
        catch (IpcFormatException e)
        {
            client.Send(new IpcResponse { Id = TryGetId(line), Ok = false, Error = e.Message });
            return true;
        }

        try
        {
            Handle(client, request);
        }
#pragma warning disable CA1031 // One bad request must not end the connection or the host.
        catch (Exception e)
#pragma warning restore CA1031
        {
            HostLog($"request {request.Op} failed: {e}");
            client.Send(Fail(request, "internal error: " + e.Message));
        }
        return true;
    }

    private static int? TryGetId(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("id", out var id)
                && id.TryGetInt32(out var value) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IpcResponse Ok(IpcRequest request) => new() { Id = request.Id, Ok = true };

    private static IpcResponse Fail(IpcRequest request, string error) => new() { Id = request.Id, Ok = false, Error = error };

    private void Handle(ClientConnection client, IpcRequest request)
    {
        switch (request)
        {
            case SettingsGetRequest:
                var send = SendSettingsAsync(client, request);
                lock (_lock)
                    _background.Add(send);
                return;
            case TakeoverRequest:
                client.Send(Fail(request, "take over is not available yet: it comes with adoption of external sessions in M3"));
                return;
        }

        lock (_lock)
        {
            var response = request switch
            {
                ListRequest => Ok(request) with { Rows = Rows() },
                AddRequest add => Add(add),
                SetRequest set => FolderRequest(set, set.Path, p => _supervisor.SetOn(p, set.On)),
                RestartRequest restart => FolderRequest(restart, restart.Path, _supervisor.Restart),
                RemoveRequest remove => FolderRequest(remove, remove.Path, _supervisor.Remove),
                AttachRequest attach => Attach(client, attach),
                InputRequest input => Input(input),
                ResizeRequest resize => Resize(client, resize),
                DetachRequest detach => Detach(client, detach),
                LogRequest log => Log(log),
                SettingsSetRequest set => SetSettings(set),
                _ => Fail(request, "unknown request"),
            };
            // Attach has already queued its response, ahead of the replay.
            if (response is not null)
                client.Send(response);
        }
    }

    /// <summary>A path from a request: the folder's own path when it is in the list, else the normalised path.</summary>
    private string? Resolve(string? raw, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "path is empty";
            return null;
        }
        if (_supervisor.Get(raw) is { } known)
            return known.Path;
        if (!Path.IsPathFullyQualified(raw))
        {
            error = $"path must be absolute: {raw}";
            return null;
        }
        string normalized;
        try
        {
            normalized = PathNormalizer.Normalize(raw);
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            error = $"invalid path: {raw} ({e.Message})";
            return null;
        }
        return _supervisor.Get(normalized)?.Path ?? normalized;
    }

    private IpcResponse Add(AddRequest request)
    {
        if (_foldersError is not null)
            return Fail(request, _foldersError);
        if (Resolve(request.Path, out var error) is not { } path)
            return Fail(request, error!);
        if (!Directory.Exists(path))
            return Fail(request, $"not an existing folder: {path}");
        var result = Run(s => s.Add(path));
        return result.Ok ? Ok(request) : Fail(request, result.Error!);
    }

    private IpcResponse FolderRequest(IpcRequest request, string rawPath, Func<string, SupervisorResult> call)
    {
        if (_foldersError is not null)
            return Fail(request, _foldersError);
        if (Resolve(rawPath, out var error) is not { } path)
            return Fail(request, error!);
        var result = Run(_ => call(path));
        return result.Ok ? Ok(request) : Fail(request, result.Error!);
    }

    private bool TryGetSession(IpcRequest request, string rawPath, out string path, out Session session, out IpcResponse? failure)
    {
        session = null!;
        path = "";
        failure = null;
        if (Resolve(rawPath, out var error) is not { } p)
        {
            failure = Fail(request, error!);
            return false;
        }
        path = p;
        if (_supervisor.Get(p) is null)
        {
            failure = Fail(request, $"folder is not in the list: {p}");
            return false;
        }
        if (!_sessions.TryGetValue(p, out session!))
        {
            failure = Fail(request, $"no claude rc session is running in {p}");
            return false;
        }
        return true;
    }

    private static string? CheckSize(int columns, int rows) =>
        columns is < 1 or > Session.MaxColumns || rows is < 1 or > Session.MaxRows
            ? Inv($"size must be 1-{Session.MaxColumns} columns and 1-{Session.MaxRows} rows")
            : null;

    private IpcResponse? Attach(ClientConnection client, AttachRequest request)
    {
        if (!TryGetSession(request, request.Path, out var path, out var session, out var failure))
            return failure;
        if (CheckSize(request.Cols, request.Rows) is { } sizeError)
            return Fail(request, sizeError);
        if (!client.Viewers.TryGetValue(path, out var viewer))
            client.Viewers[path] = viewer = new Viewer(path, request.Cols, request.Rows);
        viewer.Columns = request.Cols;
        viewer.Rows = request.Rows;
        client.Send(Ok(request));
        Subscribe(client, viewer, session);
        return null;
    }

    private IpcResponse Input(InputRequest request)
    {
        if (!TryGetSession(request, request.Path, out _, out var session, out var failure))
            return failure!;
        session.WriteInput(request.Data);
        return Ok(request);
    }

    private IpcResponse Resize(ClientConnection client, ResizeRequest request)
    {
        if (!TryGetSession(request, request.Path, out var path, out var session, out var failure))
            return failure!;
        if (CheckSize(request.Cols, request.Rows) is { } sizeError)
            return Fail(request, sizeError);
        if (client.Viewers.TryGetValue(path, out var viewer))
        {
            viewer.Columns = request.Cols;
            viewer.Rows = request.Rows;
        }
        session.Resize(request.Cols, request.Rows);
        return Ok(request);
    }

    private IpcResponse Detach(ClientConnection client, DetachRequest request)
    {
        if (Resolve(request.Path, out var error) is not { } path)
            return Fail(request, error!);
        if (client.Viewers.Remove(path, out var viewer))
            viewer.Subscription?.Dispose();
        return Ok(request);
    }

    private IpcResponse Log(LogRequest request)
    {
        if (Resolve(request.Path, out var error) is not { } path)
            return Fail(request, error!);
        var lines = FolderLogFor(path).ReadLastLines(Math.Clamp(request.Lines, 0, MaxLogLines));
        return Ok(request) with { Lines = lines };
    }

    private IpcResponse SetSettings(SettingsSetRequest request)
    {
        var claudePath = string.IsNullOrWhiteSpace(request.ClaudePath) ? null : request.ClaudePath.Trim().Trim('"');
        if (claudePath is not null && !Path.IsPathFullyQualified(claudePath))
            return Fail(request, $"claude path must be a full path: {claudePath}");
        var settings = new AppSettings(claudePath, request.Notifications);
        try
        {
            _settingsStore.Save(settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail(request, "could not save settings.json: " + e.Message);
        }
        _settings = settings;
        HostLog($"settings changed: claude_path {settings.ClaudePath ?? "(auto-detect)"}, notifications {(settings.Notifications ? "on" : "off")}");
        return Ok(request);
    }

    private async Task SendSettingsAsync(ClientConnection client, IpcRequest request)
    {
        await Task.Yield();
        AppSettings settings;
        lock (_lock)
            settings = _settings;
        string? claudePath = null, claudeVersion = null, claudeError = null;
        try
        {
            var located = new ClaudeLocator(new RealFileProbe(), _config.UserProfile)
                .Locate(settings.ClaudePath, _config.Environment().TryGetValue("PATH", out var p) ? p : null);
            if (located.Launch is { } launch)
            {
                claudePath = launch.FoundAt;
                claudeVersion = await _versionProbe.GetVersionAsync(launch).ConfigureAwait(false);
            }
            else
            {
                claudeError = located.Error;
            }
        }
        catch (Exception e) when (e is Win32Exception or IOException or ArgumentException)
        {
            claudeError = e.Message;
        }
        client.Send(Ok(request) with
        {
            Settings = new IpcSettings(settings.ClaudePath, settings.Notifications),
            Info = new HostInfo(Version, _o.DataDir, _o.LogDir, claudePath, claudeVersion, claudeError),
        });
    }

    // ------------------------------------------------------------------ shutdown

    /// <summary>
    /// Host quit (SPEC 6.4): closes the pipe, stops every session (Ctrl+C, wait, kill; not counted
    /// as exits) and waits for them. Saved desired states are kept for the next start.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_tickLoop is not null)
            await _tickLoop.ConfigureAwait(false);
        await _server.DisposeAsync().ConfigureAwait(false);

        Session[] sessions;
        lock (_lock)
        {
            Execute(_supervisor.Shutdown().Effects);
            sessions = [.. _sessions.Values];
        }
        try
        {
            await Task.WhenAll(sessions.Select(s => s.Exited))
                .WaitAsync(_config.StopGrace + TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            HostLog("sessions did not end in time; killing them");
        }
        _scanner.Dispose();
        Task[] background;
        lock (_lock)
            background = [.. _background];
        try
        {
            await Task.WhenAll(background).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Shutdown goes on whatever a leftover task did.
        catch (Exception e)
#pragma warning restore CA1031
        {
            HostLog("shutdown: " + e.Message);
        }
        foreach (var s in sessions)
            s.Dispose();
        HostLog("host stopped");
        _completion.TrySetResult();
        _stop.Dispose();
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
