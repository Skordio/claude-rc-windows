using System.Diagnostics;
using Sched = Microsoft.Win32.TaskScheduler;

namespace ClaudeRc.Tray.Hosting;

/// <summary>How a task logs on (Task Scheduler's TASK_LOGON_TYPE, the two the host uses).</summary>
internal enum TaskLogon
{
    /// <summary>"Run only when user is logged on" (TASK_LOGON_INTERACTIVE_TOKEN): no password, no admin.</summary>
    InteractiveToken,

    /// <summary>"Run whether user is logged on or not" with the stored password (TASK_LOGON_PASSWORD).</summary>
    Password,
}

/// <summary>A registered task's state (Task Scheduler's TASK_STATE).</summary>
internal enum TaskRunState
{
    /// <summary>Not known.</summary>
    Unknown,

    /// <summary>Disabled.</summary>
    Disabled,

    /// <summary>Queued to run.</summary>
    Queued,

    /// <summary>Ready: enabled and not running.</summary>
    Ready,

    /// <summary>An instance is running.</summary>
    Running,
}

/// <summary>What Task Scheduler says about a registered task.</summary>
/// <param name="Xml">Its definition as Task Scheduler returns it (defaults left out).</param>
/// <param name="State">Running, ready, disabled, ….</param>
/// <param name="LastResult">The last run's result (an exit code or an HRESULT such as <see cref="HostTaskDefinition.LogonFailure"/>).</param>
/// <param name="LastRunTime">When it last ran; null if never.</param>
/// <param name="NextRunTime">When it runs next; null if no time is scheduled (logon and boot triggers have none).</param>
internal sealed record TaskSnapshot(string Xml, TaskRunState State, int LastResult, DateTime? LastRunTime, DateTime? NextRunTime);

/// <summary>
/// The few Task Scheduler operations the app needs, so the start-mode logic can be tested with a
/// fake. Every method is synchronous (COM) and may throw: <see cref="UnauthorizedAccessException"/>
/// when the caller may not do it (a boot trigger without admin, a task an administrator created),
/// or an exception whose <see cref="Exception.HResult"/> is <see cref="HostTaskDefinition.LogonFailure"/>
/// when a password is wrong.
/// </summary>
internal interface ITaskService
{
    /// <summary>The task, or null when it (or its folder) does not exist.</summary>
    TaskSnapshot? Find(string taskPath);

    /// <summary>
    /// Creates or replaces the task (creating its folder if needed). <paramref name="password"/> is
    /// passed straight to Task Scheduler (as a BSTR zeroed right after the call) and is never kept,
    /// written or logged (SPEC 12); the caller wipes it afterwards.
    /// </summary>
    void Register(string taskPath, string xml, string account, PromptedPassword? password, TaskLogon logon);

    /// <summary>Deletes the task, and its folder when that is then empty; nothing when it does not exist.</summary>
    void Delete(string taskPath);

    /// <summary>Runs the task now (Start host, SPEC 9.4).</summary>
    /// <exception cref="FileNotFoundException">The task does not exist.</exception>
    void Run(string taskPath);

    /// <summary>
    /// Stops the task's running instances and returns once their processes have ended; nothing
    /// when none runs.
    /// </summary>
    /// <exception cref="TimeoutException">A stopped process did not end within <see cref="TaskRestart.StopTimeout"/>.</exception>
    /// <exception cref="FileNotFoundException">The task does not exist.</exception>
    void Stop(string taskPath);
}

/// <summary>Splits a task path into its folder and name.</summary>
internal static class TaskPaths
{
    /// <summary><c>\a\Host</c> → (<c>\a</c>, <c>Host</c>); <c>\Host</c> → (<c>\</c>, <c>Host</c>).</summary>
    /// <exception cref="ArgumentException">Not a full task path.</exception>
    public static (string Folder, string Name) Split(string taskPath)
    {
        ArgumentNullException.ThrowIfNull(taskPath);
        var cut = taskPath.LastIndexOf('\\');
        if (!taskPath.StartsWith('\\') || cut == taskPath.Length - 1 || taskPath.Contains(@"\\", StringComparison.Ordinal))
            throw new ArgumentException($"not a full task path: {taskPath}", nameof(taskPath));
        return (cut == 0 ? @"\" : taskPath[..cut], taskPath[(cut + 1)..]);
    }
}

/// <summary><see cref="ITaskService"/> over Windows Task Scheduler, through the TaskScheduler (dahall) library.</summary>
/// <remarks>
/// Each call connects on its own (<see cref="Sched.TaskService"/> is cheap to create and not
/// thread-safe). The definition is always the XML of <see cref="HostTaskDefinition"/>: the library
/// only creates folders, reads, runs, stops and deletes; the registration itself is
/// <see cref="TaskSchedulerCom.Register"/>, so a password never becomes a .NET string.
/// </remarks>
internal sealed class WindowsTaskService : ITaskService
{
    /// <summary>Task Scheduler's "never" for the last and next run times.</summary>
    private static readonly DateTime Never = new(2000, 1, 1);

