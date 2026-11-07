using System.Xml.Linq;
using ClaudeRc.Tray.Hosting;

namespace ClaudeRc.Tray.Tests;

/// <summary>The scheduled task's XML (SPEC 9), built in memory: nothing is registered here.</summary>
public class HostTaskDefinitionTests
{
    private static readonly XNamespace Ns = HostTaskDefinition.Namespace;
    private const string Exe = @"C:\Users\Jo Smith\AppData\Local\Programs\claude-rc-windows\ClaudeRcHost.exe";
    private const string Account = @"PC\Jo Smith";

    private static HostTaskSpec Spec(StartMode mode, IReadOnlyList<string>? args = null, bool enabled = true) => new()
    {
        TaskPath = @"\claude-rc-windows\Host",
        Mode = mode,
        HostExe = Exe,
        HostArguments = args ?? [],
        Account = Account,
        Enabled = enabled,
    };

    private static XElement Root(HostTaskSpec spec)
    {
        var doc = XDocument.Parse(HostTaskDefinition.BuildXml(spec));
        Assert.Equal(Ns + "Task", doc.Root!.Name);
        Assert.Equal("1.2", (string?)doc.Root.Attribute("version"));
        return doc.Root;
    }

    private static string? Setting(XElement root, string name) => root.Element(Ns + "Settings")!.Element(Ns + name)?.Value;

    [Theory]
    [InlineData("Logon")]
    [InlineData("Startup")]
    public void Both_modes_have_the_settings_of_9_3(string modeName)
    {
        var mode = Enum.Parse<StartMode>(modeName);
        var root = Root(Spec(mode));
        Assert.Equal("true", Setting(root, "StartWhenAvailable"));
        Assert.Equal("false", Setting(root, "DisallowStartIfOnBatteries"));
        Assert.Equal("false", Setting(root, "StopIfGoingOnBatteries"));
        Assert.Equal("PT0S", Setting(root, "ExecutionTimeLimit"));
        Assert.Equal("IgnoreNew", Setting(root, "MultipleInstancesPolicy"));
        var restart = root.Element(Ns + "Settings")!.Element(Ns + "RestartOnFailure")!;
        Assert.Equal("PT1M", restart.Element(Ns + "Interval")!.Value);
        Assert.Equal("999", restart.Element(Ns + "Count")!.Value);
        // Start host runs the task on demand (9.4).
        Assert.Equal("true", Setting(root, "AllowStartOnDemand"));
        Assert.Equal("true", Setting(root, "Enabled"));
        // Normal priority: Task Scheduler's default (7) is below normal, and claude rc would inherit it.
        Assert.Equal("5", Setting(root, "Priority"));
        Assert.Equal("false", Setting(root, "RunOnlyIfIdle"));
        Assert.Equal("false", Setting(root, "RunOnlyIfNetworkAvailable"));
        Assert.Equal("false", root.Element(Ns + "Settings")!.Element(Ns + "IdleSettings")!.Element(Ns + "StopOnIdleEnd")!.Value);
    }

    [Fact]
    public void Logon_runs_at_this_users_logon_only_while_logged_on_without_admin()
    {
        var root = Root(Spec(StartMode.Logon));
        var trigger = Assert.Single(root.Element(Ns + "Triggers")!.Elements());
        Assert.Equal(Ns + "LogonTrigger", trigger.Name);
        Assert.Equal(Account, trigger.Element(Ns + "UserId")!.Value);
        Assert.Equal("true", trigger.Element(Ns + "Enabled")!.Value);

        var principal = Assert.Single(root.Element(Ns + "Principals")!.Elements());
        Assert.Equal(Account, principal.Element(Ns + "UserId")!.Value);
        Assert.Equal("InteractiveToken", principal.Element(Ns + "LogonType")!.Value);
        Assert.Equal("LeastPrivilege", principal.Element(Ns + "RunLevel")!.Value);
        Assert.Equal(TaskLogon.InteractiveToken, HostTaskDefinition.LogonFor(StartMode.Logon));
    }

    [Fact]
    public void Startup_runs_at_boot_whether_logged_on_or_not_with_the_stored_password()
    {
        var root = Root(Spec(StartMode.Startup));
        var trigger = Assert.Single(root.Element(Ns + "Triggers")!.Elements());
        Assert.Equal(Ns + "BootTrigger", trigger.Name);
        Assert.Equal("true", trigger.Element(Ns + "Enabled")!.Value);

        var principal = Assert.Single(root.Element(Ns + "Principals")!.Elements());
        Assert.Equal(Account, principal.Element(Ns + "UserId")!.Value);
        Assert.Equal("Password", principal.Element(Ns + "LogonType")!.Value);
        // Highest privileges are not needed by the host.
        Assert.Equal("LeastPrivilege", principal.Element(Ns + "RunLevel")!.Value);
        Assert.Equal(TaskLogon.Password, HostTaskDefinition.LogonFor(StartMode.Startup));
    }

