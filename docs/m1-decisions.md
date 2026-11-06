# M1 decisions

Decisions made while implementing M1 that SPEC.md and PLAN.md leave open. Part A (Core)
decisions are listed as deviations in SPEC.md itself; this file covers the host's session layer.

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
- **Stop** (`StopAsync`, SPEC 6.4): write 0x03, wait 5 s, then `TerminateJobObject` with
  `Session.KilledExitCode` (-1). `StopResult.Killed` says whether step 3 was needed.
  `Dispose` kills at once.
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
- append each input line to a file;
- append console sizes to a file;
- log or ignore Ctrl+C;
- exit with a code after a delay.

It switches its console to UTF-8 and VT processing, as claude does.

Host integration tests run one at a time (`DisableTestParallelization`). Each one checks on
dispose that no `FakeClaude.exe` from the test output folder is still running. They never look
at, or kill, any other process.
