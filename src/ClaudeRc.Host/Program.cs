using System.Security.Principal;
using ClaudeRc.Core.Hosting;
using ClaudeRc.Core.Logging;

namespace ClaudeRc.Host;

/// <summary>Entry point of ClaudeRcHost.exe.</summary>
internal static class Program
{
    /// <summary>
    /// Runs the host: <c>[--data-dir D] [--log-dir L] [--claude-json F] [--instance S]</c> (PLAN 2.2).
    /// Takes the single-instance mutex (SPEC 9.4), then serves until it fails. A running host is
    /// normally ended with its logon session or by Task Scheduler; its sessions die with it (SPEC 6.5).
    /// </summary>
    /// <returns>A <see cref="HostExitCodes"/> value.</returns>
    private static int Main(string[] args)
    {
        HostDefaults defaults;
        HostOptions options;
        try
        {
            defaults = HostDefaults.FromEnvironment();
            options = HostOptions.Parse(args, defaults);
        }
        catch (ArgumentException)
        {
            return HostExitCodes.InvalidArguments;
        }

        var log = LogFiles.HostLog(options.LogDir, TimeProvider.System);
        var user = new SecurityIdentifier(options.UserSid);
        using var instance = SingleInstance.TryAcquire(options.MutexName, user);
        if (instance is null)
        {
            TryLog(log, $"another host is running ({options.MutexName}); exiting");
            return HostExitCodes.AlreadyRunning;
        }

        HostApp app;
        try
        {
            app = HostApp.Start(new HostConfig { Options = options, UserProfile = defaults.UserProfile });
        }
#pragma warning disable CA1031 // Whatever stops the start is logged; the scheduled task retries.
        catch (Exception e)
#pragma warning restore CA1031
        {
            TryLog(log, "host could not start: " + e);
            return HostExitCodes.Failed;
        }

        // Stays on this thread so the mutex is released by the thread that owns it.
        var completion = app.Completion;
        try
        {
            completion.GetAwaiter().GetResult();
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            // Logged by the host.
        }
        app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return completion.IsFaulted ? HostExitCodes.Failed : HostExitCodes.Ok;
    }

    private static void TryLog(RollingLog log, string message)
    {
        try
        {
            log.Write(message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
