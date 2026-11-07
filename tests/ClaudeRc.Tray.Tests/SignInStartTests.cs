using ClaudeRc.Tray.Hosting;
using Microsoft.Win32;

namespace ClaudeRc.Tray.Tests;

/// <summary>"Start the app at sign-in" (SPEC 3.4): the Run value this copy writes, reads and removes.</summary>
public sealed class SignInStartTests
{
    private const string TrayExe = @"C:\Users\me\AppData\Local\Programs\claude-rc-windows\ClaudeRc.exe";
    private const string Ours = "\"" + TrayExe + "\" --background";
    private const string Portable = @"D:\tools\claude-rc-windows\ClaudeRc.exe";

    private readonly FakeStartupRegistry _registry = new();
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase) { TrayExe, Portable };

    private SignInStart Make(string? instance = null) => new(_registry, TrayExe, instance, _files.Contains);

    [Fact]
    public void The_value_is_named_after_the_app_and_its_instance()
    {
        Assert.Equal("claude-rc-windows", Make().ValueName);
        Assert.Equal("claude-rc-windows-t1", Make("t1").ValueName);
    }

    [Fact]
    public void The_command_starts_this_exe_in_the_background()
    {
        Assert.Equal(Ours, Make().Command);
        Assert.Equal(Ours + " --instance t1", Make("t1").Command);
    }

    [Fact]
    public void The_command_quotes_the_exe_even_without_spaces()
    {
        var s = new SignInStart(_registry, @"C:\app\ClaudeRc.exe", null, _ => true);
        Assert.Equal("\"C:\\app\\ClaudeRc.exe\" --background", s.Command);
    }

    [Fact]
    public void No_value_reads_as_off()
    {
        var state = Make().Read();
        Assert.False(state.Registered);
        Assert.False(state.IsOn);
        Assert.False(state.NeedsFix);
        Assert.Null(Make().Describe(state));
    }

    [Fact]
    public void Enable_writes_this_copys_command()
    {
        Make("t1").Enable();

        Assert.Equal(Ours + " --instance t1", _registry.Run["claude-rc-windows-t1"]);
        var state = Make("t1").Read();
        Assert.True(state.IsOn);
        Assert.True(state.IsExact);
        Assert.True(state.StartsThisExe);
        Assert.False(state.NeedsFix);
        Assert.Null(Make("t1").Describe(state));
    }

    [Fact]
    public void Another_instances_value_is_not_this_ones()
    {
        Make("t1").Enable();
        Assert.False(Make().Read().Registered);
        Assert.False(Make("t2").Read().Registered);
    }

    [Fact]
    public void Disable_deletes_the_value_and_a_startup_apps_choice()
    {
        Make().Enable();
        _registry.TurnedOff.Add("claude-rc-windows");

        Make().Disable();

        Assert.Empty(_registry.Run);
        Assert.Empty(_registry.TurnedOff);
        Make().Disable(); // nothing left: no error
    }

    [Fact]
    public void The_path_is_compared_case_insensitively()
    {
        _registry.Run["claude-rc-windows"] = "\"" + TrayExe.ToUpperInvariant() + "\" --background";
        var state = Make().Read();
        Assert.True(state.StartsThisExe);
        Assert.True(state.IsExact);
    }

    [Fact]
    public void Another_copy_is_shown_and_needs_a_fix()
    {
        _registry.Run["claude-rc-windows"] = "\"" + Portable + "\" --background";
        var s = Make();

        var state = s.Read();

        Assert.True(state.IsOn);
        Assert.False(state.StartsThisExe);
        Assert.True(state.NeedsFix);
        Assert.True(state.TargetExists);
        Assert.Equal(Portable, state.TargetExe);
        var note = s.Describe(state);
        Assert.Contains(Portable, note, StringComparison.Ordinal);
        Assert.Contains("another copy", note, StringComparison.Ordinal);
        Assert.Contains("Save", note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_exe_is_shown_and_needs_a_fix()
    {
        // A portable copy that was moved: the old path is gone.
        const string moved = @"D:\old place\ClaudeRc.exe";
        _registry.Run["claude-rc-windows"] = "\"" + moved + "\" --background";
        var s = Make();

        var state = s.Read();

        Assert.True(state.NeedsFix);
        Assert.False(state.TargetExists);
        Assert.Equal(moved, state.TargetExe);
        var note = s.Describe(state);
        Assert.Contains(moved, note, StringComparison.Ordinal);
        Assert.Contains("no longer exists", note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TrayExe + " --background")] // unquoted
    [InlineData("\"" + TrayExe + "\"")] // no --background: it would open the window at sign-in
    [InlineData("\"" + TrayExe + "\" --background --instance x")]
    public void This_exe_with_other_options_needs_a_fix(string command)
    {
        _registry.Run["claude-rc-windows"] = command;
        var state = Make().Read();
        Assert.True(state.StartsThisExe);
        Assert.False(state.IsExact);
        Assert.True(state.NeedsFix);
        Assert.NotNull(Make().Describe(state));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"")]
    [InlineData("\"C:\\bad|path\\x.exe\" --background")]
    public void A_value_that_is_not_a_command_line_is_another_program(string command)
    {
        _registry.Run["claude-rc-windows"] = command;
        var state = Make().Read();
        Assert.True(state.Registered);
        Assert.False(state.StartsThisExe);
        Assert.True(state.NeedsFix);
    }

    [Fact]
    public void Turned_off_in_startup_apps_reads_as_off_and_says_so()
    {
        Make().Enable();
        _registry.TurnedOff.Add("claude-rc-windows");

        var state = Make().Read();

        Assert.True(state.Registered);
        Assert.False(state.IsOn);
        Assert.Contains("Startup apps", Make().Describe(state), StringComparison.Ordinal);
    }

    [Fact]
    public void Enable_also_undoes_a_startup_apps_choice()
    {
        _registry.Run["claude-rc-windows"] = Ours;
        _registry.TurnedOff.Add("claude-rc-windows");

        Make().Enable();

        Assert.True(Make().Read().IsOn);
    }

    [Fact]
    public void A_fresh_install_turns_it_on()
    {
        Make().ApplyInstall(freshInstall: true);
        Assert.Equal(Ours, _registry.Run["claude-rc-windows"]);
    }

    [Fact]
    public void A_fresh_install_over_a_portable_copys_value_starts_the_installed_copy()
    {
        _registry.Run["claude-rc-windows"] = "\"" + Portable + "\" --background";
        Make().ApplyInstall(freshInstall: true);
        Assert.Equal(Ours, _registry.Run["claude-rc-windows"]);
    }

    [Fact]
    public void An_upgrade_keeps_it_off_when_the_user_turned_it_off()
    {
        Make().ApplyInstall(freshInstall: false);
        Assert.Empty(_registry.Run);
    }

    [Fact]
    public void An_upgrade_keeps_it_on_and_points_it_at_this_copy()
    {
        _registry.Run["claude-rc-windows"] = "\"" + Portable + "\" --background";
        Make().ApplyInstall(freshInstall: false);
        Assert.Equal(Ours, _registry.Run["claude-rc-windows"]);
    }

    [Fact]
    public void An_upgrade_keeps_a_startup_apps_choice()
    {
        _registry.Run["claude-rc-windows"] = "\"" + Portable + "\" --background";
        _registry.TurnedOff.Add("claude-rc-windows");

        Make().ApplyInstall(freshInstall: false);

        Assert.Equal(Ours, _registry.Run["claude-rc-windows"]);
        Assert.Contains("claude-rc-windows", _registry.TurnedOff);
    }

    [Fact]
    public void Uninstall_removes_only_a_value_that_starts_this_exe()
    {
        _registry.Run["claude-rc-windows"] = "\"" + Portable + "\" --background";
        Assert.False(Make().RemoveIfThisExe());
        Assert.True(_registry.Run.ContainsKey("claude-rc-windows"));

        _registry.Run["claude-rc-windows"] = TrayExe + " --background"; // this exe, other spelling
        _registry.TurnedOff.Add("claude-rc-windows");
        Assert.True(Make().RemoveIfThisExe());
        Assert.Empty(_registry.Run);
        Assert.Empty(_registry.TurnedOff);

        Assert.False(Make().RemoveIfThisExe());
    }

    [Fact]
    public void Registry_errors_are_recognised()
    {
        Assert.True(SignInStart.IsRegistryError(new UnauthorizedAccessException()));
        Assert.True(SignInStart.IsRegistryError(new System.Security.SecurityException()));
        Assert.True(SignInStart.IsRegistryError(new IOException()));
        Assert.False(SignInStart.IsRegistryError(new InvalidOperationException()));
    }

    [Fact]
    public void The_real_keys_are_the_users_run_and_startup_approved_keys()
    {
        // Only the names: no test reads or writes the real keys.
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", RegistryStartup.RunSubKey);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", RegistryStartup.ApprovedSubKey);
    }
}

