using System.Diagnostics;
using System.Runtime.InteropServices;
using ClaudeRc.Core;
using ClaudeRc.Core.Ipc;
using ClaudeRc.Tray.Services;
using Microsoft.Win32.SafeHandles;

namespace ClaudeRc.Tray.Hosting;

/// <summary>What the installer asks ClaudeRc.exe to do (SPEC 14).</summary>
internal enum SetupAction
{
    /// <summary><c>--install</c>, after the files are copied: set up the task and start the host.</summary>
    Install,

    /// <summary><c>--stop-for-update</c>, before an upgrade replaces the files: end the tray and the host.</summary>
    StopForUpdate,

    /// <summary><c>--uninstall</c>, before the files are removed: folders OFF, task deleted, toasts unregistered.</summary>
    Uninstall,
}

/// <summary>Exit codes of the setup actions.</summary>
internal static class SetupExitCodes
{
    /// <summary>Done.</summary>
    public const int Ok = 0;

    /// <summary>Something failed; it was shown.</summary>
    public const int Failed = 1;

    /// <summary>Bad arguments.</summary>
    public const int InvalidArguments = 2;

    /// <summary><c>--uninstall</c> left the task alone: it runs another copy (the portable zip), whose data stays.</summary>
    public const int OtherCopy = 3;
}

/// <summary>The tray app running in this logon session (for the same <c>--instance</c>).</summary>
internal interface IRunningTray
{
    /// <summary>Asks it to quit and waits for it.</summary>
    /// <returns>True when no tray runs any more.</returns>
    bool Quit(TimeSpan timeout);
}

/// <summary>Ends processes by their exe path.</summary>
internal interface IProcessStopper
{
    /// <summary>
    /// Ends every process (except this one) running <paramref name="exePath"/>: waits up to
    /// <paramref name="grace"/> for them to exit on their own, then terminates them.
    /// </summary>
    /// <returns>How many still run.</returns>
    int StopAll(string exePath, TimeSpan grace);
}

