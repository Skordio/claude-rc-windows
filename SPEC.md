# claude-rc-windows: specification

Status: v1 spec, agreed 2026-11-06. Implementation in progress (see docs/PLAN.md).

claude-rc-windows is a Windows tray app that keeps Claude Code's Remote
Control server (`claude rc`) running in folders you choose. It brings them
back after a restart and keeps them running until you turn them off in the
app. It is the Windows counterpart of the Linux "Claude RC" screen in
[Skordio/control-panel](https://github.com/Skordio/control-panel) (issue #2),
without the phone/web control panel: everything is local.

Unofficial; not affiliated with or endorsed by Anthropic.

---

## 1. Goals and non-goals

**Goals**
- Pick folders with the normal Windows folder dialog; each appears in a list
  with an ON/OFF toggle.
- ON = `claude rc` runs in that folder now, after every restart, and is
  restarted if it exits, until it is turned OFF in the app.
- Nothing ever stops silently at a prompt nobody can see: known prompts are
  pre-answered, unknown ones are surfaced as "waiting for input".
- You can always look at, and type into, any folder's console.

**Non-goals (v1)**
- No remote control of the app itself (no web UI, no phone app, no network
  listener). The only remote part is `claude rc`'s own connection to Claude.
- No per-folder options (spawn mode, permission mode, custom name).
- No auto-update (no update check either; see 15).
- No macOS/Linux build.
- No code signing (see 14).

## 2. Terms

| Term | Meaning |
|---|---|
| **Folder** | An entry in the list: an absolute path plus ON/OFF. |
| **Session** | One `claude rc` process (and its children) for a folder. |
| **Host** | `ClaudeRcHost.exe`: headless background process that owns all sessions. |
| **Tray app** | `ClaudeRc.exe`: tray icon + main window. Talks to the host; owns no sessions. |
| **Start mode** | When the host starts: `logon` (default) or `startup` (at boot, before logon). |
| **Adopted** | A `claude rc` the user started themselves (not owned by the host). |

## 3. User experience

### 3.1 Main window
A single list, one row per folder:
- **Status dot + text** (see 4): green running, yellow starting/waiting/
  restarting, red failing/missing, grey off, blue "running (started outside
  the app)". A `restarting` row counts down "restarting in Ns" between host
  updates and shows the last exit code; codes outside 0-255 also show in hex
  (NTSTATUS, e.g. `-1073741510 (0xC000013A)`).
- **Folder name** (basename; `parent\name` if two share a basename, compared
  case-insensitively; if `parent\name` still collides, more leading folders are
  added until the names differ) and the full path below it.
- **ON/OFF toggle.** Turning OFF a running folder (`starting`, `running` or
  `waiting`: a hosted session is alive) asks for confirmation ("stops the
  running claude rc session; it won't come back at logon/boot").
- **Show**: opens the console viewer (3.3). Enabled only while a hosted
  session is alive (`starting`, `running`, `waiting`): disabled for adopted
  rows, and whenever nothing runs (the host has no console to attach to then).
- **Restart**: shown when status is `failing` or `restarting` (it stopped
  unexpectedly, or a start failed, and waits out the delay; section 4 has no
  separate "stopped unexpectedly" status). Also in the row menu any time the
  folder is ON, except for an adopted row.
- **Take over**: adopted rows only (see 7); confirms first.
- **Row menu (…)**: Restart, Open folder in Explorer, Open in Windows Terminal
  (`wt.exe -d <path>`; Explorer if Windows Terminal is not installed), View
  log, Remove (only when OFF and saved; confirms first).
- A prompt line under the row when `waiting`: the prompt text and "Show to
  answer it".
- The row's `error` (why it cannot start, 6.1, 6.2) in red. A `failing` row
  also shows its last console lines (5.2).

Toolbar: **+ Add folder**, **Settings**. Status bar: host connected / not
running (with **Start host** button), start mode, claude.exe path and version.
While no host answers, the list area says so and points at **Start host**.

Closing the window hides it to the tray; sessions keep running. Tray menu:
Open, Add folder, Settings, Quit tray (sessions keep running: the host is a
separate process). There is no "stop everything" in the tray; turn folders
OFF instead. One tray app runs per user session; starting it again shows the
running one's window.

### 3.2 Adding a folder
**+ Add folder** opens the native Windows "Select folder" dialog (any local
drive). Network or mapped-drive paths are allowed with a warning that they may
not exist yet at boot. The path is normalised (full path, resolved symlinks or
junctions, trailing separator removed, case as on disk). Adding an existing
folder just turns it ON. A new folder is added as ON and starts immediately.

### 3.3 Console viewer
A window per folder showing that session's live console (WebView2 +
xterm.js), rendered from the host's stream. Opening it first replays the
current screen and the scrollback (last 5,000 lines), then streams live.
Keyboard input is sent to the session (to answer a prompt, press `w`/space
in claude rc, etc.). Closing the viewer never affects the session. Resize is
forwarded to the pseudo-console.

