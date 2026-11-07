using System.Security;
using ClaudeRc.Core.Processes;
using Microsoft.Win32;

namespace ClaudeRc.Tray.Hosting;

/// <summary>
/// Where Windows keeps what starts at sign-in for this user: the values of
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>, and the choice made in Windows' own
/// Startup apps list (Settings, Task Manager) under <c>…\Explorer\StartupApproved\Run</c>.
/// </summary>
internal interface IStartupRegistry
{
    /// <summary>The command of a Run value, or null when there is none (or it is not a string).</summary>
    string? GetRunValue(string name);

    /// <summary>Writes a Run value.</summary>
    void SetRunValue(string name, string command);

    /// <summary>Deletes a Run value; nothing happens when there is none.</summary>
    void DeleteRunValue(string name);

    /// <summary>The user turned this Run value off in Windows' Startup apps list.</summary>
    bool IsTurnedOffInStartupApps(string name);

    /// <summary>Forgets the Startup apps choice for this value (Windows then runs it again).</summary>
    void ClearStartupAppsChoice(string name);
}

/// <summary><see cref="IStartupRegistry"/> on two keys under <c>HKEY_CURRENT_USER</c>.</summary>
/// <param name="runSubKey">The Run key (tests pass a throwaway key).</param>
/// <param name="approvedSubKey">The StartupApproved\Run key.</param>
internal sealed class RegistryStartup(string runSubKey, string approvedSubKey) : IStartupRegistry
{
    /// <summary>The user's Run key.</summary>
    public const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Where Windows' Startup apps list keeps the user's on/off choice for each Run value.</summary>
    public const string ApprovedSubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>The current user's real keys.</summary>
    public static RegistryStartup ForCurrentUser() => new(RunSubKey, ApprovedSubKey);

