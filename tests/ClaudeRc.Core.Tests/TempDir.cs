using System.Diagnostics;

namespace ClaudeRc.Core.Tests;

/// <summary>A fresh, empty directory under %TEMP% that is deleted when disposed.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "claude-rc-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string CreateDir(params string[] parts)
    {
        var p = Combine(parts);
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>Creates a directory junction (no admin rights needed).</summary>
    public static void CreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);
        using var p = Process.Start(psi)!;
        p.WaitForExit(10_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException("mklink /J failed: " + p.StandardError.ReadToEnd());
    }

    public void Dispose()
    {
        // A just-deleted file can keep its folder "not empty" for a moment (scanners, the
        // indexer), so retry briefly.
        for (var attempt = 0; attempt < 10 && Directory.Exists(Path); attempt++)
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }
}
