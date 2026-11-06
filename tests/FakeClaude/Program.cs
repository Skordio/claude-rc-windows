// FakeClaude: stands in for claude.exe in the host integration tests (SPEC 17).
//
// Behaviour comes from fakeclaude.json in the working folder. Every key is optional; with no
// file at all it just runs until stopped. Relative file names are relative to the working folder.
//
//   record          file: written first, {"args":[…],"cwd":"…","pid":N,"env":{name:value|null},"child_pid":N?}
//   record_env      names of environment variables to put into "env" (PATH is always included)
//   spawn_child     true: start a child FakeClaude (own hidden console, ignores Ctrl+C, sleeps
//                   forever) before writing the record, so a test can check Job Object cleanup
//   replay          file whose bytes are written to stdout unchanged (e.g. a tests/fixtures/*.raw)
//   print           text written to stdout after the replay
//   prompt          text written to stdout after that; input lines are then read (see input_file)
//   input_file      file: every input line that arrives is appended to it (reading starts
//                   when prompt or input_file is set)
//   size_file       file: the console size, appended as "<cols>x<rows>" at start and on every change
//   ctrl_c_file     file: "ctrl-c" is appended each time Ctrl+C arrives
//   ignore_ctrl_c   true: Ctrl+C is ignored (to test the 5 s kill); otherwise Ctrl+C exits with 0
//   exit_after_ms   exit on its own after this long, with exit_code (default 0), after writing exit_text
//
// "--fake-child" on the command line is the child mode used by spawn_child.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace FakeClaude;

internal static partial class Program
{
    private static readonly ManualResetEventSlim Stop = new();
    private static readonly object FileLock = new();

    private static int Main(string[] args)
    {
        if (args is ["--fake-child"])
            return Child();

        var config = Config.Load(Path.Combine(Environment.CurrentDirectory, "fakeclaude.json"));
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);
        EnableVirtualTerminal();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            if (config.CtrlCFile is { } f)
                Append(f, "ctrl-c\n");
            if (!config.IgnoreCtrlC)
                Stop.Set();
        };

        int? childPid = config.SpawnChild ? SpawnChild() : null;
        if (config.Record is { } record)
            WriteRecord(record, args, config.RecordEnv, childPid);

        var stdout = Console.OpenStandardOutput();
        if (config.Replay is { } replay)
            stdout.Write(File.ReadAllBytes(replay));
        if (config.Print is { } print)
            stdout.Write(Encoding.UTF8.GetBytes(print));
        if (config.Prompt is { } prompt)
            stdout.Write(Encoding.UTF8.GetBytes(prompt));
        stdout.Flush();

        if (config.Prompt is not null || config.InputFile is not null)
            StartThread(() => ReadInput(config.InputFile));
        if (config.SizeFile is { } sizeFile)
            StartThread(() => WatchSize(sizeFile));

        var exitCode = 0;
        if (config.ExitAfterMs is { } ms)
        {
            if (!Stop.Wait(ms))
            {
                if (config.ExitText is { } text)
                {
                    stdout.Write(Encoding.UTF8.GetBytes(text));
                    stdout.Flush();
                }
                exitCode = config.ExitCode;
            }
        }
        else
        {
            Stop.Wait();
        }
        stdout.Flush();
        Environment.Exit(exitCode);
        return exitCode;
    }

    private static int Child()
    {
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static int SpawnChild()
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--fake-child");
        using var child = Process.Start(psi)!;
        return child.Id;
    }

    private static void WriteRecord(string path, string[] args, IReadOnlyList<string> envNames, int? childPid)
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in envNames.Append("PATH"))
            env[name] = Environment.GetEnvironmentVariable(name);
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["args"] = args,
            ["cwd"] = Environment.CurrentDirectory,
            ["pid"] = Environment.ProcessId,
            ["env"] = env,
            ["child_pid"] = childPid,
        });
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    private static void ReadInput(string? file)
    {
        var input = Console.In;
        while (!Stop.IsSet)
        {
            string? line;
            try { line = input.ReadLine(); }
            catch (IOException) { line = null; }
            if (line is null)
            {
                // Ctrl+C aborts a pending console read; keep reading.
                Thread.Sleep(50);
                continue;
            }
            if (file is not null)
                Append(file, line + "\n");
        }
    }

    private static void WatchSize(string file)
    {
        var last = "";
        while (!Stop.IsSet)
        {
            string size;
            try { size = $"{Console.WindowWidth}x{Console.WindowHeight}"; }
            catch (IOException) { size = "?"; }
            if (size != last)
            {
                Append(file, size + "\n");
                last = size;
            }
            Thread.Sleep(50);
        }
    }

    private static void Append(string file, string text)
    {
        lock (FileLock)
            File.AppendAllText(file, text);
    }

    private static void StartThread(Action action) => new Thread(() => action()) { IsBackground = true }.Start();

    private static void EnableVirtualTerminal()
    {
        const uint EnableVirtualTerminalProcessing = 0x0004;
        var handle = GetStdHandle(-11);
        if (GetConsoleMode(handle, out var mode) != 0)
            _ = SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll")]
    private static partial int GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll")]
    private static partial int SetConsoleMode(nint hConsoleHandle, uint dwMode);
}

internal sealed record Config
{
    public string? Record { get; init; }
    public IReadOnlyList<string> RecordEnv { get; init; } = [];
    public bool SpawnChild { get; init; }
    public string? Replay { get; init; }
    public string? Print { get; init; }
    public string? Prompt { get; init; }
    public string? InputFile { get; init; }
    public string? SizeFile { get; init; }
    public string? CtrlCFile { get; init; }
    public bool IgnoreCtrlC { get; init; }
    public int? ExitAfterMs { get; init; }
    public int ExitCode { get; init; }
    public string? ExitText { get; init; }

    public static Config Load(string path)
    {
        if (!File.Exists(path))
            return new Config();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string? FilePath(string name) => Str(name) is { } s ? Path.GetFullPath(s) : null;
        bool Bool(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        int? Int(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        return new Config
        {
            Record = FilePath("record"),
            RecordEnv = root.TryGetProperty("record_env", out var names)
                ? [.. names.EnumerateArray().Select(n => n.GetString()!)]
                : [],
            SpawnChild = Bool("spawn_child"),
            Replay = FilePath("replay"),
            Print = Str("print"),
            Prompt = Str("prompt"),
            InputFile = FilePath("input_file"),
            SizeFile = FilePath("size_file"),
            CtrlCFile = FilePath("ctrl_c_file"),
            IgnoreCtrlC = Bool("ignore_ctrl_c"),
            ExitAfterMs = Int("exit_after_ms"),
            ExitCode = Int("exit_code") ?? 0,
            ExitText = Str("exit_text"),
        };
    }
}
