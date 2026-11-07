using System.ComponentModel;
using System.Diagnostics;
using ClaudeRc.Core.Claude;

namespace ClaudeRc.Host;

/// <summary>
/// Runs <c>claude --version</c> for the status bar (SPEC 6.1), at most once per claude build: the
/// answer is cached until the program file changes. Thread-safe.
/// </summary>
internal sealed class ClaudeVersionProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    private (string Key, string? Version)? _cached;

    /// <summary>The first line of <c>&lt;program&gt; [prefix…] --version</c>, or null when it cannot be run.</summary>
    public async Task<string?> GetVersionAsync(ClaudeLaunch launch)
    {
        string key;
        try
        {
            var info = new FileInfo(launch.Program);
            key = $"{launch.Program}|{string.Join('|', launch.PrefixArguments)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        lock (_lock)
        {
            if (_cached is { } c && c.Key == key)
                return c.Version;
        }
        var version = await RunAsync(launch).ConfigureAwait(false);
        lock (_lock)
            _cached = (key, version);
        return version;
    }

    private static async Task<string?> RunAsync(ClaudeLaunch launch)
    {
        var psi = new ProcessStartInfo(launch.Program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in launch.PrefixArguments)
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--version");
        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return null;
        }
        using (process)
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(Timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ours to stop: we just started it.
                try { process.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
                return null;
            }
            var text = (await output.ConfigureAwait(false)).Trim();
            var firstLine = text.Split('\n', 2)[0].Trim();
            return process.ExitCode == 0 && firstLine.Length > 0 ? firstLine : null;
        }
    }
}
