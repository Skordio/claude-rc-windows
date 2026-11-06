using System.Runtime.InteropServices;
using ClaudeRc.Tray.Hosting;

namespace ClaudeRc.Tray.Tests;

/// <summary>
/// Switching the start mode (SPEC 3.4, 9.1, 9.2) from the tray, unelevated: only the
/// orchestration, over fakes. Nothing here elevates, prompts or touches Task Scheduler.
/// </summary>
public class StartModeSwitcherTests
{
    private const string TaskPath = @"\claude-rc-windows-t1\Host";
    private const string Exe = @"C:\app\ClaudeRcHost.exe";
    private const string Account = @"PC\Jo";
    private readonly FakeTaskService _tasks = new();
    private readonly FakeElevation _elevation = new();
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase) { Exe };
    private readonly StartModeSwitcher _switcher;

    private static readonly StartModeSetup Setup = new(TaskPath, Exe, ["--instance", "t1"], Account);

    public StartModeSwitcherTests()
    {
        _switcher = new StartModeSwitcher(_tasks, _elevation, Setup,
            (mode, restart) => ["helper", mode.ToString(), restart ? "restart" : "no-restart"],
            _files.Contains, (_, _) => Task.CompletedTask);
    }

    [Fact]
    public async Task Logon_is_registered_directly_without_admin_or_password()
    {
        var result = await _switcher.SwitchAsync(StartMode.Logon, restartHost: false);

        Assert.Equal(SwitchOutcome.Done, result.Outcome);
        Assert.Empty(_elevation.Runs);
        var task = _tasks.Tasks[TaskPath];
        Assert.Equal(TaskLogon.InteractiveToken, task.Logon);
        Assert.Equal(Account, task.Account);
        Assert.Equal(StartMode.Logon, HostTaskDefinition.ReadMode(task.Xml));
        Assert.Contains("--instance t1", task.Xml, StringComparison.Ordinal);
        Assert.Equal([null], _tasks.Passwords);
        Assert.DoesNotContain(_tasks.Calls, c => c.StartsWith("run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Logon_with_restart_stops_the_running_host_and_runs_the_task()
    {
        _tasks.Add(TaskPath, StartMode.Logon, TaskRunState.Running);

        var result = await _switcher.SwitchAsync(StartMode.Logon, restartHost: true);

        Assert.Equal(SwitchOutcome.Done, result.Outcome);
        Assert.Equal([$"register {TaskPath}", $"stop {TaskPath}", $"find {TaskPath}", $"run {TaskPath}"], _tasks.Calls);
    }

    [Fact]
    public async Task A_task_an_administrator_made_is_replaced_by_the_elevated_helper()
    {
        // startup -> logon: the password task may only be changed with admin rights.
        _tasks.Add(TaskPath, StartMode.Startup);
        _tasks.FailNext("register", new UnauthorizedAccessException("Access is denied."));

        var result = await _switcher.SwitchAsync(StartMode.Logon, restartHost: true);

        Assert.Equal(SwitchOutcome.Done, result.Outcome);
        var run = Assert.Single(_elevation.Runs);
        Assert.Equal(["helper", "Logon", "restart"], run);
    }

    [Fact]
    public async Task A_restart_that_is_denied_is_done_by_the_elevated_helper()
    {
        _tasks.Add(TaskPath, StartMode.Startup, TaskRunState.Running);
        _tasks.FailNext("stop", new UnauthorizedAccessException("Access is denied."));

        var result = await _switcher.SwitchAsync(StartMode.Logon, restartHost: true);

        Assert.Equal(SwitchOutcome.Done, result.Outcome);
        Assert.Equal(["helper", "Logon", "restart"], Assert.Single(_elevation.Runs));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Startup_always_goes_through_the_elevated_helper(bool restart)
    {
        var result = await _switcher.SwitchAsync(StartMode.Startup, restart);

        Assert.Equal(SwitchOutcome.Done, result.Outcome);
        Assert.Equal(["helper", "Startup", restart ? "restart" : "no-restart"], Assert.Single(_elevation.Runs));
        // The tray itself never registers a startup task (it has neither admin rights nor the password).
        Assert.DoesNotContain(_tasks.Calls, c => c.StartsWith("register", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_at_the_UAC_prompt_is_a_cancel()
    {
        _elevation.ExitCode = null;
        var result = await _switcher.SwitchAsync(StartMode.Startup, restartHost: false);
        Assert.Equal(SwitchOutcome.Cancelled, result.Outcome);
        Assert.Contains("not changed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_in_the_password_prompt_is_a_cancel()
    {
        _elevation.ExitCode = HelperExitCodes.Cancelled;
        var result = await _switcher.SwitchAsync(StartMode.Startup, restartHost: false);
        Assert.Equal(SwitchOutcome.Cancelled, result.Outcome);
    }

    [Fact]
    public async Task A_failed_helper_is_a_failure()
    {
        _elevation.ExitCode = HelperExitCodes.Failed;
        var result = await _switcher.SwitchAsync(StartMode.Startup, restartHost: false);
        Assert.Equal(SwitchOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task A_helper_that_set_the_mode_but_could_not_restart_says_so()
    {
        _elevation.ExitCode = HelperExitCodes.NotRestarted;
        var result = await _switcher.SwitchAsync(StartMode.Startup, restartHost: true);
        Assert.Equal(SwitchOutcome.Done, result.Outcome);
        Assert.Contains("restart", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Other_task_scheduler_errors_are_failures_with_the_reason()
    {
        _tasks.FailNext("register", new COMException("The task XML is malformed.", unchecked((int)0x80041318)));
        var result = await _switcher.SwitchAsync(StartMode.Logon, restartHost: false);
        Assert.Equal(SwitchOutcome.Failed, result.Outcome);
        Assert.Contains("The task XML is malformed.", result.Message, StringComparison.Ordinal);
        Assert.Empty(_elevation.Runs);
    }

    [Fact]
    public async Task A_missing_host_exe_is_refused_before_anything_is_changed()
    {
        _files.Clear();
        var result = await _switcher.SwitchAsync(StartMode.Startup, restartHost: false);
        Assert.Equal(SwitchOutcome.Failed, result.Outcome);
        Assert.Contains(Exe, result.Message, StringComparison.Ordinal);
        Assert.Empty(_elevation.Runs);
        Assert.Empty(_tasks.Calls);
    }

    [Fact]
    public async Task Unknown_is_not_a_mode()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _switcher.SwitchAsync(StartMode.Unknown, restartHost: false));
    }

    [Fact]
    public async Task A_task_the_helper_would_refuse_is_not_handed_to_it()
    {
        // A host that is not next to ClaudeRc.exe (a debug build): no helper arguments.
        var switcher = new StartModeSwitcher(_tasks, _elevation, Setup, (_, _) => null, _files.Contains, (_, _) => Task.CompletedTask);

        var result = await switcher.SwitchAsync(StartMode.Startup, restartHost: false);

        Assert.Equal(SwitchOutcome.Failed, result.Outcome);
        Assert.Contains("next to ClaudeRc.exe", result.Message, StringComparison.Ordinal);
        Assert.Empty(_elevation.Runs);
        Assert.Empty(_tasks.Calls);
    }
}

/// <summary>
/// The elevated helper (<c>ClaudeRc.exe --set-start-mode …</c>, SPEC 9.2): the password goes from
/// the credential prompt straight to Task Scheduler. Over fakes only: no UAC, no prompt here.
/// </summary>
public class StartModeHelperTests
{
    private const string TaskPath = @"\claude-rc-windows-t1\Host";
    private const string Exe = @"C:\app\ClaudeRcHost.exe";
    private const string Account = @"PC\Jo";
    private readonly FakeTaskService _tasks = new();
    private readonly FakeCredentialPrompt _prompt = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase) { Exe };
    private readonly HashSet<string> _users = new(StringComparer.OrdinalIgnoreCase) { Account };
    private readonly StartModeHelper _helper;

    private static readonly StartModeSetup Setup = new(TaskPath, Exe, [], Account);

    public StartModeHelperTests()
    {
        _helper = new StartModeHelper(_tasks, _prompt, _dialogs, _files.Contains, _users.Contains, (_, _) => Task.CompletedTask);
    }

    [Fact]
    public async Task Startup_registers_with_the_password_from_the_prompt()
    {
        _prompt.Answers.Enqueue("s3cret pässword");

        var code = await _helper.RunAsync(Setup, StartMode.Startup, restartHost: false);

        Assert.Equal(HelperExitCodes.Ok, code);
        var (account, error) = Assert.Single(_prompt.Prompts);
        Assert.Equal(Account, account);
        Assert.Null(error);
        var task = _tasks.Tasks[TaskPath];
        Assert.Equal(TaskLogon.Password, task.Logon);
        Assert.Equal(Account, task.Account);
        Assert.Equal(StartMode.Startup, HostTaskDefinition.ReadMode(task.Xml));
        Assert.Equal(["s3cret pässword"], _tasks.Passwords);
        // The password is only passed through: never in the task's XML.
        Assert.DoesNotContain("s3cret", task.Xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_password_is_wiped_once_used()
    {
        _prompt.Answers.Enqueue("s3cret");
        await _helper.RunAsync(Setup, StartMode.Startup, restartHost: false);
        var handed = Assert.Single(_prompt.Handed);
        Assert.True(handed.IsDisposed);
        Assert.True(handed.Chars.ToArray().All(c => c == '\0'));
    }

    [Fact]
    public async Task A_wrong_password_asks_again_with_the_reason()
    {
        _prompt.Answers.Enqueue("wrong");
        _prompt.Answers.Enqueue("right");
        _tasks.FailNext("register", new COMException("The user name or password is incorrect.", HostTaskDefinition.LogonFailure));

        var code = await _helper.RunAsync(Setup, StartMode.Startup, restartHost: false);

        Assert.Equal(HelperExitCodes.Ok, code);
        Assert.Equal(2, _prompt.Prompts.Count);
        Assert.Null(_prompt.Prompts[0].Error);
        Assert.Contains("not accepted", _prompt.Prompts[1].Error, StringComparison.Ordinal);
        Assert.Equal(["wrong", "right"], _tasks.Passwords);
        Assert.All(_prompt.Handed, p => Assert.True(p.IsDisposed));
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public async Task Cancel_in_the_prompt_changes_nothing()
    {
        _tasks.Add(TaskPath, StartMode.Logon);
        var before = _tasks.Tasks[TaskPath].Xml;

        var code = await _helper.RunAsync(Setup, StartMode.Startup, restartHost: true);

        Assert.Equal(HelperExitCodes.Cancelled, code);
        Assert.Equal(before, _tasks.Tasks[TaskPath].Xml);
        Assert.DoesNotContain(_tasks.Calls, c => !c.StartsWith("find", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Other_errors_are_shown_without_the_password_and_fail()
    {
        _prompt.Answers.Enqueue("s3cret");
        _tasks.FailNext("register", new COMException("The task XML is malformed.", unchecked((int)0x80041318)));

        var code = await _helper.RunAsync(Setup, StartMode.Startup, restartHost: false);

        Assert.Equal(HelperExitCodes.Failed, code);
        var error = Assert.Single(_dialogs.Errors);
        Assert.Contains("The task XML is malformed.", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", error.Message, StringComparison.Ordinal);
        Assert.True(Assert.Single(_prompt.Handed).IsDisposed);
    }

    [Fact]
    public async Task Logon_needs_no_password()
    {
        var code = await _helper.RunAsync(Setup, StartMode.Logon, restartHost: false);

        Assert.Equal(HelperExitCodes.Ok, code);
        Assert.Empty(_prompt.Prompts);
        Assert.Equal(TaskLogon.InteractiveToken, _tasks.Tasks[TaskPath].Logon);
        Assert.Equal([null], _tasks.Passwords);
    }

    [Fact]
    public async Task Restart_stops_the_host_waits_for_it_and_runs_the_task()
    {
        _tasks.Add(TaskPath, StartMode.Logon, TaskRunState.Running);
        _prompt.Answers.Enqueue("pw");

        var code = await _helper.RunAsync(Setup, StartMode.Startup, restartHost: true);

        Assert.Equal(HelperExitCodes.Ok, code);
        Assert.Equal([$"register {TaskPath}", $"stop {TaskPath}", $"find {TaskPath}", $"run {TaskPath}"], _tasks.Calls);
    }

    [Fact]
    public async Task A_failed_restart_keeps_the_new_mode_and_says_so()
    {
        _prompt.Answers.Enqueue("pw");
        _tasks.AlwaysFail["run"] = new COMException("The service is busy.", unchecked((int)0x80070420));

        var code = await _helper.RunAsync(Setup, StartMode.Startup, restartHost: true);

        Assert.Equal(HelperExitCodes.NotRestarted, code);
        Assert.Equal(StartMode.Startup, HostTaskDefinition.ReadMode(_tasks.Tasks[TaskPath].Xml));
        Assert.Contains("The service is busy.", Assert.Single(_dialogs.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_that_does_not_stop_is_not_restarted()
    {
        _tasks.Add(TaskPath, StartMode.Logon, TaskRunState.Running);
        _tasks.AlwaysFail["stop"] = new COMException("cannot stop", unchecked((int)0x80004005));

        var code = await _helper.RunAsync(Setup, StartMode.Logon, restartHost: true);

        Assert.Equal(HelperExitCodes.NotRestarted, code);
        Assert.DoesNotContain(_tasks.Calls, c => c.StartsWith("run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_host_still_running_after_the_stop_timeout_is_not_run_again()
    {
        _tasks.Add(TaskPath, StartMode.Logon, TaskRunState.Running);
        _tasks.StopLeavesRunning = true;

        var code = await _helper.RunAsync(Setup, StartMode.Logon, restartHost: true);

        Assert.Equal(HelperExitCodes.NotRestarted, code);
        Assert.DoesNotContain(_tasks.Calls, c => c.StartsWith("run", StringComparison.Ordinal));
        Assert.Contains("did not stop", Assert.Single(_dialogs.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stop_that_times_out_is_not_followed_by_a_run()
    {
        _tasks.Add(TaskPath, StartMode.Logon, TaskRunState.Running);
        _tasks.FailNext("stop", new TimeoutException("the host (process 42) did not end after the task was stopped"));

        var code = await _helper.RunAsync(Setup, StartMode.Logon, restartHost: true);

        Assert.Equal(HelperExitCodes.NotRestarted, code);
        Assert.DoesNotContain(_tasks.Calls, c => c.StartsWith("run", StringComparison.Ordinal));
        Assert.Contains("did not end", Assert.Single(_dialogs.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_host_exe_fails_before_the_prompt()
    {
        _files.Clear();
        var code = await _helper.RunAsync(Setup, StartMode.Startup, restartHost: false);
        Assert.Equal(HelperExitCodes.Failed, code);
        Assert.Empty(_prompt.Prompts);
        Assert.Contains(Exe, Assert.Single(_dialogs.Errors).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Logon")]
    [InlineData("Startup")]
    public async Task An_account_that_is_not_a_user_is_refused_before_anything_changes(string mode)
    {
        // --for-user OTHER\nobody, a group or a service account: the helper sets up nothing.
        _users.Clear();
        _prompt.Answers.Enqueue("s3cret");

        var code = await _helper.RunAsync(Setup, Enum.Parse<StartMode>(mode), restartHost: true);

        Assert.Equal(HelperExitCodes.InvalidArguments, code);
        Assert.Empty(_prompt.Prompts);
        Assert.Empty(_tasks.Calls);
        Assert.Contains(Account, Assert.Single(_dialogs.Errors).Message, StringComparison.Ordinal);
    }

    // ---- --run-host: run the task an administrator made, no password (SPEC 9.4) ----

    [Fact]
    public void Run_host_runs_the_task_of_this_host_and_changes_nothing_else()
    {
        _tasks.Add(TaskPath, StartMode.Startup); // runs C:\app\ClaudeRcHost.exe

        Assert.Equal(HelperExitCodes.Ok, _helper.RunHost(Setup));

        Assert.Contains($"run {TaskPath}", _tasks.Calls);
        Assert.DoesNotContain(_tasks.Calls, c => c.StartsWith("register", StringComparison.Ordinal)
            || c.StartsWith("stop", StringComparison.Ordinal) || c.StartsWith("delete", StringComparison.Ordinal));
        Assert.Empty(_prompt.Prompts);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public void Run_host_refuses_a_task_that_runs_another_program()
    {
        _tasks.Tasks[TaskPath] = new FakeTaskService.Entry
        {
            Xml = HostTaskDefinition.BuildXml(new HostTaskSpec
            {
                TaskPath = TaskPath, Mode = StartMode.Logon, HostExe = @"D:\elsewhere\ClaudeRcHost.exe", Account = Account,
            }),
            Account = Account,
            Logon = TaskLogon.InteractiveToken,
        };

        Assert.Equal(HelperExitCodes.Failed, _helper.RunHost(Setup));

        Assert.DoesNotContain(_tasks.Calls, c => c.StartsWith("run", StringComparison.Ordinal));
        Assert.Contains(@"D:\elsewhere\ClaudeRcHost.exe", Assert.Single(_dialogs.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_host_with_no_task_says_so()
    {
        Assert.Equal(HelperExitCodes.Failed, _helper.RunHost(Setup));

        Assert.DoesNotContain(_tasks.Calls, c => c.StartsWith("run", StringComparison.Ordinal));
        Assert.Contains("does not exist", Assert.Single(_dialogs.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_host_shows_task_schedulers_error()
    {
        _tasks.Add(TaskPath, StartMode.Startup);
        _tasks.FailNext("run", new COMException("The task is disabled.", unchecked((int)0x80041326)));

        Assert.Equal(HelperExitCodes.Failed, _helper.RunHost(Setup));

        Assert.Contains("The task is disabled.", Assert.Single(_dialogs.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Exit_codes_are_stable()
    {
        Assert.Equal(0, HelperExitCodes.Ok);
        Assert.Equal(1, HelperExitCodes.Failed);
        Assert.Equal(2, HelperExitCodes.InvalidArguments);
        Assert.Equal(4, HelperExitCodes.NotRestarted);
        Assert.Equal(1223, HelperExitCodes.Cancelled); // ERROR_CANCELLED
    }
}

/// <summary>The helper's account check against this machine's accounts (a local lookup; nothing is changed).</summary>
public class WindowsAccountsTests
{
    [Fact]
    public void The_current_user_is_a_user()
    {
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        Assert.True(WindowsAccounts.IsUser(me.Name));
    }

    [Theory]
    [InlineData(@"NT AUTHORITY\SYSTEM")]
    [InlineData(@"NT AUTHORITY\LOCAL SERVICE")]
    [InlineData(@"BUILTIN\Administrators")]
    [InlineData(@"BUILTIN\Users")]
    [InlineData("Everyone")]
    [InlineData(@"NO-SUCH-DOMAIN-7f3a\nobody")]
    [InlineData(@"nobody-7f3a9c21e4")]
    [InlineData("")]
    public void Groups_service_accounts_and_unknown_names_are_not(string name)
    {
        Assert.False(WindowsAccounts.IsUser(name));
    }
}

/// <summary>
/// The password reaches Task Scheduler as a BSTR that is zeroed and freed right after the call
/// (SPEC 12): never as a .NET string, which could not be wiped.
/// </summary>
public class PasswordHandlingTests
{
    [Fact]
    public void A_prompted_password_never_becomes_a_string()
    {
        Assert.DoesNotContain(typeof(PromptedPassword).GetMethods(), m => m.ReturnType == typeof(string) && m.DeclaringType == typeof(PromptedPassword));
        Assert.DoesNotContain(typeof(PromptedPassword).GetProperties(), p => p.PropertyType == typeof(string));
    }

    [Fact]
    public void Task_scheduler_gets_the_password_as_a_bstr_of_its_characters()
    {
        using var password = new PromptedPassword("s3cret pässword".ToCharArray());

        var bstr = TaskSchedulerCom.AllocBstr(password.Chars);
        try
        {
            Assert.Equal(15, Marshal.ReadInt32(bstr, -4) / 2); // the BSTR's length prefix, in bytes
            Assert.Equal("s3cret pässword", Marshal.PtrToStringBSTR(bstr));
            Assert.Equal(0, Marshal.ReadInt16(bstr, 30)); // terminated
        }
        finally
        {
            Marshal.ZeroFreeBSTR(bstr);
        }
    }

    [Fact]
    public void An_empty_password_is_an_empty_bstr()
    {
        var bstr = TaskSchedulerCom.AllocBstr([]);
        try
        {
            Assert.NotEqual(IntPtr.Zero, bstr);
            Assert.Equal(0, Marshal.ReadInt32(bstr, -4));
        }
        finally
        {
            Marshal.ZeroFreeBSTR(bstr);
        }
    }
}
