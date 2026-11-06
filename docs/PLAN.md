# claude-rc-windows: implementation plan

Status: draft, 2026-11-06. Implements [SPEC.md](../SPEC.md) v1.

Agents implementing a milestone: read SPEC.md, this file, and
`docs/m0-findings.md` (once it exists) before writing code. The guardrails in
section 2 are hard rules.

## 1. Decisions

| Topic | Decision |
|---|---|
| Runtime | **.NET 10** (LTS), not .NET 8 (out of support 2026-11-10). SPEC §13 updated. |
| Target frameworks | `ClaudeRc.Core`: `net10.0-windows` (no WPF). Host: `net10.0-windows`, `OutputType=WinExe` (no console window). Tray: `net10.0-windows10.0.17763.0`, WPF (M2: the toast library needs the Windows SDK projection; 17763 = Windows 10 1809, SPEC 13's minimum; see `docs/m2-decisions.md` 1). |
| P/Invoke | `Microsoft.Windows.CsWin32` source generator (ConPTY, Job Objects, `NtQueryInformationProcess`, process enumeration). |
| Tests | xUnit + built-in `Assert`. Time via `TimeProvider`; tests use `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`). No FluentAssertions (it is a paid licence since v8). |
| JSON | `System.Text.Json` with source-generated contexts (IPC messages, data files). |
| IPC | `CreateNamedPipeW` with `PIPE_REJECT_REMOTE_CLIENTS` and a `PipeSecurity` that allows only the user's SID and SYSTEM and denies NETWORK (M1 review: `NamedPipeServerStreamAcl.Create` cannot reject remote clients). |
| VT emulator | Chosen in M1 (SPEC §13). Candidates: XtermSharp, a port of the Windows Terminal VT parser, or a small in-repo screen model. It must handle: CSI cursor moves, erase, SGR, scroll regions, the alternate screen, and 5,000 lines of scrollback, and serialise the screen back to VT for replay. Record the choice and the reason in `docs/m1-decisions.md`. |
| Tray icon | `H.NotifyIcon.Wpf` (or WinForms `NotifyIcon` if that package is a problem). Folder picker: WPF `Microsoft.Win32.OpenFolderDialog`. |
| Toasts | `Microsoft.Toolkit.Uwp.Notifications` (unpackaged app; the AUMID comes from the Start menu shortcut). |
| Task Scheduler | `TaskScheduler` (dahall), as in the spec. |
| Debug client | `tools/ClaudeRc.DebugCli`: pipe client for M1 testing. Internal tool, never shipped (SPEC §15: no CLI in v1). |
| Branch | All work on `impl/v1`, one commit or more per milestone, pushed after each milestone. A PR to `main` at the end. |

## 2. Guardrails for every agent

