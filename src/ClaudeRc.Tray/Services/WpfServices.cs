using System.Windows;
using ClaudeRc.Tray.Hosting;
using ClaudeRc.Tray.ViewModels;
using ClaudeRc.Tray.Views;
using Microsoft.Win32;

namespace ClaudeRc.Tray.Services;

/// <summary><see cref="IDialogs"/> with WPF message boxes and the native pickers.</summary>
internal sealed class WpfDialogs : IDialogs
{
    private const string Product = "claude-rc-windows";

    private static Window? Owner =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
        ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible);

    /// <inheritdoc/>
    public bool Confirm(string title, string message) =>
        Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <inheritdoc/>
    public void ShowError(string title, string message) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    /// <inheritdoc/>
    public void ShowInfo(string title, string message) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    /// <inheritdoc/>
    public string? PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Select folder", Multiselect = false };
        return ShowDialog(dialog) ? dialog.FolderName : null;
    }

    /// <inheritdoc/>
    public string? PickClaudeExecutable(string? current)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select claude.exe or claude.cmd",
            Filter = "claude (*.exe;*.cmd)|*.exe;*.cmd",
            CheckFileExists = true,
        };
        if (current is not null && Path.GetDirectoryName(current) is { } dir && Directory.Exists(dir))
            dialog.InitialDirectory = dir;
        return ShowDialog(dialog) ? dialog.FileName : null;
    }

    private static bool ShowDialog(CommonDialog dialog) =>
        (Owner is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true;

    private static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        var caption = $"{title} - {Product}";
        return Owner is { } owner
            ? MessageBox.Show(owner, message, caption, buttons, image)
            : MessageBox.Show(message, caption, buttons, image);
    }
}

/// <summary><see cref="IWindows"/>: one settings window at a time, one log window per folder.</summary>
internal sealed class WpfWindows(HostConnection connection, IHostTask task, IDialogs dialogs) : IWindows
{
    private readonly Dictionary<string, LogWindow> _logs = new(StringComparer.OrdinalIgnoreCase);
    private SettingsWindow? _settings;

    /// <summary>Called after settings were saved (the status bar re-reads the task).</summary>
    public event EventHandler? SettingsSaved;

    /// <inheritdoc/>
    public async void OpenSettings()
    {
        if (_settings is { } open)
        {
            Activate(open);
            return;
        }
        var vm = new SettingsViewModel(connection, task, dialogs);
        var window = new SettingsWindow(vm);
        _settings = window;
        window.Closed += (_, _) => _settings = null;
        vm.CloseRequested += (_, _) => SettingsSaved?.Invoke(this, EventArgs.Empty);
        window.Show();
        await vm.LoadAsync();
    }

    /// <inheritdoc/>
    public async void OpenLog(string path, string name)
    {
        if (_logs.TryGetValue(path, out var open))
        {
            Activate(open);
            await ((LogViewModel)open.DataContext).LoadAsync();
            return;
        }
        var vm = new LogViewModel(connection, path, name);
        var window = new LogWindow(vm);
        _logs[path] = window;
        window.Closed += (_, _) => _logs.Remove(path);
        window.Show();
        await vm.LoadAsync();
    }

    /// <inheritdoc/>
    public void OpenConsole(string path, string name) =>
        dialogs.ShowInfo("Show", $"The console viewer for {name} is not available in this build yet.");

    private static void Activate(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
