# M1 decisions

Decisions made while implementing M1 that SPEC.md and PLAN.md leave open. Part A (Core)
decisions are listed as deviations in SPEC.md itself; this file covers the host: its session layer,
the host process, the pipe server, the test seams and the debug CLI.

## 1. Headless VT emulator: a small in-repo screen model

PLAN 1 asks for a choice between XtermSharp, a port of the Windows Terminal VT parser, and a
small in-repo screen model. **Chosen: the in-repo model**, `src/ClaudeRc.Host/Terminal/`
(`TerminalEmulator`, `CharWidth`).

Why:
- **XtermSharp** is not published on nuget.org: `dotnet package search XtermSharp` finds
  nothing (checked 2026-11-06). Using it would mean vendoring its source. It is a port of
  xterm.js internals made for UI front ends, much bigger than what the host needs, and we
  would still have to write the replay serialisation (SPEC 3.3), get the soft-wrap flag per row
  out of it, and check its width handling of U+FE0E (which broke pyte in M0).
- **A Windows Terminal VT parser port** gives only the parser (state machine and dispatch). The
  screen buffer, scrollback, wrap tracking and serialisation would still be ours, and the port
  itself is a lot of C++ to translate and maintain.
- A nuget.org search for `xterm` lists mostly xterm.js wrappers for Blazor and Avalonia UIs, and
  a commercial terminal component. None is a small headless emulator with replay serialisation.
  I did not evaluate them further.
- What claude rc sends through conhost is a small, well-known subset (m0-findings 2): CUP, EL,
  ED, SGR, OSC 0/8, `?25`/`?1004`/`?9001`, plain text with CR/LF. A model that covers it plus the
  common xterm subset is about 1,200 lines with no dependency, and it is fully covered by tests.

What it does:
- C0 controls, all the usual cursor moves (CUP/HVP, CUU/CUD/CUF/CUB, CNL/CPL, CHA/HPA/HPR,
  VPA/VPR, CHT/CBT), ED, EL, ECH, ICH, DCH, IL, DL, SU, SD, IND, NEL, RI, REP, DECSC/DECRC,
  SCOSC/SCORC, DECSTR, RIS.
- SGR with 16, 256 and RGB colours and the usual attributes.
- DECSTBM scroll regions. Only lines scrolled off the top of a full-height region go to the
  scrollback, as in xterm.
- The alternate screen (`?47`, `?1047`, `?1048`, `?1049`), autowrap (`?7`) and cursor visibility
  (`?25`). Every other DEC private mode (`?1004`, `?9001`, `?2004`, mouse modes) is accepted and
  ignored.
- OSC 0/2 set the title. OSC 8 and other OSCs, DCS, SOS, PM and APC strings are skipped
  without touching the screen. Charset designations (`ESC ( B`) are consumed.
- UTF-8 decoded across chunk boundaries. Escape sequences split across chunks are parsed.
- Widths like `wcwidth`: variation selectors (U+FE0E/U+FE0F), combining marks and format
  characters have zero width and attach to the previous cell. East Asian wide characters and
  default-emoji code points take two cells. `✔` (U+2714) is narrow. Overwriting half of a wide
  character blanks the other half.
- A **soft-wrap flag per row**, set when printing past the right margin and cleared by EL 0/2
  and ED on that row. `ScreenRow.Wrapped` feeds `PromptDetector` (SPEC 8).
- **5,000 lines of scrollback**, in a ring buffer.
- `Resize` cuts or pads rows without reflow. When the screen gets shorter, blank rows below the
  cursor go first, then rows from the top move to the scrollback. conhost repaints the whole
  screen after `ResizePseudoConsole`, so no reflow is needed.
- **`Serialize()`** returns VT bytes that rebuild the terminal in a fresh one of the same size
  (SPEC 3.3 replay). In order:
  - `RIS`, then the title.
  - Scrollback and screen, written top to bottom with CR LF. A soft-wrapped row is written in
    full and relies on autowrap, so the copy wraps at the same place.
  - The alternate screen, if shown.
  - The scroll region.
  - The cursor (a pending wrap at the margin is reproduced by rewriting the last cell), the pen,
    autowrap and cursor visibility.

  Tests pin the round trip: a fresh emulator fed the bytes has the same cells, styles, wrap
  flags, scrollback, cursor and modes.

