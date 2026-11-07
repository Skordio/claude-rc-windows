using System.Globalization;
using ClaudeRc.Core.Ipc;
using ClaudeRc.Core.Paths;
using ClaudeRc.Core.Storage;

namespace ClaudeRc.Core.Supervision;

/// <summary>
/// The per-folder supervision state machine (SPEC 4, 5, 7), with no processes and no I/O.
/// </summary>
/// <remarks>
/// <para>
/// The host feeds it user requests (<see cref="Add"/>, <see cref="SetOn"/>, <see cref="Restart"/>,
/// <see cref="Remove"/>, <see cref="Takeover"/>), observations (<see cref="SessionStarted"/>,
/// <see cref="StartFailed"/>, <see cref="SessionExited"/>, <see cref="ReadySeen"/>,
/// <see cref="PromptChanged"/>, <see cref="ExternalChanged"/>) and clock ticks
/// (<see cref="Tick"/>, at least once a second or at <see cref="NextDueAt"/>). Every call
/// returns the <see cref="SupervisorEffect"/>s to carry out, in order: start or stop a session,
/// stop an external one, push rows, save the folder list, write the folder log, toast.
/// </para>
/// <para>
/// Time comes only from the <see cref="TimeProvider"/>; whether a folder exists comes only from
/// the injected predicate, asked when a start is due. Not thread-safe: the host calls it from
/// one thread (or under one lock). Paths are expected normalised and compared
/// case-insensitively.
/// </para>
/// </remarks>
public sealed class Supervisor
{
    private readonly TimeProvider _time;
    private readonly Func<string, bool> _directoryExists;
    private readonly SupervisorOptions _o;
    private readonly DateTimeOffset _hostStart;
    private readonly List<Folder> _folders = [];
    // What the last call reported; status can change with time alone, so changes are found by
    // comparing against this rather than against a snapshot taken at the start of a call.
    private Dictionary<string, FolderSnapshot> _reported = new(PathNormalizer.Comparer);
    private bool _shutdown;

