using System.Runtime.InteropServices;

namespace ClaudeRc.Host.Tests;

internal static partial class NativeTestMethods
{
    /// <summary>With a null handler: 1 makes this process (and its future children) ignore Ctrl+C, 0 restores it.</summary>
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetConsoleCtrlHandler(nint handlerRoutine, int add);

    /// <summary>
    /// Replaces the DACL of a process this test started so that only query and synchronize are
    /// granted (to this user): opening it to terminate is then denied, as for an elevated process.
    /// The test's own handle (full access, from the start) still works.
    /// </summary>
    internal static unsafe void AllowOnlyQuery(Microsoft.Win32.SafeHandles.SafeProcessHandle process)
    {
        const int QueryLimitedAndSynchronize = 0x101000;
        const uint SeKernelObject = 6, DaclSecurityInformation = 4;
        var acl = new System.Security.AccessControl.RawAcl(System.Security.AccessControl.RawAcl.AclRevision, 1);
        acl.InsertAce(0, new System.Security.AccessControl.CommonAce(System.Security.AccessControl.AceFlags.None,
            System.Security.AccessControl.AceQualifier.AccessAllowed, QueryLimitedAndSynchronize,
            System.Security.Principal.WindowsIdentity.GetCurrent().User!, false, null));
        var bytes = new byte[acl.BinaryLength];
        acl.GetBinaryForm(bytes, 0);
        fixed (byte* p = bytes)
        {
            var error = SetSecurityInfo(process.DangerousGetHandle(), SeKernelObject, DaclSecurityInformation, 0, 0, (nint)p, 0);
            if (error != 0)
                throw new System.ComponentModel.Win32Exception((int)error, "SetSecurityInfo failed");
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a thread of its own that impersonates this process's token
    /// with SeDebugPrivilege removed, as the host runs (it never enables it). An elevated test
    /// runner (the GitHub runner) may have it enabled, and with it OpenProcess ignores the DACL that
    /// <see cref="AllowOnlyQuery"/> sets. Only that thread changes: other tests running at the same
    /// time keep the process token.
    /// </summary>
    internal static unsafe T WithoutDebugPrivilege<T>(Func<T> action)
    {
        const int SecurityImpersonation = 2;
        const uint TokenAdjustPrivileges = 0x20, TokenQuery = 0x8, PrivilegeRemoved = 0x4;
        const int ErrorNotAllAssigned = 1300;
        T result = default!;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (!ImpersonateSelf(SecurityImpersonation))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "ImpersonateSelf failed");
                try
                {
                    if (!OpenThreadToken(GetCurrentThread(), TokenAdjustPrivileges | TokenQuery, true, out var token))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "OpenThreadToken failed");
                    try
                    {
                        if (!LookupPrivilegeValueW(null, "SeDebugPrivilege", out var luid))
                            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "LookupPrivilegeValue failed");
                        var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = PrivilegeRemoved };
                        // Not held at all (a non-elevated token): ERROR_NOT_ALL_ASSIGNED, which is fine.
                        if (!AdjustTokenPrivileges(token, false, &privileges, 0, 0, 0))
                            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "AdjustTokenPrivileges failed");
                        var error = Marshal.GetLastPInvokeError();
                        if (error != 0 && error != ErrorNotAllAssigned)
                            throw new System.ComponentModel.Win32Exception(error, "AdjustTokenPrivileges failed");
                    }
                    finally
                    {
                        CloseHandle(token);
                    }
                    result = action();
                }
                finally
                {
                    RevertToSelf();
                }
            }
#pragma warning disable CA1031 // rethrown on the calling thread
            catch (Exception e)
#pragma warning restore CA1031
            {
                failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e);
            }
        });
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }

    /// <summary>Whether the calling thread can open process <paramref name="pid"/> with the right to terminate it.</summary>
    internal static bool CanOpenToTerminate(int pid)
    {
        const uint ProcessTerminate = 0x1;
        var handle = OpenProcess(ProcessTerminate, false, (uint)pid);
        if (handle == 0)
            return false;
        CloseHandle(handle);
        return true;
    }

    /// <summary>TOKEN_PRIVILEGES with one LUID_AND_ATTRIBUTES (the LUID is only 4-byte aligned).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ImpersonateSelf(int impersonationLevel);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RevertToSelf();

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenThreadToken(nint thread, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValueW(string? systemName, string name, out long luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, TokenPrivileges* newState, uint bufferLength, nint previousState, nint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("advapi32.dll")]
    private static partial uint SetSecurityInfo(nint handle, uint objectType, uint securityInfo, nint owner, nint group, nint dacl, nint sacl);

    /// <summary>True when the service exists and is running.</summary>
    internal static bool IsServiceRunning(string name)
    {
        const uint ScManagerConnect = 0x0001, ServiceQueryStatus = 0x0004, ServiceRunning = 4;
        var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == 0)
            return false;
        try
        {
            var service = OpenServiceW(manager, name, ServiceQueryStatus);
            if (service == 0)
                return false;
            try
            {
                return QueryServiceStatus(service, out var status) && status.CurrentState == ServiceRunning;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenServiceW(nint manager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(nint service, out ServiceStatus status);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}
