using System.Globalization;
using System.Runtime.InteropServices;

namespace ClaudeRc.Tray.Hosting;

/// <summary>When the host starts (SPEC 2, 9).</summary>
internal enum StartMode
{
    /// <summary>Not known: no task, or a task that is neither (changed by hand).</summary>
    Unknown,

    /// <summary>At logon of this user (9.1).</summary>
    Logon,

    /// <summary>At startup, before logon (9.2).</summary>
    Startup,
}

/// <summary>Whether the host's scheduled task exists.</summary>
internal enum HostTaskState
{
    /// <summary>Could not be checked.</summary>
    Unknown,

    /// <summary>The task exists.</summary>
    Exists,

    /// <summary>There is no such task.</summary>
    Missing,
}

/// <summary>What the tray knows about the host's scheduled task (SPEC 3.1 status bar, 3.4 task status).</summary>
/// <param name="State">Whether it exists.</param>
/// <param name="StartMode">Its trigger.</param>
/// <param name="TaskPath">Its full name, e.g. <c>\claude-rc-windows\Host</c>.</param>
internal sealed record HostTaskInfo(HostTaskState State, StartMode StartMode, string TaskPath)
{
    /// <summary>SCHED_S_TASK_TERMINATED: the last run was stopped (by the user, or for a restart).</summary>
    private const int Terminated = 0x41306;

    /// <summary>The task is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>An instance of the task is running.</summary>
    public bool Running { get; init; }

    /// <summary>The last run's result, as Task Scheduler reports it.</summary>
    public int? LastResult { get; init; }

    /// <summary>When it last ran.</summary>
    public DateTime? LastRunTime { get; init; }

    /// <summary>When it runs next (logon and boot triggers have no time).</summary>
    public DateTime? NextRunTime { get; init; }

    /// <summary>Why the task could not be read (<see cref="HostTaskState.Unknown"/>).</summary>
    public string? Error { get; init; }

    /// <summary>
    /// A startup task whose last start failed with a logon failure: the Windows password changed,
    /// so Task Scheduler can't start the host until it is entered again (SPEC 9.2).
    /// </summary>
    public bool NeedsPassword =>
        State == HostTaskState.Exists && StartMode == StartMode.Startup && LastResult == HostTaskDefinition.LogonFailure;

    /// <summary>The task's health in a few words, for an existing task (the status bar).</summary>
    public string Health
    {
        get
        {
            if (NeedsPassword)
                return "re-enter your password";
            if (!Enabled)
                return "disabled";
            if (Running)
                return "running";
            return LastResult switch
            {
                null or 0 or HostTaskDefinition.HasNotRun or HostTaskDefinition.TaskIsRunning or Terminated => "ready",
                // ClaudeRcHost's AlreadyRunning: it met another host's single-instance mutex (SPEC 9.4).
                3 => "last run ended with exit code 3: another host was already running",
                >= 0 and <= 255 and var code => $"last run ended with exit code {code}",
                var code => $"last run failed (0x{code:X8})",
            };
        }
    }

    /// <summary>For the settings window: the health and the task's name, with the last and next run.</summary>
    public string StatusText => State switch
    {
        HostTaskState.Exists => $"{Health} ({TaskPath})" + Times(),
        HostTaskState.Missing => $"missing ({TaskPath})",
        _ => $"unknown ({TaskPath})" + (Error is null ? "" : ": " + Error),
    };

    private string Times()
    {
        var text = "";
        if (LastRunTime is { } last)
            text += ", last run " + last.ToString("g", CultureInfo.CurrentCulture);
        if (NextRunTime is { } next)
            text += ", next run " + next.ToString("g", CultureInfo.CurrentCulture);
        return text;
    }
}

/// <summary>Result of <see cref="IHostTask.RunAsync"/>.</summary>
/// <param name="Ok">The task was started.</param>
/// <param name="Error">Why not.</param>
internal sealed record StartHostResult(bool Ok, string? Error)
{
    /// <summary>The task was started.</summary>
    public static StartHostResult Started { get; } = new(true, null);

