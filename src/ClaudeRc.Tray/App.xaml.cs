using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClaudeRc.Tray.Hosting;
using ClaudeRc.Tray.Notifications;
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
    private EventWaitHandle? _quitEvent;
    private RegisteredWaitHandle? _quitWait;
    private TaskbarIcon? _tray;
    private HostConnection? _connection;
    private MainWindow? _main;
    private MainViewModel? _vm;
    private ToolkitToastNotifier? _notifier;
    private ToastRouter? _toasts;
    private bool _quitting;

    /// <inheritdoc/>
    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStartup(e);

        TrayOptions options;
        string account;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            account = identity.Name;
            options = TrayOptions.Parse(e.Args, identity.User?.Value ?? throw new InvalidOperationException("current user has no SID"));
        }
        catch (ArgumentException ex)
        {
            // A silent install never waits for a click, even when an older ClaudeRc.exe does not know its arguments.
            if (e.Args.Contains("--quiet", StringComparer.Ordinal))
            {
                Shutdown(2);
                return;
            }
            MessageBox.Show(ex.Message + "\n\nusage: ClaudeRc.exe [--instance <suffix>] [--host-exe <path>] [--unregister-notifications]", "claude-rc-windows",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        if (options.Setup is { } setup)
        {
            // The installer's steps (SPEC 14): no tray, no window of their own; they exit with their result.
            RunSetup(options, setup, account);
            return;
        }

        if (options.RunHost)
        {
            // The elevated run-host helper (SPEC 9.4): runs the fixed task, shows its own errors, exits with its result.
            RunHostHelper(options, account);
            return;
        }

        if (options.SetStartMode is { } mode)
        {
            // The elevated helper (SPEC 9.2): no tray, no window of its own; it exits with its result.
            RunStartModeHelper(options, mode);
            return;
        }

        if (options.UnregisterNotifications)
        {
            // For the uninstaller (M5): remove what the toast toolkit registered under HKCU, then exit.
            ToolkitToastNotifier.Unregister();
            Shutdown(0);
            return;
        }

        // The events first, so a second launch (or the installer) can always find them once the mutex exists.
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, options.ShowEventName);
        _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, options.QuitEventName);
        _mutex = new Mutex(true, options.MutexName, out var first);
        if (!first)
        {
            // The sign-in start finds a tray already running (started by hand): it opens nothing.
            if (!options.Background)
            {
                // Let the first instance take the foreground: this process has it, being just launched.
                AllowSetForegroundWindow(AsfwAny);
                _showEvent.Set();
            }
            _showEvent.Dispose();
            _showEvent = null;
            _quitEvent.Dispose();
            _quitEvent = null;
            _mutex.Dispose();
            _mutex = null;
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        var time = TimeProvider.System;
        var dialogs = new WpfDialogs();
        var tasks = new WindowsTaskService();
        var task = new HostTaskManager(tasks, options.TaskPath, ElevatedRun(options));
        var switcher = new StartModeSwitcher(tasks,
            new ShellElevation(Environment.ProcessPath ?? throw new InvalidOperationException("no process path")),
            new StartModeSetup(options.TaskPath, options.HostExe, options.HostArguments, account),
            (m, restart) => options.HelperArguments(m, account, restart), File.Exists, (delay, ct) => Task.Delay(delay, ct));
        // Between runs of host events the dispatcher handles input and rendering first.
        _connection = new HostConnection(new PipeHostConnector(options.PipeName), time, async () =>
        {
            if (Dispatcher.CheckAccess())
                await Dispatcher.Yield(DispatcherPriority.Background);
            else
                await Task.Yield();
        });
        // Start the app at sign-in (SPEC 3.4): the Run value always names this exe.
        var signIn = new SignInStart(RegistryStartup.ForCurrentUser(), Environment.ProcessPath ?? throw new InvalidOperationException("no process path"),
            options.Instance, File.Exists);
        var windows = new WpfWindows(_connection, task, switcher, dialogs, signIn, options.WebViewDataDir);
        _vm = new MainViewModel(_connection, task, switcher, dialogs, new ShellLauncher(), windows, time, FolderPaths.IsNetworkPath, signIn);
        windows.SettingsSaved += async (_, _) => await _vm.LoadTaskInfoAsync();
        _main = new MainWindow(_vm, options.Instance);
        MainWindow = _main;

        _tray = CreateTrayIcon(options.Instance);
        _connection.StateChanged += (_, _) => _tray.ToolTipText = TrayToolTip(options.Instance);

        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) =>
            Dispatcher.BeginInvoke(ShowMain), null, Timeout.Infinite, executeOnlyOnce: false);
        // The installer (an upgrade or the uninstall) asks the tray to quit before it replaces or removes the files.
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitEvent, (_, _) =>
            Dispatcher.BeginInvoke(Quit), null, Timeout.Infinite, executeOnlyOnce: true);

        // Toasts (SPEC 3.5). A click arrives on a COM thread, also when it started this process.
        _notifier = new ToolkitToastNotifier(options.Instance);
        _toasts = new ToastRouter(_connection, _notifier, path => Dispatcher.BeginInvoke(() => OpenRow(path)));

        _connection.Start();
        // Started at sign-in (--background, SPEC 3.4): only the tray icon until the user opens the window.
        if (!options.Background)
            ShowMain();
    }

    /// <summary>
    /// <c>--set-start-mode</c>: the elevated helper the tray starts with UAC (SPEC 9.2). It
    /// registers the task for <c>--for-user</c> (asking for the password for startup), restarts the
    /// host if asked, shows its own errors and exits with a <see cref="HelperExitCodes"/> value.
    /// </summary>
    private async void RunStartModeHelper(TrayOptions options, StartMode mode)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var dialogs = new WpfDialogs();
        var code = HelperExitCodes.Failed;
        try
        {
            var setup = new StartModeSetup(options.TaskPath, options.HostExe, options.HostArguments, options.ForUser!);
            var helper = new StartModeHelper(new WindowsTaskService(), new CredUiPrompt(), dialogs, File.Exists, WindowsAccounts.IsUser,
                (delay, ct) => Task.Delay(delay, ct));
            code = await helper.RunAsync(setup, mode, options.RestartHost);
        }