- **Show** on a folder whose viewer is open brings that window to the front.
- The page and a pinned, vendored xterm.js are embedded in `ClaudeRc.exe` and
  served to WebView2 by the app itself; the viewer makes no network request.
  WebView2's own checks are off too: SmartScreen's reputation check of the
  page's address (`IsReputationCheckingRequired`), and the browser's
  background traffic (`--disable-background-networking`).
  WebView2's data lives in `%LOCALAPPDATA%\claude-rc-windows\webview2` (11).
- While no hosted session runs (OFF, restarting, failing, missing, external,
  host not running) a banner above the terminal says why and the last screen
  stays. The viewer attaches by itself when a session runs again, also after
  the host comes back. A refused attach is shown in the banner and tried
  again on the folder's next status change.
- Ctrl+C copies when text is selected and is sent as Ctrl+C otherwise;
  Ctrl+V pastes (so Ctrl+V itself is not sent). A long paste goes to the host
  in pieces of at most 32,768 characters.
- Without the Microsoft Edge WebView2 Runtime the window says it is missing
  and where to get it.

### 3.4 Settings
- **Start mode**: At logon (default) / At startup (before logon). Switching
  re-creates the scheduled task (see 9); `startup` needs admin approval (UAC)
  and the user's Windows password once.
- **claude.exe path**: auto-detected (see 6.1); override field + Browse. The
  override must be a full path to a `.exe` or a `claude.cmd` npm shim (6.1).
- **Notifications**: on/off (default on).
- Read-only info: data folder, log folder, host version, task status.

The settings live in the host (`settings.get` / `settings.set`, 10), so they
can be changed only while the host is connected; otherwise the window says
so. The open window follows the connection: it turns read-only with that note
when the host goes, and loads the settings again when the host comes back
(fields the user has already changed keep what they typed). The tray checks
the override (full path, `.exe` or `.cmd`) before sending it; the host checks
it again (6.1).

### 3.5 Notifications
Windows toast when a folder becomes `failing`, `waiting`, or `missing`
(once per transition, not repeated while it stays there). Clicking it opens
the main window at that row. Off via settings.

The host sends the `toast` event (10) only while notifications are on; the
tray checks the setting it read too, and shows a status for a folder only
once until the folder's row has left that status. The toast's title names the
status ("claude rc is failing", "Waiting for input", "Folder missing"), its
text is the host's. A newer toast of a folder replaces its older one in the
Action Center. The clicked row is scrolled into view and highlighted for a few
seconds. A click that starts the tray arrives before the rows are loaded; the
row is then focused as soon as it appears (within 10 s). The app is unpackaged, so the toast library registers it for toasts
under `HKCU\Software\Classes` on first start; `ClaudeRc.exe
--unregister-notifications` removes that registration (the uninstaller runs
it, 14).

## 4. Folder status

| Status | Meaning | ON? |
|---|---|---|
| `off` | Turned off. | no |
| `starting` | Process launched, not yet seen ready (max 30 s, then `running`). | yes |
| `running` | Session process alive and not at a prompt. | yes |
| `waiting` | Alive, but its console ends at an unanswered prompt (see 8). `prompt` holds the text. | yes |
| `restarting` | Exited (or a start failed, see 6.1); waiting out the backoff before the next start. Shows "restarting in Ns" and the last exit code. | yes |
| `failing` | Too many crashes (see 5); retries stopped until **Restart**. | yes |
| `missing` | Folder does not exist (e.g. drive not mounted). Retried (see 5.3). | yes |
| `external` | A `claude rc` started outside the app is running in this folder (see 7). | either |

ON/OFF is the user's desired state and is what gets saved. Status is computed
by the host.

## 5. Supervision

### 5.1 Restart with backoff
When an ON folder's session exits for any reason other than an OFF/Remove/
Take over/Quit request:
- record exit code and time in the folder log;
- wait the backoff, then start again. Backoff: 5 s, 10 s, 20 s, 40 s, 80 s,
  160 s, then 300 s for every later attempt;
