using ClaudeRc.Core.Ipc;
using ClaudeRc.Tray.Hosting;
using ClaudeRc.Tray.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeRc.Tray.ViewModels;

/// <summary>
/// The settings window (SPEC 3.4): start mode (shown, changed only from M4 on), claude.exe path
/// override with Browse, notifications, and the read-only facts. The settings live in the host;
/// Save sends <c>settings.set</c>.
/// </summary>
internal sealed class SettingsViewModel : ObservableObject
{
    private readonly HostConnection _connection;
    private readonly IHostTask _task;
    private readonly IDialogs _dialogs;
    private bool _canEdit;
    private string _claudePathOverride = "";
    private bool _notifications = true;
    private string? _validationError;
    private HostInfo? _info;
    private HostTaskInfo? _taskInfo;

    /// <summary>Creates the view-model; call <see cref="LoadAsync"/> before showing it.</summary>
    public SettingsViewModel(HostConnection connection, IHostTask task, IDialogs dialogs)
    {
        _connection = connection;
        _task = task;
        _dialogs = dialogs;
        BrowseCommand = new RelayCommand(Browse);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
    }

    /// <summary>The window closes after a successful save.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Settings can be changed only while the host is connected.</summary>
    public bool CanEdit
    {
        get => _canEdit;
        private set => SetProperty(ref _canEdit, value);
    }

    /// <summary>The claude path override; empty means auto-detect (SPEC 6.1).</summary>
    public string ClaudePathOverride
    {
        get => _claudePathOverride;
        set
        {
            if (SetProperty(ref _claudePathOverride, value ?? ""))
                ValidationError = null;
        }
    }

    /// <summary>Toasts on or off (SPEC 3.5).</summary>
    public bool Notifications
    {
        get => _notifications;
        set => SetProperty(ref _notifications, value);
    }

    /// <summary>The start mode in words (from the task; unknown until M4).</summary>
    public string StartModeText => MainViewModel.StartModeName(_taskInfo?.StartMode ?? StartMode.Unknown);

    /// <summary>The task starts the host at logon.</summary>
    public bool StartModeIsLogon => _taskInfo?.StartMode == StartMode.Logon;

    /// <summary>The task starts the host at startup.</summary>
    public bool StartModeIsStartup => _taskInfo?.StartMode == StartMode.Startup;

    /// <summary>The start mode is changed through Task Scheduler, which comes with M4.</summary>
    public bool CanChangeStartMode => false;

    /// <summary>The host's data folder.</summary>
    public string DataDir => _info?.DataDir ?? "";

    /// <summary>The host's log folder.</summary>
    public string LogDir => _info?.LogDir ?? "";

    /// <summary>The host's version.</summary>
    public string HostVersion => _info?.HostVersion ?? "";

    /// <summary>The claude the host uses now.</summary>
    public string DetectedClaude => _info is null ? "" : MainViewModel.DescribeClaude(_info);

    /// <summary>The scheduled task's state.</summary>
    public string TaskStatusText => _taskInfo?.StatusText ?? "";

    /// <summary>Why Save did not save (shown under the fields), or null.</summary>
    public string? ValidationError
    {
        get => _validationError;
        private set => SetProperty(ref _validationError, value);
    }

    /// <summary>Browse for claude.exe or claude.cmd.</summary>
    public IRelayCommand BrowseCommand { get; }

    /// <summary>Save.</summary>
    public IAsyncRelayCommand SaveCommand { get; }

    /// <summary>Reads the settings and facts from the host, and the task's state.</summary>
    public async Task LoadAsync()
    {
        _taskInfo = await _task.GetInfoAsync();
        if (_connection.State == HostState.Connected)
            await _connection.RefreshSettingsAsync();
        var settings = _connection.Settings;
        _info = _connection.Info;
        CanEdit = settings is not null;
        if (settings is not null)
        {
            ClaudePathOverride = settings.ClaudePath ?? "";
            Notifications = settings.Notifications;
            ValidationError = null;
        }
        else
        {
            ValidationError = $"{HostConnection.NotRunningText}: the settings are kept by the host, so start it to change them.";
        }
        OnPropertyChanged(nameof(StartModeText));
        OnPropertyChanged(nameof(StartModeIsLogon));
        OnPropertyChanged(nameof(StartModeIsStartup));
        OnPropertyChanged(nameof(DataDir));
        OnPropertyChanged(nameof(LogDir));
        OnPropertyChanged(nameof(HostVersion));
        OnPropertyChanged(nameof(DetectedClaude));
        OnPropertyChanged(nameof(TaskStatusText));
    }

    /// <summary>Checks and sends the settings; true when the host took them (the window then closes).</summary>
    public async Task<bool> SaveAsync()
    {
        if (!CanEdit)
            return false;
        if (!ClaudePathRules.TryParse(ClaudePathOverride, out var path, out var error))
        {
            ValidationError = error;
            return false;
        }
        var result = await _connection.SendAsync(new SettingsSetRequest(path, Notifications));
        if (!result.Ok)
        {
            ValidationError = result.Error;
            return false;
        }
        await _connection.RefreshSettingsAsync();
        CloseRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Browse()
    {
        if (_dialogs.PickClaudeExecutable(string.IsNullOrWhiteSpace(ClaudePathOverride) ? null : ClaudePathOverride) is { } picked)
            ClaudePathOverride = picked;
    }
}

/// <summary>The claude path override rules of SPEC 3.4 and 6.1, checked before sending.</summary>
internal static class ClaudePathRules
{
    /// <summary>
    /// Empty (auto-detect, <paramref name="path"/> null) or a full path to a <c>.exe</c> or
    /// <c>.cmd</c>. Surrounding spaces and quotes are removed. Whether the file exists is the host's
    /// to report (SPEC 6.1).
    /// </summary>
    public static bool TryParse(string? text, out string? path, out string? error)
    {
        path = null;
        error = null;
        var t = (text ?? "").Trim().Trim('"').Trim();
        if (t.Length == 0)
            return true;
        if (!Path.IsPathFullyQualified(t))
        {
            error = $"The claude path must be a full path, such as C:\\Users\\me\\.local\\bin\\claude.exe: {t}";
            return false;
        }
        var ext = Path.GetExtension(t);
        if (!ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            error = $"The claude path must be claude.exe or claude.cmd (a .exe or .cmd file): {t}";
            return false;
        }
        path = t;
        return true;
    }
}
