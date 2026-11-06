namespace ClaudeRc.Tray.Hosting;

/// <summary>Starts this app again elevated (the <c>runas</c> verb, so Windows shows UAC).</summary>
internal interface IElevation
{
    /// <summary>Runs ClaudeRc.exe elevated with <paramref name="arguments"/> and waits for it.</summary>
    /// <returns>Its exit code, or null when the user said No at the UAC prompt.</returns>
    Task<int?> RunElevatedAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

/// <summary>A password typed into the Windows credential prompt. Dispose wipes it.</summary>
/// <param name="password">The characters; owned by this object from now on.</param>
internal sealed class PromptedPassword(char[] password) : IDisposable
{
    /// <summary>The characters (all <c>\0</c> once disposed).</summary>
    public ReadOnlySpan<char> Chars => password;

    /// <summary>Disposed (wiped).</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// The password as a string, made only for the one Task Scheduler call that needs it (COM takes
    /// a BSTR). The string cannot be wiped; it is never stored, logged or sent anywhere else.
    /// </summary>
    public string Reveal()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return new string(password);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Array.Clear(password);
        IsDisposed = true;
    }
}

/// <summary>The Windows credential prompt (CredUIPromptForWindowsCredentials), asking for one account's password.</summary>
internal interface ICredentialPrompt
{
    /// <summary>Asks for <paramref name="account"/>'s Windows password.</summary>
    /// <param name="account">The user the host runs as (shown, not changeable in effect: only the password is used).</param>
    /// <param name="error">Why the last password was not taken, or null the first time.</param>
    /// <returns>The password, or null when the user cancelled.</returns>
    PromptedPassword? Prompt(string account, string? error);
}

/// <summary>How a start-mode switch ended.</summary>
internal enum SwitchOutcome
{
    /// <summary>The task is set up for the new mode.</summary>
    Done,

    /// <summary>The user cancelled (UAC or the password prompt); nothing changed.</summary>
    Cancelled,

    /// <summary>It failed; <see cref="SwitchResult.Message"/> says why.</summary>
    Failed,
}

/// <summary>Result of a start-mode switch.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Message">For the user: why it failed, or a note on a done switch (the host was not restarted).</param>
internal sealed record SwitchResult(SwitchOutcome Outcome, string? Message)
{
    /// <summary>Done, nothing to add.</summary>
    public static SwitchResult Done { get; } = new(SwitchOutcome.Done, null);
}

/// <summary>Sets the start mode (SPEC 3.4, 9): what the settings window, the status bar and the first run call.</summary>
internal interface IStartModeSwitcher
{
    /// <summary>Creates or replaces the host's task for <paramref name="target"/>.</summary>
    /// <param name="target">Logon or startup.</param>
    /// <param name="restartHost">Then stop the task's running host (if any) and run the task, so the host runs the new way now.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    Task<SwitchResult> SwitchAsync(StartMode target, bool restartHost, CancellationToken cancellationToken = default);
}

/// <summary>Exit codes of the elevated helper (<c>ClaudeRc.exe --set-start-mode</c>).</summary>
internal static class HelperExitCodes
{
    /// <summary>The task is set up (and restarted, if asked).</summary>
    public const int Ok = 0;

    /// <summary>It failed; the helper showed why.</summary>
    public const int Failed = 1;

    /// <summary>Bad arguments.</summary>
    public const int InvalidArguments = 2;

    /// <summary>The task is set up, but the host could not be restarted; the helper showed why.</summary>
    public const int NotRestarted = 4;

    /// <summary>ERROR_CANCELLED: the password prompt was cancelled; nothing changed.</summary>
    public const int Cancelled = 1223;
}

/// <summary>What every switch needs: the task, the host it runs, and the user it runs as.</summary>
/// <param name="TaskPath">The task's full name.</param>
/// <param name="HostExe">ClaudeRcHost.exe.</param>
/// <param name="HostArguments">The host's arguments (none in production).</param>
/// <param name="Account">The user, <c>DOMAIN\name</c>.</param>
internal sealed record StartModeSetup(string TaskPath, string HostExe, IReadOnlyList<string> HostArguments, string Account)
{
    /// <summary>The task's definition for a mode.</summary>
    public HostTaskSpec Spec(StartMode mode) => new()
    {
        TaskPath = TaskPath,
        Mode = mode,
        HostExe = HostExe,
        HostArguments = HostArguments,
        Account = Account,
    };
}

