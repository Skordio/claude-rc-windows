using ClaudeRc.Core.Ipc;
using ClaudeRc.Tray.Hosting;
using ClaudeRc.Tray.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeRc.Tray.ViewModels;

/// <summary>
/// The settings window (SPEC 3.4): start mode, claude.exe path override with Browse,
/// notifications, start the app at sign-in, and the read-only facts. The claude path and
/// notifications live in the host (Save sends <c>settings.set</c>); the start mode is the
/// scheduled task and the sign-in start the user's Run key, both changed without the host.
/// </summary>
/// <remarks>
/// It follows the connection while the window is open: when the host goes, the fields become
/// read-only and the window says why; when it comes back, the settings are loaded again, unless
/// the user has changed a field, which then keeps what they typed.
/// </remarks>
internal sealed class SettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>Shown while no host is connected.</summary>
    public const string NoHostText = HostConnection.NotRunningText + ": the settings are kept by the host, so start it to change them.";

    private readonly HostConnection _connection;
    private readonly IHostTask _task;
    private readonly IStartModeSwitcher _switcher;
    private readonly IDialogs _dialogs;
    private readonly SignInStart _signIn;
    private bool _canEdit;
    private string _claudePathOverride = "";
    private bool _notifications = true;
    private string? _validationError;
    private HostInfo? _info;
    private HostTaskInfo? _taskInfo;
    private bool _applying;
    private bool _edited;
    private bool _disposed;
    private StartMode _selectedMode;
    private bool _modeEdited;
    private bool _switching;
    private SignInStartState? _signInState;
    private string? _signInError;
    private bool _startAtSignIn;
    private bool _signInEdited;

    /// <summary>Creates the view-model and follows the connection; call <see cref="LoadAsync"/> before showing it.</summary>
    public SettingsViewModel(HostConnection connection, IHostTask task, IStartModeSwitcher switcher, IDialogs dialogs, SignInStart signIn)
    {
        _connection = connection;
        _task = task;
        _switcher = switcher;
        _dialogs = dialogs;
        _signIn = signIn;
        BrowseCommand = new RelayCommand(Browse);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        ReenterPasswordCommand = new AsyncRelayCommand(ReenterPasswordAsync);
        _connection.StateChanged += OnStateChanged;
    }

    /// <summary>The window closed: stop following the connection.</summary>
    public void Dispose()
    {
        _disposed = true;
        _connection.StateChanged -= OnStateChanged;
    }

    /// <summary>The window closes after a successful save.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Settings can be changed only while the host is connected.</summary>
    public bool CanEdit
    {
        get => _canEdit;
        private set
        {
            if (SetProperty(ref _canEdit, value))
                OnPropertyChanged(nameof(CanSave));
        }
    }

    /// <summary>The claude path override; empty means auto-detect (SPEC 6.1).</summary>
    public string ClaudePathOverride
    {
        get => _claudePathOverride;
        set
        {
            if (SetProperty(ref _claudePathOverride, value ?? ""))
            {
                _edited |= !_applying;
                ValidationError = null;
            }
        }
    }

    /// <summary>Toasts on or off (SPEC 3.5).</summary>
    public bool Notifications
    {
        get => _notifications;
        set
        {
            if (SetProperty(ref _notifications, value))
                _edited |= !_applying;
        }
    }

    /// <summary>
    /// Start the app at sign-in (SPEC 3.4): a tray setting kept in the user's Run key, so it can be
    /// changed with or without a host.
    /// </summary>
    public bool StartAtSignIn
    {
        get => _startAtSignIn;
        set
        {
            if (!SetProperty(ref _startAtSignIn, value))
                return;
            _signInEdited = true;
            OnPropertyChanged(nameof(SignInChanged));
            OnPropertyChanged(nameof(CanSave));
        }
    }

    /// <summary>What the Run value is now when that is not simply this copy on or off (another or a missing exe, Startup apps), or why it could not be read.</summary>
    public string? SignInNote => _signInError ?? (_signInState is { } s ? _signIn.Describe(s) : null);

    /// <summary>There is a <see cref="SignInNote"/> to show.</summary>
    public bool HasSignInNote => SignInNote is not null;

    /// <summary>
    /// Save changes the Run value: the box was changed, or it is ticked and the value starts
    /// something else (another copy, a moved exe), which saving points at this copy.
    /// </summary>
    public bool SignInChanged => _signInState is { } s && (StartAtSignIn != s.IsOn || (StartAtSignIn && s.NeedsFix));

    /// <summary>The sign-in start can be changed: always, except while a start mode switch runs.</summary>
    public bool CanChangeSignIn => !IsSwitching;

    private void LoadSignIn()
    {
        try
        {
            _signInState = _signIn.Read();
            _signInError = null;
        }
        catch (Exception e) when (SignInStart.IsRegistryError(e))
        {
            _signInState = SignInStartState.None;
            _signInError = "Whether the app starts at sign-in could not be read: " + e.Message;
        }
        if (!_signInEdited)
            SetProperty(ref _startAtSignIn, _signInState.IsOn, nameof(StartAtSignIn));
        OnPropertyChanged(nameof(SignInNote));
        OnPropertyChanged(nameof(HasSignInNote));
        OnPropertyChanged(nameof(SignInChanged));
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>Writes the sign-in start; false (and the reason shown) when the registry refused.</summary>
    private bool SaveSignIn()
    {
        try
        {
            if (StartAtSignIn)
                _signIn.Enable();
            else
                _signIn.Disable();
        }
        catch (Exception e) when (SignInStart.IsRegistryError(e))
        {
            ValidationError = "The app's start at sign-in could not be changed: " + e.Message;
            return false;
        }
        _signInEdited = false;
        LoadSignIn();
        return true;
    }

    /// <summary>The task's start mode in words.</summary>
    public string StartModeText => _taskInfo?.State == HostTaskState.Missing
        ? "no scheduled task"
        : MainViewModel.StartModeName(CurrentMode);

    /// <summary>The mode the task has now (unknown without a task).</summary>
    private StartMode CurrentMode => _taskInfo?.State == HostTaskState.Exists ? _taskInfo.StartMode : StartMode.Unknown;

    /// <summary>The mode chosen with the radio buttons; Save switches to it when it differs.</summary>
    public StartMode SelectedStartMode
    {
        get => _selectedMode;
        private set
        {
            if (!SetProperty(ref _selectedMode, value))
                return;
            OnPropertyChanged(nameof(StartModeIsLogon));
            OnPropertyChanged(nameof(StartModeIsStartup));
            OnPropertyChanged(nameof(StartModeChanged));
            OnPropertyChanged(nameof(CanSave));
        }
    }

    /// <summary>"At logon" is chosen.</summary>
    public bool StartModeIsLogon
    {
        get => SelectedStartMode == StartMode.Logon;
        set => Choose(value, StartMode.Logon);
    }

    /// <summary>"At startup" is chosen.</summary>
    public bool StartModeIsStartup
    {
        get => SelectedStartMode == StartMode.Startup;
        set => Choose(value, StartMode.Startup);
    }

    private void Choose(bool chosen, StartMode mode)
    {
        if (!chosen || !CanChangeStartMode)
            return;
        _modeEdited = true;
        SelectedStartMode = mode;
    }

    /// <summary>The mode can be switched: the task could be read (it exists, or there is none yet) and no switch is running.</summary>
    public bool CanChangeStartMode => _taskInfo?.State is HostTaskState.Exists or HostTaskState.Missing && !IsSwitching;

    /// <summary>A switch is running (UAC, the password prompt).</summary>
    public bool IsSwitching
    {
        get => _switching;
        private set
        {
            if (SetProperty(ref _switching, value))
            {
                OnPropertyChanged(nameof(CanChangeStartMode));
                OnPropertyChanged(nameof(CanChangeSignIn));
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    /// <summary>The chosen mode differs from the task's: Save switches it.</summary>
    public bool StartModeChanged => SelectedStartMode is StartMode.Logon or StartMode.Startup && SelectedStartMode != CurrentMode;

    /// <summary>Save does something: the host's settings can be sent, the sign-in start changed, or the start mode switched.</summary>
    public bool CanSave => !IsSwitching && (CanEdit || SignInChanged || StartModeChanged);

    /// <summary>The startup task needs the Windows password again (SPEC 9.2).</summary>
    public bool NeedsPassword => _taskInfo?.NeedsPassword == true;

    /// <summary>Re-enter password.</summary>
    public IAsyncRelayCommand ReenterPasswordCommand { get; }

    /// <summary>Re-enter password: the startup flow again (UAC, the credential prompt).</summary>
    public async Task ReenterPasswordAsync()
    {
        var restart = _connection.State != HostState.Connected;
        var result = await RunSwitchAsync(StartMode.Startup, restart);
        if (result.Outcome != SwitchOutcome.Done)
            ValidationError = result.Message;
        else if (result.Message is { } note)
            _dialogs.ShowInfo("Start mode", note);
    }

    private async Task<SwitchResult> RunSwitchAsync(StartMode mode, bool restart)
    {
        IsSwitching = true;
        SwitchResult result;
        try
        {
            result = await _switcher.SwitchAsync(mode, restart);
        }
        finally
        {
            IsSwitching = false;
        }
        if (result.Outcome == SwitchOutcome.Done)
        {
            _modeEdited = false;
            if (restart)
                _connection.ReconnectNow();
        }
        await LoadTaskAsync();
        RaiseFactsChanged();
        return result;
    }

    private async Task LoadTaskAsync()
    {
        _taskInfo = await _task.GetInfoAsync();
        if (!_modeEdited)
            SelectedStartMode = CurrentMode;
        OnPropertyChanged(nameof(StartModeChanged));
        OnPropertyChanged(nameof(CanChangeStartMode));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(NeedsPassword));
    }

    /// <summary>Asked before a switch while the host runs.</summary>
    public static string RestartQuestion(StartMode mode) =>
        $"Restart the host now so it runs {MainViewModel.StartModeName(mode)}? Every folder that is ON stops and starts again.\n\n" +
        "If you choose No, the new start mode takes effect the next time the host starts (restart Windows, or sign out and in).";

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
        LoadSignIn();
        await LoadTaskAsync();
        if (_connection.State == HostState.Connected)
            await _connection.RefreshSettingsAsync();
        var settings = _connection.Settings;
        _info = _connection.Info;
        CanEdit = settings is not null;
        if (settings is not null)
        {
            if (!_edited)
            {
                _applying = true;
                try
                {
                    ClaudePathOverride = settings.ClaudePath ?? "";
                    Notifications = settings.Notifications;
                }
                finally
                {
                    _applying = false;
                }
            }
            ValidationError = null;
        }
        else
        {
            ValidationError = NoHostText;
        }
        RaiseFactsChanged();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        if (_connection.State == HostState.Connected)
        {
            // Loads again; fields the user has changed keep their text.
            _ = LoadAsync();
            return;
        }
        CanEdit = false;
        ValidationError = NoHostText;
        _info = null;
        RaiseFactsChanged();
    }

    private void RaiseFactsChanged()
    {
        OnPropertyChanged(nameof(StartModeText));
        OnPropertyChanged(nameof(StartModeIsLogon));
        OnPropertyChanged(nameof(StartModeIsStartup));
        OnPropertyChanged(nameof(DataDir));
        OnPropertyChanged(nameof(LogDir));
        OnPropertyChanged(nameof(HostVersion));
        OnPropertyChanged(nameof(DetectedClaude));
        OnPropertyChanged(nameof(TaskStatusText));
    }

    /// <summary>
    /// Checks and sends the settings (while the host is connected), writes the sign-in start when
    /// it changed, then switches the start mode if another one is chosen; true when all of it
    /// worked (the window then closes). The settings go first: a switch may restart the host.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        if (!CanSave)
            return false;
        if (CanEdit)
        {
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
            _edited = false;
            await _connection.RefreshSettingsAsync();
        }
        if (SignInChanged && !SaveSignIn())
            return false;
        if (StartModeChanged)
        {
            var mode = SelectedStartMode;
            // A running host is restarted only if the user wants that now; with no host the task
            // is simply run.
            var restart = _connection.State != HostState.Connected || _dialogs.Confirm("Start mode", RestartQuestion(mode));
            var switched = await RunSwitchAsync(mode, restart);
            if (switched.Outcome != SwitchOutcome.Done)
            {
                ValidationError = switched.Message ?? "The start mode was not changed.";
                return false;
            }
            if (switched.Message is { } note)
                _dialogs.ShowInfo("Start mode", note);
        }
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