    /// <inheritdoc/>
    public TaskSnapshot? Find(string taskPath)
    {
        using var ts = new Sched.TaskService();
        using var task = GetTask(ts, taskPath);
        if (task is null)
            return null;
        var state = task.State switch
        {
            Sched.TaskState.Disabled => TaskRunState.Disabled,
            Sched.TaskState.Queued => TaskRunState.Queued,
            Sched.TaskState.Ready => TaskRunState.Ready,
            Sched.TaskState.Running => TaskRunState.Running,
            _ => TaskRunState.Unknown,
        };
        return new TaskSnapshot(task.Xml, state, task.LastTaskResult, Time(task.LastRunTime), Time(task.NextRunTime));
    }

    /// <inheritdoc/>
    public void Register(string taskPath, string xml, string account, PromptedPassword? password, TaskLogon logon)
    {
        var (folderPath, name) = TaskPaths.Split(taskPath);
        using (var ts = new Sched.TaskService())
            GetOrCreateFolder(ts, folderPath).Dispose();
        // Our own call, not the library's: the password goes in as a BSTR that is zeroed at once.
        TaskSchedulerCom.Register(folderPath, name, xml, account, password, logon);
    }

    /// <inheritdoc/>
    public void Delete(string taskPath)
    {
        var (folderPath, name) = TaskPaths.Split(taskPath);
        using var ts = new Sched.TaskService();
        using var folder = GetFolder(ts, folderPath);
        if (folder is null)
            return;
        folder.DeleteTask(name, exceptionOnNotExists: false);
        if (folderPath == @"\" || folder.Tasks.Count > 0 || folder.SubFolders.Count > 0)
            return;
        var (parentPath, folderName) = TaskPaths.Split(folderPath);
        using var parent = GetFolder(ts, parentPath);
        parent?.DeleteFolder(folderName, exceptionOnNotExists: false);
    }

    /// <inheritdoc/>
    public void Run(string taskPath)
    {
        using var ts = new Sched.TaskService();
        using var task = GetTask(ts, taskPath) ?? throw new FileNotFoundException($"no scheduled task {taskPath}", taskPath);
        using var _ = task.Run();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Task Scheduler says the task is no longer running before the stopped process has ended. A
    /// run right after would start a host that meets the old one's single-instance mutex and exits
    /// at once, and then no host is left. So the instances' processes are opened before the stop
    /// (the handles keep their PIDs from being reused) and waited for.
    /// </remarks>
    public void Stop(string taskPath)
    {
        using var ts = new Sched.TaskService();
        using var task = GetTask(ts, taskPath) ?? throw new FileNotFoundException($"no scheduled task {taskPath}", taskPath);
        if (task.State != Sched.TaskState.Running)
            return;
        var processes = new List<Process>();
        try
        {
            using (var instances = task.GetInstances())
            {
                foreach (var instance in instances)
                {
                    using (instance)
                    {
                        if (OpenProcess((int)instance.EnginePID) is { } p)
                            processes.Add(p);
                    }
                }
            }
            task.Stop();
            var deadline = Environment.TickCount64 + (long)TaskRestart.StopTimeout.TotalMilliseconds;
            foreach (var p in processes)
            {
                var left = (int)Math.Max(0, deadline - Environment.TickCount64);
                if (!p.WaitForExit(left))
                    throw new TimeoutException($"the host (process {p.Id}) did not end after the task was stopped");
            }
        }
        finally
        {
            foreach (var p in processes)
                p.Dispose();
        }
    }

    private static Process? OpenProcess(int pid)
    {
        if (pid <= 0)
            return null;
        try
        {
            var p = Process.GetProcessById(pid);
            _ = p.Handle; // opened now, so the PID can't be reused while we wait
            return p;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null; // already gone, or not ours to open
        }
    }

    private static Sched.Task? GetTask(Sched.TaskService ts, string taskPath)
    {
        var (folderPath, name) = TaskPaths.Split(taskPath);
        using var folder = GetFolder(ts, folderPath);
        if (folder is null)
            return null;
        try
        {
            return folder.Tasks.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static Sched.TaskFolder? GetFolder(Sched.TaskService ts, string folderPath)
    {
        try
        {
            return folderPath == @"\" ? ts.RootFolder : ts.GetFolder(folderPath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static Sched.TaskFolder GetOrCreateFolder(Sched.TaskService ts, string folderPath)
    {
        if (GetFolder(ts, folderPath) is { } existing)
            return existing;
        var (parentPath, name) = TaskPaths.Split(folderPath);
        using var parent = GetOrCreateFolder(ts, parentPath);
        return parent.CreateFolder(name, (string?)null, exceptionOnExists: false);
    }

    private static DateTime? Time(DateTime t) => t < Never ? null : t;
}
