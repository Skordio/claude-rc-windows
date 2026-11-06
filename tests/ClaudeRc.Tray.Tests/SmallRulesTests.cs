using System.Diagnostics;
using System.ComponentModel;
using ClaudeRc.Core.Ipc;
using ClaudeRc.Tray.Hosting;
using ClaudeRc.Tray.Services;

namespace ClaudeRc.Tray.Tests;

public class TrayOptionsTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";

    [Fact]
    public void Default_names_match_the_hosts()
    {
        var o = TrayOptions.Parse([], Sid);
        Assert.Null(o.Instance);
        Assert.Equal("claude-rc-windows-" + Sid, o.PipeName);
        Assert.Equal(@"Local\claude-rc-windows-tray-" + Sid, o.MutexName);
        Assert.Equal(@"Local\claude-rc-windows-tray-show-" + Sid, o.ShowEventName);
        Assert.Equal(@"\claude-rc-windows\Host", o.TaskPath);
    }

    [Theory]
    [InlineData("--instance", "t1")]
    [InlineData("--instance=t1", null)]
    public void Instance_suffixes_every_name(string a, string? b)
    {
        var o = TrayOptions.Parse(b is null ? [a] : [a, b], Sid);
        Assert.Equal("t1", o.Instance);
        Assert.Equal("claude-rc-windows-" + Sid + "-t1", o.PipeName);
        Assert.Equal(@"Local\claude-rc-windows-tray-" + Sid + "-t1", o.MutexName);
        Assert.Equal(@"Local\claude-rc-windows-tray-show-" + Sid + "-t1", o.ShowEventName);
        // A test instance never runs the real task.
        Assert.Equal(@"\claude-rc-windows-t1\Host", o.TaskPath);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--instance")]
    [InlineData("--instance", "a b")]
    public void Bad_arguments_are_refused(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => TrayOptions.Parse(args, Sid));
    }
}

public class FolderPathsTests
{
    [Theory]
    [InlineData(@"\\server\share\app", true)]
    [InlineData(@"Z:\app", true)]
    [InlineData(@"C:\app", false)]
    [InlineData(@"D:\", false)]
    public void Network_paths_are_unc_or_network_drives(string path, bool network)
    {
        Assert.Equal(network, FolderPaths.IsNetworkPath(path, root => root.StartsWith("Z", StringComparison.OrdinalIgnoreCase) ? DriveType.Network : DriveType.Fixed));
    }

    [Fact]
    public void A_drive_that_cannot_be_read_is_not_called_network()
    {
        Assert.False(FolderPaths.IsNetworkPath(@"Q:\x", _ => throw new IOException("no drive")));
    }
}

public class ShellLauncherTests
{
    private readonly List<ProcessStartInfo> _started = [];

    [Fact]
    public void Explorer_gets_the_path_as_one_argument()
    {
        var shell = new ShellLauncher(psi => _started.Add(psi), _ => true);
        shell.OpenInExplorer(@"C:\a b\c");
        var psi = Assert.Single(_started);
        Assert.Equal("explorer.exe", psi.FileName);
        Assert.Equal([@"C:\a b\c"], psi.ArgumentList);
    }

    [Fact]
    public void Terminal_runs_wt_with_d()
    {
        var shell = new ShellLauncher(psi => _started.Add(psi), _ => true);
        shell.OpenInTerminal(@"C:\a b\c");
        var psi = Assert.Single(_started);
        Assert.Equal("wt.exe", psi.FileName);
        Assert.Equal(["-d", @"C:\a b\c"], psi.ArgumentList);
    }

    [Fact]
    public void Semicolons_are_escaped_for_wt()
    {
        // wt splits its command line into several commands at ';'.
        Assert.Equal(["-d", @"C:\x\;y"], ShellLauncher.WindowsTerminalArguments(@"C:\x;y"));
    }

    [Fact]
    public void Terminal_falls_back_to_explorer_when_wt_is_missing()
    {
        var shell = new ShellLauncher(psi =>
        {
            if (psi.FileName == "wt.exe")
                throw new Win32Exception(2, "The system cannot find the file specified.");
            _started.Add(psi);
        }, _ => true);
        shell.OpenInTerminal(@"C:\a");
        var psi = Assert.Single(_started);
        Assert.Equal("explorer.exe", psi.FileName);
    }

    [Fact]
    public void A_missing_folder_is_reported_instead_of_opening_something_else()
    {
        var shell = new ShellLauncher(psi => _started.Add(psi), _ => false);
        var e = Assert.Throws<DirectoryNotFoundException>(() => shell.OpenInExplorer(@"C:\gone"));
        Assert.Contains(@"C:\gone", e.Message, StringComparison.Ordinal);
        Assert.Empty(_started);
    }
}

public class StartHostResultTests
{
    [Fact]
    public void Missing_task_message_names_the_task_and_does_not_offer_a_child_start()
    {
        var r = StartHostResult.Missing(@"\claude-rc-windows\Host");
        Assert.False(r.Ok);
        Assert.Contains(@"\claude-rc-windows\Host", r.Error, StringComparison.Ordinal);
        Assert.Contains("does not exist", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_mismatch_text_is_the_core_one()
    {
        Assert.Equal("host and app versions differ: restart the host", IpcCodec.VersionMismatchMessage);
    }
}

public class TrayOptionsPartBTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string Local = @"C:\Users\me\AppData\Local";

    [Fact]
    public void The_viewer_data_folder_is_under_local_app_data_and_suffixed_by_the_instance()
    {
        Assert.Equal(Local + @"\claude-rc-windows\webview2", TrayOptions.Parse([], Sid, Local).WebViewDataDir);
        Assert.Equal(Local + @"\claude-rc-windows\webview2-t1", TrayOptions.Parse(["--instance", "t1"], Sid, Local).WebViewDataDir);
    }

    [Fact]
    public void The_viewer_data_folder_defaults_to_the_users_local_app_data()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "claude-rc-windows", "webview2-t1");
        Assert.Equal(expected, TrayOptions.Parse(["--instance", "t1"], Sid).WebViewDataDir);
    }

    [Fact]
    public void Toast_activation_and_unregistering_are_accepted()
    {
        var o = TrayOptions.Parse(["-ToastActivated"], Sid, Local);
        Assert.True(o.ToastActivated);
        Assert.False(o.UnregisterNotifications);

        var u = TrayOptions.Parse(["--unregister-notifications"], Sid, Local);
        Assert.True(u.UnregisterNotifications);
        Assert.False(u.ToastActivated);
        Assert.False(TrayOptions.Parse([], Sid, Local).ToastActivated);
    }
}

public class TrayOptionsStartModeTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string Local = @"C:\Users\me\AppData\Local";
    private const string Roaming = @"C:\Users\me\AppData\Roaming";
    private const string AppDir = @"C:\Users\me\AppData\Local\Programs\claude-rc-windows";

