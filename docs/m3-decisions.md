# M3 decisions

Decisions made while implementing adoption of externally started `claude rc` (SPEC 7) that
SPEC.md and PLAN.md leave open. SPEC 7 now states the behaviour; this file says how it is built
and why.

## 1. Layout

| Piece | Where | What |
|---|---|---|
| Matcher | `src/ClaudeRc.Core/Adoption/ExternalSessionMatcher.cs` | Pure: which processes are external `claude rc` sessions, over `ProcessFacts` (pid, parent pid, image name, creation time, argv, cwd, owner SID). Unit-tested in Core (`ExternalSessionMatcherTests`). |
| Reader | `src/ClaudeRc.Host/Adoption/WindowsProcesses.cs` | Thin Windows layer: Toolhelp snapshot; for candidates only, creation time, token user, and the command line and current directory from the PEB. Reads, never changes. |
| Stopper | `src/ClaudeRc.Host/Adoption/ExternalStopper.cs` | Take over's stop of one external process tree. |
| Host | `HostApp` (`ScanExternal`, `ScanLoop`, `TakeOver`) | When to scan, what to report to the supervisor, running take overs. |
| Supervisor | `Supervisor.TakeoverFailed` (new) | A failed take over shows on the row's `error`. The rest (`ExternalChanged`, `Takeover`, `StopExternal`) was already there from M1. |

## 2. The matcher

- Image names count case-insensitively. `claude.exe` always counts, and so does the Settings claude
  path's file name when it is an `.exe` (`ExternalSessionMatcher.ForHost`). A `.cmd` setting adds
  nothing: the shim runs `claude.exe` or `node`.
- The subcommand must be the **first** argument and is compared case-sensitively (`rc`,
  `remote-control`), as SPEC 7 says ("arguments start with"). `claude --debug rc` is not matched.
- node: leading node options (arguments starting with `-`) are skipped, then the script must end in
  `\claude-code\cli.js` (`/` accepted), then `rc`/`remote-control`. `node -e x cli.js rc` does not
  match (the `-e` value is taken as the script).
- **Each tree counts once.** A match whose ancestor also matches is dropped, so an `rc` child of
  an `rc` parent is not a second session. An ancestor is followed only while it was created no
  later than its child (creation times), so a parent PID that has been reused by a newer process
  is never followed. The walk stops at a process whose creation time is unknown, and survives
  cycles.
- **Hosted processes** are the members of the sessions' Job Objects plus each session's main
  PID; a match below one of them (by the same ancestor rule) is dropped too. The host's own PID is
  **not** in that set: in tests the in-process host is the test runner, which is also the parent of
  the FakeClaude the test starts "outside the host". The host's own image is never a candidate.
- `StillMatches` is what take over checks just before stopping: same PID and creation time, still
  `claude rc` of this user, normalised cwd still the row's folder.
