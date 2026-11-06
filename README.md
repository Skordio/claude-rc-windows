# claude-rc-windows

A Windows tray app that keeps Claude Code's Remote Control server
(`claude rc`) running in the folders you choose: pick a folder, flip it ON,
and it starts now, comes back after every restart (at logon, or at boot
before anyone logs on), and is restarted if it exits, until you turn it OFF.

Unofficial; not affiliated with or endorsed by Anthropic.

**Status:** v1 feature-complete (the core, the host, the tray app, adoption of hand-started
sessions, both start modes, the installer, portable zips and the release workflow). Not yet
released: the manual release checks (install, reboot in both modes, take over, uninstall) are
still to be done. See [SPEC.md](SPEC.md) and [docs/PLAN.md](docs/PLAN.md).

## How it works

Two programs, side by side in one folder:

- **ClaudeRcHost.exe**, the host: a background process with no window. It owns every
  `claude rc` session, each in its own hidden console (ConPTY), restarts sessions that exit
  (5 s, 10 s, 20 s, ... up to 5 minutes apart; after 5 exits in 10 minutes it stops trying and
  shows `failing`), and answers obvious prompts before they appear (it marks the folder as trusted
  and chooses the `same-dir` spawn mode in `~/.claude.json`, only if you haven't chosen one).
  A Windows scheduled task, `\claude-rc-windows\Host`, starts it.
- **ClaudeRc.exe**, the tray app: the list of folders, their consoles, settings and
  notifications. It only talks to the host; closing or quitting it never stops a session.

## Requirements

- Windows 10 version 1809 or later, or Windows 11 (x64 or Arm64).
- [Claude Code](https://docs.anthropic.com/en/docs/claude-code) installed and signed in for
  your Windows account (`claude` works in a terminal). The app finds `claude.exe` in
  `%USERPROFILE%\.local\bin` (the native installer), on `PATH`, or an npm install's `claude.cmd`;
  you can also set the path in Settings.
- The Microsoft Edge WebView2 Runtime for the console viewer (part of current Windows; the
  viewer says where to get it if it is missing). Everything else works without it.

## Install

Download from the [Releases](https://github.com/Skordio/claude-rc-windows/releases) page. Each
release has an installer and a portable zip per architecture (`win-x64`, or `win-arm64` for
Windows on Arm), and `SHA256SUMS.txt`.

**Installer** (`claude-rc-windows-<version>-win-x64-setup.exe`): per-user, no administrator
rights. It installs to `%LOCALAPPDATA%\Programs\claude-rc-windows`, adds a Start menu shortcut,
creates the scheduled task (start at logon), starts the host and opens the app. Running a newer
installer upgrades in place: it closes the app and stops the host (your ON folders stop), replaces
the files, and starts them again; a start-at-startup setup is kept (starting its task may need
administrator approval, UAC, but not your password). Run the installer normally, not "as
administrator": Setup stops if it was started elevated, because the scheduled task would then be
made with administrator rights. A silent install (`/SILENT` or `/VERYSILENT`) shows no error
messages; they go to `%LOCALAPPDATA%\claude-rc-windows\logs\setup.log`.

**Portable** (`claude-rc-windows-<version>-win-x64-portable.zip`): unzip anywhere you can keep
it and run `ClaudeRc.exe`. The first time (and each time it finds no scheduled task) it offers
to create the start-at-logon task for the `ClaudeRcHost.exe` next to it; no administrator rights
needed. If you move the folder later, delete `\claude-rc-windows\Host` in Task Scheduler and start
`ClaudeRc.exe` again: it offers to create the task for the new place.

### "Windows protected your PC"

The files are not code-signed yet, so SmartScreen may stop the installer or `ClaudeRc.exe` the
first time. Click **More info**, then **Run anyway**. You can check a download against
`SHA256SUMS.txt` first: `Get-FileHash .\claude-rc-windows-<version>-win-x64-setup.exe`.

## Use

- **+ Add folder**: pick a folder in the Windows folder dialog. It is added ON and its
  `claude rc` starts at once. A network or mapped drive works too, with a warning that it may not
  be there at boot.
- **ON/OFF toggle**: ON keeps `claude rc` running there, now and after every restart. Turning a
  running folder OFF asks first, then stops its session; it won't come back until you turn it ON.
- **Status**: green running, yellow starting/waiting/restarting, red failing/missing, grey off,
  blue running (started outside the app). A folder waiting at a prompt shows the prompt's text,
  and while the app runs you get a notification for failing, waiting and missing folders
  (Settings can turn them off).
- **Show**: opens the folder's live console. You can type into it (answer a prompt, press `w` or
  space in `claude rc`); Ctrl+C copies a selection and is sent as Ctrl+C otherwise, Ctrl+V pastes.
  Closing the console never affects the session.
- **Restart** (on a failing or restarting row, and in the row's `…` menu), **Open folder in
  Explorer**, **Open in Windows Terminal**, **View log**, **Remove** (only when OFF).
- **Take over**: a `claude rc` you started yourself in a terminal shows up as "running (started
  outside the app)". The app never touches it, except when you press **Take over**: that stops it
  and starts it again inside the app, so it gets a console and is kept running.
- Closing the window hides it to the tray; sessions keep running. **Quit tray** in the tray menu
  closes only the app. The app is not started at logon by itself (the host is): open it from the
  Start menu when you want the list, the consoles or the notifications.

### Start modes (Settings)

- **At logon** (default): the host starts when you sign in to Windows and stops when you sign out.
- **At startup (before logon)**: the host starts at boot, before anyone signs in, and keeps running
  after you sign out. Choosing it needs administrator approval (UAC) and your **Windows password**
  once (not a PIN or Windows Hello): it goes straight to Task Scheduler, which stores it to start
  the task; the app never stores or logs it. If you change your Windows password later, the task
  can't start; the app then shows "re-enter your password" with a button to do that.

Save asks whether to restart the host now, so it runs the new way; every ON folder then stops and
starts again.

## Where things are

| What | Where |
|---|---|
| Program (installer) | `%LOCALAPPDATA%\Programs\claude-rc-windows` |
| Folder list and settings | `%APPDATA%\claude-rc-windows` (`folders.json`, `settings.json`) |
| Logs | `%LOCALAPPDATA%\claude-rc-windows\logs` (`host.log`; `folders\*.log` per folder: starts, exits, restarts, take overs, prompts, never console output; `setup.log` only after a silent install's errors) |
| Console viewer data | `%LOCALAPPDATA%\claude-rc-windows\webview2` |
| Scheduled task | `\claude-rc-windows\Host` in Task Scheduler |
| Claude's own settings | `%USERPROFILE%\.claude.json`: the app only sets `hasTrustDialogAccepted` and, when missing, `remoteControlSpawnMode` for your folders |

Settings shows the data and log folders, the host's version and the task's status.

## Uninstall

**Settings > Apps > Installed apps > claude-rc-windows > Uninstall**. It turns every folder OFF
(stopping their sessions), closes the app, stops the host, deletes the scheduled task, removes the app's notification registration, the program and the
console viewer's data, and asks whether to keep your folder list, settings and logs (Yes keeps them
for a later install, with every folder OFF: turn them on again after installing). A silent
uninstall keeps them without asking. If the scheduled task runs another copy (you moved to the
portable zip), that copy's task, host, folders and data are left alone and nothing is asked. It
never changes `.claude.json`. A task set up for "at startup" was made with
administrator rights; if the uninstall can't delete it, it says so, and you can delete
`\claude-rc-windows\Host` in Task Scheduler.

For the portable zip, run `ClaudeRc.exe --uninstall` from its folder: it does the same steps
(folders OFF, app closed, host stopped, task deleted if it runs this copy, notification
registration removed). Then delete the folder, and `%APPDATA%\claude-rc-windows` and
`%LOCALAPPDATA%\claude-rc-windows` if you don't want to keep your list and logs.

## Build from source

Needs the .NET 10 SDK; the installers also need [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
dotnet build -warnaserror
dotnet test
# Self-contained single-file exes for win-x64 and win-arm64, portable zips, installers and
# SHA256SUMS.txt in artifacts\release (-NoInstaller skips Inno Setup; -Version 1.2.3 sets the version):
.\build\publish.ps1
```

Releases are built by [.github/workflows/release.yml](.github/workflows/release.yml) on a `v*`
tag. Run by hand it is a dry run that only uploads the files as workflow artifacts.

## License

MIT, see [LICENSE](LICENSE). Third-party components and their licences:
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