- a session that stays up 10 minutes resets the backoff and crash count.

### 5.2 Failing
5 exits within any 10 minutes, counted per folder → `failing`: no more
automatic starts, toast (if enabled), and the row shows the last exit code and
the last 20 console lines. **Restart** clears the count and starts at once.
Restarting the host also clears it (a reboot is a fresh start).

### 5.3 Missing folders
If an ON folder's path doesn't exist when it should start: status `missing`,
re-check every 10 s for 5 minutes after host start, then every 60 s. Starts
as soon as the path appears. Never counts as a crash.

### 5.4 Host start
On start the host loads the folder list and starts every ON folder, staggered
by 1 s. A folder that already has an `external` claude rc running is not
started (see 7).

## 6. Starting and stopping a session

### 6.1 Finding claude
In order: the settings override; `%USERPROFILE%\.local\bin\claude.exe`
(native installer); `claude.exe` on PATH; `claude.cmd` on PATH (npm global
install: current versions run a native `claude.exe` inside `node_modules`,
older ones run `cli.js` under node). If none: every ON folder shows an error row
"claude not found: install Claude Code or set its path in Settings", and the
host re-checks every 60 s. A settings override that does not exist is an error
("claude path from Settings not found: …"); the host does not silently fall
back to the other locations. The override must be a `.exe` (run directly) or a
`.cmd` (resolved like the npm shim below); anything else, such as a `.bat`, is
refused ("claude path from Settings must be claude.exe or claude.cmd: …")
because Windows would run it through `cmd.exe` and parse the folder name (6.3).
The detected path and `claude --version` show in
the status bar.

Any failed start (claude not found, `.claude.json` invalid (6.2), launch error)
puts the row in `restarting` with the message in the row's `error` field and
retries after 60 s. It never counts as a crash (5.2).

### 6.2 Before each start: preset Claude's per-folder prompts
Nobody is watching the console, so before every start the host makes sure
`%USERPROFILE%\.claude.json` has, under `projects["<folder path as Claude
keys it>"]`:
- `hasTrustDialogAccepted: true` (the folder-trust prompt, which `claude rc`
  shows itself: "Trust <path>? [y/N]");
- `remoteControlSpawnMode: "same-dir"`, **only if the key is absent**. In a
  git repo without a saved choice, `claude rc` otherwise stops at "Spawn mode
  for this project: [1] same-dir [2] worktree, Choose [1/2]". A saved
  `"worktree"` (or a choice made with `w` inside claude rc) is kept.

Rules: touch only these two keys; read-modify-write with an atomic replace
(write temp file in the same folder, then `File.Replace`); skip the write if
nothing changes; if the file isn't valid JSON, don't touch it and show the
error on the row. The project key (confirmed in M0 against claude 2.1.318,
see `docs/m0-findings.md`) is the working-directory string exactly as passed
to claude with every `\` replaced by `/` (`C:/Users/me/src/app`). There is no
case folding, not even of the drive letter, and claude looks keys up
case-sensitively, so the host passes the normalised path (3.2) as the working
directory and builds the key from that same string. Pinned by a test.

Do not pass `--spawn` on the command line instead: it would override a
folder's saved choice.

### 6.3 The command
`claude rc --name <window name>` with the working directory set to the
folder, inside a pseudo-console (ConPTY) owned by the host, initial size
120×40. `<window name>` = the row's display name (3.1). The arguments are
passed as an argument list (`ProcessStartInfo.ArgumentList` semantics for
`claude.exe`; for `claude.cmd`, run the program the shim launches directly
rather than going through `cmd.exe`, so no folder name is ever parsed by a
shell: in current npm installs that is
`node_modules\@anthropic-ai\claude-code\bin\claude.exe`; an older shim that
runs `node … cli.js` is run as `node` with the resolved `cli.js` path).
`--name` names only a newly created session: claude rc keeps a per-folder
pointer (`~\.claude\projects\<folder>\bridge-pointer.json`) and on a restart
reattaches that session, which keeps the name it was created with. The environment is the host's user environment (user + system
variables, refreshed from the registry at each start so PATH changes take
effect without a reboot).

Each session gets its own Job Object (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`),
so all its child processes belong to it and die with it.

### 6.4 Stopping (OFF, Remove, Take over, Quit host)
1. Send Ctrl+C to the pseudo-console.
2. Wait up to 5 s for the process to exit.
3. Terminate the Job Object (kills the whole tree).