    [Fact]
    public void The_action_runs_the_host_from_its_folder_with_no_arguments_in_production()
    {
        var root = Root(Spec(StartMode.Logon));
        var actions = root.Element(Ns + "Actions")!;
        var principal = root.Element(Ns + "Principals")!.Element(Ns + "Principal")!;
        Assert.Equal((string?)principal.Attribute("id"), (string?)actions.Attribute("Context"));
        var exec = Assert.Single(actions.Elements());
        Assert.Equal(Ns + "Exec", exec.Name);
        // Always quoted, so a path with spaces can never be read as C:\Users\Jo.exe.
        Assert.Equal("\"" + Exe + "\"", exec.Element(Ns + "Command")!.Value);
        Assert.Null(exec.Element(Ns + "Arguments"));
        Assert.Equal(Path.GetDirectoryName(Exe), exec.Element(Ns + "WorkingDirectory")!.Value);
    }

    [Fact]
    public void Host_arguments_are_quoted_like_an_argument_list()
    {
        var root = Root(Spec(StartMode.Logon, ["--instance", "t1", "--data-dir", @"C:\a b\data", "--log-dir", "x&y%"]));
        var exec = root.Element(Ns + "Actions")!.Element(Ns + "Exec")!;
        Assert.Equal("--instance t1 --data-dir \"C:\\a b\\data\" --log-dir x&y%", exec.Element(Ns + "Arguments")!.Value);
    }

    [Fact]
    public void A_disabled_definition_is_for_tests_only()
    {
        var root = Root(Spec(StartMode.Logon, enabled: false));
        Assert.Equal("false", Setting(root, "Enabled"));
    }

    [Theory]
    [InlineData("Logon")]
    [InlineData("Startup")]
    public void A_trigger_end_is_for_tests_only_and_written_before_enabled(string modeName)
    {
        var mode = Enum.Parse<StartMode>(modeName);
        // Production: the trigger never expires.
        var none = Assert.Single(Root(Spec(mode)).Element(Ns + "Triggers")!.Elements());
        Assert.Null(none.Element(Ns + "EndBoundary"));

        // A test task: its trigger expires, so a task left behind never starts at a later logon.
        var end = new DateTimeOffset(2026, 11, 6, 14, 30, 5, TimeSpan.FromHours(1));
        var trigger = Assert.Single(Root(Spec(mode) with { TriggerEndBoundary = end }).Element(Ns + "Triggers")!.Elements());
        Assert.Equal("2026-11-06T13:30:05Z", trigger.Element(Ns + "EndBoundary")!.Value);
        // Task Scheduler's schema order: StartBoundary, EndBoundary, Enabled.
        Assert.Equal(Ns + "Enabled", trigger.Element(Ns + "EndBoundary")!.ElementsAfterSelf().First().Name);
        Assert.Equal(mode, HostTaskDefinition.ReadMode(HostTaskDefinition.BuildXml(Spec(mode) with { TriggerEndBoundary = end })));
    }

    [Fact]
    public void The_switch_setup_passes_a_test_trigger_end_on()
    {
        var end = DateTimeOffset.UtcNow.AddMinutes(5);
        var setup = new StartModeSetup(@"\claude-rc-windows-t1\Host", Exe, [], Account) { TriggerEndBoundary = end };
        Assert.Equal(end, setup.Spec(StartMode.Logon).TriggerEndBoundary);
        Assert.Null(new StartModeSetup(@"\claude-rc-windows\Host", Exe, [], Account).Spec(StartMode.Logon).TriggerEndBoundary);
    }

