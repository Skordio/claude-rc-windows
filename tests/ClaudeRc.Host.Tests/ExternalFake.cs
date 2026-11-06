using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeRc.Host.Tests;

/// <summary>
/// A FakeClaude started outside any host, as a user would start <c>claude rc</c> by hand: hidden
/// (its own console with no window, <c>CREATE_NO_WINDOW</c>), in a test folder. Its behaviour comes
/// from <c>--fake-config external.json</c>, so it can differ from the host's sessions in the same
/// folder (which read fakeclaude.json). Disposing kills it and whatever it started; tests never
/// stop or touch any process they did not start.
/// </summary>
internal sealed class ExternalFake : IDisposable
{
    public const string ConfigName = "external.json";
    public const string RecordName = "external-record.json";

    private readonly Process _process;

    private ExternalFake(Process process, string folder)
    {
        _process = process;
        Folder = folder;
    }

    public string Folder { get; }
    public int Pid => _process.Id;

    /// <summary>
    /// Starts FakeClaude in <paramref name="folder"/> with <paramref name="args"/> (the config option
    /// is added after them) and waits until it has written its record, so its working folder and
    /// arguments are in place.
    /// </summary>
    public static ExternalFake Start(string folder, object? config = null, params string[] args)
    {
        var settings = JsonSerializer.SerializeToNode(config ?? new { })!.AsObject();
        settings["record"] = RecordName;
        File.WriteAllText(Path.Combine(folder, ConfigName), settings.ToJsonString());
        File.Delete(Path.Combine(folder, RecordName));

        var psi = new ProcessStartInfo(FakeClaudeHarness.FakeClaudeExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = folder,
        };
        foreach (var a in args.Length == 0 ? ["rc"] : args)
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--fake-config");
        psi.ArgumentList.Add(ConfigName);
        var external = new ExternalFake(Process.Start(psi)!, folder);
        try
        {
            external.Record();
        }
        catch
        {
            external.Dispose();
            throw;
        }
        return external;
    }

    public JsonObject Record() => TestHost.Record(Folder, RecordName);

    public int? ChildPid => Record()["child_pid"] is { } c ? (int)c : null;

    public bool IsRunning => FakeClaudeHarness.IsRunning(Pid) && !_process.HasExited;

    public void Dispose()
    {
        int? child = null;
        try { child = File.Exists(Path.Combine(Folder, RecordName)) ? ChildPid : null; }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { }
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            _process.WaitForExit(10_000);
        }
        catch (InvalidOperationException)
        {
        }
        // A child whose parent was already gone (taken over, or killed first) is not in the tree
        // any more: kill it only if it is still a FakeClaude from this test output.
        if (child is { } pid && FakeClaudeHarness.RunningFakeClaudes().Contains(pid))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill();
                p.WaitForExit(10_000);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
        _process.Dispose();
    }
}
