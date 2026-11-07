using System.Security.Principal;

namespace ClaudeRc.Core.Hosting;

/// <summary>The per-user values the default paths and names are built from.</summary>
/// <param name="AppData">Roaming app data (<c>%APPDATA%</c>).</param>
/// <param name="LocalAppData">Local app data (<c>%LOCALAPPDATA%</c>).</param>
/// <param name="UserProfile">The user's profile folder (<c>%USERPROFILE%</c>).</param>
/// <param name="UserSid">The current user's SID string, e.g. <c>S-1-5-21-…</c>.</param>
public sealed record HostDefaults(string AppData, string LocalAppData, string UserProfile, string UserSid)
{
    /// <summary>Reads the values for the current process's user.</summary>
    public static HostDefaults FromEnvironment()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new HostDefaults(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            identity.User?.Value ?? throw new InvalidOperationException("current user has no SID"));
    }
}

/// <summary>
/// Every path and name the host (and the tray app, for <see cref="PipeName"/>) uses, with the
/// command-line overrides of PLAN 2.2: <c>--data-dir</c>, <c>--log-dir</c>,
/// <c>--claude-json</c>, <c>--instance &lt;suffix&gt;</c> (appended to the pipe and mutex
/// names). Tests always override all of them.
/// </summary>
public sealed record HostOptions
{
    private const string Product = "claude-rc-windows";

    /// <summary>Data folder: <c>folders.json</c> and <c>settings.json</c> (default <c>%APPDATA%\claude-rc-windows</c>).</summary>
    public required string DataDir { get; init; }

    /// <summary>Log folder (default <c>%LOCALAPPDATA%\claude-rc-windows\logs</c>).</summary>
    public required string LogDir { get; init; }

    /// <summary>The <c>.claude.json</c> the presets go into (default <c>%USERPROFILE%\.claude.json</c>).</summary>
    public required string ClaudeJsonPath { get; init; }

    /// <summary>Optional suffix for the pipe and mutex names, so a test host never meets the real one.</summary>
    public string? Instance { get; init; }

    /// <summary>The user's SID string, part of the pipe and mutex names.</summary>
    public required string UserSid { get; init; }

    private string Suffix => Instance is null ? "" : "-" + Instance;

    /// <summary>Pipe name for <c>NamedPipeServerStream</c>/<c>NamedPipeClientStream</c> (without <c>\\.\pipe\</c>): <c>claude-rc-windows-&lt;SID&gt;[-instance]</c>.</summary>
    public string PipeName => $"{Product}-{UserSid}{Suffix}";

    /// <summary>Single-instance mutex (SPEC 9.4): <c>Global\claude-rc-windows-host-&lt;SID&gt;[-instance]</c>.</summary>
    public string MutexName => $@"Global\{Product}-host-{UserSid}{Suffix}";

    /// <summary>The host log, <c>&lt;LogDir&gt;\host.log</c>.</summary>
    public string HostLogPath => Path.Combine(LogDir, "host.log");

    /// <summary>The folder of per-folder logs, <c>&lt;LogDir&gt;\folders</c>.</summary>
    public string FolderLogDir => Path.Combine(LogDir, "folders");

    /// <summary>Parses the host's command line.</summary>
    /// <param name="args">Arguments: <c>--name value</c> or <c>--name=value</c>.</param>
    /// <param name="defaults">Where defaults come from; use <see cref="HostDefaults.FromEnvironment"/> in production.</param>
    /// <exception cref="ArgumentException">An unknown option, a missing value, or an invalid instance suffix.</exception>
    public static HostOptions Parse(IReadOnlyList<string> args, HostDefaults defaults)
    {
        string? dataDir = null, logDir = null, claudeJson = null, instance = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string name;
            string? value = null;
            var eq = arg.IndexOf('=', StringComparison.Ordinal);
            if (arg.StartsWith("--", StringComparison.Ordinal) && eq > 0)
            {
                name = arg[..eq];
                value = arg[(eq + 1)..];
            }
            else
            {
                name = arg;
            }
            if (name is not ("--data-dir" or "--log-dir" or "--claude-json" or "--instance"))
                throw new ArgumentException($"unknown argument: {arg}");
            if (value is null)
            {
                if (i + 1 >= args.Count)
                    throw new ArgumentException($"{name} needs a value");
                value = args[++i];
            }
            switch (name)
            {
                case "--data-dir": dataDir = FullPath(name, value); break;
                case "--log-dir": logDir = FullPath(name, value); break;
                case "--claude-json": claudeJson = FullPath(name, value); break;
                default: instance = ValidInstance(value); break;
            }
        }

        return new HostOptions
        {
            DataDir = dataDir ?? Path.Combine(defaults.AppData, Product),
            LogDir = logDir ?? Path.Combine(defaults.LocalAppData, Product, "logs"),
            ClaudeJsonPath = claudeJson ?? Path.Combine(defaults.UserProfile, ".claude.json"),
            Instance = instance,
            UserSid = defaults.UserSid,
        };
    }

    private static string FullPath(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} needs a value");
        return Path.GetFullPath(value);
    }

    private static string ValidInstance(string value)
    {
        if (value.Length is 0 or > 64 || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException($"--instance must be 1-64 letters, digits, '-' or '_': \"{value}\"");
        return value;
    }
}
