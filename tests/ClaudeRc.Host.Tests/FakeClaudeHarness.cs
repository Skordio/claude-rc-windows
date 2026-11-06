using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeRc.Host.Sessions;

// Integration tests start real processes and assert that none is left over, so they must not
// run in parallel with each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ClaudeRc.Host.Tests;

/// <summary>
/// One FakeClaude run for a test: a fresh folder under %TEMP%\claude-rc-tests with a
/// fakeclaude.json, sessions started on FakeClaude.exe from the test output, and a check on
/// dispose that no FakeClaude process is left running.
/// </summary>
public sealed class FakeClaudeHarness : IDisposable
{
    private readonly List<Session> _sessions = [];

    public FakeClaudeHarness()
    {
        Folder = Path.Combine(Path.GetTempPath(), "claude-rc-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Folder);
    }

    /// <summary>The session's working folder.</summary>
    public string Folder { get; }

    public static string FakeClaudeExe => Path.Combine(AppContext.BaseDirectory, "FakeClaude.exe");

    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    public string File(string name) => Path.Combine(Folder, name);

    /// <summary>Writes fakeclaude.json.</summary>
    public void Configure(object config) =>
        System.IO.File.WriteAllText(File("fakeclaude.json"), JsonSerializer.Serialize(config));

    /// <summary>The environment FakeClaude gets: from the registry, as the host does, plus DOTNET_ROOT if the tests run on a private .NET.</summary>
    public static IReadOnlyDictionary<string, string> Environment()
    {
        var env = new Dictionary<string, string>(SessionEnvironment.FromRegistry(), StringComparer.OrdinalIgnoreCase);
        if (System.Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root)
            env["DOTNET_ROOT"] = root;
        return env;
    }

    /// <param name="wrapInput">Wraps the pseudo-console's input pipe (to simulate a console that stops reading).</param>
    public Session Start(IReadOnlyList<string>? args = null, int columns = 120, int rows = 40, Func<Stream, Stream>? wrapInput = null)
    {
        var session = Session.Start(new SessionStartInfo
        {
            Program = FakeClaudeExe,
            Arguments = args ?? ["rc", "--name", "test"],
            WorkingDirectory = Folder,
            Environment = Environment(),
            Columns = columns,
            Rows = rows,
            WrapInput = wrapInput,
        });
        _sessions.Add(session);
        return session;
    }

    /// <summary>The record FakeClaude writes (waits for it).</summary>
    /// <remarks>
    /// Right after FakeClaude's File.Move, antivirus or the indexer may still hold the new file, so
    /// a read that fails with a sharing error is tried again for up to a second.
    /// </remarks>
    public JsonObject Record(string name = "record.json")
    {
        var path = File(name);
        WaitUntil(() => System.IO.File.Exists(path), $"{name} written");
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return JsonNode.Parse(System.IO.File.ReadAllText(path))!.AsObject();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && sw.ElapsedMilliseconds < 1000)
            {
                Thread.Sleep(25);
            }
        }
    }

    public string ReadText(string name) => System.IO.File.Exists(File(name)) ? System.IO.File.ReadAllText(File(name)) : "";

    public static void WaitUntil(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail($"timed out after {timeoutMs} ms waiting for: {what}");
            Thread.Sleep(25);
        }
    }

    public static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>FakeClaude processes started from this test output folder.</summary>
    public static List<int> RunningFakeClaudes()
    {
        var mine = new List<int>();
        foreach (var p in Process.GetProcessesByName("FakeClaude"))
        {
            using (p)
            {
                try
                {
                    if (!p.HasExited && string.Equals(p.MainModule?.FileName, FakeClaudeExe, StringComparison.OrdinalIgnoreCase))
                        mine.Add(p.Id);
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Exited meanwhile, or not ours to inspect.
                }
            }
        }
        return mine;
    }

    public void Dispose()
    {
        foreach (var s in _sessions)
            s.Dispose();
        var sw = Stopwatch.StartNew();
        List<int> left;
        while ((left = RunningFakeClaudes()).Count > 0 && sw.ElapsedMilliseconds < 5000)
            Thread.Sleep(50);
        for (var attempt = 0; attempt < 20 && Directory.Exists(Folder); attempt++)
        {
            try { Directory.Delete(Folder, recursive: true); }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
        Assert.True(left.Count == 0, $"FakeClaude processes left running: {string.Join(", ", left)}");
    }
}
