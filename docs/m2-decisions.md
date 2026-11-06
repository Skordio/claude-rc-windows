# M2 decisions

Decisions made while building the tray app (`src/ClaudeRc.Tray`, ClaudeRc.exe) that SPEC.md and
PLAN.md leave open. Part A is the app shell: host connection, main window, settings, tray icon.
Part B adds the console viewer, toasts and anything else left from PLAN's M2 list (sections 11 to
14).

## 1. Packages

| Package | Version | Use |
|---|---|---|
| `CommunityToolkit.Mvvm` | 8.4.2 | `ObservableObject`, `RelayCommand`, `AsyncRelayCommand`. No source generators: properties are written out, so nothing depends on partial-property support or generator diagnostics. |
| `H.NotifyIcon.Wpf` | 2.4.1 | The tray icon (PLAN 1). Created in code, so it needs `ForceCreate(enablesEfficiencyMode: false)`; efficiency mode would put the whole process in EcoQoS. |
| `Microsoft.Web.WebView2` | 1.0.4314.35 | The console viewer (part B, section 11). The newest stable SDK; it runs on the Evergreen runtime the user has (157 on this machine). |
| `Microsoft.Toolkit.Uwp.Notifications` | 7.1.3 | Toasts (part B, section 12). The last release; the toolkit is no longer developed, but it is what PLAN 1 names and it works on .NET 10. |

The folder picker is WPF's `Microsoft.Win32.OpenFolderDialog` (PLAN 1), the claude picker is
`OpenFileDialog` (`*.exe;*.cmd`). No package was unworkable.

**Target framework.** The toast toolkit can show toasts only through the Windows SDK projection,
which needs a versioned TFM (its `net5.0-windows10.0.17763` build; with plain `net10.0-windows` only
the XML builder is there, without `Show()` or click activation). ClaudeRc.Tray and its tests are
therefore `net10.0-windows10.0.17763.0` with `SupportedOSPlatformVersion` 10.0.17763.0. That is
Windows 10 1809, the oldest version SPEC 13 supports anyway (ConPTY). Core and Host stay on
`net10.0-windows`. The projection (`Microsoft.Windows.SDK.NET.dll`) makes the tray's publish larger;
M5 can trim it. PLAN 1 is updated.

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
- **Show** opens the console viewer (section 11).

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

## 11. Console viewer (part B)

- **Files.** `ViewModels/ConsoleViewModel.cs` is the bridge and has no WPF or WebView2 types;
  `Viewer/ViewerProtocol.cs` holds the page's messages; `Viewer/ViewerContent.cs` serves the page;
  `Views/ConsoleWindow.xaml(.cs)` hosts WebView2. `WpfWindows.OpenConsole` keeps one window per
  folder (paths compared with `PathNormalizer.Comparer`) and brings an open one to the front.
- **xterm.js, vendored.** `@xterm/xterm` 6.0.0 and `@xterm/addon-fit` 0.11.0, the latest stable
  releases, fetched with `npm install --ignore-scripts --save-exact` into the scratch folder. The UMD
  builds (`lib/xterm.js`, `css/xterm.css`, `lib/addon-fit.js`) and each package's `LICENSE` (MIT)
  are copied unchanged to `src/ClaudeRc.Tray/Viewer/web/xterm/` and `…/addon-fit/`. npm integrity:
  - `@xterm/xterm@6.0.0`: `sha512-TQwDdQGtwwDt+2cgKDLn0IRaSxYu1tSUjgKarSDkUM0ZNiSRXFpjxEsvc/Zgc5kq5omJ+V0a8/kIM2WD3sMOYg==`
  - `@xterm/addon-fit@0.11.0`: `sha512-jYcgT6xtVYhnhgxh3QgYDnnNMYTcf8ElbxxFzX0IZo+vabQqSPAjC3c1wJrKB5E19VwQei89QCiZZP86DCPF7g==`

  To update: install the new versions the same way, copy the same files, and run the tests
  (`ViewerContentTests` check that every file the page loads is served). The source maps are not
  copied. M5's third-party notice must carry the two MIT licences; they are also embedded in the
  exe and served at `xterm/LICENSE` and `addon-fit/LICENSE`.
- **Serving.** Every file under `Viewer/web` is an embedded resource (`viewer/<path>`), so the
  single-file publish needs nothing on disk. WebView2 navigates to
  `https://viewer.claude-rc.example/index.html`. `.example` is reserved (RFC 2606), so the name can
  never be a real site. A `WebResourceRequested` filter on `*` answers every request: the page's
  files from the assembly (`Cache-Control: no-store`), anything else 404. `NavigationStarting`
  cancels any other URL and `NewWindowRequested` is swallowed, so the viewer never reaches the
  network. The page also has a CSP (`default-src 'none'`, scripts and styles from itself only;
  xterm needs inline styles). Dev tools, context menus, script dialogs, zoom, autofill and the
  browser's accelerator keys are off, so F5, Ctrl+F and Ctrl+P reach the terminal. Copy and paste
  are not browser accelerators and still work. `SetVirtualHostNameToFolderMapping` was not used
  because it needs the files in a folder on disk.
