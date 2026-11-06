# M0 findings: claude rc on Windows

Spike run on 2026-11-06 against `claude` 2.1.318 (`%USERPROFILE%\.local\bin\claude.exe`,
native install) on Windows 11 Pro 26200, .NET SDK 10.0.403. Five real runs (A to E),
each in a throwaway folder under the session scratch directory, plus one verification
run (V) by the M0 reviewer.

| Run | Folder | Presets | `--name` | What it showed |
|---|---|---|---|---|
| A | fresh `git init` (`gitA`) | none | `m0 spike & test` | trust prompt, then spawn-mode prompt, answered by hand; ready; one Ctrl+C |
| B | fresh `git init` (`gitB`) | both | `m0 "q" & %PATH% 50%` | straight to ready; one Ctrl+C at 20 s |
| C | non-git (`NonGitC` on disk) launched as `...\RUNS\nongitc` | trust, under the on-disk-case key | `m0 spike & test` | trust prompt again (case mismatch); after answering, input ignored; killed by timeout |
| D | `gitB` again | both | `m0 spike & test` | one Ctrl+C 3 s after start, while connecting |
| E | `gitB` again | both | `m0 spike & test` | 30 s run with a desktop-wide window monitor; one Ctrl+C |
| V | non-git `NonGitC`, launched with its on-disk case | trust only, under the exact key | `m0 verify` | no prompt; reattached run C's environment and session; one Ctrl+C at 15 s |

Evidence: `tests/fixtures/*` (raw bytes and rendered screens; account ids redacted
with same-length `X` runs) and [`docs/m0-evidence/`](m0-evidence/) (per-run event logs with timings,
job process lists and window checks). The launcher was `spike/ConPtySpike/` and the helper scripts
`spike/m0-scripts/`; both were deleted at the end of M1 (they are in commit `077bd37`), and the
launcher's code lives on in `src/ClaudeRc.Host/Sessions/ConPtyProcess.cs`.

## 1. ConPTY launcher

`spike/ConPtySpike` (since removed; WinExe, `net10.0-windows`, CsWin32 0.3.367 with
`allowMarshaling: false`) creates a 120×40 pseudo-console and two anonymous pipes. It
starts the child `CREATE_SUSPENDED | EXTENDED_STARTUPINFO_PRESENT`, assigns it to a
Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, then resumes it. It builds the
command line from an argument list with the `PasteArguments` quoting rules, logs every
output chunk, polls a control folder for input bytes, and kills the job on timeout.
It works.

Implications for M1:
- **Set `STARTF_USESTDHANDLES` with null `hStd*` handles.** Without it, a parent whose
  own std handles are redirected passes them on to the child, and the child writes there
  instead of to the pseudo-console. The first sanity run lost all child output this way.
  Tests and Task Scheduler both start the host with redirected or odd std handles.
- CsWin32: `CreateJobObject` still binds to the `SafeFileHandle` overload even without
  marshalling. `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` (`0x00020016`) is not generated, so
  define it by hand. The remaining calls (`CreatePseudoConsole`, the attribute list,
  `CreateProcess`, `QueryInformationJobObject` with the process-ID list) are fine with
  raw pointers.
- The pseudo-console's own `conhost.exe` is a child of the launcher, not a member of the
  job. It exits when `ClosePseudoConsole` is called. The job does contain everything claude
  rc spawns: one child `claude.exe` per pre-created session, that session's `conhost.exe`,
  and short-lived `git.exe` processes.
- Output can arrive after the process has exited. In the sanity run the last chunk came
  0.5 s after exit. Keep reading until EOF, which comes after `ClosePseudoConsole`.

## 2. Ready screen and how claude rc draws

Fixtures:
- `tests/fixtures/claude-rc-ready.{raw,txt}`: git folder with presets (run B), up to just before Ctrl+C.
- `claude-rc-ready-nongit.{raw,txt}`: non-git folder (run C).
- `claude-rc-ready-after-prompts.{raw,txt}`: both prompts answered by hand, then ready (run A).
- `claude-rc-stopping.{raw,txt}`: ready, then one Ctrl+C, until exit (run B, complete).

