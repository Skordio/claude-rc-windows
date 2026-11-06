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
    private readonly IDialogs _dialogs;
    private readonly IShell _shell;
    private readonly IWindows _windows;
    private readonly TimeProvider _time;
    private readonly Func<string, bool> _isNetworkPath;
    private HostTaskInfo? _taskInfo;

    /// <summary>Creates the view-model and follows <paramref name="connection"/>.</summary>
    /// <param name="connection">The host connection (started by the caller).</param>
    /// <param name="task">The host's scheduled task (Start host, start mode).</param>
    /// <param name="dialogs">Message boxes and the folder picker.</param>
    /// <param name="shell">Explorer and Windows Terminal.</param>
    /// <param name="windows">The other windows.</param>
    /// <param name="time">Clock for the restart countdowns.</param>
    /// <param name="isNetworkPath">Whether a picked folder is on a network or mapped drive (SPEC 3.2).</param>
    public MainViewModel(HostConnection connection, IHostTask task, IDialogs dialogs, IShell shell, IWindows windows,
        TimeProvider time, Func<string, bool> isNetworkPath)
    {
        _connection = connection;
        _task = task;
        _dialogs = dialogs;
        _shell = shell;
        _windows = windows;
        _time = time;
        _isNetworkPath = isNetworkPath;

        AddFolderCommand = new AsyncRelayCommand(AddFolderAsync);
        StartHostCommand = new AsyncRelayCommand(StartHostAsync);
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

    /// <summary>Start mode for the status bar (from the task; unknown until M4).</summary>
    public string StartModeText => "Start mode: " + StartModeName(_taskInfo?.StartMode ?? StartMode.Unknown);

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

    /// <summary>Reads the scheduled task's state for the status bar.</summary>
    public async Task LoadTaskInfoAsync()
    {
        _taskInfo = await _task.GetInfoAsync();
        OnPropertyChanged(nameof(StartModeText));
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
        var existing = Rows.ToDictionary(r => r.Path, PathNormalizer.Comparer);
        var target = new List<FolderRowViewModel>(incoming.Count);
        foreach (var row in incoming)
        {
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
    }
}