- **Messages** (JSON through `postMessage` / `PostWebMessageAsJson`). Page to app: `ready {cols,
  rows}`, `input {data}`, `resize {cols, rows}`, `copy {text}`. App to page: `output {data}`
  (base64), `status {text|null, level}` (the banner), `focus`. An invalid message is ignored, and so
  is one with a lone surrogate, which System.Text.Json refuses. Messages are accepted only from the
  page's origin.
- **Bytes, not text.** Output goes to the page as the host's exact bytes, and the page writes them
  to xterm as a `Uint8Array`. xterm keeps a UTF-8 sequence that is split across two writes (checked
  with `@xterm/headless` 6.0.0 in the scratch folder: `a€😀b` split inside both characters comes out
  whole). Decoding each chunk to a string in .NET would turn a split character into two U+FFFD. A
  test pins that a split `€` reaches the page as its two exact halves.
- **Attach.** On `ready` the view-model sends `attach` with the page's fitted size, clamped to the
  host's 1..1000 × 1..500. Output for the folder is accepted from when the attach is sent: the host
  writes the replay right after its answer, and the replay's event can be read before the answer.
  The host keeps a viewer attached across the folder's sessions and sends a fresh replay (starting
  with RIS) for each new one, so after an exit the viewer only shows the banner and waits. A viewer
  that is not attached (nothing ran at open, the attach was refused, the connection was lost)
  attaches on the next rows change that shows the folder running. A refused attach shows the host's
  error and is retried on the next row change, not in a loop. A lost connection drops the attach (a
  new connection has no viewers), so the viewer attaches again after the reconnect.
- **Banner.** Shown while the row is not `starting`, `running` or `waiting`: OFF, restarting (with
  the exit code or the start error), failing (use Restart), missing, external (no console to show),
  folder removed, host not running, version mismatch. The terminal keeps the last screen.
- **Input** is sent one `input` request at a time, in order. Keys typed while one is in flight go
  together in the next. A paste is cut into pieces of at most 32,768 characters, never inside a
  surrogate pair: the host disconnects a client whose request line is over 1 MiB, and 32 Ki
  characters stay below that even if each is escaped as a six-character JSON escape. Input is
  dropped while nothing is attached. xterm's `onBinary` (old X10 mouse reports) is not forwarded:
  `input` is text written as UTF-8, so those bytes would arrive changed, and claude rc does not use
  that mouse mode.
- **Resize.** A `ResizeObserver` refits the terminal 80 ms after the window settles; xterm's
  `onResize` then sends `resize`, which the view-model forwards while attached. The size is also
  kept for the next attach.
- **Keys.** Ctrl+C copies when text is selected (the page sends `copy` and the app sets the WPF
  clipboard, as `navigator.clipboard` may need a permission prompt), and is ^C otherwise.
  Ctrl+Shift+C copies too. Ctrl+V and Ctrl+Shift+V paste through the browser's paste event, so a
  literal ^V cannot be typed. SPEC 3.3 now says this.
- **WebView2 environment.** One `CoreWebView2Environment` for all viewer windows, with the user
  data folder `%LOCALAPPDATA%\claude-rc-windows\webview2`, or `webview2-<instance>` with
  `--instance` (`TrayOptions.WebViewDataDir`). A failed creation is retried at the next open, as the
  user may have installed the runtime since. SPEC 11 lists the folder; SPEC 14 says the uninstaller
  removes it.
- **Runtime missing.** `WebView2RuntimeNotFoundException` (and other start errors) replace the
  terminal with a message: what is missing, the download page
  (`https://developer.microsoft.com/microsoft-edge/webview2/`), and that the session is not
  affected. The message is a read-only text box, so the address can be copied. A crashed browser
  process (`ProcessFailed`) shows a message to close and reopen the window.
- **Closing** disposes the view-model, which stops following the connection and sends `detach` if
  attached, and then the WebView2. The viewer never sends a request that changes a session; a test
  checks that closing sends only `detach`.

## 12. Toasts (part B)

- **Routing** (`Notifications/ToastRouter.cs`). The host sends `toast` only for transitions into
  `failing`, `waiting` and `missing`, and only while notifications are on. The router checks again:
  it shows nothing while the settings it read say off, and it remembers the status it showed per
  folder. The same status is not shown again until a rows event shows the folder in another status.
  That guards against a duplicate event at no cost. Title: "claude rc is failing", "Waiting for
  input", "Folder missing"; text: the host's.
