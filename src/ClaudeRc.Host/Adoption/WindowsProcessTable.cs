using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using ComFileTime = System.Runtime.InteropServices.ComTypes.FILETIME;
using WdkPInvoke = Windows.Wdk.PInvoke;
using WdkProcessInfo = Windows.Wdk.System.Threading.PROCESSINFOCLASS;

namespace ClaudeRc.Host.Adoption;

/// <summary>The real <see cref="IProcessTable"/>: the Toolhelp list, and processes opened by id.</summary>
internal sealed class WindowsProcessTable : IProcessTable
{
    public static WindowsProcessTable Instance { get; } = new();

    private WindowsProcessTable()
    {
    }

    public IReadOnlyList<ProcessEntry> List() => WindowsProcesses.List();

    public ITreeProcess? Open(int pid) => WindowsTreeProcess.Open(pid, out _);
}

/// <summary>
/// A process held open (query, synchronize and, when allowed, terminate). While the handle is open
/// the id cannot be given to another process, even after this one exits.
/// </summary>
internal sealed unsafe class WindowsTreeProcess : ITreeProcess
{
    private const uint TerminatedExitCode = 1;
    private const PROCESS_ACCESS_RIGHTS Query = PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE;

    private const uint StillActive = 259;

    private HANDLE _handle;
    private readonly bool _canWait;

    private WindowsTreeProcess(HANDLE handle, bool canTerminate, bool canWait)
    {
        _handle = handle;
        CanTerminate = canTerminate;
        _canWait = canWait;
        StartTime = WindowsProcesses.StartTime(handle);
        ParentPid = ReadParentPid(handle);
    }

    /// <summary>
    /// Opens a process, with the right to terminate it when allowed; null when it cannot even be
    /// queried. <paramref name="error"/> is the Win32 error that kept the terminate right (or, when
    /// null is returned, any access) from being granted; 0 when it was granted.
    /// </summary>
    public static WindowsTreeProcess? Open(int pid, out int error)
    {
        error = 0;
        if (pid <= 0)
        {
            error = 87;
            return null;
        }
        var h = PInvoke.OpenProcess(Query | PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE, false, (uint)pid);
        if (!h.IsNull)
            return new WindowsTreeProcess(h, canTerminate: true, canWait: true);
        error = Marshal.GetLastPInvokeError();
        if (error == 87)
            return null;
        // An elevated or protected process: query access is still granted, so its creation time
        // can tell whether it is the process that was scanned.
        h = PInvoke.OpenProcess(Query, false, (uint)pid);
        if (!h.IsNull)
            return new WindowsTreeProcess(h, canTerminate: false, canWait: true);
        h = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (!h.IsNull)
            return new WindowsTreeProcess(h, canTerminate: false, canWait: false);
        error = Marshal.GetLastPInvokeError();
        return null;
    }

    /// <summary>Whether it was opened with the right to terminate it.</summary>
    public bool CanTerminate { get; }

    public long? StartTime { get; }

    public int? ParentPid { get; }

    public bool HasExited => WaitExited(TimeSpan.Zero);

    public long? ExitTime
    {
        get
        {
            if (!HasExited)
                return null;
            ComFileTime creation, exit, kernel, user;
            if (!PInvoke.GetProcessTimes(_handle, &creation, &exit, &kernel, &user))
                return null;
            return ((long)(uint)exit.dwHighDateTime << 32) | (uint)exit.dwLowDateTime;
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for it to exit; true when it has.</summary>
    public bool WaitExited(TimeSpan timeout)
    {
        if (_canWait)
            return PInvoke.WaitForSingleObject(_handle, (uint)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue)) != WAIT_EVENT.WAIT_TIMEOUT;
        uint code;
        return PInvoke.GetExitCodeProcess(_handle, &code) && code != StillActive;
    }

    public bool Terminate() => CanTerminate && !HasExited && PInvoke.TerminateProcess(_handle, TerminatedExitCode);

    public void Dispose()
    {
        if (!_handle.IsNull)
        {
            PInvoke.CloseHandle(_handle);
            _handle = default;
        }
    }

    /// <summary>InheritedFromUniqueProcessId: the parent id as the process itself records it.</summary>
    private static int? ReadParentPid(HANDLE process)
    {
        PROCESS_BASIC_INFORMATION info;
        uint returned;
        var status = WdkPInvoke.NtQueryInformationProcess(process, WdkProcessInfo.ProcessBasicInformation, &info, (uint)sizeof(PROCESS_BASIC_INFORMATION), &returned);
        if (status.SeverityCode != NTSTATUS.Severity.Success)
            return null;
        return (int)(nint)info.InheritedFromUniqueProcessId;
    }
}