    /// <summary>There is no task to run. The host is never started as the tray's own child (SPEC 9.4).</summary>
    public static StartHostResult Missing(string taskPath) => new(false,
        $"The scheduled task {taskPath} does not exist, so the host cannot be started from here. " +
        "Choose a start mode in Settings to create it.");

    /// <summary>Running the task failed.</summary>
    public static StartHostResult Failed(string message) => new(false, message);
}

/// <summary>The host's scheduled task (SPEC 9): its state and health, and Start host.</summary>
internal interface IHostTask
{
    /// <summary>Reads the task's state, start mode and health.</summary>
    Task<HostTaskInfo> GetInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs the task (the Start host button, SPEC 9.4).</summary>
    Task<StartHostResult> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary><see cref="IHostTask"/> over Task Scheduler (<see cref="ITaskService"/>). Calls run on the thread pool.</summary>
/// <param name="tasks">Task Scheduler.</param>
/// <param name="taskPath">The task's full name (<see cref="TrayOptions.TaskPath"/>).</param>
/// <param name="elevated">Runs the task through the elevated helper when Task Scheduler refuses the user (null: no fallback).</param>
internal sealed class HostTaskManager(ITaskService tasks, string taskPath, IElevatedHostRun? elevated = null) : IHostTask
{
    /// <inheritdoc/>
    public Task<HostTaskInfo> GetInfoAsync(CancellationToken cancellationToken = default) =>
        Task.Run(Read, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// Task Scheduler may refuse the user a task an administrator made (the startup task, 9.2); the
    /// task is then run through the elevated helper (UAC, no password), if there is one.
    /// </remarks>
    public async Task<StartHostResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var (result, refused) = await Task.Run(RunUnelevated, cancellationToken).ConfigureAwait(false);
        if (refused is null)
            return result!;
        if (elevated is null)
            return StartHostResult.Failed($"Could not run the scheduled task {taskPath}: {refused.Message}");
        return await elevated.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the task as the user; the refusal, if Task Scheduler denied it.</summary>
    private (StartHostResult? Result, UnauthorizedAccessException? Refused) RunUnelevated()
    {
        var info = Read();
        switch (info.State)
        {
            case HostTaskState.Missing:
                return (StartHostResult.Missing(taskPath), null);
            case HostTaskState.Exists when !info.Enabled:
                return (StartHostResult.Failed(
                    $"The scheduled task {taskPath} is disabled, so it can't start the host. Enable it in Task Scheduler, or choose a start mode in Settings to create it again."), null);
        }
        try
        {
            tasks.Run(taskPath);
            return (StartHostResult.Started, null);
        }
        catch (FileNotFoundException)
        {
            return (StartHostResult.Missing(taskPath), null);
        }
        catch (UnauthorizedAccessException e)
        {
            return (null, e);
        }
        catch (Exception e) when (IsTaskSchedulerError(e))
        {
            return (StartHostResult.Failed($"Could not run the scheduled task {taskPath}: {e.Message}"), null);
        }
    }

    /// <summary>The errors Task Scheduler and the library report (COM errors, access denied, a missing folder, …).</summary>
    internal static bool IsTaskSchedulerError(Exception e) =>
        e is ExternalException or UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException;

    private HostTaskInfo Read()
    {
        TaskSnapshot? snapshot;
        try
        {
            snapshot = tasks.Find(taskPath);
        }
        catch (Exception e) when (IsTaskSchedulerError(e))
        {
            return new HostTaskInfo(HostTaskState.Unknown, StartMode.Unknown, taskPath) { Error = e.Message };
        }
        if (snapshot is null)
            return new HostTaskInfo(HostTaskState.Missing, StartMode.Unknown, taskPath);
        return new HostTaskInfo(HostTaskState.Exists, HostTaskDefinition.ReadMode(snapshot.Xml), taskPath)
        {
            Enabled = snapshot.State != TaskRunState.Disabled,
            Running = snapshot.State == TaskRunState.Running,
            LastResult = snapshot.LastResult,
            LastRunTime = snapshot.LastRunTime,
            NextRunTime = snapshot.NextRunTime,
        };
    }
}