Not supported: origin mode, insert mode, custom tab stops (fixed every 8 columns), DEC special
graphics, reflow, and answers to device queries (DSR/DA). conhost does not need answers when
the pseudo-console is created without `PSEUDOCONSOLE_INHERIT_CURSOR`.

Tests (`tests/ClaudeRc.Host.Tests/TerminalEmulatorTests.cs`):
- Every `tests/fixtures/*.raw` renders to exactly its `.txt`, with U+FE0E removed only for the
  comparison, because the `.txt` files were rendered without it.
- `PromptDetector` gives no prompt for the ready and stopping fixtures. For the trust fixture,
  whose path wraps at 120 columns, it gives the whole `Trust …? [y/N]` line. For the spawn-mode
  fixture it gives `Choose [1/2] (default: 1):`.
- `ReadyDetector` matches only the ready screens.
- Unit tests cover each sequence above, and the round trip is tested on every fixture.

## 2. Sessions (`src/ClaudeRc.Host/Sessions/`)

- **`ConPtyProcess`** is the M0 spike's launcher made reusable:
  - `STARTF_USESTDHANDLES` with null std handles;
  - `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` (`0x00020016`, defined by hand);
  - `CREATE_SUSPENDED`, then assign the Job Object (`KILL_ON_JOB_CLOSE`), then resume;
  - `lpApplicationName` is the full program path, and the command line is built from the argument
    list by `CommandLine.Build` (PasteArguments rules, checked against `CommandLineToArgvW`).
- **Ctrl+C reaches the session even when the host ignores Ctrl+C.** A process started in a new
  process group has the inherited "ignore Ctrl+C" flag set, and its children inherit it. The test
  runner is started that way, and Task Scheduler may start the host that way too. FakeClaude
  then never saw the 0x03 stop. The host now calls `SetConsoleCtrlHandler(NULL, FALSE)` before
  each `CreateProcess`, which clears the flag for itself and so for its children. The host has
  no console, so this has no other effect. Pinned by
  `SessionTests.Ctrl_c_reaches_the_session_even_when_the_host_process_ignores_ctrl_c`.
- **Environment**: `SessionEnvironment.FromRegistry()` calls `CreateEnvironmentBlock` with the
  host's own token and `bInherit = FALSE`. This is the environment Windows builds at logon:
  system and user variables, volatile variables, and the system PATH followed by the user PATH.
  It is read fresh at each start, and nothing comes from the host process. The host passes this
  same PATH to `ClaudeLocator.Locate`.
- **Exit**: when the main process exits (on its own or after a stop), the session:
  1. terminates its job, so leftover children die with it (SPEC 6.3);
  2. closes the pseudo-console;
  3. reads the output to EOF (at most 3 s);
  4. only then completes `Exited`.

  Output written just before exit is therefore on the screen (m0-findings 1).
- **Stop** (`StopAsync`, SPEC 6.4): queue 0x03, wait 5 s, then `TerminateJobObject` with
  `Session.KilledExitCode` (-1). `StopResult.Killed` says whether step 3 was needed.
  `Dispose` kills at once.
