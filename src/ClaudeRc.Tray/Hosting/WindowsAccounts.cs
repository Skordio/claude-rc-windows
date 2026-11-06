using System.Runtime.InteropServices;

namespace ClaudeRc.Tray.Hosting;

/// <summary>Looks up Windows accounts (LookupAccountName, a local lookup that changes nothing).</summary>
internal static class WindowsAccounts
{
    private const int SidTypeUser = 1;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>
    /// Whether <paramref name="name"/> (<c>DOMAIN\name</c> or a bare name) is a user account: not a
    /// group, a well-known or service account (SYSTEM, LOCAL SERVICE, …), a computer or an unknown
    /// name. The elevated helper sets up the task only for a user (SPEC 9.2, 12).
    /// </summary>
    public static bool IsUser(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        int sidSize = 0, domainSize = 0;
        if (LookupAccountName(null, name, null, ref sidSize, null, ref domainSize, out _)
            || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || sidSize <= 0)
        {
            return false; // unknown (ERROR_NONE_MAPPED), or nothing to look up
        }
        var sid = new byte[sidSize];
        var domain = new char[Math.Max(domainSize, 1)];
        return LookupAccountName(null, name, sid, ref sidSize, domain, ref domainSize, out var use) && use == SidTypeUser;
    }

    [DllImport("advapi32.dll", EntryPoint = "LookupAccountNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupAccountName(string? systemName, string accountName, byte[]? sid, ref int sidSize,
        char[]? referencedDomainName, ref int referencedDomainNameSize, out int use);
}
