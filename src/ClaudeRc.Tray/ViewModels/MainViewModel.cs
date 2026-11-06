using System.Collections.ObjectModel;
using System.ComponentModel;
using ClaudeRc.Core.Ipc;
using ClaudeRc.Core.Paths;
using ClaudeRc.Tray.Hosting;
using ClaudeRc.Tray.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeRc.Tray.ViewModels;

/// <summary>
/// The main window (SPEC 3.1): the rows, the toolbar (Add folder, Settings) and the status bar
/// (host state with Start host, start mode, claude path and version).
/// </summary>
internal sealed class MainViewModel : ObservableObject, IRowActions
{
    private readonly HostConnection _connection;
    private readonly IHostTask _task;
    private readonly IStartModeSwitcher _switcher;
    private readonly IDialogs _dialogs;
    private readonly IShell _shell;
    private readonly IWindows _windows;
    private readonly TimeProvider _time;
    private readonly Func<string, bool> _isNetworkPath;
    private readonly SignInStart _signIn;
    private HostTaskInfo? _taskInfo;
    private string? _pendingFocus;
    private long _pendingFocusSince;
    private bool _offeredTask;

    /// <summary>
    /// How long <see cref="FocusRow"/> waits for a folder that is not in the list yet: a toast click
    /// that starts the tray comes before the first rows. A cold start connects well within this.
    /// </summary>
    public static readonly TimeSpan PendingFocusTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Creates the view-model and follows <paramref name="connection"/>.</summary>
    /// <param name="connection">The host connection (started by the caller).</param>
    /// <param name="task">The host's scheduled task (Start host, start mode).</param>
    /// <param name="switcher">Sets the start mode (first run, re-enter password).</param>
    /// <param name="dialogs">Message boxes and the folder picker.</param>
    /// <param name="shell">Explorer and Windows Terminal.</param>
    /// <param name="windows">The other windows.</param>
    /// <param name="time">Clock for the restart countdowns.</param>
    /// <param name="isNetworkPath">Whether a picked folder is on a network or mapped drive (SPEC 3.2).</param>
    /// <param name="signIn">Starts this app at sign-in (the first run's offer).</param>
    public MainViewModel(HostConnection connection, IHostTask task, IStartModeSwitcher switcher, IDialogs dialogs, IShell shell, IWindows windows,
        TimeProvider time, Func<string, bool> isNetworkPath, SignInStart signIn)
    {
        _signIn = signIn;
        _connection = connection;
        _task = task;
        _switcher = switcher;
        _dialogs = dialogs;
        _shell = shell;
        _windows = windows;
        _time = time;
        _isNetworkPath = isNetworkPath;

        AddFolderCommand = new AsyncRelayCommand(AddFolderAsync);
        StartHostCommand = new AsyncRelayCommand(StartHostAsync);
        ReenterPasswordCommand = new AsyncRelayCommand(ReenterPasswordAsync);
        SettingsCommand = new RelayCommand(_windows.OpenSettings);

        _connection.StateChanged += (_, _) => OnStateChanged();
        _connection.RowsChanged += (_, _) => SyncRows();
        _connection.SettingsChanged += (_, _) => OnPropertyChanged(nameof(ClaudeText));
        SyncRows();
    }

    /// <summary>The rows, in the host's order.</summary>
    public ObservableCollection<FolderRowViewModel> Rows { get; } = [];

    /// <summary>Connected to the host.</summary>
    public bool IsHostConnected => _connection.State == HostState.Connected;

    /// <summary>The Start host button is shown when no usable host answers.</summary>
    public bool ShowStartHost => _connection.State is HostState.NotRunning or HostState.VersionMismatch;

    /// <summary>"No folders yet" hint.</summary>
    public bool ShowEmptyHint => IsHostConnected && Rows.Count == 0;

    /// <summary>Shown in place of the list while no usable host answers; null otherwise.</summary>
    public string? HostDownHint => _connection.State switch
    {
        HostState.NotRunning =>
            "The host is not running, so the folders can't be shown. Folders that are ON start again with the host. Use Start host below.",
        HostState.VersionMismatch =>
            "The host and this app are different versions. Restart the host (Start host below runs it again once it has stopped).",
        _ => null,
    };

    /// <summary>Host state for the status bar.</summary>
    public string HostStatusText => _connection.StateText;

    /// <summary>Start mode and task health for the status bar (SPEC 3.1).</summary>
    public string StartModeText => DescribeTask(_taskInfo);

    /// <summary>claude path and version (or why claude was not found), from <c>settings.get</c>'s info.</summary>
    public string ClaudeText => _connection.Info is { } info ? "claude: " + DescribeClaude(info) : "claude: unknown";

    /// <summary>+ Add folder.</summary>
    public IAsyncRelayCommand AddFolderCommand { get; }

    /// <summary>Start host.</summary>
    public IAsyncRelayCommand StartHostCommand { get; }

    /// <summary>Settings.</summary>
    public IRelayCommand SettingsCommand { get; }

    /// <summary>A start mode in words.</summary>
    public static string StartModeName(StartMode mode) => mode switch
    {
        StartMode.Logon => "at logon",
        StartMode.Startup => "at startup",
        _ => "unknown",
    };