    private static TrayOptions Parse(params string[] args) => TrayOptions.Parse(args, Sid, Local, Roaming, AppDir);

    [Fact]
    public void The_host_is_next_to_the_app_and_gets_no_arguments_in_production()
    {
        var o = Parse();
        Assert.Equal(AppDir + @"\ClaudeRcHost.exe", o.HostExe);
        Assert.Empty(o.HostArguments);
        Assert.Null(o.SetStartMode);
        Assert.Null(o.ForUser);
        Assert.False(o.RestartHost);
    }

    [Fact]
    public void A_test_instance_host_gets_its_own_folders()
    {
        var o = Parse("--instance", "t1");
        Assert.Equal(
            ["--instance", "t1",
             "--data-dir", Roaming + @"\claude-rc-windows-t1",
             "--log-dir", Local + @"\claude-rc-windows-t1\logs",
             "--claude-json", Local + @"\claude-rc-windows-t1\claude.json"],
            o.HostArguments);
    }

    [Fact]
    public void Host_exe_and_host_arguments_can_be_given()
    {
        var o = Parse("--instance", "t1", "--host-exe", @"D:\build\ClaudeRcHost.exe", "--host-arg", "--instance", "--host-arg", "t1",
            "--host-arg", "--data-dir", "--host-arg", @"D:\tmp\a b");
        Assert.Equal(@"D:\build\ClaudeRcHost.exe", o.HostExe);
        Assert.Equal(["--instance", "t1", "--data-dir", @"D:\tmp\a b"], o.HostArguments);
    }

    [Theory]
    [InlineData("--host-exe", "ClaudeRcHost.exe")]
    [InlineData("--host-exe")]
    [InlineData("--host-arg")]
    [InlineData("--set-start-mode", "never")]
    [InlineData("--set-start-mode")]
    [InlineData("--for-user")]
    [InlineData("--for-user", " ")]
    public void Bad_start_mode_arguments_are_refused(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => Parse(args));
    }

    [Theory]
    [InlineData("logon", "Logon")]
    [InlineData("startup", "Startup")]
    public void The_elevated_helper_is_asked_for_a_mode(string arg, string mode)
    {
        var o = Parse("--set-start-mode", arg, "--for-user", @"PC\Jo Smith", "--restart-host");
        Assert.Equal(Enum.Parse<StartMode>(mode), o.SetStartMode);
        Assert.Equal(@"PC\Jo Smith", o.ForUser);
        Assert.True(o.RestartHost);
    }

    [Fact]
    public void A_mode_without_the_user_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Parse("--set-start-mode", "startup"));
    }

    [Fact]
    public void The_helper_arguments_give_the_helper_the_same_task_host_and_arguments()
    {
        var tray = Parse("--instance", "t1", "--host-exe", @"D:\b d\ClaudeRcHost.exe");

        var args = tray.HelperArguments(StartMode.Startup, @"PC\Jo Smith", restartHost: true);

        // An elevated helper may run as another (admin) account: its own AppData must not matter.
        var helper = TrayOptions.Parse(args, Sid, @"C:\Users\admin\AppData\Local", @"C:\Users\admin\AppData\Roaming", AppDir);
        Assert.Equal(StartMode.Startup, helper.SetStartMode);
        Assert.Equal(@"PC\Jo Smith", helper.ForUser);
        Assert.True(helper.RestartHost);
        Assert.Equal(tray.TaskPath, helper.TaskPath);
        Assert.Equal(tray.HostExe, helper.HostExe);
        Assert.Equal(tray.HostArguments, helper.HostArguments);
        Assert.False(TrayOptions.Parse(tray.HelperArguments(StartMode.Logon, @"PC\Jo", restartHost: false), Sid, Local, Roaming, AppDir).RestartHost);
    }

    [Fact]
    public void Production_helper_arguments_have_no_instance_and_no_host_arguments()
    {
        var args = Parse().HelperArguments(StartMode.Logon, @"PC\Jo", restartHost: false);
        Assert.Equal(["--set-start-mode", "logon", "--for-user", @"PC\Jo", "--host-exe", AppDir + @"\ClaudeRcHost.exe"], args);
    }
}
