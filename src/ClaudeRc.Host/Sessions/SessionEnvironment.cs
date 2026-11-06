using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;

namespace ClaudeRc.Host.Sessions;

/// <summary>
/// The environment a session runs with (SPEC 6.3): the user's environment as Windows builds it
/// at logon (system and user variables from the registry, volatile variables, system PATH
/// followed by user PATH), read fresh at every start so PATH changes apply without a reboot.
/// Nothing is inherited from the host process.
/// </summary>
public static class SessionEnvironment
{
    /// <summary>
    /// Builds the environment from the registry for the user the host runs as
    /// (<c>CreateEnvironmentBlock</c> without inheritance). Names compare case-insensitively.
    /// </summary>
    /// <exception cref="Win32Exception">Windows could not build the environment.</exception>
    public static unsafe IReadOnlyDictionary<string, string> FromRegistry()
    {
        HANDLE token;
        if (!PInvoke.OpenProcessToken(PInvoke.GetCurrentProcess(),
                TOKEN_ACCESS_MASK.TOKEN_QUERY | TOKEN_ACCESS_MASK.TOKEN_DUPLICATE | TOKEN_ACCESS_MASK.TOKEN_IMPERSONATE,
                &token))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenProcessToken failed");
        try
        {
            void* block;
            if (!PInvoke.CreateEnvironmentBlock(&block, token, false))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateEnvironmentBlock failed");
            try
            {
                return Parse((char*)block);
            }
            finally
            {
                PInvoke.DestroyEnvironmentBlock(block);
            }
        }
        finally
        {
            PInvoke.CloseHandle(token);
        }
    }

    private static unsafe Dictionary<string, string> Parse(char* block)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var p = block; *p != '\0';)
        {
            var entry = new string(p);
            p += entry.Length + 1;
            // Skip the hidden "=C:=C:\…" per-drive entries; a name never starts with '='.
            var eq = entry.IndexOf('=', 1);
            if (entry.StartsWith('=') || eq < 0)
                continue;
            result[entry[..eq]] = entry[(eq + 1)..];
        }
        return result;
    }

    /// <summary>
    /// A Unicode environment block for <c>CreateProcess</c>: <c>name=value</c> entries sorted
    /// by name (ordinal, case-insensitive, as Windows expects), each NUL-terminated, plus a final NUL.
    /// </summary>
    /// <exception cref="ArgumentException">A name is empty or contains <c>=</c> or NUL, or a value contains NUL.</exception>
    public static string ToBlock(IReadOnlyDictionary<string, string> environment)
    {
        var sb = new StringBuilder();
        foreach (var (name, value) in environment.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (name.Length == 0 || name.Contains('=') || name.Contains('\0'))
                throw new ArgumentException($"invalid environment variable name: \"{name}\"", nameof(environment));
            if (value.Contains('\0'))
                throw new ArgumentException($"environment variable {name} contains NUL", nameof(environment));
            sb.Append(name).Append('=').Append(value).Append('\0');
        }
        if (sb.Length == 0)
            sb.Append('\0');
        return sb.Append('\0').ToString();
    }
}