/// <summary>
/// The installer's steps (SPEC 14), run by the installed ClaudeRc.exe so the task and the toast
/// registration are made by the same code (and for the same exe path) as the app's own.
/// Nothing here takes a path or an account from the command line: the task is the fixed one
/// (<see cref="StartModeSetup.TaskPath"/>), the exes are the ones next to ClaudeRc.exe, the user is
/// the current one. Every problem is collected and shown once at the end; each step still runs.
/// </summary>
/// <param name="tasks">Task Scheduler.</param>
/// <param name="switcher">Sets up the task (<see cref="StartModeSwitcher"/>: logon without admin, startup through the elevated helper).</param>
/// <param name="hostTask">Runs the task (<see cref="HostTaskManager"/>: through the elevated <c>--run-host</c> helper when Task Scheduler refuses).</param>
/// <param name="host">The host's pipe.</param>
/// <param name="tray">The running tray app.</param>
/// <param name="processes">Ends this copy's exes.</param>
/// <param name="unregisterNotifications">Removes the toast registration (SPEC 3.5).</param>
/// <param name="dialogs">Shows the problems.</param>
/// <param name="setup">The task, this copy's host and the user.</param>
/// <param name="trayExe">This copy's ClaudeRc.exe.</param>
/// <param name="signIn">This copy's sign-in start (SPEC 3.4).</param>
/// <param name="delay">Waits (tests pass a no-op).</param>
internal sealed class AppSetup(ITaskService tasks, IStartModeSwitcher switcher, IHostTask hostTask, IHostConnector host, IRunningTray tray,
    IProcessStopper processes, Action unregisterNotifications, IDialogs dialogs, StartModeSetup setup, string trayExe, SignInStart signIn,
    Func<TimeSpan, CancellationToken, Task> delay)
{
    /// <summary><c>--fresh-install</c>: no earlier install was there, so <c>--install</c> turns the sign-in start on.</summary>
    public bool FreshInstall { get; init; }

    /// <summary>The title of the setup's messages.</summary>
    public const string Title = "claude-rc-windows setup";

    /// <summary>How long a running tray gets to quit.</summary>
    public static readonly TimeSpan TrayQuitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a tray that quit gets to end its process before it is terminated.</summary>
    public static readonly TimeSpan TrayExitGrace = TimeSpan.FromSeconds(3);

    /// <summary>How long the uninstall waits for the sessions to stop after turning the folders OFF.</summary>
    public static readonly TimeSpan SessionsStopTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How often it looks.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    /// <summary>How long the host may take to answer the uninstall's requests, on top of <see cref="SessionsStopTimeout"/>.</summary>
    public static readonly TimeSpan HostAnswerTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The longest the uninstall talks to the host before it goes on without it.</summary>
    public static readonly TimeSpan DefaultTurnOffTimeout = SessionsStopTimeout + HostAnswerTimeout;

    /// <summary>The longest the uninstall talks to the host (tests make it short).</summary>
    public TimeSpan TurnOffTimeout { get; init; } = DefaultTurnOffTimeout;

    /// <summary>Attempts to reach the host's pipe.</summary>
    public const int ConnectAttempts = 3;

    private readonly List<string> _errors = [];
    private bool _otherCopy;

    /// <summary>Runs one action; returns a <see cref="SetupExitCodes"/> value.</summary>
    public async Task<int> RunAsync(SetupAction action, CancellationToken cancellationToken = default)
    {
        _errors.Clear();
        _otherCopy = false;
        // Back on the caller's context (the app's UI thread) after each step, so the message below is shown there.
        switch (action)
        {
            case SetupAction.Install:
                await InstallAsync(cancellationToken).ConfigureAwait(true);
                break;
            case SetupAction.StopForUpdate:
                QuitTray();
                StopHost(throughTask: true);
                break;
            case SetupAction.Uninstall:
                await UninstallAsync(cancellationToken).ConfigureAwait(true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
        if (_errors.Count > 0)
            dialogs.ShowError(Title, string.Join("\n\n", _errors));
        // The uninstaller keeps another copy's data without asking, whatever else went wrong.
        if (_otherCopy)
            return SetupExitCodes.OtherCopy;
        return _errors.Count == 0 ? SetupExitCodes.Ok : SetupExitCodes.Failed;
    }

    /// <summary>
    /// After the files are copied: the running tray quits (a portable one would otherwise only show
    /// its window when the installer starts the tray), the sign-in start is set (on for a fresh
    /// install; an upgrade keeps the user's choice), the task is set up for this copy, the running
    /// host is stopped and the task is run.
    /// </summary>
    private async Task InstallAsync(CancellationToken cancellationToken)
    {
        QuitTray();
        // The tray works without a host, so this does not wait for the task.
        try
        {
            signIn.ApplyInstall(FreshInstall);
        }
        catch (Exception e) when (SignInStart.IsRegistryError(e))
        {
            _errors.Add($"The app could not be set to start at sign-in: {e.Message} You can turn it on in the app's Settings.");
        }
        if (!await SetUpTaskAsync(cancellationToken).ConfigureAwait(false))
            return;
        if (!StopHost(throughTask: true))
            return; // A new host would only meet the old one's single-instance mutex.
        // Through the elevated helper (UAC, no password) when Task Scheduler refuses the user the
        // task an administrator made (a kept startup task, SPEC 9.4).
        var run = await hostTask.RunAsync(cancellationToken).ConfigureAwait(false);
        if (!run.Ok)
            _errors.Add($"The host was not started: {run.Error} It starts by itself the next time Windows starts (start mode \"at startup\") "
                + "or you sign in (\"at logon\"); Start host in the app tries again now.");
    }

    /// <summary>
    /// The task for this copy: a startup task that already runs it is kept (setting it up again needs
    /// administrator rights and the password); any other startup task becomes this copy's startup
    /// task through the elevated helper; everything else becomes this copy's logon task.
    /// </summary>
    private async Task<bool> SetUpTaskAsync(CancellationToken cancellationToken)
    {
        TaskSnapshot? existing;
        try
        {
            existing = tasks.Find(setup.TaskPath);
        }
        catch (Exception e) when (HostTaskManager.IsTaskSchedulerError(e))
        {
            _errors.Add($"The scheduled task {setup.TaskPath} could not be read: {e.Message}");
            return false;
        }
        var mode = existing is null ? StartMode.Unknown : HostTaskDefinition.ReadMode(existing.Xml);
        if (mode == StartMode.Startup && HostTaskDefinition.RunsHost(existing!.Xml, setup.HostExe, setup.HostArguments))
            return true;

        var result = await switcher.SwitchAsync(mode == StartMode.Startup ? StartMode.Startup : StartMode.Logon, restartHost: false,
            cancellationToken).ConfigureAwait(false);
        if (result.Outcome == SwitchOutcome.Done)
            return true;
        _errors.Add($"The scheduled task {setup.TaskPath} was not set up, so the host was not started: {result.Message ?? result.Outcome.ToString()} "
            + "Choose a start mode in the app's Settings to try again.");
        return false;
    }

    /// <summary>
    /// Before the files are removed. A task that runs another copy (the portable zip) is left alone
    /// with its host and folders; otherwise every saved ON folder is turned OFF through the host
    /// (stopping its session), the host is stopped and the task deleted. Then this copy's exes are
    /// ended, its sign-in start and the toast registration removed.
    /// </summary>
    private async Task UninstallAsync(CancellationToken cancellationToken)
    {
        TaskSnapshot? existing = null;
        try
        {
            existing = tasks.Find(setup.TaskPath);
        }
        catch (Exception e) when (HostTaskManager.IsTaskSchedulerError(e))
        {
            _errors.Add($"The scheduled task {setup.TaskPath} could not be read: {e.Message}");
        }
        // Another copy's task names another ClaudeRcHost.exe; a task that cannot be read counts as this copy's.
        var other = existing is not null && HostTaskDefinition.ReadHostExe(existing.Xml) is { } exe
            && !string.Equals(Path.GetFullPath(exe), Path.GetFullPath(setup.HostExe), StringComparison.OrdinalIgnoreCase);

        _otherCopy = other;
        if (!other)
            await TurnFoldersOffAsync(cancellationToken).ConfigureAwait(false);
        QuitTray();
        StopHost(throughTask: !other);
        if (!other && existing is not null)
        {
            try
            {
                tasks.Delete(setup.TaskPath);
            }
            catch (Exception e) when (HostTaskManager.IsTaskSchedulerError(e))
            {
                _errors.Add($"The scheduled task {setup.TaskPath} could not be deleted: {e.Message} "
                    + "Delete it in Task Scheduler (taskschd.msc); a task set up for \"at startup\" may need an administrator.");
            }
        }
        // Only a value that starts this exe: another copy's (the portable zip) stays with that copy.
        try
        {
            signIn.RemoveIfThisExe();
        }
        catch (Exception e) when (SignInStart.IsRegistryError(e))
        {
            _errors.Add($"The app's sign-in start could not be removed: {e.Message} Turn it off in Windows' Startup apps (Settings > Apps > Startup).");
        }
        try
        {
            unregisterNotifications();
        }
#pragma warning disable CA1031 // A failed unregistration is shown with the rest; the uninstall goes on.
        catch (Exception e)
#pragma warning restore CA1031
        {
            _errors.Add("The app's notification registration could not be removed: " + e.Message);
        }
    }

    /// <summary>
    /// Turns every saved ON folder OFF through the host, then waits for the sessions to stop. No
    /// host: nothing runs. A host that does not answer within <see cref="TurnOffTimeout"/>, or a
    /// pipe that refuses the connection, is noted and the uninstall goes on: the host is stopped
    /// next, and the sessions end with it.
    /// </summary>
    private async Task TurnFoldersOffAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TurnOffTimeout);
        var token = deadline.Token;
        try
        {
            IHostClient? client = null;
            for (var attempt = 1; client is null && attempt <= ConnectAttempts; attempt++)
            {
                try
                {
                    client = await host.ConnectAsync(token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    if (attempt < ConnectAttempts)
                        await delay(Poll * 2, token).ConfigureAwait(false);
                }
            }
            if (client is null)
                return;
            await using (client.ConfigureAwait(false))
                await TurnFoldersOffAsync(client, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _errors.Add($"The host did not answer within {TurnOffTimeout.TotalSeconds:0} s, so the folders were not all turned off "
                + "(their sessions end with the host; a kept folder list may still have them ON).");
        }
        catch (UnauthorizedAccessException e)
        {
            _errors.Add("The connection to the host could not be opened, so the folders were not turned off "
                + "(their sessions end with the host; a kept folder list still has them ON): " + e.Message);
        }
    }

    private async Task TurnFoldersOffAsync(IHostClient client, CancellationToken cancellationToken)
    {
        try
        {
            if (await ListAsync(client, cancellationToken).ConfigureAwait(false) is not { } rows)
                return;
            foreach (var row in rows.Where(r => r.On && r.Saved))
            {
                var answer = await client.SendAsync(new SetRequest(row.Path, false), cancellationToken).ConfigureAwait(false);
                if (!answer.Ok)
                    _errors.Add($"{row.Path} could not be turned off: {answer.Error}");
            }
            var waited = TimeSpan.Zero;
            while (waited < SessionsStopTimeout
                && await ListAsync(client, cancellationToken).ConfigureAwait(false) is { } now
                && now.Any(r => r.Status is FolderStatus.Starting or FolderStatus.Running or FolderStatus.Waiting))
            {
                // Each session gets Ctrl+C and up to 5 s; anything left ends with the host.
                await delay(Poll, cancellationToken).ConfigureAwait(false);
                waited += Poll;
            }
        }
        catch (Exception e) when (e is IpcClosedException or IOException)
        {
            _errors.Add("The connection to the host ended while the folders were turned off: " + e.Message);
        }
    }

    /// <summary>The host's rows, or null (and the reason noted) when it refuses.</summary>
    private async Task<IReadOnlyList<Row>?> ListAsync(IHostClient client, CancellationToken cancellationToken)
    {
        var answer = await client.SendAsync(new ListRequest(), cancellationToken).ConfigureAwait(false);
        if (answer.Ok)
            return answer.Rows ?? [];
        _errors.Add("The host did not list the folders, so they were not turned off: " + answer.Error);
        return null;
    }

    /// <summary>The running tray quits; then this copy's ClaudeRc.exe (other than this process) is ended if it still runs.</summary>
    private void QuitTray()
    {
        if (!tray.Quit(TrayQuitTimeout))
            _errors.Add("The running claude-rc-windows tray app did not close. Quit it from its tray icon's menu (Quit tray).");
        if (processes.StopAll(trayExe, TrayExitGrace) > 0)
            _errors.Add($"{trayExe} is still running and could not be ended.");
    }

    /// <summary>
    /// Stops the task's running host (Task Scheduler waits for its process), then ends this copy's
    /// ClaudeRcHost.exe wherever it still runs (a stop the task refused, a host started by hand).
    /// Its sessions end with it (their Job Objects close, SPEC 6.5); the next host starts them again.
    /// </summary>
    /// <returns>False when this copy's host still runs.</returns>
    private bool StopHost(bool throughTask)
    {
        if (throughTask)
        {
            try
            {
                tasks.Stop(setup.TaskPath);
            }
            catch (FileNotFoundException)
            {
                // No task: nothing it could have started.
            }
            catch (Exception e) when (e is UnauthorizedAccessException or TimeoutException || HostTaskManager.IsTaskSchedulerError(e))
            {
                // Ended below if it is this copy's host; another copy's is reported when it blocks the start.
                Trace.TraceWarning($"stopping {setup.TaskPath}: {e.Message}");
            }
        }
        if (processes.StopAll(setup.HostExe, TimeSpan.Zero) == 0)
            return true;
        _errors.Add($"{setup.HostExe} is still running and could not be ended.");
        return false;
    }
}

/// <summary>
/// The running tray, reached through its quit event (<see cref="TrayOptions.QuitEventName"/>) and
/// its single-instance mutex (<see cref="TrayOptions.MutexName"/>): the tray quits when the event
/// is set, and releases the mutex as it exits.
/// </summary>
/// <param name="mutexName">The tray's single-instance mutex.</param>
/// <param name="quitEventName">The tray's quit event.</param>
internal sealed class WindowsRunningTray(string mutexName, string quitEventName) : IRunningTray
{
    /// <inheritdoc/>
    public bool Quit(TimeSpan timeout)
    {
        if (!Mutex.TryOpenExisting(mutexName, out var mutex))
            return true;
        using (mutex)
        {
            if (EventWaitHandle.TryOpenExisting(quitEventName, out var quit))
            {
                using (quit)
                    quit.Set();
            }
            try
            {
                if (!mutex.WaitOne(timeout))
                    return false;
            }
            catch (AbandonedMutexException)
            {
                // The tray ended without releasing it: it is gone, and the mutex is now ours.
            }
            mutex.ReleaseMutex();
            return true;
        }
    }
}

/// <summary>Ends processes by exe path (<see cref="IProcessStopper"/>), each through a handle opened when it is found.</summary>
internal sealed class WindowsProcessStopper : IProcessStopper
{
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x0010_0000;
    private const uint WaitObject0 = 0;
    private static readonly TimeSpan TerminateWait = TimeSpan.FromSeconds(5);

    /// <inheritdoc/>
    public int StopAll(string exePath, TimeSpan grace)
    {
        ArgumentNullException.ThrowIfNull(exePath);
        var target = Path.GetFullPath(exePath);
        var handles = new List<SafeProcessHandle>();
        try
        {
            var left = 0;
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target)))
            {
                using (p)
                {
                    if (p.Id == Environment.ProcessId)
                        continue;
                    var h = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation | Synchronize, false, (uint)p.Id);
                    if (h.IsInvalid)
                    {
                        h.Dispose();
                        // Not ours to end (another user's, an elevated one): it counts if it runs this exe.
                        using var query = OpenProcess(ProcessQueryLimitedInformation, false, (uint)p.Id);
                        if (!query.IsInvalid && SamePath(ImagePath(query), target))
                            left++;
                        continue;
                    }
                    if (SamePath(ImagePath(h), target))
                        handles.Add(h);
                    else
                        h.Dispose();
                }
            }
            var deadline = Stopwatch.StartNew();
            foreach (var h in handles)
            {
                var wait = grace - deadline.Elapsed;
                if (wait > TimeSpan.Zero && Wait(h, wait))
                    continue;
                if (!Wait(h, TimeSpan.Zero))
                {
                    TerminateProcess(h, 1);
                    if (!Wait(h, TerminateWait))
                        left++;
                }
            }
            return left;
        }
        finally
        {
            foreach (var h in handles)
                h.Dispose();
        }
    }

    private static bool Wait(SafeProcessHandle h, TimeSpan timeout) =>
        WaitForSingleObject(h, (uint)Math.Min(timeout.TotalMilliseconds, uint.MaxValue - 1)) == WaitObject0;

    private static bool SamePath(string? a, string b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? ImagePath(SafeProcessHandle h)
    {
        var buffer = new char[32768];
        var size = (uint)buffer.Length;
        return QueryFullProcessImageNameW(h, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, [Out] char[] name, ref uint size);
}