/// <summary>
/// <see cref="RegistryStartup"/> against a throwaway key, <c>HKCU\Software\claude-rc-windows-test-&lt;guid&gt;</c>,
/// deleted afterwards. Never the real Run key.
/// </summary>
public sealed class RegistryStartupTests : IDisposable
{
    private readonly string _root = @"Software\claude-rc-windows-test-" + Guid.NewGuid().ToString("N");
    private readonly RegistryStartup _registry;

    public RegistryStartupTests()
    {
        _registry = new RegistryStartup(_root + @"\Run", _root + @"\StartupApproved\Run");
    }

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);

    [Fact]
    public void A_value_is_written_read_and_deleted()
    {
        try
        {
            Assert.Null(_registry.GetRunValue("claude-rc-windows-x"));

            _registry.SetRunValue("claude-rc-windows-x", "\"C:\\a b\\ClaudeRc.exe\" --background");

            Assert.Equal("\"C:\\a b\\ClaudeRc.exe\" --background", _registry.GetRunValue("claude-rc-windows-x"));
            using (var key = Registry.CurrentUser.OpenSubKey(_root + @"\Run"))
                Assert.Equal(RegistryValueKind.String, key!.GetValueKind("claude-rc-windows-x"));

            _registry.DeleteRunValue("claude-rc-windows-x");
            Assert.Null(_registry.GetRunValue("claude-rc-windows-x"));
            _registry.DeleteRunValue("claude-rc-windows-x");
        }
        finally
        {
            Dispose();
        }
    }

    [Fact]
    public void Missing_keys_read_as_nothing_and_deleting_needs_none()
    {
        try
        {
            Assert.Null(_registry.GetRunValue("claude-rc-windows-x"));
            Assert.False(_registry.IsTurnedOffInStartupApps("claude-rc-windows-x"));
            _registry.DeleteRunValue("claude-rc-windows-x");
            _registry.ClearStartupAppsChoice("claude-rc-windows-x");
            using var key = Registry.CurrentUser.OpenSubKey(_root);
            Assert.Null(key); // reading and deleting create nothing
        }
        finally
        {
            Dispose();
        }
    }

    [Fact]
    public void A_value_of_another_kind_reads_as_none()
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(_root + @"\Run"))
                key.SetValue("claude-rc-windows-x", 1, RegistryValueKind.DWord);
            Assert.Null(_registry.GetRunValue("claude-rc-windows-x"));
        }
        finally
        {
            Dispose();
        }
    }

    [Theory]
    [InlineData(new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[0], false)]
    public void The_startup_apps_choice_is_read_from_its_first_byte(byte[] data, bool off)
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(_root + @"\StartupApproved\Run"))
                key.SetValue("claude-rc-windows-x", data, RegistryValueKind.Binary);

            Assert.Equal(off, _registry.IsTurnedOffInStartupApps("claude-rc-windows-x"));

            _registry.ClearStartupAppsChoice("claude-rc-windows-x");
            Assert.False(_registry.IsTurnedOffInStartupApps("claude-rc-windows-x"));
            using var after = Registry.CurrentUser.OpenSubKey(_root + @"\StartupApproved\Run");
            Assert.Null(after!.GetValue("claude-rc-windows-x"));
        }
        finally
        {
            Dispose();
        }
    }
}
