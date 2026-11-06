using System.ComponentModel;
using System.Diagnostics;

namespace ClaudeRc.Tray.Hosting;

/// <summary>When the host starts (SPEC 2, 9). <see cref="Unknown"/> until M4 reads it from the task.</summary>
internal enum StartMode
{
    /// <summary>Not known (M2: the task is not read yet).</summary>
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

/// <summary>What the tray knows about the host's scheduled task.</summary>
/// <param name="State">Whether it exists.</param>
/// <param name="StartMode">Its trigger.</param>
/// <param name="TaskPath">Its full name, e.g. <c>\claude-rc-windows\Host</c>.</param>
internal sealed record HostTaskInfo(HostTaskState State, StartMode StartMode, string TaskPath)
{
    /// <summary>For the settings window: <c>exists</c>, <c>missing</c> or <c>unknown</c>, with the task name.</summary>
    public string StatusText => State switch
    {
        HostTaskState.Exists => $"exists ({TaskPath})",
        HostTaskState.Missing => $"missing ({TaskPath})",
        _ => $"unknown ({TaskPath})",
    };
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
        "The installer creates it; until then, start ClaudeRcHost.exe yourself.");

    /// <summary>Running the task failed.</summary>
    public static StartHostResult Failed(string message) => new(false, message);
}

/// <summary>
/// The host's scheduled task (SPEC 9). M2 only checks that it exists and runs it; M4 creates it,
/// switches the start mode and reports its health.
/// </summary>
internal interface IHostTask
{
    /// <summary>Reads the task's state and start mode.</summary>
    Task<HostTaskInfo> GetInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs the task (the Start host button, SPEC 9.4).</summary>
    Task<StartHostResult> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// M2's <see cref="IHostTask"/>: <c>schtasks.exe /Query</c> and <c>/Run</c>. The start mode stays
/// <see cref="StartMode.Unknown"/> until M4 replaces this with the TaskScheduler library.
/// </summary>
/// <param name="taskPath">The task's full name (<see cref="TrayOptions.TaskPath"/>).</param>
internal sealed class SchtasksHostTask(string taskPath) : IHostTask
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <inheritdoc/>
    public async Task<HostTaskInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var (code, _) = await RunSchtasks(["/Query", "/TN", taskPath], cancellationToken).ConfigureAwait(false);
        var state = code switch
        {
            0 => HostTaskState.Exists,
            1 => HostTaskState.Missing,
            _ => HostTaskState.Unknown,
        };
        return new HostTaskInfo(state, StartMode.Unknown, taskPath);
    }

    /// <inheritdoc/>
    public async Task<StartHostResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var info = await GetInfoAsync(cancellationToken).ConfigureAwait(false);
        if (info.State == HostTaskState.Missing)
            return StartHostResult.Missing(taskPath);
        var (code, output) = await RunSchtasks(["/Run", "/TN", taskPath], cancellationToken).ConfigureAwait(false);
        return code == 0
            ? StartHostResult.Started
            : StartHostResult.Failed($"Could not run the scheduled task {taskPath}: {output.Trim()}");
    }

    private static async Task<(int Code, string Output)> RunSchtasks(string[] args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("schtasks.exe did not start");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (p.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            return (-1, e.Message);
        }
    }
}
