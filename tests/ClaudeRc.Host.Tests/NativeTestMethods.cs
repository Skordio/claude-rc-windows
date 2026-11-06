using System.Runtime.InteropServices;

namespace ClaudeRc.Host.Tests;

internal static partial class NativeTestMethods
{
    /// <summary>With a null handler: 1 makes this process (and its future children) ignore Ctrl+C, 0 restores it.</summary>
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetConsoleCtrlHandler(nint handlerRoutine, int add);

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