    /// <summary>Creates a supervisor; the host-start time (for the missing-folder schedule) is now.</summary>
    /// <param name="time">Clock.</param>
    /// <param name="directoryExists">Whether a folder path exists right now.</param>
    /// <param name="options">Timings; null for the SPEC defaults.</param>
    public Supervisor(TimeProvider time, Func<string, bool> directoryExists, SupervisorOptions? options = null)
    {
        _time = time;
        _directoryExists = directoryExists;
        _o = options ?? new SupervisorOptions();
        _hostStart = time.GetUtcNow();
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    // ---------------------------------------------------------------- queries

    /// <summary>All folders (saved, then unsaved rows), in list order.</summary>
    public IReadOnlyList<FolderSnapshot> Folders
    {
        get
        {
            var now = Now;
            return _folders.Select(f => Snapshot(f, now)).ToList();
        }
    }

    /// <summary>One folder, or null when it is not known.</summary>
    public FolderSnapshot? Get(string path) => Find(path) is { } f ? Snapshot(f, Now) : null;

    /// <summary>The IPC rows (SPEC 10) for every folder, with display names and countdowns.</summary>
    public IReadOnlyList<Row> GetRows()
    {
        var now = Now;
        var names = DisplayNames.Compute(_folders.Select(f => f.Path));
        return _folders.Select(f =>
        {
            var s = Snapshot(f, now);
            return new Row
            {
                Path = s.Path,
                Name = names[s.Path],
                On = s.On,
                Saved = s.Saved,
                Status = s.Status,
                Prompt = s.Status == FolderStatus.Waiting ? s.Prompt : null,
                ExitCode = s.Status is FolderStatus.Restarting or FolderStatus.Failing ? s.LastExitCode : null,
                RestartInS = s.RestartAt is { } at ? (int)Math.Max(0, Math.Ceiling((at - now).TotalSeconds)) : null,
                Crashes10m = s.Crashes10m,
                StartedAt = s.StartedAt,
                Pid = s.Pid ?? (s.Status == FolderStatus.External ? s.ExternalPid : null),
                Error = s.Error,
            };
        }).ToList();
    }

    /// <summary>
    /// The earliest moment a <see cref="Tick"/> can change something, or null when nothing is
    /// pending. Ticking more often is harmless.
    /// </summary>
    public DateTimeOffset? NextDueAt
    {
        get
        {
            var now = Now;
            DateTimeOffset? best = null;
            void Consider(DateTimeOffset t) { if (best is null || t < best) best = t; }
            foreach (var f in _folders)
            {
                if (f.Proc == Proc.None && f.NextStartAt is { } at && !_shutdown)
                    Consider(at);
                if (f.Proc == Proc.Alive && f.StartedAt is { } started)
                {
                    if (!f.Ready && started + _o.StartingTimeout > now)
                        Consider(started + _o.StartingTimeout);
                    if (f.Exits.Count > 0 || f.BackoffIndex > 0)
                        Consider(started + _o.StableUptime);
                }
                if (f.Exits.Count > 0)
                    Consider(f.Exits.Min() + _o.FailingWindow);
            }
            return best;
        }
    }

    // ---------------------------------------------------------------- host lifecycle

    /// <summary>
    /// Host start (SPEC 5.4): loads the saved list and queues every ON folder, staggered by
    /// <see cref="SupervisorOptions.StartStagger"/>. Report external sessions found by the first
    /// process scan (<see cref="ExternalChanged"/>) before calling this, so they are not started.
    /// Call once.
    /// </summary>
    public SupervisorResult Load(IEnumerable<FolderEntry> folders) => Run(effects =>
    {
        var now = Now;
        var loaded = new List<Folder>();
        var index = 0;
        foreach (var entry in folders)
        {
            if (loaded.Any(f => PathNormalizer.Comparer.Equals(f.Path, entry.Path)))
                continue;
            var f = Find(entry.Path) ?? new Folder(entry.Path);
            f.Saved = true;
            f.On = entry.On;
            if (f.On)
            {
                f.Schedule(now + _o.StartStagger * index, Wait.Queued);
                index++;
            }
            loaded.Add(f);
        }
        var unsaved = _folders.Where(f => !loaded.Contains(f)).ToList();
        _folders.Clear();
        _folders.AddRange(loaded);
        _folders.AddRange(unsaved);
        return null;
    });

    /// <summary>Evaluates timers: due starts, missing re-checks, starting → running, window pruning.</summary>
    public SupervisorResult Tick() => Run(_ => null);

    /// <summary>
    /// Host quit: stops every hosted session (their exits are not counted) and starts nothing
    /// more. Desired states are kept, so the next host start brings the folders back.
    /// </summary>
    public SupervisorResult Shutdown() => Run(effects =>
    {
        _shutdown = true;
        foreach (var f in _folders)
        {
            f.NextStartAt = null;
            if (f.Proc == Proc.Alive)
            {
                f.Proc = Proc.Stopping;
                effects.Add(new StopSession(f.Path));
                Log(effects, f, "stopping: host is quitting");
            }
        }
        return null;
    });

    // ---------------------------------------------------------------- user requests

    /// <summary><c>add</c>: saves the folder as ON and starts it; an existing folder is just turned ON.</summary>
    /// <param name="path">Normalised path of an existing directory (the host checks that).</param>
    public SupervisorResult Add(string path)
    {
        if (Find(path) is not null)
            return SetOn(path, true);
        return Run(effects =>
        {
            var f = new Folder(path) { On = true, Saved = true };
            f.Schedule(Now, Wait.Queued);
            _folders.Add(f);
            Log(effects, f, "added (ON)");
            effects.Add(SavedList());
            return null;
        });
    }

    /// <summary><c>set</c>: turns a folder ON (start now, crash count cleared) or OFF (stop its session).</summary>
    public SupervisorResult SetOn(string path, bool on) => Run(effects =>
    {
        if (Find(path) is not { } f)
            return "folder is not in the list";
        if (on)
        {
            if (f.On && f.Saved)
                return null;
            f.On = true;
            f.Saved = true;
            f.ClearCrashes();
            if (f.Proc == Proc.None && f.ExternalPid is null)
                f.Schedule(Now, Wait.Queued);
            Log(effects, f, "turned ON");
        }
        else
        {
            if (!f.On)
                return null;
            f.On = false;
            f.NextStartAt = null;
            f.ClearCrashes();
            f.TakeoverPending = false;
            if (f.Proc == Proc.Alive)
            {
                f.Proc = Proc.Stopping;
                effects.Add(new StopSession(f.Path));
            }
            Log(effects, f, "turned OFF");
        }
        effects.Add(SavedList());
        return null;
    });

    /// <summary>
    /// <c>restart</c>: clears the crash count and failing state and starts now; a running
    /// session is stopped first and started again when it has exited.
    /// </summary>
    public SupervisorResult Restart(string path) => Run(effects =>
    {
        if (Find(path) is not { } f)
            return "folder is not in the list";
        if (!f.On)
            return "turn the folder ON first";
        if (f.Proc == Proc.None && f.ExternalPid is not null)
            return "this claude rc was started outside the app: use Take over";
        f.ClearCrashes();
        f.Error = null;
        switch (f.Proc)
        {
            case Proc.Alive:
                f.Proc = Proc.Stopping;
                effects.Add(new StopSession(f.Path));
                Log(effects, f, "restart requested: stopping the session");
                break;
            case Proc.None:
                f.Schedule(Now, Wait.Queued);
                Log(effects, f, "restart requested");
                break;
        }
        return null;
    });

    /// <summary><c>remove</c>: removes a saved folder; only allowed when it is OFF.</summary>
    public SupervisorResult Remove(string path) => Run(effects =>
    {
        if (Find(path) is not { Saved: true } f)
            return "folder is not in the list";
        if (f.On)
            return "turn the folder OFF before removing it";
        if (f.ExternalPid is not null)
            f.Saved = false; // stays visible as an unsaved row while the external session runs
        else
            _folders.Remove(f);
        Log(effects, f, "removed");
        effects.Add(SavedList());
        return null;
    });

    /// <summary>
    /// <c>takeover</c> (SPEC 7): turns the folder ON and saves it, asks the host to stop the
    /// external claude rc, and starts a hosted session once the external one has gone.
    /// </summary>
    public SupervisorResult Takeover(string path) => Run(effects =>
    {
        if (Find(path) is not { ExternalPid: { } pid } f)
            return "no claude rc started outside the app is running in this folder";
        var listChanged = !f.On || !f.Saved;
        f.On = true;
        f.Saved = true;
        f.TakeoverPending = true;
        f.NextStartAt = null;
        f.ClearCrashes();
        f.Error = null;
        effects.Add(new StopExternal(f.Path, pid));
        Log(effects, f, Inv($"taking over the external claude rc (pid {pid})"));
        if (listChanged)
            effects.Add(SavedList());
        return null;
    });

    /// <summary>
    /// The host could not stop the external claude rc of a <see cref="Takeover"/> (access denied,
    /// the process changed). The row stays <c>external</c> and shows the error; the folder stays ON,
    /// so the host starts its own session when the external one ends.
    /// </summary>
    public SupervisorResult TakeoverFailed(string path, string error) => Run(effects =>
    {
        if (Find(path) is not { } f)
            return null;
        f.TakeoverPending = false;
        f.Error = "take over failed: " + error;
        Log(effects, f, f.Error);
        return null;
    });

    // ---------------------------------------------------------------- observations

    /// <summary>The host launched the session requested by <see cref="StartSession"/>.</summary>
    public SupervisorResult SessionStarted(string path, int pid) => Run(effects =>
    {
        if (Find(path) is not { Proc: Proc.Launching } f)
            return null;
        f.Proc = Proc.Alive;
        f.Pid = pid;
        f.StartedAt = Now;
        f.Ready = false;
        f.Prompt = null;
        f.Error = null;
        Log(effects, f, Inv($"started (pid {pid})"));
        if (!f.On || _shutdown)
        {
            f.Proc = Proc.Stopping;
            effects.Add(new StopSession(f.Path));
            Log(effects, f, "stopping: turned OFF while starting");
        }
        return null;
    });

    /// <summary>
    /// The host could not start the session (claude not found, <c>.claude.json</c> invalid, launch
    /// error). Shown on the row; retried after <see cref="SupervisorOptions.StartErrorRetry"/>; not a crash.
    /// </summary>
    public SupervisorResult StartFailed(string path, string error) => Run(effects =>
    {
        if (Find(path) is not { Proc: Proc.Launching } f)
            return null;
        f.Proc = Proc.None;
        f.Error = error;
        Log(effects, f, "start failed: " + error);
        if (f.On && !_shutdown)
            f.Schedule(Now + _o.StartErrorRetry, Wait.StartError);
        return null;
    });

    /// <summary>
    /// The hosted session's process exited. After a host-requested stop it is not counted;
    /// otherwise it counts toward failing and is restarted after the backoff (SPEC 5.1, 5.2).
    /// </summary>
    public SupervisorResult SessionExited(string path, int exitCode) => Run(effects =>
    {
        if (Find(path) is not { } f || f.Proc == Proc.None)
            return null;
        var now = Now;
        var requested = f.Proc == Proc.Stopping || _shutdown;
        var uptime = f.StartedAt is { } started ? now - started : TimeSpan.Zero;
        f.Proc = Proc.None;
        f.Pid = null;
        f.StartedAt = null;
        f.Ready = false;
        f.Prompt = null;

        if (requested)
        {
            Log(effects, f, Inv($"stopped (exit code {exitCode})"));
            if (f.On && !_shutdown)
                f.Schedule(now, Wait.Queued);
            return null;
        }

        Log(effects, f, Inv($"exited with code {exitCode} after {uptime.TotalSeconds:0.#} s"));
        if (!f.On)
            return null;
        if (uptime >= _o.StableUptime)
            f.ClearCrashes();
        Prune(f, now);
        f.Exits.Add(now);
        f.LastExitCode = exitCode;
        if (f.Exits.Count >= _o.FailingExits)
        {
            f.Failing = true;
            Log(effects, f, Inv($"failing: {f.Exits.Count} exits within {_o.FailingWindow.TotalMinutes:0} minutes; automatic restarts stopped"));
            return null;
        }
        var delay = _o.Backoff[Math.Min(f.BackoffIndex, _o.Backoff.Count - 1)];
        f.BackoffIndex++;
        f.Schedule(now + delay, Wait.Backoff);
        Log(effects, f, Inv($"restarting in {delay.TotalSeconds:0} s"));
        return null;
    });

    /// <summary>The session printed its ready output (<c>Continue coding in the Claude mobile app</c>): starting → running.</summary>
    public SupervisorResult ReadySeen(string path) => Run(effects =>
    {
        if (Find(path) is { Proc: Proc.Alive, Ready: false } f)
        {
            f.Ready = true;
            Log(effects, f, "ready");
        }
        return null;
    });

    /// <summary>
    /// The prompt scan result for a hosted session (SPEC 8): the prompt text, or null when the
    /// screen no longer ends at a prompt.
    /// </summary>
    public SupervisorResult PromptChanged(string path, string? prompt) => Run(effects =>
    {
        if (string.IsNullOrWhiteSpace(prompt))
            prompt = null;
        if (Find(path) is not { Proc: Proc.Alive } f || f.Prompt == prompt)
            return null;
        f.Prompt = prompt;
        Log(effects, f, prompt is null ? "prompt answered" : "waiting for input: " + prompt);
        return null;
    });

    /// <summary>
    /// The process scan's finding for a folder (SPEC 7): the pid of a claude rc not owned by the
    /// host running there, or null when there is none (any more). An unlisted folder gets an
    /// unsaved row; an ON folder whose external session ends is started.
    /// </summary>
    public SupervisorResult ExternalChanged(string path, int? externalPid) => Run(effects =>
    {
        var f = Find(path);
        if (f is null)
        {
            if (externalPid is null)
                return null;
            f = new Folder(path) { On = false, Saved = false, ExternalPid = externalPid };
            _folders.Add(f);
            Log(effects, f, Inv($"external claude rc seen (pid {externalPid})"));
            return null;
        }
        if (f.ExternalPid == externalPid)
            return null;
        f.ExternalPid = externalPid;
        if (externalPid is not null)
        {
            Log(effects, f, Inv($"external claude rc seen (pid {externalPid})"));
            return null;
        }

        Log(effects, f, "external claude rc ended");
        f.TakeoverPending = false;
        if (!f.Saved && f.Proc == Proc.None)
        {
            _folders.Remove(f);
            return null;
        }
        if (f.On && f.Proc == Proc.None && !f.Failing)
            f.Schedule(Now, Wait.Queued);
        return null;
    });

    // ---------------------------------------------------------------- core

    private SupervisorResult Run(Func<List<SupervisorEffect>, string?> action)
    {
        var effects = new List<SupervisorEffect>();
        var error = action(effects);
        if (error is not null)
            return new SupervisorResult([], error);
        Evaluate(effects);
        Diff(effects);
        return new SupervisorResult(effects);
    }

    private void Evaluate(List<SupervisorEffect> effects)
    {
        var now = Now;
        foreach (var f in _folders.ToList())
        {
            Prune(f, now);
            if (f.Proc == Proc.Alive && f.StartedAt is { } started && now - started >= _o.StableUptime
                && (f.Exits.Count > 0 || f.BackoffIndex > 0))
            {
                f.Exits.Clear();
                f.BackoffIndex = 0;
                Log(effects, f, "up 10 minutes: backoff and crash count reset");
            }
            if (f.Proc == Proc.None && f.NextStartAt is { } at && at <= now)
                TryStart(f, effects, now);
        }
    }

    private void TryStart(Folder f, List<SupervisorEffect> effects, DateTimeOffset now)
    {
        var wasMissing = f.WaitReason == Wait.Missing;
        f.NextStartAt = null;
        if (_shutdown || !f.On || f.Failing || f.ExternalPid is not null)
            return;
        if (!_directoryExists(f.Path))
        {
            var interval = now - _hostStart < _o.MissingFastPeriod ? _o.MissingFastInterval : _o.MissingSlowInterval;
            f.Schedule(now + interval, Wait.Missing);
            if (!wasMissing)
                Log(effects, f, "folder not found; waiting for it to appear");
            return;
        }
        f.WaitReason = Wait.Queued;
        f.Proc = Proc.Launching;
        effects.Add(new StartSession(f.Path));
    }

    private void Prune(Folder f, DateTimeOffset now) => f.Exits.RemoveAll(t => now - t >= _o.FailingWindow);

    private void Diff(List<SupervisorEffect> effects)
    {
        var now = Now;
        var before = _reported;
        var reported = new Dictionary<string, FolderSnapshot>(PathNormalizer.Comparer);
        var names = new Lazy<IReadOnlyDictionary<string, string>>(() => DisplayNames.Compute(_folders.Select(f => f.Path)));
        foreach (var f in _folders)
        {
            var after = Snapshot(f, now);
            reported[f.Path] = after;
            before.Remove(f.Path, out var old);
            if (old == after)
                continue;
            effects.Add(new FolderChanged(after, old?.Status));
            if (old?.Status != after.Status && NotifyText(after, names.Value[after.Path]) is { } text)
                effects.Add(new Notify(after.Path, after.Status, text));
        }
        foreach (var gone in before.Keys)
            effects.Add(new FolderRemoved(gone));
        _reported = reported;
    }

    private static string? NotifyText(FolderSnapshot s, string name) => s.Status switch
    {
        FolderStatus.Failing => Inv($"{name}: claude rc keeps exiting (last exit code {s.LastExitCode}); automatic restarts stopped"),
        FolderStatus.Waiting => $"{name} is waiting for input: {s.Prompt}",
        FolderStatus.Missing => $"{name}: folder not found ({s.Path})",
        _ => null,
    };

    private FolderStatus StatusOf(Folder f, DateTimeOffset now)
    {
        switch (f.Proc)
        {
            case Proc.Launching or Proc.Alive:
                if (!f.On || _shutdown)
                    return FolderStatus.Off;
                if (f.Prompt is not null)
                    return FolderStatus.Waiting;
                if (f.Proc == Proc.Alive && (f.Ready || now - f.StartedAt >= _o.StartingTimeout))
                    return FolderStatus.Running;
                return FolderStatus.Starting;
            case Proc.Stopping:
                return f.On && !_shutdown ? FolderStatus.Starting : FolderStatus.Off;
        }
        if (f.ExternalPid is not null)
            return FolderStatus.External;
        if (!f.On)
            return FolderStatus.Off;
        if (f.Failing)
            return FolderStatus.Failing;
        return f.WaitReason switch
        {
            Wait.Missing => FolderStatus.Missing,
            Wait.Backoff or Wait.StartError => FolderStatus.Restarting,
            _ => FolderStatus.Starting,
        };
    }

    private FolderSnapshot Snapshot(Folder f, DateTimeOffset now)
    {
        var status = StatusOf(f, now);
        var hosted = f.Proc is Proc.Alive or Proc.Stopping;
        return new FolderSnapshot
        {
            Path = f.Path,
            On = f.On,
            Saved = f.Saved,
            Status = status,
            Prompt = f.Proc == Proc.Alive ? f.Prompt : null,
            LastExitCode = f.LastExitCode,
            RestartAt = status == FolderStatus.Restarting ? f.NextStartAt : null,
            Crashes10m = f.Exits.Count(t => now - t < _o.FailingWindow),
            StartedAt = hosted ? f.StartedAt : null,
            Pid = hosted ? f.Pid : null,
            ExternalPid = f.ExternalPid,
            Error = f.Error,
        };
    }

    private FoldersChanged SavedList() =>
        new(_folders.Where(f => f.Saved).Select(f => new FolderEntry(f.Path, f.On)).ToList());

    private static void Log(List<SupervisorEffect> effects, Folder f, string message) => effects.Add(new FolderLog(f.Path, message));

    private Folder? Find(string path) => _folders.FirstOrDefault(f => PathNormalizer.Comparer.Equals(f.Path, path));

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private enum Proc { None, Launching, Alive, Stopping }

    private enum Wait { Queued, Backoff, Missing, StartError }

    private sealed class Folder(string path)
    {
        public string Path { get; } = path;
        public bool On { get; set; }
        public bool Saved { get; set; }
        public Proc Proc { get; set; }
        public int? Pid { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public bool Ready { get; set; }
        public string? Prompt { get; set; }
        public DateTimeOffset? NextStartAt { get; set; }
        public Wait WaitReason { get; set; }
        public bool Failing { get; set; }
        public int? ExternalPid { get; set; }
        public bool TakeoverPending { get; set; }
        public int? LastExitCode { get; set; }
        public List<DateTimeOffset> Exits { get; } = [];
        public int BackoffIndex { get; set; }
        public string? Error { get; set; }

        public void Schedule(DateTimeOffset at, Wait reason)
        {
            NextStartAt = at;
            WaitReason = reason;
        }

        public void ClearCrashes()
        {
            Exits.Clear();
            BackoffIndex = 0;
            Failing = false;
            LastExitCode = null;
        }
    }
}
