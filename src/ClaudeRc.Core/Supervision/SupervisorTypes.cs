using ClaudeRc.Core.Storage;

namespace ClaudeRc.Core.Supervision;

/// <summary>Timings and limits of <see cref="Supervisor"/>; the defaults are SPEC 4 and 5.</summary>
public sealed record SupervisorOptions
{
    /// <summary>Delay before restart N (1-based); the last value repeats. Default 5, 10, 20, 40, 80, 160, 300 s.</summary>
    public IReadOnlyList<TimeSpan> Backoff { get; init; } =
        [.. new[] { 5, 10, 20, 40, 80, 160, 300 }.Select(s => TimeSpan.FromSeconds(s))];

    /// <summary>A session up this long resets the backoff and the crash count. Default 10 min.</summary>
    public TimeSpan StableUptime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>This many exits within <see cref="FailingWindow"/> make a folder failing. Default 5.</summary>
    public int FailingExits { get; init; } = 5;

    /// <summary>The sliding window for <see cref="FailingExits"/>. Default 10 min.</summary>
    public TimeSpan FailingWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Missing-folder re-check interval early after host start. Default 10 s.</summary>
    public TimeSpan MissingFastInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long after host start <see cref="MissingFastInterval"/> applies. Default 5 min.</summary>
    public TimeSpan MissingFastPeriod { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Missing-folder re-check interval afterwards. Default 60 s.</summary>
    public TimeSpan MissingSlowInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A started session with no ready output counts as running after this. Default 30 s.</summary>
    public TimeSpan StartingTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gap between folder starts at host start. Default 1 s.</summary>
    public TimeSpan StartStagger { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Retry delay after a failed start (claude not found, invalid <c>.claude.json</c>, …). Default 60 s.</summary>
    public TimeSpan StartErrorRetry { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>A folder as the supervisor sees it at one moment.</summary>
public sealed record FolderSnapshot
{
    /// <summary>Normalised path.</summary>
    public required string Path { get; init; }
    /// <summary>Desired state.</summary>
    public required bool On { get; init; }
    /// <summary>False for an unsaved row (external session in an unlisted folder).</summary>
    public required bool Saved { get; init; }
    /// <summary>Computed status.</summary>
    public required FolderStatus Status { get; init; }
    /// <summary>Prompt text while waiting.</summary>
    public string? Prompt { get; init; }
    /// <summary>Exit code of the last unexpected exit.</summary>
    public int? LastExitCode { get; init; }
    /// <summary>When the next start is due (restart backoff, missing re-check, start retry).</summary>
    public DateTimeOffset? RestartAt { get; init; }
    /// <summary>Unexpected exits in the failing window.</summary>
    public required int Crashes10m { get; init; }
    /// <summary>When the hosted session process started.</summary>
    public DateTimeOffset? StartedAt { get; init; }
    /// <summary>Hosted session process id.</summary>
    public int? Pid { get; init; }
    /// <summary>Process id of an external claude rc in this folder.</summary>
    public int? ExternalPid { get; init; }
    /// <summary>Why the last start failed.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Result of every <see cref="Supervisor"/> call: the effects for the host to carry out, in
/// order, and an error message when a user request was refused (then nothing changed).
/// </summary>
/// <param name="Effects">What the host must do.</param>
/// <param name="Error">Why the request was refused, for the IPC response.</param>
public sealed record SupervisorResult(IReadOnlyList<SupervisorEffect> Effects, string? Error = null)
{
    /// <summary>True when the request was accepted.</summary>
    public bool Ok => Error is null;
}

/// <summary>Something the host must do on the supervisor's behalf.</summary>
public abstract record SupervisorEffect;

/// <summary>
/// Start a session: preset <c>.claude.json</c>, locate claude, launch it in the folder. Then
/// call <see cref="Supervisor.SessionStarted"/> or <see cref="Supervisor.StartFailed"/>.
/// </summary>
/// <param name="Path">Folder path (also the working directory).</param>
public sealed record StartSession(string Path) : SupervisorEffect;

/// <summary>
/// Stop the hosted session (Ctrl+C, wait 5 s, terminate the job); then call
/// <see cref="Supervisor.SessionExited"/>. Its exit is not counted as a crash.
/// </summary>
/// <param name="Path">Folder path.</param>
public sealed record StopSession(string Path) : SupervisorEffect;

/// <summary>
/// Stop an external claude rc (take over, SPEC 7); the host's next scan then reports
/// <see cref="Supervisor.ExternalChanged"/> with no pid and the hosted start follows.
/// </summary>
/// <param name="Path">Folder path.</param>
/// <param name="Pid">The external process to stop.</param>
public sealed record StopExternal(string Path, int Pid) : SupervisorEffect;

/// <summary>A folder's snapshot changed: push rows to clients.</summary>
/// <param name="Folder">The new snapshot.</param>
/// <param name="PreviousStatus">The status before, or null for a new folder.</param>
public sealed record FolderChanged(FolderSnapshot Folder, FolderStatus? PreviousStatus) : SupervisorEffect;

/// <summary>A folder row went away (removed, or an unsaved row whose external session ended).</summary>
/// <param name="Path">Folder path.</param>
public sealed record FolderRemoved(string Path) : SupervisorEffect;

/// <summary>The saved list changed: write <c>folders.json</c>.</summary>
/// <param name="Folders">The full saved list, in order.</param>
public sealed record FoldersChanged(IReadOnlyList<FolderEntry> Folders) : SupervisorEffect;

/// <summary>Append a line to the folder's log.</summary>
/// <param name="Path">Folder path.</param>
/// <param name="Message">Log text.</param>
public sealed record FolderLog(string Path, string Message) : SupervisorEffect;

/// <summary>
/// A folder entered <see cref="FolderStatus.Failing"/>, <see cref="FolderStatus.Waiting"/> or
/// <see cref="FolderStatus.Missing"/> (once per transition): send a toast event if
/// notifications are on.
/// </summary>
/// <param name="Path">Folder path.</param>
/// <param name="Status">The status entered.</param>
/// <param name="Text">Notification text.</param>
public sealed record Notify(string Path, FolderStatus Status, string Text) : SupervisorEffect;
