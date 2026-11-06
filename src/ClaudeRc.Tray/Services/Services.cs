using System.ComponentModel;
using System.Diagnostics;

namespace ClaudeRc.Tray.Services;

/// <summary>Message boxes and file pickers, so view-models can be tested without windows.</summary>
internal interface IDialogs
{
    /// <summary>Asks Yes/No; true for Yes.</summary>
    bool Confirm(string title, string message);

    /// <summary>Shows an error.</summary>
    void ShowError(string title, string message);

    /// <summary>Shows a message.</summary>
    void ShowInfo(string title, string message);

    /// <summary>The native "Select folder" dialog (SPEC 3.2); null when cancelled.</summary>
    string? PickFolder();

    /// <summary>Picks a claude.exe or claude.cmd (SPEC 3.4); null when cancelled.</summary>
    string? PickClaudeExecutable(string? current);
}

/// <summary>Opens a folder in other programs (row menu, SPEC 3.1).</summary>
internal interface IShell
{
    /// <summary>Opens the folder in Explorer.</summary>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    void OpenInExplorer(string path);

    /// <summary>Opens Windows Terminal in the folder, or Explorer when Windows Terminal is not installed.</summary>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    void OpenInTerminal(string path);
}

/// <summary>Opens the app's other windows.</summary>
internal interface IWindows
{
    /// <summary>The settings window (one at a time).</summary>
    void OpenSettings();

    /// <summary>The folder log viewer.</summary>
    void OpenLog(string path, string name);

    /// <summary>The console viewer (SPEC 3.3).</summary>
    void OpenConsole(string path, string name);
}

/// <summary>Starts <c>explorer.exe</c> and <c>wt.exe</c> with the path as an argument (never through a shell).</summary>
/// <param name="start">Starts a process; throws <see cref="Win32Exception"/> when the program is not found.</param>
/// <param name="directoryExists">Whether a folder exists.</param>
internal sealed class ShellLauncher(Action<ProcessStartInfo> start, Func<string, bool> directoryExists) : IShell
{
    /// <summary>The real launcher.</summary>
    public ShellLauncher()
        : this(psi => Process.Start(psi)?.Dispose(), Directory.Exists)
    {
    }

    /// <summary>
    /// Arguments for <c>wt.exe -d &lt;path&gt;</c>. wt splits its command line into several commands
    /// at <c>;</c>, so a <c>;</c> in the path is escaped as <c>\;</c>.
    /// </summary>
    public static IReadOnlyList<string> WindowsTerminalArguments(string path) =>
        ["-d", path.Replace(";", @"\;", StringComparison.Ordinal)];

    /// <inheritdoc/>
    public void OpenInExplorer(string path)
    {
        CheckExists(path);
        start(Psi("explorer.exe", [path]));
    }

    /// <inheritdoc/>
    public void OpenInTerminal(string path)
    {
        CheckExists(path);
        try
        {
            start(Psi("wt.exe", WindowsTerminalArguments(path)));
        }
        catch (Win32Exception)
        {
            start(Psi("explorer.exe", [path]));
        }
    }

    private void CheckExists(string path)
    {
        if (!directoryExists(path))
            throw new DirectoryNotFoundException($"The folder does not exist: {path}");
    }

    private static ProcessStartInfo Psi(string file, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        return psi;
    }
}

/// <summary>Rules for folder paths picked in the tray.</summary>
internal static class FolderPaths
{
    /// <summary>
    /// A UNC path or a path on a network (mapped) drive. Such folders may not exist yet at boot
    /// (SPEC 3.2), so adding one shows a warning.
    /// </summary>
    /// <param name="path">A full path.</param>
    /// <param name="driveType">The type of the drive with the given root (e.g. <c>Z:\</c>).</param>
    public static bool IsNetworkPath(string path, Func<string, DriveType> driveType)
    {
        ArgumentNullException.ThrowIfNull(driveType);
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return true;
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return false;
        try
        {
            return driveType(root) == DriveType.Network;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary><see cref="IsNetworkPath(string, Func{string, DriveType})"/> with the real drive types.</summary>
    public static bool IsNetworkPath(string path) => IsNetworkPath(path, root => new DriveInfo(root).DriveType);
}