Each `.txt` is the final 120×40 screen, rendered with pyte with trailing blank rows
removed. Before rendering, U+FE0E was removed, because pyte drops the rest of a line after
it.

The ready screen of a git folder with presets:

```
Spawn mode: same-dir (saved for this folder; press w to change)

·✔︎· Connected · gitB · master
    Capacity: 1/32 · New sessions will be created in the current directory
    m0 "q" & %PATH% 50%

Continue coding in the Claude mobile app or https://claude.ai/code?environment=env_01XXXX…
Or ask Claude to work in this folder from your projects on claude.ai
space to show QR code · w to toggle spawn mode
```

In a non-git folder there is no `Spawn mode:` line, the status line has no branch
(`·✔︎· Connected · nongitc`), and the last line is just `space to show QR code`.

Drawing behaviour:
- **No alternate screen** (no `?1049h`) and no scroll regions. Output starts with
  `ESC[2J ESC[m ESC[H`, then plain lines.
- claude rc redraws its status block in place about once a second, even when idle (57
  redraws in 70 s in run C). Each redraw hides the cursor, moves to an absolute
  position (`ESC[24;1H`), writes each row followed by `ESC[K`, and shows the cursor again.
  Log lines such as `[10:11:33] Shutting down 1 active session(s)…` are inserted above
  the block.
- Other sequences seen:
  - `ESC]0;<exe path>BEL`: sets the title.
  - OSC 8 hyperlinks: `ESC]8;id=…;https://claude.ai/code/session_…?from=cli ESC\` around the session-name line.
  - `?25l`/`?25h`.
  - `?1004h`/`?1004l` (focus events) and `?9001h`/`?9001l` (win32-input-mode). conhost sends these to ask the terminal for input modes.
- The session line first reads `Attached`, then the session name. The capacity line
  reads `Capacity: 0/32` until the pre-created session exists, then `1/32`.
- Timing (runs B, D and E): `Connected` and `Continue coding…` appear 1.5 to 2.0 s after
  start. The session line appears about 0.8 s later, or 17 s later on a folder's very
  first run (run B).

Implications for M1:
- The VT emulator needs: CUP, EL, ED, SGR, OSC 0 and OSC 8 (ignored or passed on), and
  DECSET `?25`, `?1004` and `?9001`, ignored.
- It must give zero width to variation selectors (U+FE0E/U+FE0F) and treat `✔` as narrow.
  Otherwise the status line goes wrong, as it did in pyte.
- Expect constant output from every running session (a redraw about every second). The
  `output` event stream and the scrollback must cope; the redraws do not add scrollback
  lines, because they overwrite in place.
- A `starting`→`running` marker is available: the line
  `Continue coding in the Claude mobile app` appears within about 2 s. The 30 s cap in
  SPEC §4 is plenty.

## 3. `.claude.json` project key

The key is **the working-directory string exactly as passed to claude, with every `\`
replaced by `/`**, with no other change.

| cwd passed to CreateProcess | key written |
|---|---|
| `C:\Users\Steven\…\scratchpad\m0\runs\gitA` | `C:/Users/Steven/…/scratchpad/m0/runs/gitA` |
| `c:\users\steven\…\scratchpad\m0\RUNS\nongitc` (on disk: `C:\Users\Steven\…\runs\NonGitC`) | `c:/users/steven/…/scratchpad/m0/RUNS/nongitc` |

- There is no case folding, not even of the drive letter, and no canonicalisation to the
  case on disk.
- **The lookup is case-sensitive.** In run C the trust preset stored under the on-disk-case
  key was ignored, the trust prompt appeared, and accepting it created a second key in the
  passed case. Evidence: [`docs/m0-evidence/claude-json-new-project-keys.json`](m0-evidence/claude-json-new-project-keys.json).
- No trailing separator. Not tested: a drive root (`C:\` is inferred to become `C:/`) and
  UNC paths. The user's file already contains `//Home/its/ITS Work/Clients`, written by an
  earlier claude session, which is consistent with `\\Home\its\…` → `//Home/its/…`.
- When the trust prompt is accepted by hand, claude creates a full default project object
  (`allowedTools`, `mcpServers`, …, `hasTrustDialogAccepted: true`). When the presets
  already exist, it adds nothing (run B left exactly the two preset keys).
