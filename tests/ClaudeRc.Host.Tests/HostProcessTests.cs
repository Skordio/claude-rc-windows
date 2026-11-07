using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ClaudeRc.Core;
using ClaudeRc.Core.Ipc;
using ClaudeRc.Core.Paths;
using ClaudeRc.Core.Storage;

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
            // The real host exe matches claude.exe (SPEC 7), so the user's own claude rc sessions may be
            // listed too, as unsaved rows it never acts on: look only at this test's folder.
            var rows = (await client.SendAsync(new ListRequest())).Rows!;
            var row = rows.Single(r => PathNormalizer.Comparer.Equals(r.Path, folder));
            Assert.Equal(pid, row.Pid);
            Assert.NotEqual(FolderStatus.Off, row.Status);
            Assert.All(rows.Where(r => !PathNormalizer.Comparer.Equals(r.Path, folder)),
                r => Assert.True(r is { Status: FolderStatus.External, Saved: false, On: false }, TestHost.Describe(r)));

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
    public async Task The_live_pipe_allows_only_the_user_and_system_and_denies_network_logons()
    {
        await using var host = await TestHost.StartAsync();
        var user = WindowsIdentity.GetCurrent().User!;
        using var pipe = new NamedPipeClientStream(".", host.Options.PipeName,
            PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.ReadPermissions,
            PipeOptions.None, TokenImpersonationLevel.Identification, HandleInheritability.None);
        await pipe.ConnectAsync(10_000);

        // The security the pipe really has, read back from the handle.
        var descriptor = new RawSecurityDescriptor(pipe.GetAccessControl().GetSecurityDescriptorBinaryForm(), 0);
        Assert.Equal(user, descriptor.Owner);
        Assert.True(descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected), "DACL not protected");
        var aces = descriptor.DiscretionaryAcl!.Cast<CommonAce>().ToList();
        Assert.Equal(3, aces.Count);
        // The deny for network logons comes first, so it wins over the user's allow.
        Assert.Equal(AceQualifier.AccessDenied, aces[0].AceQualifier);
        Assert.Equal(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), aces[0].SecurityIdentifier);
        var allowed = aces.Skip(1).ToList();
        Assert.All(allowed, a => Assert.Equal(AceQualifier.AccessAllowed, a.AceQualifier));
        Assert.Equal(
            new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), user }.OrderBy(s => s.Value),
            allowed.Select(a => a.SecurityIdentifier).OrderBy(s => s.Value));
        Assert.All(aces, a => Assert.Equal(AceFlags.None, a.AceFlags));
    }

    [Fact]
    public async Task The_host_mutex_is_owned_by_the_user_and_open_only_to_the_user_and_system()
    {
        // An elevated host's default owner would be BUILTIN\Administrators; the owner is set explicitly.
        // TestHost only provides the temp folders here; the mutex belongs to the real process.
        await using var dirs = await TestHost.StartAsync();
        await dirs.StopHostAsync();
        using var host = StartHost(dirs.HostArguments);
        RawSecurityDescriptor descriptor;
        try
        {
            await using (await IpcClient.ConnectAsync(dirs.Options.PipeName, TimeSpan.FromSeconds(20)))
            {
            }
            using var mutex = MutexAcl.OpenExisting(dirs.Options.MutexName, MutexRights.ReadPermissions | MutexRights.Synchronize);
            descriptor = new RawSecurityDescriptor(mutex.GetAccessControl().GetSecurityDescriptorBinaryForm(), 0);
        }
        finally
        {
            Kill(host);
        }

        var user = WindowsIdentity.GetCurrent().User!;
        Assert.Equal(user, descriptor.Owner);
        Assert.True(descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected), "DACL not protected");
        var aces = descriptor.DiscretionaryAcl!.Cast<CommonAce>().ToList();
        Assert.All(aces, a => Assert.Equal(AceQualifier.AccessAllowed, a.AceQualifier));
        Assert.Equal(
            new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), user }.OrderBy(s => s.Value),
            aces.Select(a => a.SecurityIdentifier).OrderBy(s => s.Value));
    }

    [ServerServiceFact]
    public async Task A_client_over_the_network_path_is_refused()
    {
        await using var host = await TestHost.StartAsync();
        // \\127.0.0.1\pipe\<name> goes through the Server service (SMB): the path a remote
        // machine's client uses (SPEC 12: no network listener).
        using var remote = new NamedPipeClientStream("127.0.0.1", host.Options.PipeName, PipeDirection.InOut);
        Assert.Throws<UnauthorizedAccessException>(() => remote.Connect(10_000));

        Assert.True((await host.Send(new ListRequest())).Ok);
    }
}

/// <summary>A fact that needs the Server (LanmanServer) service, which serves <c>\\127.0.0.1\pipe\…</c>; skipped without it.</summary>
public sealed class ServerServiceFactAttribute : FactAttribute
{
    public ServerServiceFactAttribute()
    {
        if (!NativeTestMethods.IsServiceRunning("LanmanServer"))
            Skip = "the Server (LanmanServer) service is not running";
    }
}
