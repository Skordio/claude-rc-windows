using ClaudeRc.Core.Hosting;
using ClaudeRc.Tray.Hosting;

namespace ClaudeRc.Tray;

/// <summary>
/// The tray app's command line (PLAN 2.2):
/// <c>[--instance &lt;suffix&gt;] [-ToastActivated] [--unregister-notifications] [--host-exe &lt;path&gt;] [--host-arg &lt;arg&gt;]…</c>,
/// and for the elevated start-mode helper (SPEC 9.2)
/// <c>--set-start-mode logon|startup --for-user &lt;DOMAIN\name&gt; [--restart-host]</c>. The instance suffix is
/// added to the pipe name (so it meets the host started with the same <c>--instance</c>), to the
/// single-instance names, and to the scheduled task folder, so a test tray never runs the real task.
/// </summary>
internal sealed record TrayOptions
{
    private const string Product = "claude-rc-windows";

    /// <summary>The host's file name, next to ClaudeRc.exe in the installed or portable folder.</summary>
    public const string HostExeName = "ClaudeRcHost.exe";

    /// <summary>What Windows adds when it starts the app for a toast click (Microsoft.Toolkit.Uwp.Notifications).</summary>
    public const string ToastActivatedArgument = "-ToastActivated";

    /// <summary>The <c>--instance</c> suffix, if any.</summary>
    public string? Instance { get; init; }

    /// <summary>The host's pipe name (same rule as <see cref="HostOptions.PipeName"/>).</summary>
    public required string PipeName { get; init; }

    /// <summary>One tray per instance in this logon session.</summary>
    public required string MutexName { get; init; }

    /// <summary>A second launch sets this event; the first one then shows its window.</summary>
    public required string ShowEventName { get; init; }

    /// <summary>The host's scheduled task (SPEC 9): <c>\claude-rc-windows[-instance]\Host</c>.</summary>
    public required string TaskPath { get; init; }

    /// <summary>The WebView2 user data folder of the console viewer: <c>%LOCALAPPDATA%\claude-rc-windows\webview2[-instance]</c>.</summary>
    public string WebViewDataDir { get; init; } = "";

    /// <summary>Started by Windows because a toast was clicked while the app was not running (<c>-ToastActivated</c>).</summary>
    public bool ToastActivated { get; init; }

    /// <summary><c>--unregister-notifications</c>: remove the toast registration (for the uninstaller) and exit.</summary>
    public bool UnregisterNotifications { get; init; }

    /// <summary>ClaudeRcHost.exe, which the scheduled task runs: next to ClaudeRc.exe, or <c>--host-exe</c>.</summary>
    public string HostExe { get; init; } = "";

    /// <summary>
    /// The host's arguments in the task: none in production. A test instance's host gets
    /// <c>--instance</c> and folders of its own (data, logs, <c>.claude.json</c>), so it never
    /// shares the real host's state; <c>--host-arg</c>s replace them.
    /// </summary>
    public IReadOnlyList<string> HostArguments { get; init; } = [];

    /// <summary><c>--set-start-mode</c>: run as the elevated helper that sets the start mode, then exit.</summary>
    public StartMode? SetStartMode { get; init; }

    /// <summary><c>--for-user</c>: the account (<c>DOMAIN\name</c>) the helper sets the task up for.</summary>
    public string? ForUser { get; init; }

    /// <summary><c>--restart-host</c>: after setting the mode, the helper stops the task's host and runs the task again.</summary>
    public bool RestartHost { get; init; }

    /// <summary>
    /// The elevated helper's arguments, so it sets up the same task, host and host arguments as this
    /// app. They are all explicit: an elevated process can run as another (administrator) account,
    /// whose own folders must not leak into the task.
    /// </summary>
    public IReadOnlyList<string> HelperArguments(StartMode mode, string account, bool restartHost)
    {
        List<string> args = ["--set-start-mode", ModeArgument(mode), "--for-user", account];
        if (restartHost)
            args.Add("--restart-host");
        if (Instance is not null)
            args.AddRange(["--instance", Instance]);
        args.AddRange(["--host-exe", HostExe]);
        foreach (var a in HostArguments)
            args.AddRange(["--host-arg", a]);
        return args;
    }

