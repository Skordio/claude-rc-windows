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
