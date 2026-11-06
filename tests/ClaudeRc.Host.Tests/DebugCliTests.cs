using System.Diagnostics;
using ClaudeRc.Core;

namespace ClaudeRc.Host.Tests;

/// <summary>tools/ClaudeRc.DebugCli against an in-process host (PLAN 1: the M1 debug client).</summary>
public sealed class DebugCliTests
{
    private static string CliExe => Path.Combine(AppContext.BaseDirectory, "ClaudeRc.DebugCli.exe");

    private static async Task<(int Code, string Out, string Err)> Run(TestHost host, params string[] args)
    {
        var psi = new ProcessStartInfo(CliExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        psi.ArgumentList.Add("--instance");
        psi.ArgumentList.Add(host.Instance);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            p.Kill();
            throw;
        }
        return (p.ExitCode, await stdout, await stderr);
    }

    [Fact]
    public async Task Add_list_set_input_log_and_remove()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.NewFolder(new { input_file = "input.txt" }, "cli folder");

        var add = await Run(host, "add", folder);
        Assert.True(add.Code == 0, add.Err);
        await host.WaitForStatus(folder, FolderStatus.Running);

        var list = await Run(host, "list");
        Assert.Equal(0, list.Code);
        Assert.Contains(folder, list.Out, StringComparison.Ordinal);
        Assert.Contains("running", list.Out, StringComparison.Ordinal);

        var input = await Run(host, "input", folder, @"hi there\r");
        Assert.True(input.Code == 0, input.Err);
        FakeClaudeHarness.WaitUntil(() => TestHost.ReadText(folder, "input.txt") == "hi there\n", "input reached FakeClaude");

        var refused = await Run(host, "remove", folder);
        Assert.Equal(1, refused.Code);
        Assert.Contains("OFF", refused.Err, StringComparison.Ordinal);

        Assert.Equal(0, (await Run(host, "set", folder, "off")).Code);
        await host.WaitForStatus(folder, FolderStatus.Off);

        var log = await Run(host, "log", folder, "50");
        Assert.Equal(0, log.Code);
        Assert.Contains("turned OFF", log.Out, StringComparison.Ordinal);

        Assert.Equal(0, (await Run(host, "remove", folder)).Code);
        Assert.Empty(await host.List());
    }

    [Fact]
    public async Task Attach_prints_the_replay_and_ends_when_stdin_closes()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.NewFolder(new { print = "visible to the cli\r\n" });
        await host.SendOk(new Core.Ipc.AddRequest(folder));
        await host.WaitForStatus(folder, FolderStatus.Running);

        var attach = await Run(host, "attach", folder);

        Assert.True(attach.Code == 0, attach.Err);
        Assert.Contains("visible to the cli", attach.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Usage_errors_and_a_missing_host()
    {
        await using var host = await TestHost.StartAsync();
        Assert.Equal(2, (await Run(host, "frobnicate")).Code);
        Assert.Equal(2, (await Run(host, "set", "C:\\x")).Code);

        await host.StopHostAsync();
        var noHost = await Run(host, "list");
        Assert.Equal(3, noHost.Code);
        Assert.Contains("host", noHost.Err, StringComparison.OrdinalIgnoreCase);
    }
}
