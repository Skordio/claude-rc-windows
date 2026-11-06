using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeRc.Core.Hosting;
using ClaudeRc.Core.Ipc;
using ClaudeRc.Core.Paths;
using ClaudeRc.Core.Storage;

namespace ClaudeRc.Host.Tests;

/// <summary>
/// A host run in-process for one test (<see cref="HostApp"/>): its own temp data, log and profile
/// folders, a temp <c>.claude.json</c>, a random <c>--instance</c> (so the pipe and mutex never meet
/// the real host's), <c>claude_path</c> set to FakeClaude.exe, and timings scaled down by
/// <see cref="TimeScale"/>. Talks to the host through the real named pipe. On dispose it stops the
/// host, checks that no FakeClaude process is left, and deletes everything.
/// </summary>
internal sealed class TestHost : IAsyncDisposable
{
    /// <summary>Host timings in tests: SPEC values × this (backoff 5 s → 100 ms, start retry 60 s → 1.2 s, …).</summary>
    public const double TimeScale = 0.02;

    private readonly ConcurrentQueue<IpcEvent> _events = new();
    private readonly List<IpcClient> _clients = [];
    private Func<HostConfig, HostConfig>? _configure;
    private Task? _eventPump;

    private TestHost()
    {
        Root = Path.Combine(Path.GetTempPath(), "claude-rc-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
        Instance = "test-" + Guid.NewGuid().ToString("N")[..12];
        Options = HostOptions.Parse(
            ["--data-dir", Path.Combine(Root, "data"), "--log-dir", Path.Combine(Root, "logs"),
             "--claude-json", Path.Combine(Root, "profile", ".claude.json"), "--instance", Instance],
            HostDefaults.FromEnvironment());
        Directory.CreateDirectory(Profile);
    }

    public string Root { get; }
    public string Instance { get; }
    public HostOptions Options { get; }
    public string DataDir => Options.DataDir;
    public string LogDir => Options.LogDir;
    public string ClaudeJson => Options.ClaudeJsonPath;
    public string Profile => Path.Combine(Root, "profile");
    public HostApp? App { get; private set; }
    public IpcClient Client { get; private set; } = null!;
    public IReadOnlyCollection<IpcEvent> Events => _events;

    /// <summary>The host arguments that point everything at this test's temp folders.</summary>
    public IReadOnlyList<string> HostArguments =>
        ["--data-dir", DataDir, "--log-dir", LogDir, "--claude-json", ClaudeJson, "--instance", Instance];

    /// <summary>Creates the temp folders and settings, runs <paramref name="before"/>, then starts the host and connects.</summary>
    public static async Task<TestHost> StartAsync(Action<TestHost>? before = null, Func<HostConfig, HostConfig>? configure = null)
    {
        var host = new TestHost { _configure = configure };
        try
        {
            host.WriteSettings(FakeClaudeHarness.FakeClaudeExe);
            before?.Invoke(host);
            await host.StartHostAsync();
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    /// <summary>The config the in-process host runs with.</summary>
    public HostConfig Config()
    {
        var config = new HostConfig
        {
            Options = Options,
            UserProfile = Profile,
            Environment = SessionEnvironmentForTests,
            // Adoption: only FakeClaude counts as claude, so a test host never lists (let alone
            // touches) the user's own claude rc sessions.
            ExternalImageNames = [Path.GetFileName(FakeClaudeHarness.FakeClaudeExe)],
        }.WithTimeScale(TimeScale);
        return _configure is null ? config : _configure(config);
    }

    /// <summary>
    /// The registry environment (as the host uses) with PATH cut down to Windows' own folders, so
    /// nothing in a test can ever find the real claude on PATH. DOTNET_ROOT is kept for FakeClaude.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SessionEnvironmentForTests()
    {
        var env = new Dictionary<string, string>(FakeClaudeHarness.Environment(), StringComparer.OrdinalIgnoreCase);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        env["PATH"] = Path.Combine(windows, "System32") + ";" + windows;
        return env;
    }

    public async Task StartHostAsync()
    {
        App = HostApp.Start(Config());
        Client = await ConnectAsync();
        _eventPump = PumpEvents(Client);
    }

    /// <summary>Stops the host gracefully (as on quit) and starts a new one on the same folders.</summary>
    public async Task RestartHostAsync()
    {
        await StopHostAsync();
        _events.Clear();
        await StartHostAsync();
    }

    public async Task StopHostAsync()
    {
        foreach (var c in _clients)
            await c.DisposeAsync();
        _clients.Clear();
        if (_eventPump is not null)
            await _eventPump;
        if (App is not null)
            await App.DisposeAsync();
        App = null;
    }

    /// <summary>Another client on this host's pipe.</summary>
    public async Task<IpcClient> ConnectAsync()
    {
        var client = await IpcClient.ConnectAsync(Options.PipeName, TimeSpan.FromSeconds(10));
        _clients.Add(client);
        return client;
    }

    private async Task PumpEvents(IpcClient client)
    {
        try
        {
            await foreach (var e in client.Events.ReadAllAsync())
                _events.Enqueue(e);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public void WriteSettings(string? claudePath, bool notifications = true) =>
        new SettingsStore(DataDir).Save(new AppSettings(claudePath, notifications));

    /// <summary>A new folder under the test root (with a fakeclaude.json when given), as its normalised path.</summary>
    public string NewFolder(object? fakeConfig = null, string? name = null)
    {
        var path = Path.Combine(Root, "f", name ?? Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        if (fakeConfig is not null)
            ConfigureFolder(path, fakeConfig);
        return PathNormalizer.Normalize(path);
    }

    public static void ConfigureFolder(string folder, object fakeConfig) =>
        File.WriteAllText(Path.Combine(folder, "fakeclaude.json"), JsonSerializer.Serialize(fakeConfig));

    public static JsonObject Record(string folder, string name = "record.json")
    {
        var path = Path.Combine(folder, name);
        FakeClaudeHarness.WaitUntil(() => File.Exists(path), $"{name} written");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    public static string ReadText(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch (IOException) { return ""; }
    }

    public async Task<IpcResponse> Send(IpcRequest request) => await Client.SendAsync(request);

    public async Task<IpcResponse> SendOk(IpcRequest request)
    {
        var r = await Send(request);
        Assert.True(r.Ok, $"{request.Op} failed: {r.Error}");
        return r;
    }

    public async Task<IReadOnlyList<Row>> List() => (await SendOk(new ListRequest())).Rows!;

    /// <summary>Polls <c>list</c> until the folder's row matches (null: until it has no row).</summary>
    public async Task<Row?> WaitForRow(string path, Func<Row?, bool> condition, string what, int timeoutMs = 15_000)
    {
        var sw = Stopwatch.StartNew();
        Row? row = null;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            row = (await List()).FirstOrDefault(r => PathNormalizer.Comparer.Equals(r.Path, path));
            if (condition(row))
                return row;
            await Task.Delay(25);
        }
        Assert.Fail($"timed out after {timeoutMs} ms waiting for: {what}; last row: {Describe(row)}");
        return null;
    }

    public async Task<Row> WaitForStatus(string path, Core.FolderStatus status, int timeoutMs = 15_000) =>
        (await WaitForRow(path, r => r?.Status == status, $"{path} {status}", timeoutMs))!;

    /// <summary>Waits until an event matching <paramref name="condition"/> has been received (any time since the host started).</summary>
    public async Task<T> WaitForEvent<T>(Func<T, bool> condition, string what, int timeoutMs = 15_000) where T : IpcEvent
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (_events.OfType<T>().FirstOrDefault(condition) is { } found)
                return found;
            await Task.Delay(25);
        }
        Assert.Fail($"timed out after {timeoutMs} ms waiting for event: {what}");
        return null!;
    }

    /// <summary>Waits for a <c>rows</c> event in which the folder's row matches.</summary>
    public Task<RowsEvent> WaitForRowEvent(string path, Func<Row, bool> condition, string what, int timeoutMs = 15_000) =>
        WaitForEvent<RowsEvent>(e => e.Rows.Any(r => PathNormalizer.Comparer.Equals(r.Path, path) && condition(r)), what, timeoutMs);

    public static string Describe(Row? r) => r is null ? "(none)" : IpcCodec.Encode(new RowsEvent([r]));

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopHostAsync();
        }
        finally
        {
            var sw = Stopwatch.StartNew();
            List<int> left;
            while ((left = FakeClaudeHarness.RunningFakeClaudes()).Count > 0 && sw.ElapsedMilliseconds < 5000)
                await Task.Delay(50);
            for (var attempt = 0; attempt < 20 && Directory.Exists(Root); attempt++)
            {
                try { Directory.Delete(Root, recursive: true); }
                catch (IOException) { await Task.Delay(50); }
                catch (UnauthorizedAccessException) { await Task.Delay(50); }
            }
            Assert.True(left.Count == 0, $"FakeClaude processes left running: {string.Join(", ", left)}");
        }
    }
}
