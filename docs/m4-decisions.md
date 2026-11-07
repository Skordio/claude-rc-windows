# M4 decisions

Decisions made while building the start modes (SPEC 9, 3.4, 14's portable first run) that SPEC.md
and PLAN.md leave open. SPEC 3.1, 3.4, 9.2, 9.3, 9.4, 10 and 14 now state the behaviour; this file
says how it is built and why.

## 1. Task Scheduler access

- **Package**: `TaskScheduler` (dahall) 2.12.2, as PLAN 1 names. Its `net9.0-windows7.0` build
  runs on .NET 10; it pulls in `System.Diagnostics.EventLog`. MIT licence (M5's third-party
  notice must list it). Its translated resources are left out of the build
  (`SatelliteResourceLanguages=en`).
- **The definition is our own XML** (Task Scheduler schema 1.2, `HostTaskDefinition.BuildXml`),
  registered with `ITaskFolder::RegisterTask(name, xml, TASK_CREATE_OR_UPDATE, account, password,
  logonType)` through our own small COM declaration (`TaskSchedulerCom`, M4 review): the library's
  `TaskFolder.RegisterTask` takes the password as a .NET string, which can't be wiped (section 5).
  The library is used to create folders, read, run, stop and delete. Reason: the
  library's `TaskDefinition` needs a live Task Scheduler connection even to be built, while the XML
  is pure, so the unit tests assert on it directly (`HostTaskDefinitionTests`), and the same string
  is what gets registered. Raw COM through our own interop was the other choice; the library's
  wrappers were already in the plan and are well tested, and the only thing they would add over
  raw COM is risk-free typed access, which the integration tests use to read the task back.
- **Seam**: `ITaskService` (`Find`, `Register`, `Delete`, `Run`, `Stop`) with
  `WindowsTaskService` over the library. Each call opens its own `TaskService` connection. Tests
  use `FakeTaskService` (in memory, every call recorded, errors injected).
- `RegisterTask` with `TASK_LOGON_PASSWORD` is the call the task text calls
  `RegisterTaskDefinition`: same COM method family, same password parameter, but it takes the XML.
- `TaskSchedulerCom` declares `ITaskService` and `ITaskFolder` as dual interfaces in vtable order
  (only `Connect`, `GetFolder` and `RegisterTask` are called) and passes the VARIANTs as a
  blittable struct (16 bytes on x86, 24 on x64). Every registration goes through it, so the
  integration tests exercise it (the account VARIANT is read back as the principal; E_ACCESSDENIED
  still arrives as `UnauthorizedAccessException`). The password VARIANT has the same shape and is
  only filled for startup, which is not registered on this machine.

## 2. The task definition

| | `logon` (9.1) | `startup` (9.2) |
|---|---|---|
| Trigger | `LogonTrigger` with `UserId` = the account | `BootTrigger` |
| Principal | the account, `InteractiveToken` | the account, `Password` |
| Run level | `LeastPrivilege` | `LeastPrivilege` |
| Needs | nothing (checked: a standard token registers it) | admin (checked: a boot trigger is refused with `E_ACCESSDENIED` without it) and the password |

Both: `StartWhenAvailable`, no battery start/stop limits, `ExecutionTimeLimit PT0S`,
`RestartOnFailure PT1M × 999`, `MultipleInstancesPolicy IgnoreNew`, `AllowStartOnDemand`,
`Priority 5`, no idle conditions, not hidden, no wake. Action: `"<full path>\ClaudeRcHost.exe"`
(always quoted), the host's arguments (`CommandLine.JoinArguments`, the same quoting the host
uses for claude, moved from Host to `ClaudeRc.Core.Processes`), working directory = its folder.

- **Priority 5**: Task Scheduler's default 7 is `BELOW_NORMAL`, and the host's sessions inherit
  the priority class. 4 to 6 map to normal. SPEC 9.3 now says so.
- **User profile**: there is no XML setting; Task Scheduler loads the profile for a password
  logon. SPEC 9.2 says so.
- **Account**: `WindowsIdentity.GetCurrent().Name` (`DOMAIN\name`). Task Scheduler stores the
  principal as a SID; the trigger keeps the name.
- **Reading the mode back** (`ReadMode`): exactly one trigger, `LogonTrigger` → logon,
  `BootTrigger` → startup; anything else (a task changed by hand, invalid XML) → unknown. Task
  Scheduler leaves defaults out of the XML it returns, so the reader only looks at the trigger.