    [Fact]
    public void Description_says_what_the_task_is()
    {
        var root = Root(Spec(StartMode.Logon));
        Assert.Contains("ClaudeRcHost.exe", root.Element(Ns + "RegistrationInfo")!.Element(Ns + "Description")!.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_relative_host_path_or_empty_account_is_refused()
    {
        Assert.Throws<ArgumentException>(() => HostTaskDefinition.BuildXml(Spec(StartMode.Logon) with { HostExe = "ClaudeRcHost.exe" }));
        Assert.Throws<ArgumentException>(() => HostTaskDefinition.BuildXml(Spec(StartMode.Logon) with { Account = " " }));
        Assert.Throws<ArgumentException>(() => HostTaskDefinition.BuildXml(Spec(StartMode.Unknown)));
    }

    [Theory]
    [InlineData("Logon")]
    [InlineData("Startup")]
    public void The_mode_reads_back_from_the_xml(string modeName)
    {
        var mode = Enum.Parse<StartMode>(modeName);
        Assert.Equal(mode, HostTaskDefinition.ReadMode(HostTaskDefinition.BuildXml(Spec(mode))));
    }

    [Fact]
    public void The_mode_reads_back_from_task_schedulers_own_xml()
    {
        // As Task Scheduler returns a registered logon task (defaults left out, the user as a SID).
        const string registered = """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Principals><Principal id="Author"><UserId>S-1-5-21-1-2-3-1000</UserId><LogonType>InteractiveToken</LogonType></Principal></Principals>
              <Triggers><LogonTrigger><UserId>PC\Jo</UserId></LogonTrigger></Triggers>
              <Actions Context="Author"><Exec><Command>"C:\x\ClaudeRcHost.exe"</Command></Exec></Actions>
            </Task>
            """;
        Assert.Equal(StartMode.Logon, HostTaskDefinition.ReadMode(registered));
    }

    [Theory]
    [InlineData("<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Triggers><TimeTrigger /></Triggers></Task>")]
    [InlineData("<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Triggers><LogonTrigger /><BootTrigger /></Triggers></Task>")]
    [InlineData("<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\" />")]
    [InlineData("not xml")]
    [InlineData("")]
    public void Anything_else_is_an_unknown_mode(string xml)
    {
        Assert.Equal(StartMode.Unknown, HostTaskDefinition.ReadMode(xml));
    }

    [Fact]
    public void The_host_reads_back_from_the_xml()
    {
        var xml = HostTaskDefinition.BuildXml(Spec(StartMode.Startup));
        Assert.Equal(Exe, HostTaskDefinition.ReadHostExe(xml));
        Assert.True(HostTaskDefinition.RunsHost(xml, Exe, []));
        // Paths are compared as Windows does: case is ignored.
        Assert.True(HostTaskDefinition.RunsHost(xml, Exe.ToUpperInvariant(), []));
        Assert.False(HostTaskDefinition.RunsHost(xml, @"D:\portable\ClaudeRcHost.exe", []));
        Assert.False(HostTaskDefinition.RunsHost(xml, Exe, ["--instance", "t1"]));
    }

    [Fact]
    public void A_hosts_arguments_must_match_too()
    {
        string[] args = ["--instance", "t1", "--data-dir", @"C:\a b"];
        var xml = HostTaskDefinition.BuildXml(Spec(StartMode.Logon, args));
        Assert.True(HostTaskDefinition.RunsHost(xml, Exe, args));
        Assert.False(HostTaskDefinition.RunsHost(xml, Exe, []));
        Assert.False(HostTaskDefinition.RunsHost(xml, Exe, ["--instance", "t2", "--data-dir", @"C:\a b"]));
    }

    [Theory]
    [InlineData("<Command>\"C:\\x\\ClaudeRcHost.exe\"</Command>", @"C:\x\ClaudeRcHost.exe")]
    [InlineData("<Command>C:\\x\\ClaudeRcHost.exe</Command>", @"C:\x\ClaudeRcHost.exe")]
    [InlineData("<Command>  \"C:\\x y\\ClaudeRcHost.exe\"  </Command><Arguments>--instance t</Arguments>", @"C:\x y\ClaudeRcHost.exe")]
    public void The_host_reads_back_from_task_schedulers_own_xml(string exec, string expected)
    {
        var xml = $"""
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><LogonTrigger /></Triggers>
              <Actions Context="Author"><Exec>{exec}</Exec></Actions>
            </Task>
            """;
        Assert.Equal(expected, HostTaskDefinition.ReadHostExe(xml));
    }

    [Theory]
    [InlineData("<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Actions><Exec><Command>a.exe</Command></Exec><Exec><Command>b.exe</Command></Exec></Actions></Task>")]
    [InlineData("<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Actions><Exec><Command>  </Command></Exec></Actions></Task>")]
    [InlineData("<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Actions><Exec><Command>ClaudeRcHost.exe</Command></Exec></Actions></Task>")]
    [InlineData("<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\" />")]
    [InlineData("not xml")]
    [InlineData("")]
    public void A_task_without_one_full_host_path_has_no_host(string xml)
    {
        Assert.Null(HostTaskDefinition.ReadHostExe(xml));
        Assert.False(HostTaskDefinition.RunsHost(xml, @"C:\x\ClaudeRcHost.exe", []));
    }

    [Fact]
    public void Result_codes_are_task_schedulers()
    {
        Assert.Equal(unchecked((int)0x8007052E), HostTaskDefinition.LogonFailure);
        Assert.Equal(0x41303, HostTaskDefinition.HasNotRun);
        Assert.Equal(0x41301, HostTaskDefinition.TaskIsRunning);
    }
}
