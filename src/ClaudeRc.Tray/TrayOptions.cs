using ClaudeRc.Core.Hosting;

namespace ClaudeRc.Tray;

/// <summary>
/// The tray app's command line: <c>[--instance &lt;suffix&gt;]</c> (PLAN 2.2). The instance suffix is
/// added to the pipe name (so it meets the host started with the same <c>--instance</c>), to the
/// single-instance names, and to the scheduled task folder, so a test tray never runs the real task.
/// </summary>
internal sealed record TrayOptions
{
    private const string Product = "claude-rc-windows";

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

    /// <summary>Parses the command line.</summary>
    /// <exception cref="ArgumentException">An unknown option or a bad instance suffix.</exception>
    public static TrayOptions Parse(IReadOnlyList<string> args, string userSid)
    {
        string? instance = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--instance")
            {
                if (i + 1 >= args.Count)
                    throw new ArgumentException("--instance needs a value");
                instance = args[++i];
            }
            else if (arg.StartsWith("--instance=", StringComparison.Ordinal))
            {
                instance = arg["--instance=".Length..];
            }
            else
            {
                throw new ArgumentException($"unknown argument: {arg}");
            }
        }

        // The host's own parser checks the suffix and builds the pipe name, so both always agree.
        var host = HostOptions.Parse(instance is null ? [] : ["--instance", instance],
            new HostDefaults("unused", "unused", "unused", userSid));
        var suffix = instance is null ? "" : "-" + instance;
        return new TrayOptions
        {
            Instance = instance,
            PipeName = host.PipeName,
            MutexName = $@"Local\{Product}-tray-{userSid}{suffix}",
            ShowEventName = $@"Local\{Product}-tray-show-{userSid}{suffix}",
            TaskPath = $@"\{Product}{suffix}\Host",
        };
    }
}