1. **Tests first.** Write the failing test, run it and see it fail, then implement (AGENTS.md).
2. **Never touch the real user state in tests.** Every path and name the host uses must be injectable, and tests always override them:
   - `.claude.json` path (default `%USERPROFILE%\.claude.json`);
   - data folder (default `%APPDATA%\claude-rc-windows`) and log folder (default `%LOCALAPPDATA%\claude-rc-windows\logs`);
   - pipe name and mutex name (tests add a random suffix);
   - claude path (tests point at `FakeClaude.exe`);
   - scheduled task folder (tests use `\claude-rc-windows-test-<guid>\` and delete it afterwards).
   The host takes these overrides on its command line (`--data-dir`, `--log-dir`, `--claude-json`, `--instance <suffix>`). The tray app takes `--instance` too.
3. **Real `claude rc` runs only in M0 and in manual checks**, always in a throwaway folder under the scratch or `%TEMP%` directory. Back up `%USERPROFILE%\.claude.json` before the first real run.
4. **No leftover processes.** Every Host integration test asserts that no FakeClaude process is left running after the test.
5. `dotnet build -warnaserror` and `dotnet test` must pass before a milestone is called done.
6. Any change to the spec's behaviour goes into SPEC.md in the same commit, with the reason.

## 3. Repository layout (end state)

```
ClaudeRc.sln
Directory.Build.props        net10.0-windows, nullable, warnings as errors, LangVersion latest
src/ClaudeRc.Core/
src/ClaudeRc.Host/
src/ClaudeRc.Tray/
tests/ClaudeRc.Core.Tests/
tests/ClaudeRc.Host.Tests/
tests/FakeClaude/
tests/fixtures/              real claude rc output captured in M0
tools/ClaudeRc.DebugCli/
installer/claude-rc-windows.iss
.github/workflows/ci.yml     dotnet test on push/PR (added in M1)
.github/workflows/release.yml  on tags v* (added in M5)
docs/PLAN.md, docs/m0-findings.md, docs/m1-decisions.md … docs/m3-decisions.md
```

## 4. Milestones

Run one at a time. Each milestone is run as one workflow:

1. **Implement**: one Opus 5.5 agent, tests first, using the guardrails above.
2. **Verify**: build with warnings as errors and run every test; check for leftover processes.
3. **Review**: one Opus 5.5 agent with no stake in the code reviews it against SPEC.md and this plan (the `code-review` skill: Standards + Spec).
4. **Fix**: the implementing agent fixes the confirmed findings; verify again.
5. **Commit and push** to `impl/v1`; short status to the user.

### M0: spike (about 45 min)

Throwaway code in `spike/`, deleted at the end of M1. Proves the risky parts
against the real `claude` 2.1.318 at `%USERPROFILE%\.local\bin\claude.exe`.

1. A minimal ConPTY launcher (WinExe, so no console window) that runs `claude rc --name "m0 spike & test"` in a scratch folder and writes all output to a file.
2. Capture claude rc's ready output verbatim into `tests/fixtures/claude-rc-ready.txt`. These lines are the negative cases for the prompt heuristic (SPEC §8).
3. Find the exact `projects[...]` key that claude writes into `.claude.json` for a Windows folder: path separators, drive-letter case, trailing separator.
4. In a scratch `git init` folder, confirm that presetting `hasTrustDialogAccepted` and `remoteControlSpawnMode: "same-dir"` stops both prompts. Without the presets, capture the spawn-mode prompt verbatim as a positive fixture.
5. Confirm that `--name` with spaces, quotes, `&` and `%` arrives unchanged. Check claude rc's output; if the name only shows in the Claude app, ask the user to look.
6. Writing `0x03` to the ConPTY input stops claude rc. Measure how long it takes.
7. Find out how an npm install launches. Install `@anthropic-ai/claude-code` with `npm install --prefix <scratch>` (not global), and find the `cli.js` path that `claude.cmd` resolves to.

Output: `docs/m0-findings.md`, the fixtures, and SPEC.md corrections for anything that differs.

### M1: Core + Host (about 3 h)

- **Core**: path normalisation (resolving junctions, case as on disk, duplicate basenames → `parent\name`); `folders.json` and `settings.json` stores with atomic replace; the supervisor state machine (all statuses in SPEC §4, backoff 5→300 s, reset after 10 min up, failing after 5 exits in 10 min, missing-folder re-check every 10 s for 5 min then every 60 s); finding claude (§6.1); the `.claude.json` preset writer (§6.2, using the key format from M0); the prompt heuristic (§8); IPC message types; folder and host logs with rolling.
- **FakeClaude**: behaviour chosen by `fakeclaude.json` in its working folder: run until stopped, exit with code N after T seconds, print a prompt and wait, print the M0 ready fixtures, record its arguments and environment to a file.
- **Host**: ConPTY session + a Job Object per session; stop sequence (Ctrl+C, wait 5 s, kill the job); the environment refreshed from the registry at each start; the headless VT emulator; 2 s prompt scan; the pipe server with every op and event in §10; the single-instance mutex; staggered start (1 s apart).
- **DebugCli**: `list`, `add`, `set`, `attach` (prints the stream), `input`.
- **CI**: `.github/workflows/ci.yml` running `dotnet test` on `windows-latest`.
- Every Core unit test and Host integration test listed in SPEC §17 (adoption tests come in M3).

### M2: Tray app (about 2 h)

Main window list (status dot, name and path, toggle, Show, Restart, row menu, prompt line); + Add folder (with the network-path warning); OFF and Remove confirmations; status bar with **Start host**; Settings window (claude path, notifications; start mode is shown but greyed until M4); console viewer (WebView2 + xterm.js served from embedded files, attach/replay/input/resize); toasts, once per transition, click opens the row; hide to tray on close; tray menu; log viewer. View-models are unit-tested against a fake pipe client. Done when an agent has driven it by hand against a real host with FakeClaude and checked it with screenshots.

### M3: Adoption (about 1 h)

Scan processes every 5 s: `claude.exe rc|remote-control …` and `node.exe … cli.js rc …`, owned by the same user, with the cwd read from the PEB and normalised. `external` rows, unsaved rows (toggle OFF; turning ON saves them), never a second session in a folder, Take over (close the window, then kill the tree, then a hosted start), and a hosted start when an ON folder's external session exits. Tested with a FakeClaude started outside the host.

### M4: Start modes (about 1 h)

The `\claude-rc-windows\Host` task: `logon` (no admin needed) and `startup` (UAC elevation, password from the Windows credential prompt, sent straight to Task Scheduler and never stored). Task settings from SPEC §9.3. Start host = run the task. Task health in the status bar, including `0x8007052E` → "re-enter your password". The portable zip's first run offers to create the `logon` task. Automated tests cover only `logon`, in a test task folder. `startup` mode is a manual check (section 5).

### M5: Release (about 1 h)

Install Inno Setup (`winget install JRSoftware.InnoSetup`; recorded in `~/CHANGES.md`). Per-user installer exactly as SPEC §14 (install, shortcut, `logon` task, start host and tray; uninstall turns every folder OFF, deletes the task, and asks whether to keep the data folder). Portable zip. `release.yml`: on `v*` tags, test, publish x64 and arm64 single-file self-contained, build installers, attach everything with SHA-256 sums. README: install, SmartScreen steps, usage, status updated. Then open a PR `impl/v1` → `main`.

## 5. What needs you (manual checks)

| When | What | Why it can't be automated |
|---|---|---|
| M0, maybe | Confirm the session name shows correctly in the Claude app | Only visible in your Claude account |
| M2 | Look at the main window and viewer once | Taste and real-use feel |
| M4 | Switch to `startup` mode: approve UAC, type your Windows password | Needs elevation and your password |
| M5 | Run the installer, reboot in both modes, take over a hand-started `claude rc`, uninstall | SPEC §17 manual release checks; needs reboots |

## 6. Total

About 9 hours of agent time, run one milestone at a time. About 18 Opus 5.5 agents in total (implement, review, fix per milestone), one running at a time.