In M0 claude rc exited with code 0 about 0.5 to 0.9 s after one Ctrl+C. It
can also stop reading its input (seen after a trust prompt was answered by
hand), and then Ctrl+C does nothing, so step 3 is always needed.

### 6.5 If the host dies
The Job Objects close with it, so every session dies too (no orphans the host
can't reattach to). The scheduled task restarts the host (9.3), which starts
every ON folder again.

## 7. Adopted (external) sessions

The host scans running processes every 5 s for a `claude rc` it doesn't own:
- `claude.exe` whose arguments start with `rc` or `remote-control` (any
  options after), or `node.exe` running Claude's `cli.js` with those
  arguments (older npm installs; current ones run `claude.exe`);
- owned by the same user;
- its current directory (read from the process with
  `NtQueryInformationProcess` → PEB → `ProcessParameters.CurrentDirectory`)
  equals a folder path, compared case-insensitively after normalisation.

A match shows the row as `external` ("running, started outside the app") and
the host never starts a second session for that folder. If the folder isn't in
the list, it is listed anyway as an unsaved row with the toggle OFF; turning it
ON saves it (and leaves the external session running; it is taken over only by
**Take over**).

**Take over** (confirm: "stops the claude rc you started yourself and
restarts it inside the app"): stop the external process tree (Ctrl+C is not
possible from outside, so close its main window / terminate the tree), then
start a hosted session as in 6. Its console, supervision and restart-at-boot
then apply.

If an ON folder's external session exits, the host starts its own on the next
scan.

## 8. Waiting-for-input detection

The host keeps a headless terminal emulator per session, fed from the
ConPTY output, so it always knows the current screen. Every 2 s, for each
running session, look at the last non-empty line of the screen, with
soft-wrapped rows joined back into one line (the trust prompt's path often
wraps at 120 columns and could split `[y/N]`). It is a prompt if it:
- contains a bracketed choice: `[1/2]`, `[1/2/3]` (any `/`-separated list of
  numbers in square brackets), `[y/N]`, `[Y/n]`, `[yes/no]`, `(y/n)`,
  `(yes/no)` (case-insensitive), and
- ends with `:`, `?`, `]` or `)`, meaning nothing has been typed after it.

Then status = `waiting`, `prompt` = that line (trimmed, max 200 chars; a
longer line keeps its start and its end around `…`, so the choice stays
visible). Keep
the heuristic conservative: claude rc's normal ready output ("Continue coding
in the Claude mobile app or …", "Capacity: 0/32 · …", "space to show QR code",
"Spawn mode: same-dir (saved for this folder; press w to change)") must never
match. These lines are fixtures in the tests (17).

Adopted sessions have no console to read, so they never show `waiting`.

## 9. Start modes

There is always exactly one scheduled task, `\claude-rc-windows\Host`, that
runs `ClaudeRcHost.exe` as the user.

### 9.1 `logon` (default)
Trigger "At log on" of this user, "Run only when user is logged on". Set up
by the installer (no admin needed). The host runs in the user's session and
ends at logoff (sessions with it).

### 9.2 `startup`
Trigger "At startup", "Run whether user is logged on or not", run as the
user with the password stored by Task Scheduler, "Do not store password" off
(it needs network and profile access), user profile loaded. Creating it needs
elevation (UAC) and the user's password, entered in a Windows credential
prompt opened by the tray app; the app never stores or logs it. The host runs
in session 0 (no desktop); ConPTY works there, and the tray app reaches it
over the named pipe (10). If the Windows password changes, Task Scheduler
can't start the task: the tray app detects the task's last result
(`0x8007052E` logon failure) and shows "re-enter your password".

### 9.3 Task settings (both modes)
Start immediately if a scheduled start was missed; don't stop on battery;
no execution time limit; "If the task fails, restart every 1 minute, up to
999 times"; "If the task is already running: do not start a new instance".

### 9.4 Single host
The host takes a global named mutex `Global\claude-rc-windows-host-<user
SID>`; a second instance exits at once. The tray app's **Start host** button
runs the scheduled task (it never starts the host as its own child).

### 9.5 Sleep, hibernate and network changes
After resume the host does nothing special: `claude rc` reconnects on its own
or exits, and exits are handled by 5. Folders on drives that vanished show
`missing` until they come back.

## 10. Host ⇄ tray IPC

