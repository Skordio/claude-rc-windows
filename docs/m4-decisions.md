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
  registered with `TaskFolder.RegisterTask(name, xml, CreateOrUpdate, account, password,
  logonType)`. The library is used only to register, read, run, stop and delete. Reason: the
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
- **Exit codes** (`HelperExitCodes`): 0 ok, 1 failed (the helper showed why), 2 bad arguments, 4
  set but not restarted (shown), 1223 cancelled.

## 5. The elevated helper (`StartModeHelper`, `ClaudeRc.exe --set-start-mode …`)

`App.OnStartup` runs it before the single-instance check (the tray is running), with no tray
icon or window of its own, `ShutdownMode.OnExplicitShutdown`, and exits with its code. For
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
array on dispose (after each attempt). The one `string` (COM takes a BSTR) is made right at the
`RegisterTask` call and never stored, logged, shown or sent over the pipe; a .NET string can't be
wiped, which is the one exposure left (it lives in the elevated helper only, which exits right
after). The "save" box is not offered.

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
`\claude-rc-windows-X\Host` (M2).

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
  test is skipped on GitHub Actions, whose runner may have no interactive session.
- **Safety**: only `\claude-rc-windows-test-<guid>` folders are created; every path is checked by
  `TestTaskFolders.Guard`; each test deletes its folder in `Dispose`/`finally`; a collection fixture
  sweeps leftover `\claude-rc-windows-test-*` folders when the suite starts; the end-to-end test
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
