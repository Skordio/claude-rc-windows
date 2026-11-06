# M2 decisions

Decisions made while building the tray app (`src/ClaudeRc.Tray`, ClaudeRc.exe) that SPEC.md and
PLAN.md leave open. Part A is the app shell: host connection, main window, settings, tray icon.
Part B adds the console viewer, toasts and anything else left from PLAN's M2 list.

## 1. Packages

| Package | Version | Use |
|---|---|---|
| `CommunityToolkit.Mvvm` | 8.4.2 | `ObservableObject`, `RelayCommand`, `AsyncRelayCommand`. No source generators: properties are written out, so nothing depends on partial-property support or generator diagnostics. |
| `H.NotifyIcon.Wpf` | 2.4.1 | The tray icon (PLAN 1). Created in code, so it needs `ForceCreate(enablesEfficiencyMode: false)`; efficiency mode would put the whole process in EcoQoS. |

The folder picker is WPF's `Microsoft.Win32.OpenFolderDialog` (PLAN 1), the claude picker is
`OpenFileDialog` (`*.exe;*.cmd`). `Microsoft.Toolkit.Uwp.Notifications` and `Microsoft.Web.WebView2`
are part B's (toasts, viewer). No package was unworkable.

## 2. Layout

- `Hosting/HostClient.cs`: `IHostClient` and `IHostConnector`, the seam for tests. The app uses
  `PipeHostConnector`, which wraps Core's `IpcClient` (one attempt waits 500 ms for the pipe).
  `ReconnectBackoff` waits 0.5, 1, 2, 4, then 5 s between attempts, and resets after a connection.
- `Hosting/HostConnection.cs`: the connection loop. It connects, sends `list` and `settings.get`,
  and then applies `rows` events. It forwards `toast` and `output` events as .NET events for
  part B. When the connection ends, it clears the rows and tries again. `ReconnectNow` skips the
  wait; Start host calls it. Requests go through `SendAsync`, which returns a `HostResult` and never
  throws when the host is gone (the error is "Host not running").
- `Hosting/HostTask.cs`: `IHostTask` (task state, start mode, run). M2's implementation,
  `SchtasksHostTask`, runs `schtasks.exe /Query` and `/Run` with an argument list. Start mode is
  always `unknown`; M4 replaces the class with one built on the TaskScheduler library.
- `ViewModels/`: `MainViewModel`, `FolderRowViewModel`, `SettingsViewModel`, `LogViewModel`. They use
  no WPF types, and the tests drive them with a fake client.
- `Services/`: `IDialogs`, `IShell` and `IWindows`, with their WPF and process implementations.
- `Views/`: `MainWindow`, `SettingsWindow`, `LogWindow`. `App.xaml.cs` does startup, single instance
  and the tray icon.
- `Assets/tray.ico`: drawn by `tools/make-tray-icon.ps1` (a terminal prompt with a green dot, no
  external branding). It has PNG entries at 16 to 256 px. Run the script again to change it.

## 3. Threading

`HostConnection.Start` is called on the UI thread. Its awaits do not use `ConfigureAwait(false)`,
so every state change, rows update and event comes back on the dispatcher, and the view-models
never marshal anything. In tests there is no synchronization context, so the loop runs on the
thread pool; the tests wait for conditions (`Eventually.True`), and the reconnect tests advance a
`FakeTimeProvider` until the next attempt happens.

## 4. Rows

- A row's identity is its path, compared with `PathNormalizer.Comparer` (case-insensitive). A rows
  event updates the existing view-model in place and moves rows into the host's order, so the
  ON/OFF switch and an open row menu do not flicker or lose their row.
- The host sends `restart_in_s` only when the status changes. The row remembers when it got it and
  counts down by itself (the window ticks every 500 ms): `restarting in 4s (exit code 3)`, then
  `restarting now` at 0. Exit codes outside 0–255 are also shown in hex, because the Windows codes
  that matter (`0xC000013A`, …) are unreadable in decimal.
- Status words: off, starting, running, waiting for input, restarting in Ns, failing, folder
  missing, running (started outside the app). Starting has a yellow dot. SPEC 3.1 named no colour
  for it, and the session is not ready yet. SPEC 3.1 now says so.
- **Restart button**: SPEC 3.1 said "`failing` or `stopped-unexpectedly`", but section 4 has no
  such status. The button now shows for `failing` and `restarting`. Both mean the session is not
  running against the user's wish, and Restart starts it without waiting out the delay. In the row
  menu, Restart is enabled any time the folder is ON, except for an external row (the host would not
  start a second session there). SPEC 3.1 is updated.
- **Show** is enabled only for `starting`, `running` and `waiting`. These are the states with a hosted
  console. The host refuses `attach` without a running session, so an enabled Show elsewhere would
  only produce an error. SPEC 3.1 is updated.
- **Remove** needs the row to be OFF and saved. An unsaved row (an external session in a folder that
  is not in the list) has nothing to remove. Remove asks first; SPEC gives no wording, so the
  question names the folder and says the folder itself is not changed.
- **Turn OFF** asks only when a hosted session is alive (`starting`, `running`, `waiting`), with the
  spec's text. Turning OFF a `restarting`, `failing` or `missing` folder stops nothing, so it does not
  ask.
- **Take over** asks with the spec's text (SPEC 7) and shows the host's error. Until M3 the error is
  "take over is not available yet …".
- The ON/OFF switch is a `Button` styled as a switch, not a `ToggleButton`. A click only asks the
  view-model, and the switch shows the host's state when the next rows event arrives. A ToggleButton
  would flip at once, and with a OneWay binding it would also lose the binding.

