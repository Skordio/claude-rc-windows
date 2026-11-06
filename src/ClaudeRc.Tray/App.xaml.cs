using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClaudeRc.Tray.Hosting;
using ClaudeRc.Tray.Services;
using ClaudeRc.Tray.ViewModels;
using ClaudeRc.Tray.Views;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;

namespace ClaudeRc.Tray;

/// <summary>
/// ClaudeRc.exe: the tray icon and main window (SPEC 3). One per user session and
/// <c>--instance</c>; a second launch shows the first one's window and exits. The app is only a
/// client of the host: quitting it never stops a session.
/// </summary>
internal partial class App
{
    private const int AsfwAny = -1;

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private TaskbarIcon? _tray;
    private HostConnection? _connection;
    private MainWindow? _main;
    private MainViewModel? _vm;
    private bool _quitting;

    /// <inheritdoc/>
    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStartup(e);

        TrayOptions options;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            options = TrayOptions.Parse(e.Args, identity.User?.Value ?? throw new InvalidOperationException("current user has no SID"));
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message + "\n\nusage: ClaudeRc.exe [--instance <suffix>]", "claude-rc-windows",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        // The event first, so a second launch can always find it once the mutex exists.
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, options.ShowEventName);
        _mutex = new Mutex(true, options.MutexName, out var first);
        if (!first)
        {
            // Let the first instance take the foreground: this process has it, being just launched.
            AllowSetForegroundWindow(AsfwAny);
            _showEvent.Set();
            _showEvent.Dispose();
            _showEvent = null;
            _mutex.Dispose();
            _mutex = null;
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        var time = TimeProvider.System;
        var dialogs = new WpfDialogs();
        var task = new SchtasksHostTask(options.TaskPath);
        _connection = new HostConnection(new PipeHostConnector(options.PipeName), time);
        var windows = new WpfWindows(_connection, task, dialogs);
        _vm = new MainViewModel(_connection, task, dialogs, new ShellLauncher(), windows, time, FolderPaths.IsNetworkPath);
        windows.SettingsSaved += async (_, _) => await _vm.LoadTaskInfoAsync();
        _main = new MainWindow(_vm, options.Instance);
        MainWindow = _main;

        _tray = CreateTrayIcon(options.Instance);
        _connection.StateChanged += (_, _) => _tray.ToolTipText = TrayToolTip(options.Instance);

        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) =>
            Dispatcher.BeginInvoke(ShowMain), null, Timeout.Infinite, executeOnlyOnce: false);

        _connection.Start();
        _ = _vm.LoadTaskInfoAsync();
        ShowMain();
    }

    private TaskbarIcon CreateTrayIcon(string? instance)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Open", Command = new RelayCommand(ShowMain), FontWeight = FontWeights.SemiBold });
        menu.Items.Add(new MenuItem { Header = "Add folder", Command = new AsyncRelayCommand(AddFolderFromTray) });
        menu.Items.Add(new MenuItem { Header = "Settings", Command = new RelayCommand(() => _vm!.SettingsCommand.Execute(null)) });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "Quit tray",
            ToolTip = "Closes this app only. Folders keep running: the host is a separate process.",
            Command = new RelayCommand(Quit),
        });

        var tray = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/tray.ico")),
            ToolTipText = TrayToolTip(instance),
            ContextMenu = menu,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(ShowMain),
            DoubleClickCommand = new RelayCommand(ShowMain),
        };
        // Created in code (not in a window), so it must be created explicitly. No efficiency mode:
        // it would lower the whole process's priority.
        tray.ForceCreate(enablesEfficiencyMode: false);
        return tray;
    }

    private string TrayToolTip(string? instance)
    {
        var name = instance is null ? "claude-rc-windows" : $"claude-rc-windows ({instance})";
        return _connection is null ? name : $"{name}\n{_connection.StateText}";
    }

    private void ShowMain() => _main?.ShowAndActivate();

    private async Task AddFolderFromTray()
    {
        ShowMain();
        await _vm!.AddFolderAsync();
    }

    private async void Quit()
    {
        if (_quitting)
            return;
        _quitting = true;
        foreach (var w in Windows.OfType<Window>().ToList())
        {
            if (w is MainWindow m)
                m.AllowClose = true;
            w.Close();
        }
        if (_connection is not null)
            await _connection.DisposeAsync();
        Shutdown(0);
    }

    /// <inheritdoc/>
    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _tray?.Dispose();
        _showEvent?.Dispose();
        if (_mutex is not null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the tray alive; the host is unaffected either way.
        e.Handled = true;
        MessageBox.Show("Something went wrong in the app:\n\n" + e.Exception.Message, "claude-rc-windows",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