    /// <inheritdoc/>
    public string? GetRunValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(runSubKey);
        // A REG_EXPAND_SZ value comes back expanded, which is what Windows runs.
        return key?.GetValue(name) as string;
    }

    /// <inheritdoc/>
    public void SetRunValue(string name, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(runSubKey, writable: true);
        key.SetValue(name, command, RegistryValueKind.String);
    }

    /// <inheritdoc/>
    public void DeleteRunValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(runSubKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Windows writes 12 bytes: a flag, then the time of the change. The flag is even (2, 6) for on
    /// and odd (3, 7) for off; no entry means on.
    /// </remarks>
    public bool IsTurnedOffInStartupApps(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(approvedSubKey);
        return key?.GetValue(name) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
    }

    /// <inheritdoc/>
    public void ClearStartupAppsChoice(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(approvedSubKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>What the Run value of this app's name holds now.</summary>
/// <param name="Command">The value's command line; null when there is no value.</param>
/// <param name="TargetExe">The program the command starts (its first, possibly quoted, token).</param>
/// <param name="StartsThisExe">The program is this ClaudeRc.exe.</param>
/// <param name="IsExact">The command is exactly the one this copy writes.</param>
/// <param name="TargetExists">The program exists.</param>
/// <param name="TurnedOffInStartupApps">The user turned it off in Windows' Startup apps list.</param>
internal sealed record SignInStartState(string? Command, string? TargetExe, bool StartsThisExe, bool IsExact, bool TargetExists,
    bool TurnedOffInStartupApps)
{
    /// <summary>No value.</summary>
    public static SignInStartState None { get; } = new(null, null, false, false, false, false);

    /// <summary>A Run value of this app's name exists.</summary>
    public bool Registered => Command is not null;

    /// <summary>Windows starts something at sign-in under this name (the Settings checkbox).</summary>
    public bool IsOn => Registered && !TurnedOffInStartupApps;

    /// <summary>The value exists but is not this copy's command (another copy, a moved or missing exe): saving rewrites it.</summary>
    public bool NeedsFix => Registered && !IsExact;
}

/// <summary>
/// "Start the app at sign-in" (SPEC 3.4): a value in the user's Run key that starts this
/// ClaudeRc.exe with <c>--background</c> (and its <c>--instance</c>). The value is the only record
/// of the choice (the tray has no settings file); it always names this exe, never a path from
/// elsewhere.
/// </summary>
/// <param name="registry">The Run key.</param>
/// <param name="trayExe">This ClaudeRc.exe.</param>
/// <param name="instance">The <c>--instance</c> suffix, if any.</param>
/// <param name="fileExists">Whether a file exists (tests).</param>
internal sealed class SignInStart(IStartupRegistry registry, string trayExe, string? instance, Func<string, bool> fileExists)
{
    /// <summary>Starts the tray in the notification area without opening its window.</summary>
    public const string BackgroundArgument = "--background";

    private const string Product = "claude-rc-windows";

    /// <summary>The Run value's name: <c>claude-rc-windows[-instance]</c>.</summary>
    public string ValueName { get; } = instance is null ? Product : $"{Product}-{instance}";

    /// <summary>The arguments this copy writes after the exe.</summary>
    private string Arguments { get; } =
        CommandLine.JoinArguments(instance is null ? [BackgroundArgument] : [BackgroundArgument, "--instance", instance]);

    /// <summary>What this copy writes: <c>"&lt;this exe&gt;" --background [--instance &lt;suffix&gt;]</c>.</summary>
    public string Command => $"\"{trayExe}\" {Arguments}";

    /// <summary>Reads the value.</summary>
    public SignInStartState Read()
    {
        var command = registry.GetRunValue(ValueName);
        if (command is null)
            return SignInStartState.None;
        var (exe, arguments) = Split(command);
        var here = SamePath(exe, trayExe);
        var exists = exe.Length > 0 && fileExists(exe);
        // Unquoted, the exe would be misread if its path has a space; this copy always quotes it.
        var exact = here && command.TrimStart().StartsWith('"') && string.Equals(arguments, Arguments, StringComparison.Ordinal);
        return new SignInStartState(command, exe, here, exact, exists, registry.IsTurnedOffInStartupApps(ValueName));
    }

    /// <summary>Starts this copy at sign-in, also when the user had turned it off in Windows' Startup apps list.</summary>
    public void Enable()
    {
        registry.SetRunValue(ValueName, Command);
        registry.ClearStartupAppsChoice(ValueName);
    }

    /// <summary>Nothing of this name starts at sign-in any more.</summary>
    public void Disable()
    {
        registry.DeleteRunValue(ValueName);
        registry.ClearStartupAppsChoice(ValueName);
    }

    /// <summary>
    /// The installer's <c>--install</c>: a fresh install turns it on; an upgrade keeps the user's
    /// choice (no value stays off, a Startup apps choice stays), only pointing a value at this copy.
    /// </summary>
    public void ApplyInstall(bool freshInstall)
    {
        if (freshInstall)
        {
            Enable();
            return;
        }
        if (Read().NeedsFix)
            registry.SetRunValue(ValueName, Command);
    }

    /// <summary>The uninstall: removes the value only when it starts this exe. True when it removed one.</summary>
    public bool RemoveIfThisExe()
    {
        if (!Read().StartsThisExe)
            return false;
        Disable();
        return true;
    }

    /// <summary>What Settings says about the value, or null when there is nothing to say.</summary>
    public string? Describe(SignInStartState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.Registered)
            return null;
        if (state.TurnedOffInStartupApps)
            return "Turned off in Windows' Startup apps (Settings > Apps > Startup, or Task Manager). Tick it and Save to turn it on again.";
        if (!state.NeedsFix)
            return null;
        if (state.StartsThisExe)
            return $"At sign-in Windows runs this app with other options: {state.Command}. Save to set it right.";
        if (state.TargetExe is not { Length: > 0 } exe)
            return $"At sign-in Windows runs \"{state.Command}\" under this app's name. Save to start this copy ({trayExe}) instead.";
        return state.TargetExists
            ? $"At sign-in Windows starts another copy of the app: {exe}. Save to start this copy ({trayExe}) instead."
            : $"At sign-in Windows would start {exe}, which no longer exists. Save to start this copy ({trayExe}) instead.";
    }

    /// <summary>A command line's program (its first token, quoted or not) and the rest, trimmed.</summary>
    private static (string Exe, string Arguments) Split(string command)
    {
        var t = command.Trim();
        if (t.StartsWith('"'))
        {
            var end = t.IndexOf('"', 1);
            return end < 0 ? (t[1..], "") : (t[1..end], t[(end + 1)..].Trim());
        }
        var space = t.AsSpan().IndexOfAny(' ', '\t');
        return space < 0 ? (t, "") : (t[..space], t[(space + 1)..].Trim());
    }

    private static bool SamePath(string a, string b)
    {
        if (a.Length == 0 || !Path.IsPathFullyQualified(a))
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>An error the registry calls throw (access denied, a key removed meanwhile).</summary>
    public static bool IsRegistryError(Exception e) => e is UnauthorizedAccessException or SecurityException or IOException;
}