- Folders are compared with `PathNormalizer.Comparer` after `PathNormalizer.Normalize` (which
  removes the PEB's trailing `\`). `ByFolder` keeps every external session per folder, oldest first;
  the supervisor is told the oldest PID.

## 3. Reading processes

- `CreateToolhelp32Snapshot` lists every process (pid, parent pid, image file name). Only
  candidates (by image name) are opened, with `PROCESS_QUERY_INFORMATION | PROCESS_VM_READ`
  (falling back to `PROCESS_QUERY_LIMITED_INFORMATION`, which gives the creation time and owner but
  no cwd). Their ancestors are opened with the limited right for their creation time only.
- Owner: `OpenProcessToken(TOKEN_QUERY)` → `TokenUser` → string SID.
- PEB: `NtQueryInformationProcess(ProcessBasicInformation)` → `PebBaseAddress`, then
  `PEB.ProcessParameters` (0x20) and in `RTL_USER_PROCESS_PARAMETERS` the `UNICODE_STRING`s
  `CurrentDirectory.DosPath` (0x38) and `CommandLine` (0x70). A WOW64 target:
  `ProcessWow64Information` gives the 32-bit PEB; offsets 0x10, 0x24 and 0x40, 32-bit
  `UNICODE_STRING`. An x64 process on ARM64 is not WOW64 and uses the 64-bit layout. Strings are
  capped at 64 KB. `WindowsProcessesTests` read a 64-bit FakeClaude and a 32-bit
  `SysWOW64\PING.EXE`, both started hidden in a temp folder by the test.
- argv: `CommandLineToArgvW` (an empty command line gives no arguments rather than this process's
  own path).
- Anything that fails (access denied, the process exited, still being created) leaves that fact
  null; nothing throws. So an **elevated** `claude rc` is not seen by a host that is not elevated
  (SPEC 7 says so). The System process (pid 4) reads as "no details".
- All P/Invoke goes through CsWin32 (PLAN 1). `CommandLineToArgvW` is generated as
  `PInvoke.CommandLineToArgv`; `EnumWindows` takes an `[UnmanagedCallersOnly]` callback with a
  `GCHandle` for its state.

## 4. When the host scans

`HostApp.ScanExternal` lists processes outside the host lock, then under the lock takes the job
members, drops matches that are no longer alive with the same creation time
(`WindowsProcesses.IsAlive`), and reports each folder whose oldest PID changed to
`Supervisor.ExternalChanged`. Doing the job and liveness checks under the lock closes two races:
a session started after the snapshot is already in its job when the lock is taken (it is started
under the lock), and a hosted process that died after the snapshot is not alive any more (a
session is removed from the host only after its process has exited). Scans never overlap (a
semaphore).

It runs:
- **before `Load`** at host start (SPEC 5.4), under the lock that also guards the load, so a
  folder with an external session is never started, and the very first `list` already shows it;
- every `HostConfig.ExternalScanInterval` (5 s; scaled by `WithTimeScale` like the other timings,
  so 100 ms in the integration tests; one test runs at the real 5 s);
- **before `add`, `set` ON and `restart`**, so a folder added a moment after the user started their
  own `claude rc` there gets no second session. This is not in the spec text before M3; SPEC 7 now
  says it. A crash-restart after the backoff can still race an external session started in the
  same seconds; the scan then shows both, and the host never stops either;
- **right after a take over**, so the hosted start follows at once instead of up to 5 s later.

A scan that fails (Toolhelp error) is logged to host.log and the next one tries again.

## 5. Take over

- The pipe op `takeover` now goes to `Supervisor.Takeover` (until M3 it answered an error). The
  supervisor saves the folder ON, logs "taking over the external claude rc (pid N)" and emits
  `StopExternal`. The host runs the stop on a background task with every external session the last
  scan found **in that folder** (a folder can have more than one; the row shows the oldest).
- `ExternalStopper.Stop`, per process:
  1. open it (query, synchronize, terminate). If it is gone, or its creation time differs (the
     PID now names another process), it counts as stopped and nothing is touched. With the handle
     held (so the PID cannot be reused meanwhile) it reads the process again and checks
     `StillMatches`; if it no longer is `claude rc` in that folder, nothing is stopped and the take
     over fails with that message;
  2. finds its tree: processes whose parent is in the tree and that were created after that parent;
  3. **windows**: visible, unowned top-level windows of the `claude rc` process itself or of a
     `conhost.exe` that is its direct child (its own console window). Those get `WM_CLOSE`, and the
     stop waits up to `TakeoverCloseGrace` (3 s) for it to exit. A window of any other process is
     never closed: on Windows 11 a console program normally runs in a Windows Terminal tab, whose
     window belongs to `WindowsTerminal.exe` and holds the user's other tabs; a `claude rc` started
     from a shell shares the shell's console, whose host is the shell's child, not claude's. Closing
     those would close the user's terminal. In those cases step 4 does the stop;
  4. terminates the process (exit code 1), then, in two passes (to catch children started
     meanwhile), every tree member whose creation time still matches, each through its own handle.
     It waits up to 5 s for the process to be gone.
- The outcome goes to the folder log ("took over: stopped the external claude rc (pid N), …
  process(es) of its tree terminated", or "take over: could not stop …") and host.log. A failure
  calls `Supervisor.TakeoverFailed`: the row stays `external`, ON, and its `error` reads
  "take over failed: …" until the next start or take over. Then the host scans at once.

## 6. Safety: the user's own claude rc sessions

The user had real `claude rc` sessions running while M3 was built and tested. Nothing here may
stop, signal or take over a process a test did not start.

- **What a host can stop.** The only effect that stops an external process is `StopExternal`, and
  only `Supervisor.Takeover` emits it, for the folder named in the request; the host stops only the
  processes the last scan found in that folder, each checked again (`StillMatches`) just before.
  OFF, Remove, Restart, host quit and host restart never stop an external process. Pinned by
  `SupervisorTests.Only_take_over_ever_asks_to_stop_an_external_session`,
  `…Take_over_names_only_its_own_folders_external_process`,
  `AdoptionTests.Turning_off_removing_restarting_the_host_and_another_folders_take_over_never_stop_an_external_session`
  and `ExternalStopperTests` (another process under the same PID, a process that moved to another
  folder and one without `rc` are left running).
- **What a test host sees.** SPEC 7 lists an external `claude rc` in a folder that is not in the
  list as an unsaved row. A test host using the production matcher would therefore list the user's
  real sessions. That would be harmless (it never acts on them), but it makes tests depend on what
  the user is running. So `HostConfig.ExternalImageNames` is a test seam: when set, only those image
  names count as claude and the node rule is off. `TestHost` sets it to `FakeClaude.exe`, so an
  in-process test host never even opens the user's `claude.exe` processes. Pinned by
  `AdoptionTests.A_test_host_lists_only_its_own_temp_folders`.
- **The real ClaudeRcHost.exe in tests** (`HostProcessTests`) uses the production matcher, because
  the seam is not a command-line option (no test-only switch in the shipped exe). It may list the
  user's real `claude rc` sessions as unsaved rows. That is accepted: it only reads them
  (`PROCESS_QUERY_INFORMATION | PROCESS_VM_READ`, the same access Task Manager uses for the
  command-line column), never asks to stop one, and runs with temp data folders and an
  `--instance`. `HostProcessTests` now looks only at its own folder's row and checks that every
  other row is an unsaved, OFF `external` row.
- Tests start their "external" FakeClaude hidden (`CreateNoWindow`, `ExternalFake`) in temp
  folders, and kill only those processes (and a child FakeClaude of theirs, after checking it is
  a FakeClaude from the test output). The 32-bit reader test starts `SysWOW64\PING.EXE -n 30
  127.0.0.1` hidden and kills it.

## 7. FakeClaude

New options, all for adoption tests:
- `--fake-config <file>` anywhere on the command line reads that file instead of
  `fakeclaude.json`, so the external one (`rc --fake-config external.json`) can behave differently
  from the host's sessions in the same folder (for example exit after 1.5 s, while the host's runs
  until stopped).
- `--fake-child` anywhere on the command line is the child mode, and `spawn_child_args` puts
  arguments before it, so a child can look like `claude rc` itself (`rc --fake-child`).

## 8. Tray

The view-model rules for external rows were already right in M2 (blue "running (started outside
the app)", Show disabled, Take over shown and enabled, Restart and Remove off, no OFF
confirmation). M3 adds:
- `IsUnsaved`, `UnsavedText` ("not in your list") and `ToggleToolTip`. An unsaved row has its name
  in italics, muted, and a blue-bordered "not in your list" tag; its switch's tooltip says that ON
  adds the folder and leaves that claude rc running. SPEC 3.1 now says so.
