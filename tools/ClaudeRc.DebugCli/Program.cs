// ClaudeRc.DebugCli: drives a running ClaudeRcHost.exe over its pipe, for M1 testing by hand
// (PLAN 1). Internal tool; never shipped (SPEC 15: no CLI in v1).
using System.Globalization;
using System.Text;
using ClaudeRc.Core.Hosting;
using ClaudeRc.Core.Ipc;

namespace ClaudeRc.DebugCli;

internal static class Program
{
    private const int Ok = 0, HostError = 1, Usage = 2, NoHost = 3;

    private const string UsageText = """
        usage: ClaudeRc.DebugCli [--instance <suffix>] <command> [arguments]

          list                      show every row
          add <path>                add a folder (ON) and start it
          set <path> on|off         turn a folder ON or OFF
          restart <path>            clear the crash count and start now
          remove <path>             remove a folder (only when OFF)
          takeover <path>           take over an external claude rc
          attach <path> [cols rows] print the console stream; keys are sent to the session;
                                    Ctrl+] (or the end of redirected input) detaches
          input <path> <text>       send keystrokes; escapes: \r \n \t \e \\ \xHH
          log <path> [lines]        last lines of the folder log (default 50)
          settings                  show settings and host info
          watch                     print events until Ctrl+C
        """;

    private static async Task<int> Main(string[] args)
    {
        string? instance = null;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--instance" && i + 1 < args.Length)
                instance = args[++i];
            else if (args[i].StartsWith("--instance=", StringComparison.Ordinal))
                instance = args[i]["--instance=".Length..];
            else
                rest.Add(args[i]);
        }
        if (rest.Count == 0 || !TryParse(rest, out var command))
            return Fail(Usage, UsageText);

        string pipeName;
        try
        {
            pipeName = HostOptions.Parse(instance is null ? [] : ["--instance", instance], HostDefaults.FromEnvironment()).PipeName;
        }
        catch (ArgumentException e)
        {
            return Fail(Usage, e.Message);
        }