- File format: 2-space indent, LF line endings, no BOM, no trailing newline, non-ASCII
  written unescaped. claude rewrites the whole file and keeps rolling copies in
  `~\.claude\backups\.claude.json.backup.<ms>`.

Implications for M1:

```csharp
// The host passes exactly `normalisedPath` (SPEC 3.2) as the session's working directory.
static string ClaudeProjectKey(string normalisedPath) => normalisedPath.Replace('\\', '/');
```

- Pin this with a test (`C:\Users\me\src\app` → `C:/Users/me/src/app`). The preset writer
  and the session launcher must use the same string.
- Keep key lookup ordinal (case-sensitive). Do not "find a case-insensitive match": a
  key in a different case is a different key to claude.
- To keep the diff small, write the JSON in the same style (2-space indent, LF, relaxed
  escaping). `System.Text.Json` needs `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` and
  `\n` newlines for that.

## 4. Prompts and presets

Without presets, a fresh git folder shows two prompts, in this order.

1. **Trust**, shown by `claude rc` itself (`tests/fixtures/prompt-trust.{raw,txt}`):
   ```
   Remote Control starts Claude Code sessions in this folder, driven from your other devices:
     <path>

   Quick safety check: is this a project you created or one you trust? Sessions started here can read, edit, and execute files, and this folder's project settings (hooks, MCP servers, permission rules, helper commands) apply to them.
   Security guide: https://code.claude.com/docs/en/security
   Trust <path>? [y/N]
   ```
   At 120 columns the path wraps, so the last screen row of the fixture is `\gitA? [y/N]`.
   Answering means typing `y` then Enter. The answer is echoed (`[y/N] y`).
2. **Spawn mode**, git folders only (`tests/fixtures/prompt-spawn-mode.{raw,txt}`):
   ```
   Remote Control is launching in spawn mode, which lets you start new sessions in this project from claude.ai/code or the
   Claude mobile app. Learn more: https://code.claude.com/docs/en/remote-control

   Spawn mode for this project:
     [1] same-dir — sessions share the current directory (default)
     [2] worktree — each session gets an isolated git worktree

   This can be changed later or explicitly set with --spawn=same-dir or --spawn=worktree.

   Choose [1/2] (default: 1):
   ```
   Answering with Enter writes `remoteControlSpawnMode: "same-dir"`.

**With both presets** (`hasTrustDialogAccepted: true`, `remoteControlSpawnMode: "same-dir"`)
under the exact key, a fresh git folder (run B) went straight to
`Spawn mode: same-dir (saved for this folder; press w to change)`, and `Connected`
appeared after 1.5 s with no prompt. A non-git folder needs only the trust key; no
spawn prompt and no spawn key appear (run V: trust key only, under the exact key, no
prompt; [`docs/m0-evidence/run-V.events.txt`](m0-evidence/run-V.events.txt)).

No other prompt was seen. Two caveats:
- This account already has the top-level keys `hasUsedRemoteControl` and
  `remoteDialogSeen`. An account that has never used Remote Control may get a one-time
  dialog. This is not verified; waiting detection should surface it.
- Not tested: whether trust is inherited from a trusted parent folder. The scratch
  folder's ancestor key `C:/Users/Steven` has `hasTrustDialogAccepted: false`.

The SPEC §8 heuristic, run in Python against these fixtures, flags the last line of both
prompt fixtures and of none of the ready or stopping fixtures (`spike/m0-scripts/analyze.py`, in commit `077bd37`; the same cases
are now pinned by `PromptDetectorTests` and `TerminalEmulatorTests`).

Implications for M1:
- **Positives:**
  - `Choose [1/2] (default: 1):`
  - `Trust <path>? [y/N]`
  - The trust prompt split across rows, since the path wraps.
- **Negatives:**
  - the last non-empty line of each of the four ready and stopping fixtures. Not every
    line: `claude-rc-ready-after-prompts.txt` still shows the answered spawn prompt
    `Choose [1/2] (default: 1):` (Enter is not echoed), which matches the heuristic on
    its own. Only the last line counts;
  - `Trust …? [y/N] y` (already answered);
  - `[10:11:33] Shutting down 1 active session(s)…`, a bracket that is not a choice.
