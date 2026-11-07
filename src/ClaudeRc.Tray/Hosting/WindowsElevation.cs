using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClaudeRc.Core.Processes;

namespace ClaudeRc.Tray.Hosting;

/// <summary>
/// <see cref="IElevation"/> with ShellExecute's <c>runas</c> verb: Windows shows UAC, and the app
/// runs again elevated as the helper. The tray waits for it and reads its exit code.
/// </summary>
/// <param name="exePath">ClaudeRc.exe (<see cref="Environment.ProcessPath"/>).</param>
internal sealed class ShellElevation(string exePath) : IElevation
{
    private const int ErrorCancelled = 1223;

    /// <inheritdoc/>
    public async Task<int?> RunElevatedAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = CommandLine.JoinArguments(arguments),
        };
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return null; // No at the UAC prompt
        }
        if (process is null)
            throw new InvalidOperationException("the elevated helper did not start");
        using (process)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
    }
}

/// <summary>
/// <see cref="ICredentialPrompt"/> with the Windows credential prompt
/// (<c>CredUIPromptForWindowsCredentials</c>), as plain user name and password fields with the
/// account filled in. The password is unpacked into a char array that <see cref="PromptedPassword"/>
/// wipes; the prompt's own buffer is zeroed before it is freed. Nothing is saved
/// (the "remember" box is not offered).
/// </summary>
internal sealed class CredUiPrompt : ICredentialPrompt
{
    private const int ErrorCancelled = 1223;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorLogonFailure = 1326;
    private const uint CreduiwinGeneric = 0x1;
    private const uint CredPackGenericCredentials = 0x4;

    /// <inheritdoc/>
    public PromptedPassword? Prompt(string account, string? error)
    {
        var info = new CredUiInfo
        {
            cbSize = Marshal.SizeOf<CredUiInfo>(),
            pszCaptionText = "claude-rc-windows: start at startup",
            pszMessageText = (error is null ? "" : error + "\n\n") +
                $"Enter the Windows password of {account} (not a PIN). Task Scheduler stores it to start the host " +
                "before you sign in. This app never saves, logs or sends it.",
        };
        var inBuffer = Pack(account, out var inSize);
        try
        {
            uint package = 0;
            var save = false;
            var result = CredUIPromptForWindowsCredentialsW(ref info, error is null ? 0 : ErrorLogonFailure, ref package,
                inBuffer, inSize, out var outBuffer, out var outSize, ref save, CreduiwinGeneric);
            if (result == ErrorCancelled)
                return null;
            if (result != 0)
                throw new Win32Exception(result);
            try
            {
                return Unpack(outBuffer, outSize);
            }
            finally
            {
                Wipe(outBuffer, outSize);
            }
        }
        finally
        {
            Wipe(inBuffer, inSize);
        }
    }

    private static IntPtr Pack(string account, out uint size)
    {
        size = 0;
        CredPackAuthenticationBufferW(CredPackGenericCredentials, account, "", IntPtr.Zero, ref size);
        if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer || size == 0)
        {
            size = 0;
            return IntPtr.Zero; // no prefilled account then
        }
        var buffer = Marshal.AllocCoTaskMem((int)size);
        if (!CredPackAuthenticationBufferW(CredPackGenericCredentials, account, "", buffer, ref size))
        {
            Marshal.FreeCoTaskMem(buffer);
            size = 0;
            return IntPtr.Zero;
        }
        return buffer;
    }

    private static PromptedPassword Unpack(IntPtr buffer, uint size)
    {
        var user = new char[514];
        var domain = new char[338];
        var password = new char[1024];
        uint userLength = (uint)user.Length, domainLength = (uint)domain.Length, passwordLength = (uint)password.Length;
        try
        {
            if (!CredUnPackAuthenticationBufferW(0, buffer, size, user, ref userLength, domain, ref domainLength, password, ref passwordLength))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            var length = Array.IndexOf(password, '\0');
            if (length < 0)
                length = (int)Math.Min(passwordLength, (uint)password.Length);
            return new PromptedPassword(password.AsSpan(0, length).ToArray());
        }
        finally
        {
            Array.Clear(password);
        }
    }

    private static void Wipe(IntPtr buffer, uint size)
    {
        if (buffer == IntPtr.Zero)
            return;
        Marshal.Copy(new byte[size], 0, buffer, (int)size);
        Marshal.FreeCoTaskMem(buffer);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CredUiInfo
    {
        public int cbSize;
        public IntPtr hwndParent;
        public string pszMessageText;
        public string pszCaptionText;
        public IntPtr hbmBanner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CredUIPromptForWindowsCredentialsW(ref CredUiInfo pUiInfo, int dwAuthError, ref uint pulAuthPackage,
        IntPtr pvInAuthBuffer, uint ulInAuthBufferSize, out IntPtr ppvOutAuthBuffer, out uint pulOutAuthBufferSize,
        [MarshalAs(UnmanagedType.Bool)] ref bool pfSave, uint dwFlags);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredPackAuthenticationBufferW(uint dwFlags, string pszUserName, string pszPassword,
        IntPtr pPackedCredentials, ref uint pcbPackedCredentials);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredUnPackAuthenticationBufferW(uint dwFlags, IntPtr pAuthBuffer, uint cbAuthBuffer,
        [Out] char[] pszUserName, ref uint pcchMaxUserName, [Out] char[] pszDomainName, ref uint pcchMaxDomainName,
        [Out] char[] pszPassword, ref uint pcchMaxPassword);
}
