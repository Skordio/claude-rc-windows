using System.Globalization;
using ClaudeRc.Core;
using ClaudeRc.Core.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeRc.Tray.ViewModels;

/// <summary>The status dot's colour (SPEC 3.1).</summary>
internal enum StatusColor
{
    /// <summary>Off.</summary>
    Grey,

    /// <summary>Running.</summary>
    Green,

    /// <summary>Starting, waiting, restarting.</summary>
    Yellow,

    /// <summary>Failing, missing.</summary>
    Red,

    /// <summary>Running, started outside the app.</summary>
    Blue,
}

/// <summary>What a row's buttons and menu do; the main view-model implements it.</summary>
internal interface IRowActions
{
    /// <summary>The ON/OFF toggle.</summary>
    Task ToggleAsync(FolderRowViewModel row);

    /// <summary>Restart.</summary>
    Task RestartAsync(FolderRowViewModel row);

    /// <summary>Take over.</summary>
    Task TakeOverAsync(FolderRowViewModel row);

    /// <summary>Remove.</summary>
    Task RemoveAsync(FolderRowViewModel row);

    /// <summary>Show (console viewer).</summary>
    void Show(FolderRowViewModel row);

    /// <summary>Open folder in Explorer.</summary>
    void OpenInExplorer(FolderRowViewModel row);

    /// <summary>Open in Windows Terminal.</summary>
    void OpenInTerminal(FolderRowViewModel row);

    /// <summary>View log.</summary>
    void ViewLog(FolderRowViewModel row);
}

/// <summary>
/// One row of the main window (SPEC 3.1): the host's <see cref="Row"/> turned into text, colour
/// and the rules for which buttons are on. A restarting row counts down locally between rows
/// events (the host sends <c>restart_in_s</c> only when the status changes).
/// </summary>
internal sealed class FolderRowViewModel : ObservableObject
{
    /// <summary>The second half of the prompt line.</summary>
    public const string PromptHint = "Show to answer it";

    private static readonly string[] DerivedProperties =
    [
        nameof(Path), nameof(Name), nameof(IsOn), nameof(Status), nameof(StatusText), nameof(StatusColor),
        nameof(CanShow), nameof(CanRemove), nameof(CanRestart), nameof(ShowRestartButton), nameof(ShowTakeOver),
        nameof(HasPrompt), nameof(PromptText), nameof(HasError), nameof(ErrorText), nameof(HasLastLines),
        nameof(LastLinesText), nameof(TurnOffConfirmation),
    ];

    private readonly TimeProvider _time;
    private Row _row;
    private DateTimeOffset _receivedAt;
    private string _statusText;

    /// <summary>Creates the row.</summary>
    /// <param name="row">The host's row.</param>
    /// <param name="time">Clock for the restart countdown.</param>
    /// <param name="actions">What the commands call; null for a row without commands (tests).</param>
    public FolderRowViewModel(Row row, TimeProvider time, IRowActions? actions = null)
    {
        _time = time;
        _row = row;
        _receivedAt = time.GetUtcNow();
        _statusText = ComputeStatusText();

        ToggleCommand = new AsyncRelayCommand(() => actions?.ToggleAsync(this) ?? Task.CompletedTask);
        RestartCommand = new AsyncRelayCommand(() => actions?.RestartAsync(this) ?? Task.CompletedTask, () => CanRestart || ShowRestartButton);
        TakeOverCommand = new AsyncRelayCommand(() => actions?.TakeOverAsync(this) ?? Task.CompletedTask, () => ShowTakeOver);
        RemoveCommand = new AsyncRelayCommand(() => actions?.RemoveAsync(this) ?? Task.CompletedTask, () => CanRemove);
        ShowCommand = new RelayCommand(() => actions?.Show(this), () => CanShow);
        OpenInExplorerCommand = new RelayCommand(() => actions?.OpenInExplorer(this));
        OpenInTerminalCommand = new RelayCommand(() => actions?.OpenInTerminal(this));
        ViewLogCommand = new RelayCommand(() => actions?.ViewLog(this));
    }

    /// <summary>The host's row as last received.</summary>
    public Row Row => _row;

    /// <summary>Full folder path (the row's identity).</summary>
    public string Path => _row.Path;

    /// <summary>Display name from the host (basename or <c>parent\name</c>).</summary>
    public string Name => _row.Name;

    /// <summary>The toggle's state.</summary>
    public bool IsOn => _row.On;

    /// <summary>The host's status.</summary>
    public FolderStatus Status => _row.Status;

    /// <summary>Status text next to the dot (SPEC 4).</summary>
    public string StatusText => _statusText;

    /// <summary>Status dot colour.</summary>
    public StatusColor StatusColor => _row.Status switch
    {
        FolderStatus.Running => StatusColor.Green,
        FolderStatus.Starting or FolderStatus.Waiting or FolderStatus.Restarting => StatusColor.Yellow,
        FolderStatus.Failing or FolderStatus.Missing => StatusColor.Red,
        FolderStatus.External => StatusColor.Blue,
        _ => StatusColor.Grey,
    };

    /// <summary>
    /// Show: only while a hosted session runs (starting, running, waiting). Never for external rows
    /// (SPEC 3.1: there is no console to show), nor while nothing runs.
    /// </summary>
    public bool CanShow => _row.Status is FolderStatus.Starting or FolderStatus.Running or FolderStatus.Waiting;