- **Click.** The toast carries `action=open;path=…;instance=…` (`ToastArgs`, in the toolkit's
  `ToastArguments` encoding, so any path round-trips). A click on a toast of another `--instance` is
  ignored, so a test tray never acts on the real app's toasts and the other way round. The click
  arrives on a COM thread and is passed to the dispatcher. It shows the main window, highlights the
  row for 4 s and scrolls it into view (`MainViewModel.FocusRow`).
- **Toolkit** (`Notifications/ToolkitToastNotifier.cs`, used only by the running app). Each toast
  has its folder's tag (16 hex chars of SHA-256 of the upper-cased path) in group `folders`, so a
  folder's newer toast replaces its older one in the Action Center. Errors from `Show` (toasts
  turned off for the app in Windows, no notification platform) are swallowed.
- **AUMID for the unpackaged exe.** Nothing is needed for the exe run directly. On first use
  (subscribing to `OnActivated` at startup) the toolkit takes the process's AppUserModelID from the
  shell, which for an exe without an explicit one is derived from the exe's path, and writes under
  HKCU:
  - `Software\Classes\AppUserModelId\<aumid>`: `DisplayName` (from the Start menu shortcut, else the
    exe's version info), `IconUri` (an icon taken from the exe and cached under
    `%LOCALAPPDATA%\ToastNotificationManagerCompat\Apps\`) and `CustomActivator` (a CLSID made from
    the AUMID);
  - `Software\Classes\CLSID\{clsid}\LocalServer32` = the exe with `-ToastActivated`, so a click
    while the app is not running starts it. `TrayOptions` accepts `-ToastActivated`; the app then
    starts as usual (single instance; the click arrives through `OnActivated`).

  The key names were checked against the strings in the 7.1.3 DLL; the values follow the toolkit's
  documented behaviour. The Drive step should look at them with `reg query` after its first run.
  Because the AUMID follows the exe's path, a debug build and the installed app never share a
  registration.
- **What M5 must do.**
  - Uninstall: run `ClaudeRc.exe --unregister-notifications` before deleting the exe. It calls
    `ToastNotificationManagerCompat.Uninstall()`, which removes the keys above, the cached icon and
    the app's toasts. SPEC 14 says so.
  - Start menu shortcut: give it no AppUserModelID of its own (Inno Setup: no `AppUserModelID:` on
    the `[Icons]` entry), so the app has the same path-based AUMID whether it is started from the
    shortcut, at logon or by a toast click. The toolkit then takes the shortcut's name and icon for
    the toast header. If M5 wants a fixed AUMID instead, the app must call
    `SetCurrentProcessExplicitAppUserModelID` with that ID before the toolkit's first use, and the
    shortcut must set the same `AppUserModelID:` and `AppUserModelToastActivatorCLSID:` (the
    toolkit's CLSID for that AUMID).
  - Whoever runs the tray by hand (the Drive step, a developer) leaves these HKCU keys for that exe;
    `ClaudeRc.exe --unregister-notifications` removes them.
- **Test instances.** With `--instance`, quitting the tray clears its toasts from the Action Center
  (`History.Clear()`), so an old test toast cannot later start a tray without the instance.

## 13. Log viewer

Done in part A (section 8): View log opens a window per folder with the last 500 lines from the
`log` op, and Refresh asks again. Part B changed nothing there.

## 14. Tests (part B: 93 more, 225 in the tray tests)

- `ConsoleViewModelTests`: attach with the page's size (clamped, at least 1 × 1); output passed on
  as exact bytes (a split `€` stays two exact halves); output of other folders and from before the
  attach ignored; output before the attach answer kept; path case ignored; input in order; input
  coalesced while a send is in flight; a large paste split below the host's line limit without
  cutting a surrogate pair; input dropped while not attached; resize forwarded and clamped; the
  banner for each status; attach when the folder starts; a refused attach shown and retried on the
  next row change; an exit while attached (banner, no new attach); folder removed; host lost and
  reattach after the reconnect; closing sends only detach; copy; invalid page messages; focus.
- `ViewerProtocolTests`; `ViewerContentTests` (every page file served with its type, query and
  fragment ignored, other origins and unknown files not found, our own files contain no URL);
  `ViewerRuntimeTests` (the missing-runtime message).
- `ToastRouterTests` (once per transition, a new transition after the row left the status, a
  different status, setting off, click, dispose); `ToastArgsTests` (round-trip of odd paths, other
  instances and other arguments ignored).
- `MainViewModelTests` (`FocusRow`); `TrayOptionsPartBTests` (WebView2 data folder per instance,
  `-ToastActivated`, `--unregister-notifications`).

All of them ran against stubs first (91 failing; the large-paste test was added, failing, before
the input code was written) and passed after the implementation. The WebView2 window and the
toolkit notifier have no automated tests: they need a visible window or a real toast, which this
step must not create; the Drive step checks them. The page script was checked headlessly with node
against stubbed `Terminal` and DOM objects (scratch only): `ready` is posted, output is written as
bytes, the banner shows and hides, input and resize are posted, and Ctrl+C and Ctrl+V behave as
described.