- **Input never blocks.** Writes to the pseudo-console go through `InputWriter`: a queue (at most
  4 MiB; more is refused) and a writer thread per session, which owns the pipe. A console that
  stops draining its input would otherwise block the caller, and callers hold the host's lock
  (`input`, the stop's Ctrl+C). The grace timer of a stop starts before the 0x03 is queued, and
  `TerminateJob` takes a handle reference instead of a lock, so step 3 always runs.
- **Size limits**: 1–1000 columns and 1–500 rows. They bound the emulator's memory when a
  viewer asks for a size.
- **Attach**: `Attach(callback, out replay)` takes the replay bytes and adds the subscriber under
  the same lock that feeds the emulator, so nothing is lost or repeated between replay and live
  stream. The callback runs on the output thread and must only queue the chunk. A callback that
  throws is dropped.
- **`GetLastLines(n)`** returns the last non-blank logical lines of the scrollback and screen,
  for the failing row (SPEC 5.2).

## 3. Prompt scan

`PromptScanner` runs every 2 s on a `TimeProvider` timer. For each session it runs
`PromptDetector` and `ReadyDetector` on the screen, and reports only changes:
- `onPrompt(path, text | null)`, which the host forwards to `Supervisor.PromptChanged`;
- `onReady(path)`, once per added session, forwarded to `Supervisor.ReadySeen`.

Callbacks run outside the scanner's lock. The host forwards them to the supervisor under its own
lock. Adding a folder again (a new process) starts it fresh.

## 4. FakeClaude

`tests/FakeClaude` reads `fakeclaude.json` from its working folder. The keys are documented at
the top of its `Program.cs`. They let a test:
- record argv, cwd, pid, chosen environment variables and a child's pid;
- spawn a child (in its own hidden console, ignoring Ctrl+C);
- replay a fixture `.raw` file, print text, and print a prompt;
- append each input line to a file. Input is read key by key with `ReadKey` and echoed by FakeClaude
  itself, as a raw-mode program such as claude does. conhost's cooked line input (`ReadLine`) now
  and then dropped a whole line written right after the previous one had been read, which made
  the input tests flaky (about one run in three). Ctrl+C is still a signal;
- append console sizes to a file;
- log or ignore Ctrl+C;
- exit with a code after a delay;
- answer `--version` (`0.0.0-fake (Claude Code)`), for the `settings.get` info.

It switches its console to UTF-8 and VT processing, as claude does.

Host integration tests run one at a time (`DisableTestParallelization`). Each one checks on
dispose that no `FakeClaude.exe` from the test output folder is still running. They never look
at, or kill, any other process.

## 5. The host process (`src/ClaudeRc.Host/HostApp.cs`, `Program.cs`)

- **`Program`** parses the PLAN 2.2 options, takes the single-instance mutex
  (`SingleInstance`, `Global\claude-rc-windows-host-<SID>[-instance]`, ACL user + SYSTEM) and runs
  a `HostApp` until it fails. Exit codes (`HostExitCodes`): 0 stopped, 1 failed (see host.log),
  2 bad arguments, 3 another host holds the mutex. There is no "quit" request in SPEC 10, so in
  production the host runs until its logon session ends or it is killed; its sessions then die with
  their Job Objects (SPEC 6.5, pinned by `HostProcessTests`). `HostApp.DisposeAsync` is the
  graceful quit of SPEC 6.4 (Ctrl+C every session, wait, kill), used by the in-process tests.
- **One lock** guards the `Supervisor`, the sessions and the clients' viewer state. Each input
  (pipe request, session exit, prompt-scan result, clock tick) takes it, calls the supervisor and
  carries out the effects in order. A `StartSession` effect runs the presets (6.2), the locator (6.1)
  and `Session.Start` (6.3), then feeds `SessionStarted` or `StartFailed` back in the same pass. Any
  exception during a start becomes `StartFailed`, so a folder can never stay "launching". Stops
  run on their own task (`Session.StopAsync`), and the exit is reported when `Session.Exited`
  completes. Nothing slow runs under the lock: `claude --version` runs outside it.
- **Clock**: a loop calls `Supervisor.Tick` once a second, or sooner at `NextDueAt`, and every other
  input wakes it, so a 100 ms backoff (tests) is not rounded up to a second.
- **Start order**: the pipe is created first (so a host that cannot own the pipe starts nothing),
  but under the lock, so a client that connects at once waits until the saved list is loaded. A
  request served before `Load` could save a list without the loaded folders.
- **Claude not found** (SPEC 6.1): a failed start is retried after `StartErrorRetry` (60 s), and
  every start locates claude again, so this is the 60 s re-check. The locator gets the PATH of the
  freshly read environment.
- **Failing rows** (SPEC 5.2): when a session exits, the host keeps `Session.GetLastLines(20)` and
  sends them in the row's `last_lines` field while the row is `failing` (SPEC 10 now lists the field).
- **Logs**: supervisor `FolderLog` effects go to the folder log; host events (start, settings
  changes, failures) go to host.log. Log write errors never stop the host.
- **A corrupt `folders.json`** is never overwritten: the host starts with no folders, refuses `add`,
  `set`, `restart` and `remove` with a message that names the file, and skips every save.
  A corrupt `settings.json` gives the defaults until the next `settings.set`.
- **`settings.set`** refuses a `claude_path` that is not a full path, or not a `.exe` or `.cmd`
  (SPEC 6.1; `ClaudeLocator` refuses the same when it reads the setting).

## 6. Pipe server (`src/ClaudeRc.Host/Ipc/`)

- `PipeServer` creates `\\.\pipe\claude-rc-windows-<SID>[-instance]` with
  `CreateNamedPipeW` and wraps the handle in a `NamedPipeServerStream`.
  `NamedPipeServerStreamAcl.Create` cannot set `PIPE_REJECT_REMOTE_CLIENTS`, and without it a
  client on `\\127.0.0.1\pipe\…` (SMB, the same path a remote machine uses) connected: a
  loopback SMB client keeps its interactive token, so a DACL alone cannot tell it apart. The
  security descriptor has the user as owner, a deny for the NETWORK SID (a second layer, which a
  real remote logon carries), access for the user and SYSTEM only, and no inherited entries. The
  first instance uses `FILE_FLAG_FIRST_PIPE_INSTANCE`, so the host fails rather than join a pipe
  that someone else created under its name. There is always one waiting instance, so any number of
  clients are served at once. `HostProcessTests` reads the live pipe's security back and checks
  that the `127.0.0.1` path is refused (skipped when the Server service is not running).
- `ClientConnection` reads UTF-8 lines (at most 1 MiB each) and writes through a queue bounded at
  20,000 messages and 32 MiB (an output event is about 22 KB, a replay a few MB). A client that
  falls that far behind is disconnected rather than slowing the host or making it buffer hundreds
  of MB. A single message larger than the cap is still sent when the queue is otherwise empty.
  A version mismatch gets `{"v":1,"ok":false,"error":"host and app versions differ: restart
  the host"}` (no `id`), and then the connection is closed. A line that is not a valid request gets
  an error response, and the connection stays open.
- `takeover` answers an error until M3 (adoption).
- **Attach** needs a folder in the list with a running session. The host resizes the session to the
  viewer, then sends the replay as the first `output` event and streams after it. Output that arrives
  while the replay is being queued waits behind it, so the order is always right. A viewer stays
  attached to the folder, not to the process: when the folder's next session starts (after a
  restart or a crash), the viewer gets a fresh replay of the new session (it starts with `RIS`, so
  the viewer's screen is reset). `detach`, or closing the connection, ends it. When several
  viewers have different sizes, the last attach or resize wins.
- `IpcClient` (`ClaudeRc.Core.Ipc`) is the client side: it matches responses by `id`, puts events
  in a channel, and connects with `PipeOptions.CurrentUserOnly` (it refuses a pipe that another
  account owns). The debug CLI and the tests use it, and so will the tray app.

## 7. Test seams

- **Every path and name is injectable** (PLAN 2.2). `HostConfig` holds the parsed `HostOptions`
  (`--data-dir`, `--log-dir`, `--claude-json`, `--instance`). It also holds the user profile, which
  is where the locator looks for the native install, and the environment provider. Tests point
  `claude_path` at FakeClaude.exe through the temp `settings.json`, so no special switch is needed.
  In-process test hosts also get a temp user profile and a PATH cut down to the Windows folders,
  so nothing can find the real claude even if the setting were lost. Out-of-process tests check
  `settings.get` before they turn anything ON.
- **Time**: `HostConfig.WithTimeScale(f)` multiplies every supervision timing (backoff, failing
  window, stable uptime, missing-folder re-checks, the starting timeout, the start stagger and the
  start-error retry) and the prompt-scan interval by `f`. The integration tests run the host
  in-process (`TestHost`) with `f = 0.02`: the backoff becomes 100, 200, 400 ms…, the start retry
  1.2 s and the failing window 12 s. They drive it through the real pipe, with real FakeClaude
  sessions. The stop grace (5 s) is not scaled, because it waits for a real process.
  `HostConfig.Time` (a `TimeProvider`) is also injectable, but real processes need real time, so
  the tests scale the timings instead of using a fake clock. The supervisor's own unit tests use a
  fake clock.
- `HostProcessTests` run the real ClaudeRcHost.exe for the mutex, the exit codes and "sessions die
  with the host".

## 8. Debug CLI (`tools/ClaudeRc.DebugCli`)

`ClaudeRc.DebugCli [--instance S] list | add P | set P on|off | restart P | remove P | takeover P |
attach P [cols rows] | input P TEXT | log P [N] | settings | watch`. `input` understands `\r \n \t
\e \\ \xHH`. `attach` prints the replay and the stream as raw bytes and sends keys (Ctrl+] detaches;
with redirected input, its end detaches). Exit codes: 0 ok, 1 the host refused, 2 usage, 3 no host.
It is an internal tool and is never shipped (SPEC 15).