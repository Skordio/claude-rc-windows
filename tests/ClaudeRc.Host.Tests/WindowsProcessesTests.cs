using System.Diagnostics;
using System.Security.Principal;
using ClaudeRc.Core.Paths;
using ClaudeRc.Host.Adoption;

namespace ClaudeRc.Host.Tests;

/// <summary>
/// The Windows process reader of the scan (SPEC 7). It only reads; the processes here are ones the
/// test started itself, plus read-only looks at the System process and the test process.
/// </summary>
public sealed class WindowsProcessesTests : IDisposable
{
    private readonly FakeClaudeHarness _dir = new();
    private static string MySid => WindowsIdentity.GetCurrent().User!.Value;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Reads_the_arguments_folder_owner_and_start_time_of_a_process()
    {
        var folder = Directory.CreateDirectory(_dir.File("my folder & co")).FullName;
        using var external = ExternalFake.Start(folder, null, "rc", "--name", "a \"quoted\" name", "x y");

        var facts = WindowsProcesses.Read(external.Pid);

        Assert.NotNull(facts);
        Assert.Equal(external.Pid, facts.Pid);
        Assert.Equal("FakeClaude.exe", facts.ImageName, ignoreCase: true);
        Assert.Equal(["rc", "--name", "a \"quoted\" name", "x y", "--fake-config", ExternalFake.ConfigName], facts.Arguments);
        Assert.Equal(PathNormalizer.Normalize(folder), PathNormalizer.Normalize(facts.CurrentDirectory!));
        Assert.Equal(MySid, facts.OwnerSid);
        Assert.Equal(Process.GetCurrentProcess().Id, facts.ParentPid);
        using var p = Process.GetProcessById(external.Pid);
        Assert.Equal(p.StartTime.ToUniversalTime().ToFileTimeUtc(), facts.StartTime);
    }

    [Fact]
    public void Snapshot_lists_every_process_with_details_only_for_candidates_and_start_times_for_their_ancestors()
    {
        using var external = ExternalFake.Start(_dir.Folder);

        var all = WindowsProcesses.Snapshot(name => name.Equals("FakeClaude.exe", StringComparison.OrdinalIgnoreCase));

        var fake = Assert.Single(all, p => p.Pid == external.Pid);
        Assert.NotNull(fake.Arguments);
        Assert.NotNull(fake.CurrentDirectory);
        Assert.NotNull(fake.StartTime);
        var me = Assert.Single(all, p => p.Pid == Environment.ProcessId);
        Assert.NotNull(me.StartTime);   // an ancestor of a candidate
        Assert.Null(me.Arguments);      // not a candidate
        Assert.True(all.Count > 20);
        Assert.Contains(all, p => p.ImageName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) || p.Pid == 4);
    }

    [Wow64Fact]
    public void A_32_bit_process_is_read_through_its_wow64_peb()
    {
        var psi = new ProcessStartInfo(Wow64FactAttribute.Ping) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _dir.Folder };
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("30");
        psi.ArgumentList.Add("127.0.0.1");
        using var p = Process.Start(psi)!;
        try
        {
            FakeClaudeHarness.WaitUntil(() => WindowsProcesses.Read(p.Id)?.CurrentDirectory is not null, "32-bit process readable");
            var facts = WindowsProcesses.Read(p.Id)!;
            Assert.Equal(["-n", "30", "127.0.0.1"], facts.Arguments);
            Assert.Equal(PathNormalizer.Normalize(_dir.Folder), PathNormalizer.Normalize(facts.CurrentDirectory!));
            Assert.Equal(MySid, facts.OwnerSid);
        }
        finally
        {
            p.Kill();
            p.WaitForExit(10_000);
        }
    }

    [Fact]
    public void A_process_it_may_not_read_gives_no_details_and_no_error()
    {
        var system = WindowsProcesses.Read(4);
        Assert.NotNull(system);
        Assert.Null(system.CurrentDirectory);
        Assert.Null(system.Arguments);
    }

    [Fact]
    public void A_process_that_does_not_exist_reads_as_null()
    {
        using var p = Process.Start(new ProcessStartInfo(FakeClaudeHarness.FakeClaudeExe, "--version")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        p.WaitForExit(10_000);
        var pid = p.Id;
        p.Dispose();
        // The id may already be reused by another process; then whatever it is reads without an error.
        var facts = WindowsProcesses.Read(pid);
        Assert.True(facts is null || facts.Pid == pid);
        Assert.Null(WindowsProcesses.Read(-1));
    }

    [Fact]
    public void Is_alive_needs_the_same_start_time_and_a_running_process()
    {
        var external = ExternalFake.Start(_dir.Folder);
        var pid = external.Pid;
        var start = WindowsProcesses.Read(pid)!.StartTime!.Value;

        Assert.True(WindowsProcesses.IsAlive(pid, start));
        Assert.False(WindowsProcesses.IsAlive(pid, start + 1));

        external.Dispose();
        Assert.False(WindowsProcesses.IsAlive(pid, start));
    }
}

/// <summary>A fact that needs a 32-bit program (SysWOW64\PING.EXE); skipped without the WOW64 subsystem.</summary>
public sealed class Wow64FactAttribute : FactAttribute
{
    public static string Ping => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "PING.EXE");

    public Wow64FactAttribute()
    {
        if (!File.Exists(Ping))
            Skip = "no 32-bit subsystem (SysWOW64) on this machine";
    }
}
