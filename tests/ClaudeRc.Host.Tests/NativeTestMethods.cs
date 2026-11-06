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
