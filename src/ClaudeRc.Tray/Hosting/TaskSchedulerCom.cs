using System.Runtime.InteropServices;

namespace ClaudeRc.Tray.Hosting;

/// <summary>
/// The one Task Scheduler call made without the TaskScheduler library: <c>ITaskFolder::RegisterTask</c>.
/// The library takes the password as a .NET string, which can't be wiped (and its marshalling makes
/// a BSTR it frees without zeroing). Here the password goes from <see cref="PromptedPassword"/>'s
/// characters straight into a BSTR, which is zeroed and freed right after the call (SPEC 12).
/// </summary>
internal static class TaskSchedulerCom
{
    private static readonly Guid TaskSchedulerClsid = new("0F87369F-A4E5-4CFC-BD3E-73E6154572DD");

    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonPassword = 1;
    private const int TaskLogonInteractiveToken = 3;

    /// <summary>Creates or replaces the task <paramref name="name"/> in the existing folder <paramref name="folderPath"/>.</summary>
    /// <param name="folderPath">The task's folder, e.g. <c>\claude-rc-windows</c> (it must exist).</param>
    /// <param name="name">The task's name.</param>
    /// <param name="xml">Its definition.</param>
    /// <param name="account">The user it runs as.</param>
    /// <param name="password">The password for <see cref="TaskLogon.Password"/>; null for none.</param>
    /// <param name="logon">How it logs on.</param>
    /// <exception cref="UnauthorizedAccessException">E_ACCESSDENIED (no admin rights for a boot trigger, an administrator's task).</exception>
    /// <exception cref="COMException">Any other Task Scheduler error; <see cref="HostTaskDefinition.LogonFailure"/> for a wrong password.</exception>
    public static void Register(string folderPath, string name, string xml, string account, PromptedPassword? password, TaskLogon logon)
    {
        var type = Type.GetTypeFromCLSID(TaskSchedulerClsid, throwOnError: true)!;
        var service = (ITaskService)Activator.CreateInstance(type)!;
        ITaskFolder? folder = null;
        var user = IntPtr.Zero;
        var secret = IntPtr.Zero;
        try
        {
            service.Connect(default, default, default, default);
            folder = service.GetFolder(folderPath);
            user = Marshal.StringToBSTR(account);
            if (password is not null)
                secret = AllocBstr(password.Chars);
            var registered = folder.RegisterTask(name, xml, TaskCreateOrUpdate, Variant.Bstr(user),
                secret == IntPtr.Zero ? default : Variant.Bstr(secret),
                logon == TaskLogon.Password ? TaskLogonPassword : TaskLogonInteractiveToken, default);
            if (registered != IntPtr.Zero)
                Marshal.Release(registered);
        }
        finally
        {
            if (secret != IntPtr.Zero)
                Marshal.ZeroFreeBSTR(secret);
            if (user != IntPtr.Zero)
                Marshal.FreeBSTR(user);
            if (folder is not null)
                Marshal.FinalReleaseComObject(folder);
            Marshal.FinalReleaseComObject(service);
        }
    }

    /// <summary>A BSTR holding <paramref name="chars"/>, without a .NET string in between. Free it with <see cref="Marshal.ZeroFreeBSTR"/>.</summary>
    /// <exception cref="OutOfMemoryException">SysAllocStringLen failed.</exception>
    public static IntPtr AllocBstr(ReadOnlySpan<char> chars)
    {
        // As ushort (blittable), so the marshaller pins the caller's characters instead of copying them.
        var units = MemoryMarshal.Cast<char, ushort>(chars);
        ushort none = 0;
        var bstr = units.IsEmpty
            ? SysAllocStringLen(ref none, 0)
            : SysAllocStringLen(ref MemoryMarshal.GetReference(units), (uint)units.Length);
        return bstr == IntPtr.Zero ? throw new OutOfMemoryException("SysAllocStringLen failed") : bstr;
    }

    [DllImport("oleaut32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SysAllocStringLen(ref ushort str, uint length);

    /// <summary>A VARIANT, passed by value: VT_EMPTY (default) or a BSTR the caller owns.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Variant
    {
        private const ushort VtBstr = 8;

        private ushort _vt;
        private ushort _reserved1;
        private ushort _reserved2;
        private ushort _reserved3;
        private IntPtr _value;
        private IntPtr _record; // the union's second pointer (BRECORD): VARIANT is 16 bytes on x86, 24 on x64

        public static Variant Bstr(IntPtr bstr) => new() { _vt = VtBstr, _value = bstr };
    }

    // taskschd.h. Dual interfaces: the methods must stay in vtable order; the ones not called are
    // declared only to keep the slots.

    [ComImport]
    [Guid("2FABA4C7-4DA9-4013-9697-20CC3FD40F85")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface ITaskService
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        ITaskFolder GetFolder([MarshalAs(UnmanagedType.BStr)] string path);

        IntPtr GetRunningTasks(int flags);

        IntPtr NewTask(int flags);

        void Connect(Variant serverName, Variant user, Variant domain, Variant password);
    }

    [ComImport]
    [Guid("8CFAC062-A080-4C15-9A88-AA7C2AF80DFC")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface ITaskFolder
    {
        IntPtr NameSlot();

        IntPtr PathSlot();

        IntPtr GetFolder(IntPtr path);

        IntPtr GetFolders(int flags);

        IntPtr CreateFolder(IntPtr subFolderName, Variant sddl);

        void DeleteFolder(IntPtr subFolderName, int flags);

        IntPtr GetTask(IntPtr path);

        IntPtr GetTasks(int flags);

        void DeleteTask(IntPtr name, int flags);

        /// <returns>The IRegisteredTask, which the caller releases.</returns>
        IntPtr RegisterTask([MarshalAs(UnmanagedType.BStr)] string path, [MarshalAs(UnmanagedType.BStr)] string xmlText, int flags,
            Variant userId, Variant password, int logonType, Variant sddl);
    }
}