Named pipe `\\.\pipe\claude-rc-windows-<user SID>`, ACL: the user's SID
only (plus SYSTEM), so other accounts can't connect. Newline-delimited JSON,
UTF-8. Every message has `"v": 1`. A version mismatch closes the connection
with an error the tray app shows ("host and app versions differ: restart the
host").

Requests (tray → host), each with an `id`; the host replies
`{"id", "ok", "error"?, ...}`:

| `op` | Fields | Effect |
|---|---|---|
| `list` | | Returns all rows (below). |
| `add` | `path` | Save as ON and start. |
| `set` | `path`, `on` | Toggle. |
| `restart` | `path` | Clear crash count, start now. |
| `remove` | `path` | Only when OFF. |
| `takeover` | `path` | See 7. |
| `attach` | `path`, `cols`, `rows` | Replay screen and scrollback, then stream output events. |
| `input` | `path`, `data` | Send keystrokes to the pseudo-console. |
| `resize` | `path`, `cols`, `rows` | |
| `detach` | `path` | Stop streaming. |
| `log` | `path`, `lines` | Return the last N lines of the folder log. |
| `settings.get` / `settings.set` | | claude path, notifications (start mode is changed by the tray app through Task Scheduler, then the host reloads). |

Events (host → tray, no `id`), named by an `event` field: `rows` (pushed on
any status change), `output` (`path`, base64 bytes in `data`, while attached),
`toast` (`path`, `status`, `text`).

Row: `{path, name, on, saved, status, prompt?, exit_code?, restart_in_s?,
crashes_10m, started_at?, pid?, error?, last_lines?}`. Optional fields are left out when
empty. `error` holds why the folder cannot be started (6.1, 6.2); the spec
asks for that error "on the row", so the row needs a field for it. For the
same reason `last_lines` holds the last 20 console lines of the session that
exited last, sent only while the row is `failing` (5.2).

Payloads: `list` replies with `rows`; `log` with `lines`; `settings.get` with
`settings` (`{claude_path, notifications}`, `claude_path` always present, null
= auto-detect) and `info` (`{host_version, data_dir, log_dir, claude_path?,
claude_version?, claude_error?}`, the read-only facts of 3.4 and the status
bar). `settings.set` sends both settings. `input.data` is text (the viewer's
keystrokes), written to the pseudo-console as UTF-8. The message types and
their encoding live in `ClaudeRc.Core.Ipc` and are pinned by tests.

The tray app is purely a client: if the host isn't running it shows "Host not
running" and **Start host**.

## 11. Data files

| File | Content |
|---|---|
| `%APPDATA%\claude-rc-windows\folders.json` | `{"v":1,"folders":[{"path","on"}]}`. Written by the host only, atomic replace. |
| `%APPDATA%\claude-rc-windows\settings.json` | `{"v":1,"claude_path":null,"notifications":true}` |
| `%LOCALAPPDATA%\claude-rc-windows\logs\host.log` | Host events, rolled at 5 MB, keep 3. |
| `%LOCALAPPDATA%\claude-rc-windows\logs\folders\<hash>.log` | Per-folder: starts, exits, exit codes, restarts, takeovers, prompt detections (not console output). Rolled at 1 MB, keep 2. |
| `%LOCALAPPDATA%\claude-rc-windows\webview2\` | The console viewer's WebView2 user data (browser cache and state; no console output is saved by the app). |

`<hash>` = first 16 hex chars (lower case) of SHA-256 of the UTF-8 normalised
path; the first line of each folder log file is `# folder: <path>`. "Keep N"
means N files in total, the current one included: a rolled file becomes
`<name>.1`, older ones `<name>.2`, …. Each entry is one line,
`<local ISO-8601 time> <message>`.

## 12. Security

- No network listener; the named pipe accepts only the user's account, and
  only on this machine: it is created with `PIPE_REJECT_REMOTE_CLIENTS`, and its
  DACL also denies the NETWORK SID, so a client over SMB
  (`\\machine\pipe\…`) is refused even with the user's own credentials.
- The host only ever runs claude (6.3) with arguments it builds itself. The
  tray app sends paths, never command text.
- Paths are normalised and must be existing directories when added.
- The Windows password for `startup` mode goes straight from the credential
  prompt to Task Scheduler. It is never written to disk, logged or sent over
  the pipe.
- `~/.claude.json` changes are limited to the two keys in 6.2.

## 13. Architecture

```
src/
  ClaudeRc.Core/      folder list, settings, supervisor state machine,
                      backoff, prompt heuristic, ~/.claude.json presets,
                      path normalisation, IPC message types (no Windows UI)
  ClaudeRc.Host/      ClaudeRcHost.exe: ConPTY sessions, Job Objects,
                      headless VT emulator, process scan, pipe server
  ClaudeRc.Tray/      ClaudeRc.exe: WPF tray + main window, folder dialog,
                      WebView2 + xterm.js viewer, toasts, Task Scheduler setup
tests/
  ClaudeRc.Core.Tests/
  ClaudeRc.Host.Tests/   uses tests/FakeClaude (a tiny console exe)
installer/            Inno Setup script
```

.NET 10 (LTS; .NET 8 support ends 2026-11-10), C#, nullable enabled. Self-contained single-file publishes for
`win-x64` and `win-arm64`. Libraries: TaskScheduler (dahall) for tasks,
Microsoft.Web.WebView2 for the viewer, a headless VT parser (pick in M1:
e.g. `XtermSharp` or `Microsoft.Terminal` VT parser port) for 8 and replay.
Supported OS: Windows 10 1809+ (ConPTY) and Windows 11.

## 14. Packaging and releases

- **Installer** (Inno Setup, per-user, no admin): installs to
  `%LOCALAPPDATA%\Programs\claude-rc-windows`, Start menu shortcut, creates
  the `logon` task, starts the host and tray app. Uninstall: turns every
  folder OFF (stops sessions), deletes the task, runs `ClaudeRc.exe
  --unregister-notifications` (3.5), removes the program and
  `%LOCALAPPDATA%\claude-rc-windows\webview2`; asks
  whether to keep `%APPDATA%\claude-rc-windows`. Never touches `.claude.json`.
- **Portable zip**: same exes; first run of the tray app offers to create the
  `logon` task.
- Built by GitHub Actions on tags `v*`: test, publish x64 and arm64, build the
  installers, attach everything to the GitHub Release with SHA-256 sums.
- Unsigned in v1. The README explains SmartScreen's "Windows protected your
  PC" → **More info** → **Run anyway**. Signing (e.g. Azure Trusted Signing)
  is a later addition.

## 15. Explicitly left out of v1

Update check, per-folder options, auto-update, signing, multiple users per
host, running folders as another account, a CLI. Each is a separate issue
when wanted.

## 16. Milestones

- **M0, spike** (no UI): prove `claude rc` runs in a ConPTY from a test exe;
  confirm on Windows: the `.claude.json` project key format, that the two
  presets stop both prompts, that `--name` works, how `claude.cmd` installs
  launch, and that the session survives with no console window visible.
- **M1, Core + Host**: folder list, supervisor with backoff/failing/missing,
  presets, ConPTY sessions + Job Objects, VT emulator, prompt detection,
  pipe server. Driven by tests and a debug CLI client.
- **M2, Tray app**: list, add/toggle/remove/restart, settings, viewer,
  toasts, row menu, log viewer.
- **M3, Adoption**: process scan + PEB cwd, `external` rows, Take over.
- **M4, Start modes**: `logon` and `startup` tasks, password flow, task
  health in the status bar.
- **M5, Release**: installer, portable zip, CI release pipeline, README.

## 17. Test plan

- **Core (unit)**: backoff schedule and reset after 10 min; failing after 5
  exits in 10 min; missing-folder retry timing (fake clock); preset writer
  (adds both keys, keeps an existing `worktree`, touches nothing else, no write
  when unchanged, refuses invalid JSON); path normalisation and duplicate
  basenames; prompt heuristic with the real claude rc ready lines as
  negatives and `Choose [1/2] (default: 1):` and `Trust <path>? [y/N]` as
  positives (fixtures in `tests/fixtures/`, captured in M0).
- **Host (integration)** with `FakeClaude.exe`, whose behaviour is chosen by
  a file in its working folder: run until stopped, exit with code N after T
  seconds, print a prompt and wait for input, print ready output, record its
  arguments. Covers start/stop, Ctrl+C then kill, Job Object cleanup (no
  leftover processes), restart and failing, waiting detection, attach/replay,
  input reaching the process, `--name` with spaces/quotes/`&`/`%` reaching
  the process unchanged, and adopting an externally started FakeClaude.
- **Manual on real Windows (each release)**: install, add a git repo folder
  (no prompts), reboot in both modes, toggle OFF, take over a hand-started
  `claude rc`, uninstall.
- CI: `windows-latest`, `dotnet test` on every push and PR.
