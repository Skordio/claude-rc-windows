using System.Runtime.InteropServices;
using System.Windows;
using ClaudeRc.Core.Paths;
using ClaudeRc.Tray.Hosting;
using ClaudeRc.Tray.ViewModels;
using ClaudeRc.Tray.Views;
using Microsoft.Web.WebView2.Core;
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

/// <summary><see cref="IWindows"/>: one settings window at a time, one log window and one console window per folder.</summary>
/// <param name="connection">The host connection.</param>
/// <param name="task">The host's scheduled task.</param>
/// <param name="dialogs">Message boxes.</param>
/// <param name="webViewDataDir">The console viewer's WebView2 user data folder (<see cref="TrayOptions.WebViewDataDir"/>).</param>
internal sealed class WpfWindows(HostConnection connection, IHostTask task, IDialogs dialogs, string webViewDataDir) : IWindows
{
    private readonly Dictionary<string, LogWindow> _logs = new(PathNormalizer.Comparer);
    private readonly Dictionary<string, ConsoleWindow> _consoles = new(PathNormalizer.Comparer);
    private Task<CoreWebView2Environment>? _webView;
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
    public void OpenConsole(string path, string name)
    {
        if (_consoles.TryGetValue(path, out var open))
        {
            Activate(open);
            return;
        }
        var vm = new ConsoleViewModel(connection, path, name, CopyToClipboard);
        var window = new ConsoleWindow(vm, WebViewEnvironment);
        _consoles[path] = window;
        window.Closed += (_, _) => _consoles.Remove(path);
        window.Show();
    }

    /// <summary>
    /// One WebView2 environment for every console window, in the instance's own user data folder.
    /// Created again after a failure (the user may have installed the runtime since).
    /// </summary>
    private Task<CoreWebView2Environment> WebViewEnvironment()
    {
        if (_webView is null || _webView.IsFaulted || _webView.IsCanceled)
            _webView = CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: webViewDataDir);
        return _webView;
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (COMException)
        {
            // Another program holds the clipboard; the copy is lost, as in any app.
        }
    }

    private static void Activate(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