## 5. Host connection and Start host

- **Version mismatch** (SPEC 10): when the connection ends with `IpcCodec.VersionMismatchMessage`,
  that message is shown in the status bar and in the list area. Retries go on with the backoff. An
  attempt that finds no host shows "Host not running"; one that meets the mismatched host again
  keeps the mismatch.
- **Start host** runs the scheduled task and then reconnects at once. If the task does not exist,
  a message says so and tells the user to start ClaudeRcHost.exe themselves. The tray never starts
  the host as its own child (SPEC 9.4).
- **`--instance`**: the tray takes `--instance <suffix>` (PLAN 2.2) and checks it with the host's own
  parser, so both build the same pipe name. The suffix also goes into the tray's single-instance
  names and into the task path, `\claude-rc-windows-<suffix>\Host`, so a test tray can never query or
  run the real `\claude-rc-windows\Host`. PLAN 2.2's `\claude-rc-windows-test-<guid>\` is the same
  idea; M4's tests can pass any suffix.
- While no host answers, Add folder is disabled, Settings opens read-only with a note, and the list
  area explains why it is empty.

## 6. Single instance

The app uses a mutex `Local\claude-rc-windows-tray-<SID>[-instance]`, which is per logon session
like the tray itself. It also uses an auto-reset event `Local\claude-rc-windows-tray-show-<SID>[-instance]`.
The event is created before the mutex, so a second launch can always find it. A second launch calls
`AllowSetForegroundWindow(ASFW_ANY)` (it has the foreground, having just been started), sets the
event and exits with code 0. The first instance waits on the event and shows and activates its main
window.

## 7. Settings window

- The start mode is shown as two disabled radio buttons, with "Current: unknown" until M4 reads the
  task.
- The settings live in the host. Without a connection the fields are disabled and the window says
  why.
- The override is checked before sending: surrounding quotes and spaces are removed, empty means
  auto-detect (`null`), and anything else must be a full path to a `.exe` or `.cmd`. The host checks it
  again and its refusal is shown in the window, which stays open. SPEC 3.4 now says this.
- The window is modeless. `IsCancel` closes only a `ShowDialog` window, so Cancel also has a Click
  handler that closes it. This bug was found while driving the app by hand.

## 8. Row menu actions

- **Open folder in Explorer**: `explorer.exe <path>` with an argument list. A folder that does not
  exist gives an error instead, because Explorer would open Documents.
- **Open in Windows Terminal**: `wt.exe -d <path>`. wt splits its command line into commands at `;`,
  so `;` is escaped as `\;`. If `wt.exe` cannot be started (`Win32Exception`), Explorer opens instead.
- **View log**: a window per folder with the last 500 lines from the host's `log` op, scrolled to the
  end, with Refresh.
- **Show** (console viewer) is part B. Until then it shows a message that the viewer is not in this
  build yet.

## 9. Tests (`tests/ClaudeRc.Tray.Tests`, 132)

- `FolderRowViewModelTests`: status text and colour for every status, the countdown (local ticks,
  reset by a new row, "restarting now"), exit code formatting, prompt, error and last-lines fields,
  and the enable rules for Show, Remove, Restart (button and menu), Take over and the turn-off
  confirmation.
- `HostConnectionTests`: connect and load, rows and toast events, no host → not running with
  retries, `ReconnectNow`, host closing (rows cleared, client disposed, reconnect), version mismatch
  (after connecting and on the first request), and `SendAsync` errors.
- `MainViewModelTests`: rows kept in place and reordered (also case-insensitively), host gone →
  Start host and hint, status bar texts, Start host (missing task, reconnect at once),
  toggle/remove/take over/restart confirmations and requests, host errors shown, the row menu, Add
  folder (cancel, network warning, no host), the countdown tick and the empty hint.
- `SettingsViewModelTests`: load, start mode read-only, the override rules, the host's refusal,
  Browse, and no host.
- `LogViewModelTests`, `TrayOptionsTests`, `FolderPathsTests`, `ShellLauncherTests`, `StartHostResultTests`.

The small helpers (`TrayOptions`, `FolderPaths`, `ShellLauncher`, `ReconnectBackoff`,
`StartHostResult`) were written together with their tests. The connection and the view-models were
stubs when their tests first ran (110 failing), and were implemented after that.

## 10. Checked by hand

A real ClaudeRcHost.exe (debug build) ran with `--instance m2t<random>`, a temp data and log folder,
a temp `.claude.json`, and `claude_path` pointing at FakeClaude.exe. The tray ran with the same
`--instance`. Folders were added with the debug CLI, and the app was driven with UI Automation and
`BM_CLICK` on message boxes. Checked:
- the row states: running, waiting with its prompt line, restarting with the countdown and exit
  code, failing with the last lines and Restart, off, and missing;
- the OFF confirmation, answered No (stays ON) and Yes (turns OFF);
- Settings: loaded from the host, an invalid `.bat` override refused, and Cancel (after the fix);
- the row menu enable rules and View log;
- the host killed: "Host not running", Start host with the missing instance task gives the message,
  and no FakeClaude is left;
- the host started again: the tray reconnects by itself;
- close hides to the tray, and a second launch with the same instance exits and shows the first
  window.

Not driven by automation: the tray icon's own context menu (Quit tray, and Add folder from the
tray), and actually launching Explorer or Windows Terminal. These are unit-tested at the
`IShell` level. PLAN 5 already lists a look at the app by the user.
