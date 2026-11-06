using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ClaudeRc.Core.Adoption;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ClaudeRc.Host.Adoption;

/// <summary>How stopping one external claude rc went.</summary>
/// <param name="Pid">Its process id.</param>
/// <param name="Stopped">True when it is gone (or was already gone).</param>
/// <param name="ClosedWindow">True when its window was asked to close.</param>
/// <param name="Terminated">How many processes of its tree were terminated.</param>
/// <param name="Error">Why it was not stopped.</param>
internal sealed record StopOutcome(int Pid, bool Stopped, bool ClosedWindow, int Terminated, string? Error);

/// <summary>
/// Take over's stop of an external claude rc (SPEC 7). Ctrl+C cannot be sent from outside, so:
/// <list type="number">
/// <item>check that the process is still the one the scan found (same id and creation time, still
/// <c>claude rc</c> of this user, in the same folder); otherwise touch nothing;</item>
/// <item>if it has a window of its own (its own console window, or a window of the process itself),
/// post <c>WM_CLOSE</c> to it and wait up to the grace for it to exit. A window of any other process
/// is never closed: a Windows Terminal tab or a shell's console that claude rc only shares belongs to
/// the user's terminal;</item>
/// <item>terminate the process and its descendants (<see cref="ProcessTree"/>: every member held open
/// from when it is found, so a reused id is never followed, and terminated through that handle).
/// Nothing is stopped if the host or one of its sessions is in the tree.</item>
/// </list>
/// </summary>
internal static unsafe class ExternalStopper
{
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Stops <paramref name="target"/> if <paramref name="matcher"/> confirms it is still that claude rc.
    /// Nothing is stopped if one of <paramref name="protectedPids"/> (the host and its sessions) runs
    /// in its tree.
    /// </summary>
    public static StopOutcome Stop(ExternalSession target, ExternalSessionMatcher matcher, TimeSpan closeGrace, IReadOnlySet<int> protectedPids)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(protectedPids);
        using var root = WindowsTreeProcess.Open(target.Pid, out var openError);
        if (root is null)
        {
            // ERROR_INVALID_PARAMETER: no such process any more.
            return openError == ErrorInvalidParameter
                ? Gone(target)
                : new StopOutcome(target.Pid, false, false, 0, CannotOpen(target, openError));
        }
        if (root.StartTime != target.StartTime || root.HasExited)
            return Gone(target); // the id now names another process (which may be one we cannot touch)
        if (!root.CanTerminate)
            return new StopOutcome(target.Pid, false, false, 0, CannotOpen(target, openError));
        // Read again with the handle held: the id cannot be reused while it is open.
        if (WindowsProcesses.Read(target.Pid) is not { } fresh || !matcher.StillMatches(fresh, target))
            return root.HasExited
                ? Gone(target)
                : new StopOutcome(target.Pid, false, false, 0,
                    $"process {target.Pid} is no longer the claude rc in {target.Path}; nothing was stopped");

        // Every member stays open until the stop is done, so no id in the tree can be reused.
        using var tree = new ProcessTree(WindowsProcessTable.Instance, target.Pid, root, protectedPids);
        tree.Expand();
        var inside = protectedPids.Contains(target.Pid) ? target.Pid : tree.ExcludedFound.Cast<int?>().FirstOrDefault();
        if (inside is not null)
            return new StopOutcome(target.Pid, false, false, 0, string.Create(CultureInfo.InvariantCulture,
                $"the app's host or one of its sessions (pid {inside}) runs inside this claude rc; nothing was stopped. Stop it by hand"));

        var closed = CloseOwnWindows(target.Pid, tree.Pids);
        if (closed)
            root.WaitExited(closeGrace);

        var terminated = 0;
        if (root.Terminate())
            terminated++;
        // Two passes: children started while the first pass ran are found by the second.
        for (var pass = 0; pass < 2; pass++)
        {
            tree.Expand();
            terminated += tree.TerminateAll();
        }
        return root.WaitExited(ExitWait)
            ? new StopOutcome(target.Pid, true, closed, terminated, null)
            : new StopOutcome(target.Pid, false, closed, terminated, $"process {target.Pid} did not exit after it was terminated");
    }

    private const int ErrorInvalidParameter = 87;

    private static StopOutcome Gone(ExternalSession target) => new(target.Pid, true, false, 0, null);

    private static string CannotOpen(ExternalSession target, int error) =>
        $"cannot open process {target.Pid} ({new System.ComponentModel.Win32Exception(error).Message}); is it running as administrator?";

    /// <summary>
    /// Posts WM_CLOSE to the visible top-level windows of claude rc itself and of its own console
    /// host (a <c>conhost.exe</c> child of it); returns whether there was one.
    /// </summary>
    private static bool CloseOwnWindows(int rootPid, IReadOnlyCollection<int> tree)
    {
        var owners = new HashSet<int> { rootPid };
        foreach (var e in WindowsProcesses.List())
        {
            if (e.ParentPid == rootPid && tree.Contains(e.Pid) && e.ImageName.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase))
                owners.Add(e.Pid);
        }
        var windows = TopLevelWindows(owners);
        foreach (var hwnd in windows)
            PInvoke.PostMessage(hwnd, PInvoke.WM_CLOSE, default, default);
        return windows.Count > 0;
    }

    private static List<HWND> TopLevelWindows(HashSet<int> owners)
    {
        var state = new WindowSearch(owners);
        var handle = GCHandle.Alloc(state);
        try
        {
            PInvoke.EnumWindows(&OnWindow, (LPARAM)GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return state.Found;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL OnWindow(HWND hwnd, LPARAM lParam)
    {
        var state = (WindowSearch)GCHandle.FromIntPtr(lParam).Target!;
        uint pid;
        PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        if (state.Owners.Contains((int)pid) && PInvoke.IsWindowVisible(hwnd)
            && PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER).IsNull)
            state.Found.Add(hwnd);
        return true;
    }

    private sealed class WindowSearch(HashSet<int> owners)
    {
        public HashSet<int> Owners { get; } = owners;
        public List<HWND> Found { get; } = [];
    }
}