    /// <summary>The claude the host found, with its version, or the host's reason it found none.</summary>
    public static string DescribeClaude(HostInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.ClaudePath is { } path)
            return info.ClaudeVersion is { } version ? $"{path} · {version}" : path;
        return info.ClaudeError ?? "not found";
    }

    /// <summary>Start mode and task health for the status bar (SPEC 3.1), e.g. <c>Start mode: at logon · running</c>.</summary>
    public static string DescribeTask(HostTaskInfo? info) => info switch
    {
        null => "Start mode: unknown",
        { State: HostTaskState.Missing } => "Start mode: no scheduled task",
        { State: HostTaskState.Unknown, Error: { } error } => $"Start mode: unknown (the task could not be read: {error})",
        { State: HostTaskState.Unknown } => "Start mode: unknown (the task could not be read)",
        _ => $"Start mode: {StartModeName(info.StartMode)} · {info.Health}",
    };

    /// <summary>The startup task needs the Windows password again (SPEC 9.2): shows Re-enter password.</summary>
    public bool ShowReenterPassword => _taskInfo?.NeedsPassword == true;

    /// <summary>Re-enter password.</summary>
    public IAsyncRelayCommand ReenterPasswordCommand { get; }

    /// <summary>
    /// Re-enter password: runs the startup flow again (UAC and the credential prompt). A host that
    /// is not connected could not start, so the task runs again at once.
    /// </summary>
    public Task ReenterPasswordAsync() => SwitchAsync(StartMode.Startup, "Re-enter password");

    /// <summary>
    /// When the window is shown: reads the task and, when there is none (the portable zip's first
    /// run, SPEC 14), offers to create the logon task and to start this app at sign-in too. Asked
    /// once per run of the tray.
    /// </summary>
    public async Task StartupCheckAsync()
    {
        await LoadTaskInfoAsync();
        if (_taskInfo?.State != HostTaskState.Missing || _offeredTask)
            return;
        _offeredTask = true;
        if (!_dialogs.Confirm("Start the host at logon",
                $"No scheduled task starts the claude-rc-windows host ({_taskInfo.TaskPath}), so it doesn't start by itself.\n\n" +
                "Create it now, so the host starts when you sign in? This app then starts at sign-in too, in the notification area. " +
                "This needs no administrator rights. You can switch to starting before logon, or stop this app starting at sign-in, in Settings."))
            return;
        try
        {
            _signIn.Enable();
        }
        catch (Exception e) when (SignInStart.IsRegistryError(e))
        {
            _dialogs.ShowError("Start the app at sign-in", "This app could not be set to start at sign-in: " + e.Message);
        }
        await SwitchAsync(StartMode.Logon, "Start the host at logon");
    }

    private async Task SwitchAsync(StartMode mode, string title)
    {
        // A host that answers keeps running (the new mode applies from its next start); one that
        // does not is started through the task now.
        var run = !IsHostConnected;
        var result = await _switcher.SwitchAsync(mode, run);
        switch (result.Outcome)
        {
            case SwitchOutcome.Failed:
                _dialogs.ShowError(title, result.Message ?? "The start mode could not be changed.");
                break;
            case SwitchOutcome.Done when result.Message is { } note:
                _dialogs.ShowInfo(title, note);
                break;
        }
        if (result.Outcome == SwitchOutcome.Done && run)
            _connection.ReconnectNow();
        await LoadTaskInfoAsync();
    }

    /// <summary>Reads the scheduled task's state for the status bar.</summary>
    public async Task LoadTaskInfoAsync()
    {
        _taskInfo = await _task.GetInfoAsync();
        OnPropertyChanged(nameof(StartModeText));
        OnPropertyChanged(nameof(ShowReenterPassword));
    }

    /// <summary>
    /// Start host: runs the scheduled task (SPEC 9.4), never the host as a child of the tray. When
    /// the task is missing, says so.
    /// </summary>
    public async Task StartHostAsync()
    {
        var result = await _task.RunAsync();
        if (result.Ok)
            _connection.ReconnectNow();
        else
            _dialogs.ShowError("Start host", result.Error ?? "The host could not be started.");
        await LoadTaskInfoAsync();
    }

    /// <summary>+ Add folder (SPEC 3.2): pick, warn about network drives, send <c>add</c>.</summary>
    public async Task AddFolderAsync()
    {
        var path = _dialogs.PickFolder();
        if (path is null)
            return;
        if (_isNetworkPath(path) && !_dialogs.Confirm("Add folder",
                $"{path} is on a network or mapped drive. It may not exist yet at boot, so the folder can show as missing until the drive is there.\n\nAdd it anyway?"))
            return;
        await SendAsync(new AddRequest(path), "Add folder");
    }

    /// <inheritdoc/>
    public async Task ToggleAsync(FolderRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var on = !row.IsOn;
        if (!on && row.TurnOffConfirmation is { } question && !_dialogs.Confirm("Turn off", question))
            return;
        await SendAsync(new SetRequest(row.Path, on), on ? "Turn on" : "Turn off");
    }

    /// <inheritdoc/>
    public async Task RestartAsync(FolderRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.CanRestart && !row.ShowRestartButton)
            return;
        await SendAsync(new RestartRequest(row.Path), "Restart");
    }

    /// <inheritdoc/>
    public async Task TakeOverAsync(FolderRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.ShowTakeOver)
            return;
        if (!_dialogs.Confirm("Take over",
                $"Take over {row.Name}?\n\nThis stops the claude rc you started yourself and restarts it inside the app."))
            return;
        await SendAsync(new TakeoverRequest(row.Path), "Take over");
    }

    /// <inheritdoc/>
    public async Task RemoveAsync(FolderRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.CanRemove)
            return;
        if (!_dialogs.Confirm("Remove", $"Remove {row.Name} from the list?\n\n{row.Path}\n\nThe folder itself is not changed."))
            return;
        await SendAsync(new RemoveRequest(row.Path), "Remove");
    }

    /// <inheritdoc/>
    public void Show(FolderRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.CanShow)
            _windows.OpenConsole(row.Path, row.Name);
    }

    /// <inheritdoc/>
    public void OpenInExplorer(FolderRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        RunShell("Open folder", () => _shell.OpenInExplorer(row.Path));
    }

    /// <inheritdoc/>
    public void OpenInTerminal(FolderRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        RunShell("Open in Windows Terminal", () => _shell.OpenInTerminal(row.Path));
    }

    /// <inheritdoc/>
    public void ViewLog(FolderRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _windows.OpenLog(row.Path, row.Name);
    }

    /// <summary>A row should be scrolled into view (a toast was clicked, SPEC 3.5).</summary>
    public event EventHandler<FolderRowViewModel>? RowFocusRequested;

    /// <summary>
    /// Highlights the folder's row and asks the window to bring it into view. A folder that is not
    /// in the list yet is focused when it appears, within <see cref="PendingFocusTimeout"/>.
    /// </summary>
    public void FocusRow(string path)
    {
        var target = Rows.FirstOrDefault(r => PathNormalizer.Comparer.Equals(r.Path, path));
        if (target is null)
        {
            _pendingFocus = path;
            _pendingFocusSince = _time.GetTimestamp();
            return;
        }
        _pendingFocus = null;
        foreach (var row in Rows)
            row.IsHighlighted = ReferenceEquals(row, target);
        RowFocusRequested?.Invoke(this, target);
    }

    /// <summary>Ends the highlight of <see cref="FocusRow"/>.</summary>
    public void ClearHighlight()
    {
        foreach (var row in Rows)
            row.IsHighlighted = false;
    }

    /// <summary>Moves every restart countdown on; the window calls it about once a second.</summary>
    public void Tick()
    {
        foreach (var row in Rows)
            row.Tick();
    }

    private void RunShell(string title, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is DirectoryNotFoundException or Win32Exception or InvalidOperationException)
        {
            _dialogs.ShowError(title, e.Message);
        }
    }

    private async Task SendAsync(IpcRequest request, string title)
    {
        var result = await _connection.SendAsync(request);
        if (!result.Ok)
            _dialogs.ShowError(title, result.Error ?? "The host refused the request.");
    }

    private void OnStateChanged()
    {
        OnPropertyChanged(nameof(IsHostConnected));
        OnPropertyChanged(nameof(ShowStartHost));
        OnPropertyChanged(nameof(HostStatusText));
        OnPropertyChanged(nameof(HostDownHint));
        OnPropertyChanged(nameof(ShowEmptyHint));
        OnPropertyChanged(nameof(ClaudeText));
    }

    /// <summary>Makes <see cref="Rows"/> match the host's rows, keeping each folder's view-model.</summary>
    private void SyncRows()
    {
        var incoming = _connection.Rows;
        var existing = new Dictionary<string, FolderRowViewModel>(PathNormalizer.Comparer);
        foreach (var r in Rows)
            existing.TryAdd(r.Path, r);
        var target = new List<FolderRowViewModel>(incoming.Count);
        var seen = new HashSet<string>(PathNormalizer.Comparer);
        foreach (var row in incoming)
        {
            // The host never sends one folder twice; if it did, the first row wins.
            if (!seen.Add(row.Path))
                continue;
            if (existing.Remove(row.Path, out var vm))
                vm.Update(row);
            else
                vm = new FolderRowViewModel(row, _time, this);
            target.Add(vm);
        }
        foreach (var gone in existing.Values)
            Rows.Remove(gone);
        for (var i = 0; i < target.Count; i++)
        {
            var at = Rows.IndexOf(target[i]);
            if (at < 0)
                Rows.Insert(i, target[i]);
            else if (at != i)
                Rows.Move(at, i);
        }
        OnPropertyChanged(nameof(ShowEmptyHint));

        if (_pendingFocus is { } pending)
        {
            if (_time.GetElapsedTime(_pendingFocusSince) > PendingFocusTimeout)
                _pendingFocus = null;
            else if (Rows.Any(r => PathNormalizer.Comparer.Equals(r.Path, pending)))
                FocusRow(pending);
        }
    }
}
