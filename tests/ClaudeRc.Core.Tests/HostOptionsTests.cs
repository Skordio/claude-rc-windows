using ClaudeRc.Core.Hosting;

namespace ClaudeRc.Core.Tests;

public sealed class HostOptionsTests
{
    private static readonly HostDefaults Defaults = new(
        AppData: @"C:\Users\me\AppData\Roaming",
        LocalAppData: @"C:\Users\me\AppData\Local",
        UserProfile: @"C:\Users\me",
        UserSid: "S-1-5-21-1-2-3-1001");

    [Fact]
    public void Defaults_follow_the_spec()
    {
        var o = HostOptions.Parse([], Defaults);
        Assert.Equal(@"C:\Users\me\AppData\Roaming\claude-rc-windows", o.DataDir);
        Assert.Equal(@"C:\Users\me\AppData\Local\claude-rc-windows\logs", o.LogDir);
        Assert.Equal(@"C:\Users\me\.claude.json", o.ClaudeJsonPath);
        Assert.Null(o.Instance);
        Assert.Equal("claude-rc-windows-S-1-5-21-1-2-3-1001", o.PipeName);
        Assert.Equal(@"Global\claude-rc-windows-host-S-1-5-21-1-2-3-1001", o.MutexName);
        Assert.Equal(@"C:\Users\me\AppData\Local\claude-rc-windows\logs\host.log", o.HostLogPath);
        Assert.Equal(@"C:\Users\me\AppData\Local\claude-rc-windows\logs\folders", o.FolderLogDir);
        Assert.Equal(@"C:\Users\me\AppData\Roaming\claude-rc-windows\folders.json", Path.Combine(o.DataDir, "folders.json"));
    }

    [Fact]
    public void Every_path_and_name_can_be_overridden()
    {
        var o = HostOptions.Parse(
            ["--data-dir", @"D:\t\data", "--log-dir", @"D:\t\logs", "--claude-json", @"D:\t\c.json", "--instance", "test-1a2b"],
            Defaults);
        Assert.Equal(@"D:\t\data", o.DataDir);
        Assert.Equal(@"D:\t\logs", o.LogDir);
        Assert.Equal(@"D:\t\c.json", o.ClaudeJsonPath);
        Assert.Equal("test-1a2b", o.Instance);
        Assert.Equal("claude-rc-windows-S-1-5-21-1-2-3-1001-test-1a2b", o.PipeName);
        Assert.Equal(@"Global\claude-rc-windows-host-S-1-5-21-1-2-3-1001-test-1a2b", o.MutexName);
    }

    [Fact]
    public void Equals_form_is_accepted()
    {
        var o = HostOptions.Parse(["--instance=abc", @"--data-dir=D:\x"], Defaults);
        Assert.Equal("abc", o.Instance);
        Assert.Equal(@"D:\x", o.DataDir);
    }

    [Fact]
    public void Relative_override_paths_become_absolute()
    {
        var o = HostOptions.Parse(["--data-dir", "rel"], Defaults);
        Assert.True(Path.IsPathFullyQualified(o.DataDir));
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--data-dir")]
    [InlineData("--instance", "has space")]
    [InlineData("--instance", "a\\b")]
    [InlineData("--instance", "")]
    [InlineData("stray")]
    public void Bad_arguments_are_rejected(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(args, Defaults));
    }

    [Fact]
    public void Real_defaults_come_from_the_environment()
    {
        var d = HostDefaults.FromEnvironment();
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), d.AppData);
        Assert.StartsWith("S-1-", d.UserSid);
    }
}