    /// <summary>Remove: only when OFF (SPEC 3.1), and only for a saved row.</summary>
    public bool CanRemove => !_row.On && _row.Saved;

    /// <summary>Restart in the row menu: any time the folder is ON, except for an external session.</summary>
    public bool CanRestart => _row.On && _row.Status != FolderStatus.External;

    /// <summary>
    /// The Restart button: when the folder is failing or stopped unexpectedly, i.e. <c>failing</c>
    /// or <c>restarting</c> (an exit or a failed start, waiting out the delay).
    /// </summary>
    public bool ShowRestartButton => _row.Status is FolderStatus.Failing or FolderStatus.Restarting;

    /// <summary>Take over: external rows only (SPEC 7).</summary>
    public bool ShowTakeOver => _row.Status == FolderStatus.External;

    /// <summary>The prompt line is shown while waiting.</summary>
    public bool HasPrompt => _row.Status == FolderStatus.Waiting && !string.IsNullOrEmpty(_row.Prompt);

    /// <summary>The prompt text.</summary>
    public string? PromptText => HasPrompt ? _row.Prompt : null;

    /// <summary>Whether the row has an error (why it cannot be started).</summary>
    public bool HasError => !string.IsNullOrEmpty(_row.Error);

    /// <summary>The row's error.</summary>
    public string? ErrorText => HasError ? _row.Error : null;

    /// <summary>A failing row shows the last console lines (SPEC 5.2).</summary>
    public bool HasLastLines => _row.Status == FolderStatus.Failing && _row.LastLines is { Count: > 0 };

    /// <summary>The last console lines, one per line.</summary>
    public string? LastLinesText => HasLastLines ? string.Join("\n", _row.LastLines!) : null;

    /// <summary>
    /// The confirmation to show before turning this row OFF, or null when none is needed. Only a
    /// folder with a running hosted session asks (SPEC 3.1).
    /// </summary>
    public string? TurnOffConfirmation => _row.Status is FolderStatus.Starting or FolderStatus.Running or FolderStatus.Waiting
        ? $"Turn off {Name}?\n\nThis stops the running claude rc session; it won't come back at logon/boot."
        : null;

    /// <summary>The ON/OFF toggle.</summary>
    public IAsyncRelayCommand ToggleCommand { get; }

    /// <summary>Restart (button and menu).</summary>
    public IAsyncRelayCommand RestartCommand { get; }

    /// <summary>Take over.</summary>
    public IAsyncRelayCommand TakeOverCommand { get; }

    /// <summary>Remove (menu).</summary>
    public IAsyncRelayCommand RemoveCommand { get; }

    /// <summary>Show.</summary>
    public IRelayCommand ShowCommand { get; }

    /// <summary>Open folder in Explorer (menu).</summary>
    public IRelayCommand OpenInExplorerCommand { get; }

    /// <summary>Open in Windows Terminal (menu).</summary>
    public IRelayCommand OpenInTerminalCommand { get; }

    /// <summary>View log (menu).</summary>
    public IRelayCommand ViewLogCommand { get; }

    /// <summary>
    /// An exit code as text. Values outside 0–255 are usually NTSTATUS or HRESULT codes, so they also
    /// get their hex form, e.g. <c>-1073741510 (0xC000013A)</c>.
    /// </summary>
    public static string FormatExitCode(int code) => code is >= 0 and <= 255
        ? code.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{code} (0x{unchecked((uint)code):X8})");

    /// <summary>Takes a newer row for the same folder.</summary>
    public void Update(Row row)
    {
        _row = row;
        _receivedAt = _time.GetUtcNow();
        _statusText = ComputeStatusText();
        foreach (var name in DerivedProperties)
            OnPropertyChanged(name);
        RestartCommand.NotifyCanExecuteChanged();
        TakeOverCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        ShowCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Moves the restart countdown on; call about once a second.</summary>
    public void Tick()
    {
        var text = ComputeStatusText();
        if (text == _statusText)
            return;
        _statusText = text;
        OnPropertyChanged(nameof(StatusText));
    }

    private string ComputeStatusText()
    {
        var exit = _row.ExitCode is { } code ? $" (exit code {FormatExitCode(code)})" : "";
        switch (_row.Status)
        {
            case FolderStatus.Off: return "off";
            case FolderStatus.Starting: return "starting";
            case FolderStatus.Running: return "running";
            case FolderStatus.Waiting: return "waiting for input";
            case FolderStatus.Missing: return "folder missing";
            case FolderStatus.External: return "running (started outside the app)";
            case FolderStatus.Failing: return "failing" + exit;
            case FolderStatus.Restarting:
                if (_row.RestartInS is not { } seconds)
                    return "restarting" + exit;
                var elapsed = (_time.GetUtcNow() - _receivedAt).TotalSeconds;
                var left = (int)Math.Max(0, Math.Ceiling(seconds - elapsed - 1e-9));
                return (left == 0 ? "restarting now" : string.Create(CultureInfo.InvariantCulture, $"restarting in {left}s")) + exit;
            default:
                return _row.Status.ToString().ToLowerInvariant();
        }
    }
}