- The heuristic must join soft-wrapped rows into one logical line. Otherwise, with a path
  of the wrong length, `[y/N]` could be split across two rows and never match. SPEC §8 now
  says this, so the emulator must record which rows soft-wrapped.

## 5. `--name` with spaces, quotes, `&` and `%`

Passed as one argument, `m0 "q" & %PATH% 50%` (command line
`claude.exe rc --name "m0 \"q\" & %PATH% 50%"`). claude rc shows it unchanged as the
session line, as OSC 8 link text, in `tests/fixtures/claude-rc-ready.txt`. `%PATH%` is
not expanded, because no shell is involved. In run A, `m0 spike & test` also arrived
unchanged.

**Restarts reattach the same session.** When claude rc stops it prints
`Environment preserved. Restart \`claude remote-control\` to reconnect existing sessions.`
and leaves `~\.claude\projects\<sanitised cwd>\bridge-pointer.json`
(`{"sessionId","environmentId","source":"standalone","pid","procStart"}`). Runs D and E in
the same folder passed `--name "m0 spike & test"` but reattached run B's environment and
session (same IDs) and still showed `m0 "q" & %PATH% 50%`. **`--name` names only a newly
created session.** Run V (cwd `…\runs\NonGitC`) also reattached run C's environment,
although run C used `…\RUNS\nongitc`: the pointer folder lookup is case-insensitive
(NTFS), while the `.claude.json` key lookup is not.

Implications for M1:
- Restarts after a crash or reboot do not create new sessions in the Claude app. Each
  folder keeps one environment and session, which is good.
- A later change to a row's display name (a second folder with the same basename turns
  `name` into `parent\name`) does not rename the existing session. This is documented in
  SPEC §6.3, and v1 does nothing about it.
- The FakeClaude test that checks `--name` arrives unchanged still holds. The host must
  build the command line with `PasteArguments` rules (`ProcessStartInfo.ArgumentList`
  semantics).

## 6. Ctrl+C (0x03 on the pseudo-console input)

| Run | When sent | Exit | Time to exit |
|---|---|---|---|
| A | 18 s after ready (prompts answered by hand) | 0 | 0.577 s |
| B | 20 s after start | 0 | 0.489 s |
| D | 3 s after start (still attaching) | 0 | 0.717 s |
| E | 30 s after start | 0 | 0.521 s |
| V | 15 s after start (non-git, trust preset) | 0 | 0.871 s |
| C | after a trust prompt answered by hand, non-git folder | none | no reaction to two 0x03 (25 s apart) or a space; job killed at 90 s |

With presets, **one 0x03 is enough**. There is no "press again" hint: claude rc prints
`Shutting down 1 active session(s)…` and `Environment preserved…`, ends its child
sessions, and exits with code 0 in 0.5 to 0.9 s, in git and non-git folders (run V). After that the job was empty.

In run C, after the hand-answered trust prompt, claude rc stopped reading its input
entirely. The status block kept redrawing, but keys did nothing. In run A, the first input
after the trust prompt (`1\r` for the spawn prompt) was also lost, and a second Enter
worked. `TerminateJobObject` killed both claude processes and the session conhost at once;
the exit code is the one passed in (here `0xDEAD`).

Implications for M1:
- Keep SPEC §6.4 as it is: one Ctrl+C, wait 5 s, terminate the job. The 5 s grace is
  roughly 7× the slowest stop seen. Step 3 is required, not theoretical.
- Exit code 0 after a host-requested stop is normal. Since it is the host's own stop, the
  supervisor does not count it as an exit.
- A session that exits on its own with code 0 is still an exit, and is restarted per §5.
- Viewer input: send keystrokes as plain bytes (`y`, `\r`, space, 0x03 all work). The host
  does not need to answer `?9001h`; conhost accepts plain VT input. The host's replay
  can drop `?9001h` and `?1004h`.

## 7. npm install launch

`npm install --prefix <scratch>\npm @anthropic-ai/claude-code` installed 2.1.318 in 34 s
(log: [`docs/m0-evidence/npm-install.txt`](m0-evidence/npm-install.txt)). npm 11.19.1 warned that the `postinstall` script
is "not yet covered by allowScripts", but the script still ran.

