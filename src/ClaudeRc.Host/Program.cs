using ClaudeRc.Core.Hosting;

namespace ClaudeRc.Host;

/// <summary>Entry point of ClaudeRcHost.exe.</summary>
internal static class Program
{
    /// <summary>
    /// Parses the command line (PLAN 2.2 overrides). The pipe server, single-instance mutex and
    /// supervisor loop are wired up in the next M1 step; until then the host only validates its
    /// arguments.
    /// </summary>
    /// <returns>0, or 2 for invalid arguments.</returns>
    private static int Main(string[] args)
    {
        try
        {
            _ = HostOptions.Parse(args, HostDefaults.FromEnvironment());
        }
        catch (ArgumentException)
        {
            return 2;
        }
        return 0;
    }
}