/// <summary>
/// The tray's side of a switch, unelevated. <c>logon</c> needs no admin, so the tray registers it
/// itself; when Task Scheduler refuses (the old task is a startup task an administrator made),
/// the elevated helper does it. <c>startup</c> always needs the helper: a boot trigger needs admin,
/// and the password is asked for there.
/// </summary>
/// <param name="tasks">Task Scheduler.</param>
/// <param name="elevation">Runs the helper elevated.</param>
/// <param name="setup">The task, host and user.</param>
/// <param name="helperArguments">The helper's arguments for a mode and restart (<see cref="TrayOptions.HelperArguments"/>).</param>
/// <param name="fileExists">Whether a file exists.</param>
/// <param name="delay">Waits (tests pass a no-op).</param>
internal sealed class StartModeSwitcher(ITaskService tasks, IElevation elevation, StartModeSetup setup,
    Func<StartMode, bool, IReadOnlyList<string>> helperArguments, Func<string, bool> fileExists,
    Func<TimeSpan, CancellationToken, Task> delay) : IStartModeSwitcher
{
    /// <inheritdoc/>
    public async Task<SwitchResult> SwitchAsync(StartMode target, bool restartHost, CancellationToken cancellationToken = default)
    {
        var xml = HostTaskDefinition.BuildXml(setup.Spec(target)); // refuses Unknown
        if (!fileExists(setup.HostExe))
            return new SwitchResult(SwitchOutcome.Failed, TaskRestart.HostMissing(setup.HostExe));

        if (target == StartMode.Logon)
        {
            try
            {
                await Task.Run(() => tasks.Register(setup.TaskPath, xml, setup.Account, null, TaskLogon.InteractiveToken),
                    cancellationToken).ConfigureAwait(false);
                if (!restartHost)
                    return SwitchResult.Done;
                var restart = await TaskRestart.RestartAsync(tasks, setup.TaskPath, delay, cancellationToken).ConfigureAwait(false);
                return restart is null ? SwitchResult.Done : new SwitchResult(SwitchOutcome.Done, TaskRestart.NotRestartedNote + " " + restart);
            }
            catch (UnauthorizedAccessException)
            {
                // A task an administrator made (the old startup task): the helper replaces it.
            }
            catch (Exception e) when (HostTaskManager.IsTaskSchedulerError(e))
            {
                return new SwitchResult(SwitchOutcome.Failed, $"Could not set up the scheduled task {setup.TaskPath}: {e.Message}");
            }
        }

        var code = await elevation.RunElevatedAsync(helperArguments(target, restartHost), cancellationToken).ConfigureAwait(false);
        return code switch
        {
            null or HelperExitCodes.Cancelled => new SwitchResult(SwitchOutcome.Cancelled, "Cancelled: the start mode was not changed."),
            HelperExitCodes.Ok => SwitchResult.Done,
            HelperExitCodes.NotRestarted => new SwitchResult(SwitchOutcome.Done, TaskRestart.NotRestartedNote),
            _ => new SwitchResult(SwitchOutcome.Failed,
                $"The start mode could not be changed (the elevated helper ended with code {code}; it showed the reason)."),
        };
    }
}

/// <summary>Restarting the host through its task: stop the running instance, wait for it to end, run the task.</summary>
internal static class TaskRestart
{
    /// <summary>How long a stopped host may take to end before the restart gives up.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);

    /// <summary>Added to a switch that worked but whose restart did not.</summary>
    public const string NotRestartedNote =
        "The start mode is changed, but the host could not be restarted: the new mode takes effect the next time the host starts (restart Windows, or sign out and in).";

    /// <summary>ClaudeRcHost.exe is not where the task would run it.</summary>
    public static string HostMissing(string hostExe) =>
        $"ClaudeRcHost.exe was not found at {hostExe}, so no task was set up. It must be in the same folder as ClaudeRc.exe.";