The package **no longer ships `cli.js`**:
- `package.json` has `"bin": {"claude": "bin/claude.exe"}` and no `main`.
- Its files are `bin/claude.exe`, `install.cjs`, `cli-wrapper.cjs` and `sdk-tools.d.ts`.
- The native binary comes from the optional dependency `@anthropic-ai/claude-code-win32-x64`
  (`claude.exe`, 342 MB). `install.cjs` hard-links it over the `bin/claude.exe`
  placeholder: `fsutil hardlink list` shows both paths, and the SHA-1 hashes match.
- `.bin\claude.cmd` ([`docs/m0-evidence/npm-claude.cmd.txt`](m0-evidence/npm-claude.cmd.txt)) is:
  ```bat
  "%dp0%\..\@anthropic-ai\claude-code\bin\claude.exe"   %*
  ```
  `claude.ps1` points at the same file. Both report `2.1.318 (Claude Code)`.
- If the postinstall is skipped (`--ignore-scripts`), `bin\claude.exe` stays a small
  placeholder stub. The documented fallback is `node cli-wrapper.cjs`, which `spawnSync`s
  the native binary.

claude rc was not run from the npm copy. It is the same binary as the native install.

Implications for M1 (SPEC §6.1, §6.3 and §7 updated):
- Resolving `claude.cmd`: read the shim, take the quoted target on its last command line,
  and replace `%dp0%` with the shim's own folder.
  - If the target is an `.exe`, run it directly with the argument list.
  - If it is the legacy `node … cli.js` form, run `node.exe` with the resolved `cli.js`.
  - If the `.exe` is under 4 KB (the placeholder stub), show "npm install of Claude Code is
    incomplete (postinstall did not run)".
- FakeClaude tests for this need a fake `claude.cmd` of each shape.
- M3 adoption: current npm installs appear as `claude.exe rc …` under `node_modules`, so
  the `claude.exe` rule covers them. The `node.exe … cli.js` rule only matters for old
  versions.

## No console window

The spike is a WinExe launched from PowerShell. claude rc and its children ran for 107 s
(run A), 90 s (run C) and 30 s (run E):
- The spike's every-2-seconds check found no visible top-level window owned by any job
  process.
- In run E, a desktop-wide monitor ([`docs/m0-evidence/run-E.windows.txt`](m0-evidence/run-E.windows.txt), every 500 ms, all
  visible top-level windows) found one new window. It was an unrelated `msedge.exe` popup
  from a process started the day before. No conhost or Windows Terminal window appeared,
  even though claude rc starts child consoles for its sessions.
- Sessions stayed connected throughout.

Not covered: session 0 (`startup` mode), which belongs to M4.

## Left behind by M0 (user state)

- **Backup:** `%USERPROFILE%\.claude.json` was copied before the first real run to
  `C:\Users\Steven\AppData\Local\Temp\claude\df26003eaca5f50f\f2d7163b-5b2c-5af6-9458-d70f3598d9aa\scratchpad\m0\claude.json.backup`.
  It was not restored, because other claude sessions kept writing the live file.
- **New project keys in the live file:** four keys under `…/scratchpad/m0/runs/`
  (gitA, gitB, NonGitC, and `c:/users/…/RUNS/nongitc`). They are harmless, and can be
  deleted by hand.
- **Folders:** `~\.claude\projects\` has a folder for each of the three scratch folders.
- **Account:** three Remote Control environments, each with one session, on the user's
  account:
  - "m0 spike & test" for gitA;
  - `m0 "q" & %PATH% 50%` for gitB;
  - "m0 spike & test" for nongitc.

  They can be archived in the Claude app. Run V reattached the nongitc environment, so it
  added no environment. It left the `…/runs/NonGitC` key as it was (trust only) and
  created no new folder in `~\.claude\projects\`.

## Not tested (open)

- A folder that is a subfolder of a git repository: whether claude keys
  `remoteControlSpawnMode` (and trust) by the folder or by the repository root. If it
  uses the root, the preset under the folder key will not stop the spawn prompt. Waiting
  detection would show it; check it by hand in M1.
- A working directory passed with a trailing `\`. The normaliser (SPEC 3.2) must strip
  it (except for a drive root) so the key never ends in `/`.