        IpcClient client;
        try
        {
            client = await IpcClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException e)
        {
            return Fail(NoHost, e.Message);
        }
        await using (client)
        {
            try
            {
                return await command(client);
            }
            catch (IpcClosedException e)
            {
                return Fail(HostError, e.Message);
            }
        }
    }

    private delegate Task<int> Command(IpcClient client);

    private static bool TryParse(List<string> a, out Command command)
    {
        command = null!;
        switch (a[0], a.Count)
        {
            case ("list", 1): command = List; return true;
            case ("add", 2): command = c => Simple(c, new AddRequest(a[1])); return true;
            case ("set", 3) when a[2] is "on" or "off": command = c => Simple(c, new SetRequest(a[1], a[2] == "on")); return true;
            case ("restart", 2): command = c => Simple(c, new RestartRequest(a[1])); return true;
            case ("remove", 2): command = c => Simple(c, new RemoveRequest(a[1])); return true;
            case ("takeover", 2): command = c => Simple(c, new TakeoverRequest(a[1])); return true;
            case ("input", 3): command = c => Simple(c, new InputRequest(a[1], Unescape(a[2]))); return true;
            case ("attach", 2): command = c => Attach(c, a[1], null); return true;
            case ("attach", 4) when int.TryParse(a[2], CultureInfo.InvariantCulture, out var cols) && int.TryParse(a[3], CultureInfo.InvariantCulture, out var rows):
                command = c => Attach(c, a[1], (cols, rows)); return true;
            case ("log", 2): command = c => Log(c, a[1], 50); return true;
            case ("log", 3) when int.TryParse(a[2], CultureInfo.InvariantCulture, out var n): command = c => Log(c, a[1], n); return true;
            case ("settings", 1): command = Settings; return true;
            case ("watch", 1): command = Watch; return true;
            default: return false;
        }
    }

    private static int Fail(int code, string message)
    {
        Console.Error.WriteLine(message);
        return code;
    }

    private static async Task<IpcResponse?> Send(IpcClient client, IpcRequest request)
    {
        var response = await client.SendAsync(request);
        if (response.Ok)
            return response;
        Console.Error.WriteLine($"error: {response.Error}");
        return null;
    }

    private static async Task<int> Simple(IpcClient client, IpcRequest request) =>
        await Send(client, request) is null ? HostError : Ok;

    private static async Task<int> List(IpcClient client)
    {
        if (await Send(client, new ListRequest()) is not { } r)
            return HostError;
        foreach (var row in r.Rows ?? [])
            PrintRow(row);
        return Ok;
    }

    private static void PrintRow(Row row)
    {
        var details = new List<string>();
        if (row.Pid is { } pid) details.Add(Inv($"pid {pid}"));
        if (row.ExitCode is { } code) details.Add(Inv($"exit {code}"));
        if (row.RestartInS is { } s) details.Add(Inv($"restart in {s} s"));
        if (row.Crashes10m > 0) details.Add(Inv($"{row.Crashes10m} crash(es) in 10 min"));
        if (!row.Saved) details.Add("unsaved");
        var status = row.Status.ToString().ToLowerInvariant();
        Console.WriteLine($"{(row.On ? "ON " : "OFF")} {status,-10} {row.Name,-24} {row.Path}{(details.Count > 0 ? "  (" + string.Join(", ", details) + ")" : "")}");
        if (row.Prompt is { } prompt) Console.WriteLine($"    prompt: {prompt}");
        if (row.Error is { } error) Console.WriteLine($"    error: {error}");
        foreach (var line in row.LastLines ?? [])
            Console.WriteLine($"    | {line}");
    }

    private static async Task<int> Log(IpcClient client, string path, int lines)
    {
        if (await Send(client, new LogRequest(path, lines)) is not { } r)
            return HostError;
        foreach (var line in r.Lines ?? [])
            Console.WriteLine(line);
        return Ok;
    }

    private static async Task<int> Settings(IpcClient client)
    {
        if (await Send(client, new SettingsGetRequest()) is not { } r)
            return HostError;
        Console.WriteLine($"claude_path:    {r.Settings?.ClaudePath ?? "(auto-detect)"}");
        Console.WriteLine($"notifications:  {(r.Settings?.Notifications == true ? "on" : "off")}");
        if (r.Info is { } info)
        {
            Console.WriteLine($"host version:   {info.HostVersion}");
            Console.WriteLine($"data folder:    {info.DataDir}");
            Console.WriteLine($"log folder:     {info.LogDir}");
            Console.WriteLine($"claude found:   {info.ClaudePath ?? "-"}");
            Console.WriteLine($"claude version: {info.ClaudeVersion ?? "-"}");
            if (info.ClaudeError is { } e) Console.WriteLine($"claude error:   {e}");
        }
        return Ok;
    }

    private static async Task<int> Watch(IpcClient client)
    {
        await foreach (var e in client.Events.ReadAllAsync())
        {
            switch (e)
            {
                case RowsEvent rows:
                    Console.WriteLine($"-- rows ({DateTime.Now:HH:mm:ss.fff})");
                    foreach (var row in rows.Rows)
                        PrintRow(row);
                    break;
                case ToastEvent toast:
                    Console.WriteLine($"-- toast {toast.Status}: {toast.Text}");
                    break;
                case OutputEvent output:
                    Console.WriteLine($"-- output {output.Path}: {output.Data.Length} bytes");
                    break;
            }
        }
        return Ok;
    }

    private static async Task<int> Attach(IpcClient client, string path, (int Cols, int Rows)? size)
    {
        var (cols, rows) = size ?? ConsoleSize();
        if (await Send(client, new AttachRequest(path, cols, rows)) is null)
            return HostError;
        var stdout = Console.OpenStandardOutput();

        // The replay comes right after the response; print it before anything is typed.
        using (var first = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            try
            {
                await WriteNextOutput(client, stdout, first.Token);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("no replay received");
            }
        }
        var printer = Task.Run(async () =>
        {
            try
            {
                while (true)
                    await WriteNextOutput(client, stdout, CancellationToken.None);
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                // The connection ended.
            }
        });

        if (Console.IsInputRedirected)
        {
            using var stdin = Console.OpenStandardInput();
            var buffer = new byte[4096];
            int n;
            while ((n = await stdin.ReadAsync(buffer)) > 0)
                await Send(client, new InputRequest(path, Encoding.UTF8.GetString(buffer, 0, n)));
        }
        else
        {
            Console.TreatControlCAsInput = true;
            while (true)
            {
                var key = await Task.Run(() => Console.ReadKey(intercept: true));
                if (key.Key == ConsoleKey.Oem6 && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                    break; // Ctrl+]
                var text = KeyText(key);
                if (text.Length > 0)
                    await Send(client, new InputRequest(path, text));
            }
        }
        await client.SendAsync(new DetachRequest(path));
        _ = printer;
        await stdout.FlushAsync();
        return Ok;
    }

    private static async Task WriteNextOutput(IpcClient client, Stream stdout, CancellationToken token)
    {
        while (true)
        {
            var e = await client.Events.ReadAsync(token);
            if (e is OutputEvent output)
            {
                await stdout.WriteAsync(output.Data, token);
                await stdout.FlushAsync(token);
                return;
            }
        }
    }

    private static (int, int) ConsoleSize()
    {
        try
        {
            if (!Console.IsOutputRedirected && Console.WindowWidth > 0 && Console.WindowHeight > 0)
                return (Console.WindowWidth, Console.WindowHeight);
        }
        catch (IOException)
        {
        }
        return (120, 40);
    }

    private static string KeyText(ConsoleKeyInfo key) => key.Key switch
    {
        ConsoleKey.Enter => "\r",
        ConsoleKey.Backspace => "\x7f",
        ConsoleKey.Escape => "\x1b",
        ConsoleKey.UpArrow => "\x1b[A",
        ConsoleKey.DownArrow => "\x1b[B",
        ConsoleKey.RightArrow => "\x1b[C",
        ConsoleKey.LeftArrow => "\x1b[D",
        ConsoleKey.Tab => "\t",
        _ => key.KeyChar == '\0' ? "" : key.KeyChar.ToString(),
    };

    /// <summary>Turns \r, \n, \t, \e, \\ and \xHH into the characters they name.</summary>
    internal static string Unescape(string text)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length)
            {
                sb.Append(text[i]);
                continue;
            }
            var c = text[++i];
            switch (c)
            {
                case 'r': sb.Append('\r'); break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'e': sb.Append('\x1b'); break;
                case '\\': sb.Append('\\'); break;
                case 'x' when i + 2 < text.Length && int.TryParse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b):
                    sb.Append((char)b);
                    i += 2;
                    break;
                default: sb.Append('\\').Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