## 3. Status and health (`HostTaskManager`, the new `IHostTask`)

`HostTaskManager` replaces M2's `SchtasksHostTask` (and its process runner). Calls run on the
thread pool. `HostTaskInfo` now carries `Enabled`, `Running`, `LastResult`, `LastRunTime`,
`NextRunTime` and `Error`:

- `NeedsPassword`: exists, mode startup, last result `0x8007052E`.
- `Health`: `re-enter your password` / `disabled` / `running` / `ready` (last result 0,
  `SCHED_S_TASK_HAS_NOT_RUN`, `SCHED_S_TASK_RUNNING`, or `SCHED_S_TASK_TERMINATED` after a stop)
  / `last run ended with exit code N` (0-255; 3 adds "another host was already running", the
  host's `AlreadyRunning`) / `last run failed (0x…)`.
- Status bar: `Start mode: at logon · running`, `Start mode: no scheduled task`, or
  `Start mode: unknown (the task could not be read: …)`. Settings' "Scheduled task" line:
  `running (\claude-rc-windows\Host), last run …, next run …`.
- Start host on a disabled task says it is disabled instead of running it.
- `StartHostResult.Missing` now says to choose a start mode in Settings.

## 4. Switching (`StartModeSwitcher`, unelevated)

- **logon**: the tray registers it itself (no admin). If Task Scheduler refuses with
  `UnauthorizedAccessException` (the old task is the administrator-made startup task), or a
  requested restart is refused, the whole switch is handed to the elevated helper.
- **startup**: always the helper (boot trigger needs admin; the password is asked for there).
- A missing `ClaudeRcHost.exe` is refused before anything changes.
- **Elevation**: `ShellElevation` runs `Environment.ProcessPath` with the `runas` verb and the
  helper's arguments, waits, and reads the exit code. `ERROR_CANCELLED` at UAC → cancelled.
- **Helper arguments** (`TrayOptions.HelperArguments`): `--set-start-mode`, `--for-user`,
  `--restart-host`, `--instance`, `--host-exe` and every host argument as `--host-arg`. All
  explicit, because for a standard user UAC runs the helper as an administrator account, whose own
  `%APPDATA%` and identity must not leak into the task. The task is set up for `--for-user`.
  `HelperArguments` returns null when the helper would refuse this tray's task (section 5): a host
  that is not next to ClaudeRc.exe (a debug build's `--host-exe`) or other host arguments. The
  switch then fails with that reason instead of showing UAC for a helper that would refuse.
- **Exit codes** (`HelperExitCodes`): 0 ok, 1 failed (the helper showed why), 2 bad arguments, 4
  set but not restarted (shown), 1223 cancelled.

## 5. The elevated helper (`StartModeHelper`, `ClaudeRc.exe --set-start-mode …`)

`App.OnStartup` runs it before the single-instance check (the tray is running), with no tray
icon or window of its own, `ShutdownMode.OnExplicitShutdown`, and exits with its code.

**Its command line is not trusted** (M4 review). Any process running as the user can start
`ClaudeRc.exe --set-start-mode …` with `runas`, and the UAC prompt shows claude-rc's own exe, which
the user expects to approve when switching modes. So in helper mode:

- the host is always `Path.Combine(AppContext.BaseDirectory, "ClaudeRcHost.exe")`; a `--host-exe`
  naming anything else (compared as full paths, ignoring case) is refused;
- `--host-arg` is allowed only with `--instance`, and only `--instance <the same suffix>` (required)
  and `--data-dir`, `--log-dir`, `--claude-json`, each at most once with a full path
  (`TrayOptions.HelperHostArgumentsError`);
- `--for-user` must be a user account: `LookupAccountName` with `SID_NAME_USE == SidTypeUser`
  (`WindowsAccounts.IsUser`; groups, SYSTEM, LOCAL SERVICE, computers and unknown names are not).
  `NTAccount.Translate` was the suggested call, but it can't tell a user from a group.

The first two are argument errors (the usage message, exit code 2); the account is checked by
`StartModeHelper` before anything else (an error dialog, exit code 2). Nothing is registered or
prompted for in either case. Not done: checking that `--for-user` is the user of the helper's own
logon session (WTS). It would also stop "another user" in logon mode, but the elevated paths can't
be tried on this machine, and with the program and arguments fixed, the most such a run can do is
start the real host at another user's logon. For
startup it loops: prompt → register with the password → on `0x8007052E` prompt again with "The
password was not accepted…" → Cancel returns 1223 and changes nothing. Any other error is shown
(Task Scheduler's message only) and returns 1. Logon needs no prompt. It works unelevated too for
logon, so M5's installer can run `ClaudeRc.exe --set-start-mode logon --for-user <DOMAIN\name>
--restart-host` instead of writing the task itself.

**Password handling** (SPEC 12): `CredUiPrompt` calls `CredUIPromptForWindowsCredentials` with
`CREDUIWIN_GENERIC` (plain user name and password fields: the Windows-credential UI could offer a
PIN or Windows Hello, which can't be unpacked to a password) and the account prefilled
(`CredPackAuthenticationBuffer`). Only the password is used; the task is always set up for the
account the tray named, and Task Scheduler checks the password against it. It is unpacked into
a `char[]`; both CredUI buffers are zeroed before `CoTaskMemFree`. `PromptedPassword` wipes its
array on dispose (after each attempt). It never becomes a .NET string (M4 review; the first
version made one per attempt for the library): `TaskSchedulerCom` copies the characters
straight into a BSTR (`SysAllocStringLen` over the pinned array), passes it as the password
VARIANT and frees it with `Marshal.ZeroFreeBSTR` right after the call, wrong password or not. It is
never stored, logged, shown or sent over the pipe. The "save" box is not offered.

None of the elevated or credential code ran during M4 (no UAC or prompts on the user's machine
while they work); it is unit-tested through `IElevation`, `ICredentialPrompt` and `ITaskService`
fakes. See section 10.

## 6. Restarting the host (found by the integration test)

"Restart" = stop the task's running instance, then run the task. The first version polled the
task's state and ran it as soon as Task Scheduler said "not running". The integration test showed
that this is too early: Task Scheduler reports the task stopped while the old ClaudeRcHost is
still exiting, the new host met the old one's mutex and exited with code 3, and a moment later the
old one was gone too, so **no host was left**. `WindowsTaskService.Stop` now opens the instances'
processes (`RunningTask.EnginePID`, which for an exec action is the host's PID; the open handle
keeps the PID from being reused) before stopping, and returns only when they have ended, or
throws `TimeoutException` after 10 s. The restart then refuses to run the task ("did not stop")
rather than start a host that would exit at once. SPEC 9.4 says so. The test now also checks that
the restarted host still runs 3 s later.

Updating a running task (`CreateOrUpdate`) leaves its running host alone (checked in the
integration test), so a switch without restart never stops anything.

## 7. The tray

- **Settings**: the radios are enabled while the task could be read (it exists or there is none),
  two-way, and remember the user's choice across reloads. Save sends the host settings first
  (while connected; a switch may restart the host), then switches if the chosen mode differs. With
  a host connected it asks "Restart the host now so it runs at startup? …" (No: the new mode
  applies from the next start); with no host the task is just run. The start mode can be switched
  without a host. Cancel/failure keep the window open with the reason in the error line; a note
  from a done switch (not restarted) is shown as a message. "Re-enter password…" appears under the
  radios when `NeedsPassword`.
- **Main window**: status bar text from section 3; a **Re-enter password** button when needed.
  Re-enter runs the startup switch, restarting (= running) the task only if no host answers,
  since a host that could not log on is not running.
- After a switch that ran the task, the connection reconnects at once.

## 8. Portable first run (SPEC 14)

`MainViewModel.StartupCheckAsync`, called by the app once the window shows: read the task; if it
is missing, ask once per tray run ("Start the host at logon": no administrator rights needed) and,
on Yes, switch to logon, running the task if no host answers yet. An unreadable task is not
offered (it may exist). There is no "don't ask again": the tray has no settings of its own, an
installed app always has the task, and without a task Start host can't work anyway. Declining
asks again at the next tray start; SPEC 14 says so.

## 9. Test instances

`--instance X` now also gives the task's host its own folders, so a test task never shares the
real host's state: `--instance X --data-dir %APPDATA%\claude-rc-windows-X --log-dir
%LOCALAPPDATA%\claude-rc-windows-X\logs --claude-json %LOCALAPPDATA%\claude-rc-windows-X\claude.json`.
`--host-arg` (repeatable) replaces that list; `--host-exe` points at a host that is not next to
ClaudeRc.exe (a debug build: the tray's and the host's bin folders differ). The task path stays
`\claude-rc-windows-X\Host` (M2). The elevated helper accepts neither another host nor other
arguments (section 5), so a debug tray can switch to logon itself but not to startup; startup is
tried from a folder where both exes sit together (the portable zip or the installed app).

`HostTaskSpec.TriggerEndBoundary` (and `StartModeSetup.TriggerEndBoundary`) is for tests only: it
writes the trigger's `EndBoundary` (UTC, before `Enabled` in the schema's order). Null in
production.

## 10. Tests

- `HostTaskDefinitionTests` (18): every 9.3 setting in both modes, both triggers and principals,
  quoting, the disabled flag, refusals, reading the mode back (also from Task Scheduler's own
  XML), result codes.
- `HostTaskManagerTests` (17): missing, exists with state/times, needs password (only for
  startup), each health text, changed by hand, Task Scheduler errors, Run (missing, disabled,
  errors).
- `StartModeSwitcherTests` (13) and `StartModeHelperTests` (13): logon without elevation, the
  helper on access denied (register or restart), startup always elevated, UAC No, prompt Cancel,
  failures, not restarted, missing host exe; password from the prompt to Register only, never in
  the XML or an error, wiped after each try; a wrong password asks again; restart order (stop,
  wait, run); a host that does not stop (still running, or `TimeoutException`) is not run again.
- `TrayOptionsStartModeTests` (15), `MainViewModelTests` (9 new), `SettingsViewModelTests` (12
  new).
- `HostTaskIntegrationTests` (7, real Task Scheduler, `[Collection]` without parallelism): a
  disabled logon task running `cmd /c exit 0` is registered without admin and read back with the
  library's typed definition (trigger, principal, every 9.3 setting, action); status through the
  manager; a disabled task is not run; missing task and folder; delete removes the empty folder;
  a boot trigger is refused without admin; the startup XML is parsed by Task Scheduler in memory
  (`TaskDefinition.XmlText`, never registered); and end to end: the logon switch with restart
  registers an enabled task running a **test** ClaudeRcHost (`--instance m4task<guid>` and temp
  folders) and runs it, the host answers `list` on its pipe, an update leaves it running, a
  restart replaces it with one new host that is still running 3 s later, Stop ends it. That last
  test is skipped on GitHub Actions, whose runner may have no interactive session. Its task is
  enabled (it is run), so its logon trigger expires 10 minutes ahead (`EndBoundary`, read back
  through the library): if a killed test run leaves it behind, it never starts a test host at a
  later logon, and Run still works on demand.
- M4 review: `TrayOptionsStartModeTests` gained the helper refusals (a foreign `--host-exe`,
  foreign or malformed `--host-arg`s, no helper arguments for a tray the helper would refuse),
  `StartModeHelperTests` an account that is not a user, `StartModeSwitcherTests` a refused task
  not handed to the helper, `WindowsAccountsTests` (the current user is a user; groups, service
  accounts and unknown names are not), `PasswordHandlingTests` (no string API on
  `PromptedPassword`; the BSTR holds exactly its characters), and `HostTaskDefinitionTests` the
  trigger end. The fake Task Scheduler checks that the password is still intact when handed over.
- **Safety**: only `\claude-rc-windows-test-<guid>` folders are created; every path is checked by
  `TestTaskFolders.Guard`; each test deletes its folder in `Dispose`/`finally`; a collection fixture
  sweeps leftover `\claude-rc-windows-test-*` folders when the suite starts and again when the
  collection ends; the end-to-end test
  kills any ClaudeRcHost started from its own bin folder during the test. `schtasks /query` after
  the runs showed no test task left.
- Seen failing first: every new unit test ran against a stub (`NotImplementedException`) and
  failed before the implementation; the integration tests failed against a stub
  `WindowsTaskService` (all but the in-memory startup check, which only uses the library); the
  restart test failed against the first `Stop` (section 6).

## 11. Not done here

- Registering a real `startup` task, UAC, the credential prompt, a reboot in startup mode and
  the password-change path: manual (PLAN 5), steps in the M4 report.
- `ITaskService.Delete` is not used by the app yet; M5's uninstaller deletes the task.
