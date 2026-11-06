using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ClaudeRc.Core.Adoption;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
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
/// <item>terminate the process and its descendants (found by parent id, each checked to be younger
/// than its parent so a reused id is never followed, and terminated through a handle whose creation
/// time was checked).</item>
/// </list>
/// </summary>
internal static unsafe class ExternalStopper
{
    private const uint TerminatedExitCode = 1;
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);

    /// <summary>Stops <paramref name="target"/> if <paramref name="matcher"/> confirms it is still that claude rc.</summary>
    public static StopOutcome Stop(ExternalSession target, ExternalSessionMatcher matcher, TimeSpan closeGrace)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(matcher);
        var root = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE
            | PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE, false, (uint)target.Pid);
        if (root.IsNull)
        {
            var error = Marshal.GetLastPInvokeError();
            // ERROR_INVALID_PARAMETER: no such process any more.
            return error == 87
                ? new StopOutcome(target.Pid, true, false, 0, null)
                : new StopOutcome(target.Pid, false, false, 0, $"cannot open process {target.Pid} ({new System.ComponentModel.Win32Exception(error).Message}); is it running as administrator?");
        }
        try
        {
            if (WindowsProcesses.StartTime(root) != target.StartTime || HasExited(root))
                return new StopOutcome(target.Pid, true, false, 0, null);
            // Read again with the handle held: the id cannot be reused while it is open.
            if (WindowsProcesses.Read(target.Pid) is not { } fresh || !matcher.StillMatches(fresh, target))
                return HasExited(root)
                    ? new StopOutcome(target.Pid, true, false, 0, null)
                    : new StopOutcome(target.Pid, false, false, 0,
                        $"process {target.Pid} is no longer the claude rc in {target.Path}; nothing was stopped");

            var tree = Descendants(new Dictionary<int, long> { [target.Pid] = target.StartTime });
            var closed = CloseOwnWindows(target.Pid, tree);
            if (closed)
                PInvoke.WaitForSingleObject(root, (uint)Math.Clamp(closeGrace.TotalMilliseconds, 0, int.MaxValue));

            var terminated = 0;
            if (!HasExited(root) && PInvoke.TerminateProcess(root, TerminatedExitCode))
                terminated++;
            // Two passes: children started while the first pass ran are found by the second.
            for (var pass = 0; pass < 2; pass++)
            {
                tree = Descendants(tree);
                foreach (var (pid, start) in tree)
                {
                    if (pid != target.Pid && Terminate(pid, start))
                        terminated++;
                }
            }
            PInvoke.WaitForSingleObject(root, (uint)ExitWait.TotalMilliseconds);
            return HasExited(root)
                ? new StopOutcome(target.Pid, true, closed, terminated, null)
                : new StopOutcome(target.Pid, false, closed, terminated, $"process {target.Pid} did not exit after it was terminated");
        }
        finally
        {
            PInvoke.CloseHandle(root);
        }
    }

    private static bool HasExited(HANDLE process) => PInvoke.WaitForSingleObject(process, 0) != WAIT_EVENT.WAIT_TIMEOUT;

    /// <summary>
    /// <paramref name="known"/> (pid → creation time) plus every running process below them: a
    /// process counts as a child only if it was created after its parent.
    /// </summary>
    internal static Dictionary<int, long> Descendants(Dictionary<int, long> known)
    {
        var result = new Dictionary<int, long>(known);
        var entries = WindowsProcesses.List();
        var starts = new Dictionary<int, long?>();
        bool added;
        do
        {
            added = false;
            foreach (var e in entries)
            {
                if (result.ContainsKey(e.Pid) || !result.TryGetValue(e.ParentPid, out var parentStart) || e.Pid == e.ParentPid)
                    continue;
                if (!starts.TryGetValue(e.Pid, out var start))
                    starts[e.Pid] = start = WindowsProcesses.StartTimeOf(e.Pid);
                if (start is { } s && s >= parentStart)
                {
                    result[e.Pid] = s;
                    added = true;
                }
            }
        }
        while (added);
        return result;
    }

    private static bool Terminate(int pid, long startTime)
    {
        var h = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE
            | PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE, false, (uint)pid);
        if (h.IsNull)
            return false;
        try
        {
            return WindowsProcesses.StartTime(h) == startTime && !HasExited(h) && PInvoke.TerminateProcess(h, TerminatedExitCode);
        }
        finally
        {
            PInvoke.CloseHandle(h);
        }
    }

    /// <summary>
    /// Posts WM_CLOSE to the visible top-level windows of claude rc itself and of its own console
    /// host (a <c>conhost.exe</c> child of it); returns whether there was one.
    /// </summary>
    private static bool CloseOwnWindows(int rootPid, Dictionary<int, long> tree)
    {
        var owners = new HashSet<int> { rootPid };
        foreach (var e in WindowsProcesses.List())
        {
            if (e.ParentPid == rootPid && tree.ContainsKey(e.Pid) && e.ImageName.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase))
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