#pragma warning disable CA1031 // The helper reports anything that goes wrong and exits.
        catch (Exception e)
#pragma warning restore CA1031
        {
            dialogs.ShowError("Start mode", "The start mode could not be changed: " + e.Message);
        }
        finally
        {
            Shutdown(code);
        }
    }

    /// <summary>
    /// <c>--run-host</c>: the elevated helper the tray (or the installer's <c>--install</c>) starts with
    /// UAC when Task Scheduler refuses to run the task for the user (SPEC 9.4). It only runs the fixed
    /// task, and only when that runs the host next to this exe; no password.
    /// </summary>
    private void RunHostHelper(TrayOptions options, string account)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var dialogs = new WpfDialogs();
        var code = HelperExitCodes.Failed;
        try
        {
            var helper = new StartModeHelper(new WindowsTaskService(), new CredUiPrompt(), dialogs, File.Exists, WindowsAccounts.IsUser,
                (delay, ct) => Task.Delay(delay, ct));
            code = helper.RunHost(new StartModeSetup(options.TaskPath, options.AppHostExe, [], account));
        }
#pragma warning disable CA1031 // The helper reports anything that goes wrong and exits.
        catch (Exception e)
#pragma warning restore CA1031
        {
            dialogs.ShowError("Start host", "The host could not be started: " + e.Message);
        }
        finally
        {
            Shutdown(code);
        }
    }

    /// <summary>
    /// <c>--install</c>, <c>--stop-for-update</c>, <c>--uninstall</c>: the installer's steps
    /// (<see cref="AppSetup"/>). They show their own errors (with <c>--quiet</c>, a silent install:
    /// write them to the setup log) and exit with a <see cref="SetupExitCodes"/> value.
    /// </summary>
    private async void RunSetup(TrayOptions options, SetupAction action, string account)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        IDialogs dialogs = options.Quiet ? new SetupLogDialogs(options.SetupLogPath, TimeProvider.System) : new WpfDialogs();
        var code = SetupExitCodes.Failed;
        try
        {
            var tasks = new WindowsTaskService();
            var setup = new StartModeSetup(options.TaskPath, options.HostExe, options.HostArguments, account);
            var trayExe = Path.Combine(Path.GetDirectoryName(options.AppHostExe)!, "ClaudeRc.exe");
            var switcher = new StartModeSwitcher(tasks,
                new ShellElevation(Environment.ProcessPath ?? throw new InvalidOperationException("no process path")),
                setup, (m, restart) => options.HelperArguments(m, account, restart), File.Exists, (delay, ct) => Task.Delay(delay, ct));
            var appSetup = new AppSetup(tasks, switcher, new HostTaskManager(tasks, options.TaskPath, ElevatedRun(options)),
                new PipeHostConnector(options.PipeName),
                new WindowsRunningTray(options.MutexName, options.QuitEventName), new WindowsProcessStopper(),
                ToolkitToastNotifier.Unregister, dialogs, setup,
                trayExe, new SignInStart(RegistryStartup.ForCurrentUser(), trayExe, options.Instance, File.Exists),
                (delay, ct) => Task.Delay(delay, ct))
            {
                FreshInstall = options.FreshInstall,
            };
            code = await appSetup.RunAsync(action);
        }
#pragma warning disable CA1031 // The installer's step reports anything that goes wrong and exits.
        catch (Exception e)
#pragma warning restore CA1031
        {
            dialogs.ShowError(AppSetup.Title, "Setup could not finish: " + e.Message);
        }
        finally
        {
            Shutdown(code);
        }
    }

    /// <summary>Runs the host's task through the elevated <c>--run-host</c> helper when Task Scheduler refuses the user (SPEC 9.4).</summary>
    private static ElevatedHostRun ElevatedRun(TrayOptions options) =>
        new(new ShellElevation(Environment.ProcessPath ?? throw new InvalidOperationException("no process path")), options.TaskPath,
            options.RunHostArguments());

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

    /// <summary>
    /// Shows the window and reads the task for its status bar. With no task (a portable first run,
    /// SPEC 14) the first show offers to create it, so a tray started at sign-in asks nothing until
    /// the user opens it; the offer is made once per run.
    /// </summary>
    private void ShowMain()
    {
        if (_main is null)
            return;
        _main.ShowAndActivate();
        _ = _vm?.StartupCheckAsync();
    }

    private void OpenRow(string path)
    {
        ShowMain();
        _vm?.FocusRow(path);
    }

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
        try
        {
            foreach (var w in Windows.OfType<Window>().ToList())
            {
                if (w is MainWindow m)
                    m.AllowClose = true;
                w.Close();
            }
            if (_connection is not null)
                await _connection.DisposeAsync();
        }
        finally
        {
            // Whatever failed above, Quit ends the process.
            Shutdown(0);
        }
    }

    /// <inheritdoc/>
    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _quitWait?.Unregister(null);
        _toasts?.Dispose();
        _notifier?.Dispose();
        _tray?.Dispose();
        _showEvent?.Dispose();
        _quitEvent?.Dispose();
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
