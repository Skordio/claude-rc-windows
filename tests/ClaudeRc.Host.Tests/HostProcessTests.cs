using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ClaudeRc.Core;
using ClaudeRc.Core.Ipc;
using ClaudeRc.Core.Storage;
using ClaudeRc.Host.Ipc;

namespace ClaudeRc.Host.Tests;

/// <summary>
/// ClaudeRcHost.exe as a real process (SPEC 9.4, 6.5), always with the test's temp folders and a
/// random <c>--instance</c>. Only host processes these tests start are ever stopped.
/// </summary>
public sealed class HostProcessTests
{
    private static string HostExe => Path.Combine(AppContext.BaseDirectory, "ClaudeRcHost.exe");

    private static Process StartHost(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(HostExe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    private static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited)
                p.Kill(entireProcessTree: false);
            p.WaitForExit(10_000);
        }
        catch (InvalidOperationException)
        {
        }
    }

    [Fact]
    public async Task A_second_host_with_the_same_instance_exits_at_once_and_the_first_keeps_serving()
    {
        // TestHost only provides the temp folders here; the hosts are real processes.
        await using var dirs = await TestHost.StartAsync();
        await dirs.StopHostAsync();
        using var first = StartHost(dirs.HostArguments);
        try
        {
            await using var client = await IpcClient.ConnectAsync(dirs.Options.PipeName, TimeSpan.FromSeconds(20));
            Assert.True((await client.SendAsync(new ListRequest())).Ok);

            using var second = StartHost(dirs.HostArguments);
            Assert.True(second.WaitForExit(10_000), "second host still running");
            Assert.Equal(HostExitCodes.AlreadyRunning, second.ExitCode);

            Assert.True((await client.SendAsync(new ListRequest())).Ok);
            Assert.False(first.HasExited);
        }
        finally
        {
            Kill(first);
        }
    }

    [Fact]
    public async Task A_host_with_another_instance_suffix_runs_alongside()
    {
        await using var a = await TestHost.StartAsync();
        await using var b = await TestHost.StartAsync();
        Assert.NotEqual(a.Options.PipeName, b.Options.PipeName);
        Assert.True((await a.Send(new ListRequest())).Ok);
        Assert.True((await b.Send(new ListRequest())).Ok);
    }

    [Fact]
    public async Task When_the_host_process_dies_its_sessions_die_with_it()
    {
        await using var dirs = await TestHost.StartAsync();
        await dirs.StopHostAsync();
        var folder = dirs.NewFolder(new { spawn_child = true, record = "record.json" });
        using var host = StartHost(dirs.HostArguments);
        try
        {
            await using var client = await IpcClient.ConnectAsync(dirs.Options.PipeName, TimeSpan.FromSeconds(20));
            // Only start anything once it is certain the host runs FakeClaude, never the real claude.
            Assert.Equal(FakeClaudeHarness.FakeClaudeExe, (await client.SendAsync(new SettingsGetRequest())).Info!.ClaudePath);
            Assert.True((await client.SendAsync(new AddRequest(folder))).Ok);
            var record = TestHost.Record(folder);
            var pid = (int)record["pid"]!;
            var child = (int)record["child_pid"]!;
            var rows = (await client.SendAsync(new ListRequest())).Rows!;
            Assert.Equal(pid, rows.Single().Pid);
            Assert.NotEqual(FolderStatus.Off, rows.Single().Status);

            host.Kill();
            Assert.True(host.WaitForExit(10_000));

            FakeClaudeHarness.WaitUntil(() => !FakeClaudeHarness.IsRunning(pid) && !FakeClaudeHarness.IsRunning(child), "session and child gone");
        }
        finally
        {
            Kill(host);
        }
    }

    [Fact]
    public void Invalid_arguments_exit_with_code_2()
    {
        using var host = StartHost(["--no-such-option"]);
        Assert.True(host.WaitForExit(10_000));
        Assert.Equal(HostExitCodes.InvalidArguments, host.ExitCode);
    }

    [Fact]
    public void Pipe_acl_allows_only_the_user_and_system()
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var security = PipeServer.CreateSecurity(user);

        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        Assert.Equal(2, rules.Count);
        Assert.All(rules, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
        Assert.Contains(rules, r => r.IdentityReference.Equals(user));
        Assert.Contains(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.Equal(user, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(security.AreAccessRulesProtected);
    }
}