- Tests for these, for the external row's command states, and for the main view-model: turning an
  unsaved row ON sends `set` without a question, turning an ON external row OFF asks nothing, and
  a take over that the host accepts shows no error.

## 9. Tests

- Core: `ExternalSessionMatcherTests` (38 cases) and seven new `SupervisorTests`. Written first
  and seen failing (40 failures against `NotImplementedException` stubs; the supervisor pins that
  describe M1 behaviour passed at once, as expected).
- Host: `AdoptionTests` (13), `WindowsProcessesTests` (6), `ExternalStopperTests` (5). The adoption
  and reader tests ran first against stubs and an unchanged host and failed. The first real run
  found one bug (the host's own PID in the excluded set hid every process started by the test
  runner), fixed in section 2. `ExternalStopperTests` were added after the stopper, as extra
  safety pins.
- Tray: 10 new test cases (251 → 261); the new properties' tests failed to compile before the properties existed.
- Every Host test still checks that no FakeClaude is left running.

## 10. Not done, or left for later

- An elevated `claude rc` is not seen by a non-elevated host (it cannot read its PEB). SPEC 7 says
  so; the user would take it over by hand.
- Closing a real `claude rc`'s own console window (step 3) is not tested automatically: it needs a
  visible window. The Drive step should start `claude rc` with its own console
  (`start "" claude rc` from cmd, with conhost as the default terminal) in a throwaway folder,
  and take it over.