    private static string ModeArgument(StartMode mode) => mode switch
    {
        StartMode.Logon => "logon",
        StartMode.Startup => "startup",
        _ => throw new ArgumentException($"no start mode {mode}", nameof(mode)),
    };

    /// <summary>Parses the command line.</summary>
    /// <param name="args">The arguments.</param>
    /// <param name="userSid">The user's SID (pipe and single-instance names).</param>
    /// <param name="localAppData"><c>%LOCALAPPDATA%</c> (tests); default: this user's.</param>
    /// <param name="appData"><c>%APPDATA%</c> (tests); default: this user's.</param>
    /// <param name="appDir">The folder of ClaudeRc.exe (tests); default: <see cref="AppContext.BaseDirectory"/>.</param>
    /// <exception cref="ArgumentException">An unknown option, a missing or bad value.</exception>
    public static TrayOptions Parse(IReadOnlyList<string> args, string userSid, string? localAppData = null,
        string? appData = null, string? appDir = null)
    {
        string? instance = null, hostExe = null, forUser = null;
        StartMode? setMode = null;
        List<string>? hostArgs = null;
        bool toastActivated = false, unregister = false, restartHost = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string Value()
            {
                if (i + 1 >= args.Count)
                    throw new ArgumentException($"{arg} needs a value");
                return args[++i];
            }

            switch (arg)
            {
                case "--instance":
                    instance = Value();
                    break;
                case var _ when arg.StartsWith("--instance=", StringComparison.Ordinal):
                    instance = arg["--instance=".Length..];
                    break;
                case ToastActivatedArgument:
                    toastActivated = true;
                    break;
                case "--unregister-notifications":
                    unregister = true;
                    break;
                case "--host-exe":
                    hostExe = Value();
                    if (!Path.IsPathFullyQualified(hostExe))
                        throw new ArgumentException($"--host-exe must be a full path: {hostExe}");
                    break;
                case "--host-arg":
                    (hostArgs ??= []).Add(Value());
                    break;
                case "--set-start-mode":
                    setMode = Value() switch
                    {
                        "logon" => StartMode.Logon,
                        "startup" => StartMode.Startup,
                        var v => throw new ArgumentException($"--set-start-mode must be logon or startup: {v}"),
                    };
                    break;
                case "--for-user":
                    forUser = Value();
                    if (string.IsNullOrWhiteSpace(forUser))
                        throw new ArgumentException("--for-user needs an account");
                    break;
                case "--restart-host":
                    restartHost = true;
                    break;
                default:
                    throw new ArgumentException($"unknown argument: {arg}");
            }
        }
        if (setMode is not null && forUser is null)
            throw new ArgumentException("--set-start-mode needs --for-user");

        // The host's own parser checks the suffix and builds the pipe name, so both always agree.
        var host = HostOptions.Parse(instance is null ? [] : ["--instance", instance],
            new HostDefaults("unused", "unused", "unused", userSid));
        var suffix = instance is null ? "" : "-" + instance;
        var local = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        IReadOnlyList<string> derived = instance is null
            ? []
            :
            [
                "--instance", instance,
                "--data-dir", Path.Combine(roaming, Product + suffix),
                "--log-dir", Path.Combine(local, Product + suffix, "logs"),
                "--claude-json", Path.Combine(local, Product + suffix, "claude.json"),
            ];
        return new TrayOptions
        {
            Instance = instance,
            PipeName = host.PipeName,
            MutexName = $@"Local\{Product}-tray-{userSid}{suffix}",
            ShowEventName = $@"Local\{Product}-tray-show-{userSid}{suffix}",
            TaskPath = $@"\{Product}{suffix}\Host",
            WebViewDataDir = Path.Combine(local, Product, "webview2" + suffix),
            ToastActivated = toastActivated,
            UnregisterNotifications = unregister,
            HostExe = hostExe ?? Path.Combine(appDir ?? AppContext.BaseDirectory, HostExeName),
            HostArguments = hostArgs ?? derived,
            SetStartMode = setMode,
            ForUser = forUser,
            RestartHost = restartHost,
        };
    }
}