    /// <summary>
    /// Stops the task's running host, waits up to <see cref="StopTimeout"/> for it to end (a new run
    /// is ignored while one still runs: "do not start a new instance", 9.3), then runs the task.
    /// </summary>
    /// <returns>Null when the task was run again; otherwise why not.</returns>
    /// <exception cref="UnauthorizedAccessException">Task Scheduler refused (an administrator's task).</exception>
    public static async Task<string?> RestartAsync(ITaskService tasks, string taskPath, Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(() => tasks.Stop(taskPath), cancellationToken).ConfigureAwait(false);
            var waited = TimeSpan.Zero;
            while (tasks.Find(taskPath)?.State == TaskRunState.Running)
            {
                if (waited >= StopTimeout)
                    return "the running host did not stop.";
                await delay(Poll, cancellationToken).ConfigureAwait(false);
                waited += Poll;
            }
            await Task.Run(() => tasks.Run(taskPath), cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (TimeoutException e)
        {
            // The stopped host did not end: a run now would only meet its mutex.
            return e.Message;
        }
        catch (Exception e) when (e is not UnauthorizedAccessException && HostTaskManager.IsTaskSchedulerError(e))
        {
            return e.Message;
        }
    }
}

/// <summary>
/// The elevated helper (<c>ClaudeRc.exe --set-start-mode logon|startup --for-user …</c>): registers
/// the task, asking for the password for <c>startup</c> and passing it straight to Task Scheduler
/// (SPEC 9.2, 12), then restarts the host if asked.
/// </summary>
/// <param name="tasks">Task Scheduler.</param>
/// <param name="prompt">The Windows credential prompt.</param>
/// <param name="dialogs">Error messages.</param>
/// <param name="fileExists">Whether a file exists.</param>
/// <param name="delay">Waits (tests pass a no-op).</param>
internal sealed class StartModeHelper(ITaskService tasks, ICredentialPrompt prompt, Services.IDialogs dialogs,
    Func<string, bool> fileExists, Func<TimeSpan, CancellationToken, Task> delay)
{
    private const string Title = "Start mode";

    /// <summary>Shown again in the prompt after Task Scheduler refused the password.</summary>
    public const string PasswordNotAccepted =
        "The password was not accepted. Enter the Windows password of this account (not a PIN or Windows Hello).";

    /// <summary>Sets the mode; returns a <see cref="HelperExitCodes"/> value. Errors are shown here, as the tray can't see them.</summary>
    public async Task<int> RunAsync(StartModeSetup setup, StartMode mode, bool restartHost, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setup);
        var xml = HostTaskDefinition.BuildXml(setup.Spec(mode));
        if (!fileExists(setup.HostExe))
        {
            dialogs.ShowError(Title, TaskRestart.HostMissing(setup.HostExe));
            return HelperExitCodes.Failed;
        }

        if (mode == StartMode.Logon)
        {
            if (!TryRegister(setup, xml, null, TaskLogon.InteractiveToken, out _))
                return HelperExitCodes.Failed;
        }
        else
        {
            string? error = null;
            while (true)
            {
                using var password = prompt.Prompt(setup.Account, error);
                if (password is null)
                    return HelperExitCodes.Cancelled;
                if (TryRegister(setup, xml, password, TaskLogon.Password, out var logonFailed))
                    break;
                if (!logonFailed)
                    return HelperExitCodes.Failed;
                error = PasswordNotAccepted;
            }
        }

        if (!restartHost)
            return HelperExitCodes.Ok;
        string? why;
        try
        {
            why = await TaskRestart.RestartAsync(tasks, setup.TaskPath, delay, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException e)
        {
            why = e.Message;
        }
        if (why is null)
            return HelperExitCodes.Ok;
        dialogs.ShowError(Title, TaskRestart.NotRestartedNote + "\n\n" + why);
        return HelperExitCodes.NotRestarted;
    }

    /// <summary>Registers the task. The password (if any) is revealed only for this one call.</summary>
    private bool TryRegister(StartModeSetup setup, string xml, PromptedPassword? password, TaskLogon logon, out bool logonFailed)
    {
        logonFailed = false;
        try
        {
            tasks.Register(setup.TaskPath, xml, setup.Account, password?.Reveal(), logon);
            return true;
        }
        catch (Exception e) when (e.HResult == HostTaskDefinition.LogonFailure && password is not null)
        {
            logonFailed = true;
            return false;
        }
        catch (Exception e) when (HostTaskManager.IsTaskSchedulerError(e))
        {
            // Task Scheduler's message, never the password.
            dialogs.ShowError(Title, $"Could not set up the scheduled task {setup.TaskPath}: {e.Message}");
            return false;
        }
    }
}
