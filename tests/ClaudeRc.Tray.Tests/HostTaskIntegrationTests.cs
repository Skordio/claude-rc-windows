using System.Diagnostics;
using System.Security.Principal;
using ClaudeRc.Core.Hosting;
using ClaudeRc.Core.Ipc;
using ClaudeRc.Tray.Hosting;
using Microsoft.Win32.TaskScheduler;
using SchedTask = Microsoft.Win32.TaskScheduler.Task;
using Task = System.Threading.Tasks.Task;

namespace ClaudeRc.Tray.Tests;

/// <summary>
/// Test task folders in the real Task Scheduler. Only folders named
/// <c>\claude-rc-windows-test-&lt;guid&gt;</c> are ever created or deleted; the real
/// <c>\claude-rc-windows\</c> is never touched. Leftovers of an earlier run are swept when the
/// suite starts.
/// </summary>
public sealed class TestTaskFolders
{
    public const string Prefix = "claude-rc-windows-test-";

    public TestTaskFolders() => Sweep();

    /// <summary>A new folder path, <c>\claude-rc-windows-test-&lt;guid&gt;</c>.</summary>
    public static string NewFolder() => @"\" + Prefix + Guid.NewGuid().ToString("N");

    /// <summary>Deletes every test folder (and its tasks, stopping running ones).</summary>
    public static void Sweep()
    {
        using var ts = new TaskService();
        foreach (var name in ts.RootFolder.SubFolders.Select(f => f.Name).ToList())
        {
            if (name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                Remove(@"\" + name);
        }
    }

    /// <summary>Stops and deletes the tasks of one test folder, then the folder.</summary>
    public static void Remove(string folderPath)
    {
        Guard(folderPath);
        using var ts = new TaskService();
        TaskFolder? folder;
        try
        {
            folder = ts.GetFolder(folderPath);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        if (folder is null)
            return;
        using (folder)
        {
            foreach (var t in folder.Tasks.ToList())
            {
                using (t)
                {
                    if (t.State == TaskState.Running)
                        t.Stop();
                    folder.DeleteTask(t.Name, exceptionOnNotExists: false);
                }
            }
        }
        ts.RootFolder.DeleteFolder(folderPath.TrimStart('\\'), exceptionOnNotExists: false);
    }

    /// <summary>Whether the folder exists.</summary>
    public static bool Exists(string folderPath)
    {
        using var ts = new TaskService();
        try
        {
            using var f = ts.GetFolder(folderPath);
            return f is not null;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Refuses anything but a test folder (never <c>\claude-rc-windows</c> itself).</summary>
    public static void Guard(string path)
    {
        if (!path.StartsWith(@"\" + Prefix, StringComparison.OrdinalIgnoreCase) || path.Length <= Prefix.Length + 1)
            throw new InvalidOperationException($"not a test task path: {path}");
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TaskSchedulerCollection : ICollectionFixture<TestTaskFolders>
{
    public const string Name = "Task Scheduler";
}

/// <summary>
/// The host task in the real Task Scheduler (SPEC 9): a <c>logon</c> task is registered in a test
/// folder, read back, run and deleted. A <c>startup</c> task needs admin and a password, so it is
/// only checked in memory (Task Scheduler parses its XML; nothing is registered).
/// </summary>
[Collection(TaskSchedulerCollection.Name)]
public sealed class HostTaskIntegrationTests : IDisposable
{
    private static readonly string Account = WindowsIdentity.GetCurrent().Name;
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private readonly string _folder = TestTaskFolders.NewFolder();
    private readonly WindowsTaskService _service = new();

    private string TaskPath => _folder + @"\Host";

    public void Dispose() => TestTaskFolders.Remove(_folder);

    /// <summary>Disabled and harmless: it could never start, and would only run <c>cmd /c exit 0</c>.</summary>
    private HostTaskSpec HarmlessLogon() => new()
    {
        TaskPath = TaskPath,
        Mode = StartMode.Logon,
        HostExe = Cmd,
        HostArguments = ["/c", "exit 0"],
        Account = Account,
        Enabled = false,
    };

    [Fact]
    public void A_logon_task_registers_without_admin_and_reads_back_with_every_setting()
    {
        TestTaskFolders.Guard(TaskPath);
        _service.Register(TaskPath, HostTaskDefinition.BuildXml(HarmlessLogon()), Account, null, TaskLogon.InteractiveToken);

        using var ts = new TaskService();
        using var task = ts.GetTask(TaskPath);
        Assert.NotNull(task);
        var td = task.Definition;

        // 9.1: at logon of this user, only while logged on, no highest privileges.
        var trigger = Assert.IsType<LogonTrigger>(Assert.Single(td.Triggers));
        Assert.Equal(Account, trigger.UserId, ignoreCase: true);
        Assert.True(trigger.Enabled);
        Assert.Equal(TaskLogonType.InteractiveToken, td.Principal.LogonType);
        Assert.Equal(TaskRunLevel.LUA, td.Principal.RunLevel);

        // 9.3.
        var s = td.Settings;
        Assert.True(s.StartWhenAvailable);
        Assert.False(s.DisallowStartIfOnBatteries);
        Assert.False(s.StopIfGoingOnBatteries);
        Assert.Equal(TimeSpan.Zero, s.ExecutionTimeLimit);
        Assert.Equal(TimeSpan.FromMinutes(1), s.RestartInterval);
        Assert.Equal(999, s.RestartCount);
        Assert.Equal(TaskInstancesPolicy.IgnoreNew, s.MultipleInstances);
        Assert.True(s.AllowDemandStart);
        Assert.Equal(ProcessPriorityClass.Normal, s.Priority);
        Assert.False(s.Enabled); // the test's own choice: it can never start by itself

        var exec = Assert.IsType<ExecAction>(Assert.Single(td.Actions));
        Assert.Equal(Cmd, exec.Path.Trim('"'), ignoreCase: true);
        Assert.Equal("/c \"exit 0\"", exec.Arguments);
        Assert.Equal(Environment.SystemDirectory, exec.WorkingDirectory, ignoreCase: true);
    }

    [Fact]
    public async Task Status_of_a_registered_task_reads_through_the_manager()
    {
        _service.Register(TaskPath, HostTaskDefinition.BuildXml(HarmlessLogon()), Account, null, TaskLogon.InteractiveToken);

        var snapshot = _service.Find(TaskPath);
        Assert.NotNull(snapshot);
        Assert.Equal(TaskRunState.Disabled, snapshot.State);
        Assert.Equal(HostTaskDefinition.HasNotRun, snapshot.LastResult);
        Assert.Null(snapshot.LastRunTime);
        Assert.Null(snapshot.NextRunTime);

        var manager = new HostTaskManager(_service, TaskPath);
        var info = await manager.GetInfoAsync();
        Assert.Equal(HostTaskState.Exists, info.State);
        Assert.Equal(StartMode.Logon, info.StartMode);
        Assert.False(info.Enabled);
        Assert.False(info.Running);
        Assert.False(info.NeedsPassword);
        Assert.Equal("disabled", info.Health);

        // A disabled task is not run.
        var run = await manager.RunAsync();
        Assert.False(run.Ok);
        Assert.Equal(TaskRunState.Disabled, _service.Find(TaskPath)!.State);
    }

    [Fact]
    public async Task A_missing_task_and_folder_read_as_missing_and_delete_is_a_no_op()
    {
        Assert.Null(_service.Find(TaskPath));
        Assert.Equal(HostTaskState.Missing, (await new HostTaskManager(_service, TaskPath).GetInfoAsync()).State);
        _service.Delete(TaskPath);
        Assert.Throws<FileNotFoundException>(() => _service.Run(TaskPath));
    }

    [Fact]
    public void Delete_removes_the_task_and_its_empty_folder()
    {
        _service.Register(TaskPath, HostTaskDefinition.BuildXml(HarmlessLogon()), Account, null, TaskLogon.InteractiveToken);
        Assert.True(TestTaskFolders.Exists(_folder));

        _service.Delete(TaskPath);

        Assert.Null(_service.Find(TaskPath));
        Assert.False(TestTaskFolders.Exists(_folder));
    }

    [Fact]
    public void A_boot_trigger_is_refused_without_admin()
    {
        if (IsElevated())
            return; // an elevated test run could register it; this machine's tests never are
        var xml = HostTaskDefinition.BuildXml(HarmlessLogon() with { Mode = StartMode.Startup });
        // Interactive logon (no password): it must be refused before any password matters.
        Assert.Throws<UnauthorizedAccessException>(() => _service.Register(TaskPath, xml, Account, null, TaskLogon.InteractiveToken));
        Assert.Null(_service.Find(TaskPath));
    }

    [Fact]
    public void The_startup_definition_is_valid_for_task_scheduler_in_memory_only()
    {
        var xml = HostTaskDefinition.BuildXml(HarmlessLogon() with { Mode = StartMode.Startup, Enabled = true });

        using var ts = new TaskService();
        using var td = ts.NewTask();
        td.XmlText = xml; // parsed and checked by Task Scheduler; nothing is registered

        Assert.IsType<BootTrigger>(Assert.Single(td.Triggers));
        Assert.Equal(TaskLogonType.Password, td.Principal.LogonType);
        Assert.Equal(TaskRunLevel.LUA, td.Principal.RunLevel);
        Assert.Equal(Account, td.Principal.UserId, ignoreCase: true);
        Assert.True(td.Settings.StartWhenAvailable);
        Assert.Equal(TimeSpan.Zero, td.Settings.ExecutionTimeLimit);
        Assert.Equal(999, td.Settings.RestartCount);
        Assert.Equal(TaskInstancesPolicy.IgnoreNew, td.Settings.MultipleInstances);
        Assert.False(TestTaskFolders.Exists(_folder));
    }

    /// <summary>
    /// Start host = run the task (9.4), end to end: the logon switch registers an enabled task that
    /// runs a test host (its own instance and temp folders, never the real host), the host answers
    /// on its pipe, an update leaves it running, a restart brings it back, and Stop ends it.
    /// </summary>
    [Fact]
    public async Task Switching_to_logon_with_restart_runs_a_test_host_through_the_task()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            return; // a CI runner may have no interactive logon session to run the task in
        var hostExe = Path.Combine(AppContext.BaseDirectory, "ClaudeRcHost.exe");
        Assert.True(File.Exists(hostExe), hostExe);
        var instance = "m4task" + Guid.NewGuid().ToString("N")[..12];
        var temp = Path.Combine(Path.GetTempPath(), "claude-rc-m4-" + instance);
        Directory.CreateDirectory(temp);
        var started = DateTime.Now.AddSeconds(-1);
        string[] hostArgs =
        [
            "--instance", instance,
            "--data-dir", Path.Combine(temp, "data"),
            "--log-dir", Path.Combine(temp, "logs"),
            "--claude-json", Path.Combine(temp, "claude.json"),
        ];
        var setup = new StartModeSetup(TaskPath, hostExe, hostArgs, Account);
        var elevation = new FakeElevation { ExitCode = HelperExitCodes.Failed };
        var switcher = new StartModeSwitcher(_service, elevation, setup, (_, _) => ["never"], File.Exists, (d, ct) => Task.Delay(d, ct));
        var pipe = HostOptions.Parse(["--instance", instance], new HostDefaults("x", "x", "x", WindowsIdentity.GetCurrent().User!.Value)).PipeName;
        try
        {
            TestTaskFolders.Guard(TaskPath);
            var result = await switcher.SwitchAsync(StartMode.Logon, restartHost: true);
            Assert.Equal(SwitchOutcome.Done, result.Outcome);
            Assert.Null(result.Message);
            Assert.Empty(elevation.Runs); // logon needed no elevation
            await AssertHostAnswers(pipe);
            Assert.True((await new HostTaskManager(_service, TaskPath).GetInfoAsync()).Running);

            // Updating the task leaves the running host alone.
            Assert.Equal(SwitchOutcome.Done, (await switcher.SwitchAsync(StartMode.Logon, restartHost: false)).Outcome);
            Assert.Equal(TaskRunState.Running, _service.Find(TaskPath)!.State);

            // A restart stops it and runs the task again.
            var before = HostProcesses(hostExe, started).Select(p => p.Id).ToHashSet();
            Assert.Equal(SwitchOutcome.Done, (await switcher.SwitchAsync(StartMode.Logon, restartHost: true)).Outcome);
            // The old host is gone before the new run: otherwise the new host meets its mutex and
            // exits at once (code 3), and once the old one dies no host is left.
            Assert.DoesNotContain(HostProcesses(hostExe, started), p => before.Contains(p.Id));
            await Task.Delay(3000);
            await AssertHostAnswers(pipe);
            var after = _service.Find(TaskPath)!;
            Assert.Equal(TaskRunState.Running, after.State);
            Assert.Single(HostProcesses(hostExe, started));

            _service.Stop(TaskPath);
            await Eventually.True(() => _service.Find(TaskPath)!.State != TaskRunState.Running, "task stopped", 15000);
            await Eventually.True(() => HostProcesses(hostExe, started).Count == 0, "host ended", 15000);
        }
        finally
        {
            TestTaskFolders.Remove(_folder);
            foreach (var p in HostProcesses(hostExe, started))
            {
                try
                {
                    p.Kill();
                }
                catch (InvalidOperationException)
                {
                }
            }
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task AssertHostAnswers(string pipe)
    {
        var deadline = Environment.TickCount64 + 20000;
        while (true)
        {
            try
            {
                await using var client = await IpcClient.ConnectAsync(pipe, TimeSpan.FromSeconds(1));
                var answer = await client.SendAsync(new ListRequest());
                Assert.True(answer.Ok);
                return;
            }
            catch (TimeoutException) when (Environment.TickCount64 < deadline)
            {
            }
            catch (IpcClosedException) when (Environment.TickCount64 < deadline)
            {
            }
        }
    }

    /// <summary>ClaudeRcHost processes run from this test's own copy (never the installed host), started during the test.</summary>
    private static List<Process> HostProcesses(string hostExe, DateTime since)
    {
        var result = new List<Process>();
        foreach (var p in Process.GetProcessesByName("ClaudeRcHost"))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, hostExe, StringComparison.OrdinalIgnoreCase) && p.StartTime >= since && !p.HasExited)
                {
                    result.Add(p);
                    continue;
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
            p.Dispose();
        }
        return result;
    }

    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
