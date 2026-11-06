# M5 decisions

Decisions made while building the release (SPEC 14): the publish, the installer, the portable zip
and the release workflow. SPEC 3.5, 14 and 15 now state the behaviour; this file says how it is
built and why.

## 1. Publish (`build/publish.ps1`)

One script, used locally and by `release.yml`. Per runtime (`win-x64`, `win-arm64`) it publishes
`ClaudeRc.Tray` and `ClaudeRc.Host` into one folder, `artifacts/publish/<rid>/`, then zips the
portable build, compiles the installer and writes `artifacts/release/SHA256SUMS.txt`.

- **Self-contained single-file** (`--self-contained -p:PublishSingleFile=true`), as SPEC 13 says.
- **Native DLLs beside the exe** (`IncludeNativeLibrariesForSelfExtract=false`): WPF's
  `wpfgfx_cor3.dll`, `PresentationNative_cor3.dll`, `PenImc_cor3.dll`, `vcruntime140_cor3.dll`,
  `D3DCompiler_47_cor3.dll` and WebView2's `WebView2Loader.dll` (also under
  `runtimes\<rid>\native`, where the WebView2 package puts it) stay in the folder. Extracting them
  into `%TEMP%\.net` at every start was the other choice; it writes to `%TEMP%` (which cleaners
  empty) and gains nothing for an app that is installed as a folder anyway. Native libraries are
  found next to the exe by the normal DLL search, which is how WPF and WebView2 document
  single-file use. The managed parts (the Windows SDK projection `Microsoft.Windows.SDK.NET.dll`,
  `WinRT.Runtime.dll`, the toast toolkit, TaskScheduler, our own assemblies) are in the bundle.
- **Not compressed** (`EnableCompressionInSingleFile=false`): a compressed bundle is unpacked into
  memory, and the host runs all the time. The installer (LZMA2) and the zip compress anyway.
- **Not trimmed**: WPF does not support trimming, and the host is small next to the runtime.
- `DebugType=none`, no XML doc files (`PublishDocumentationFile=false`,
  `PublishReferencesDocumentationFiles=false`). The script fails if any of our own `.dll`, a `.pdb`
  or `.xml` ends up in the folder, or a file the app needs is missing.
- **Size** (0.9.0 test build, x64): ClaudeRc.exe 150 MB, ClaudeRcHost.exe 74 MB; the installer
  72 MB, the zip 99 MB. arm64 is a little smaller.
- **Version**: `-p:Version=<version>` (FileVersion `a.b.c.0`, ProductVersion
  `<version>+<commit>`, which the host reports as `host_version`). The script takes `-Version`;
  without it, the `v*` tag on HEAD, else `0.0.0-dev`. `release.yml` passes the tag without its `v`,
  or `0.0.0-dryrun.<run number>` on a branch. MinVer was not added: one more package for something
  a property does, and it needs the full git history in CI.
- **Version resources** (`Directory.Build.props`): Product `claude-rc-windows`, Company
  `claude-rc-windows (unofficial; not affiliated with Anthropic)`, the MIT copyright; title
  `claude-rc-windows` for ClaudeRc.exe (the toast header's fallback name, M2 12) and
  `claude-rc-windows host` for ClaudeRcHost.exe (Task Manager).
- **Checks without running the app**: each exe's FileVersion and ProductVersion; and, for the
  machine's own architecture, `ClaudeRcHost.exe --release-smoke-test` must exit with code 2 at
  once (the host parses its command line before it touches anything), which proves the bundle
  starts. Locally, also: `ClaudeRc.exe --unregister-notifications` from a scratch copy exited 0
  with no change to the HKCU toast keys (it loads WPF, the WinRT projection and the toolkit, then
  exits before any window, mutex or task), and the PE headers are AMD64 for x64 and ARM64 for
  arm64 (exes and native DLLs).

## 2. Installer (`installer/claude-rc-windows.iss`)

- **One installer per architecture** (`…-win-x64-setup.exe`, `…-win-arm64-setup.exe`), one
  `AppId` for both, so installing one over the other upgrades in place. A combined installer would
  carry both 220 MB builds. The x64 one is allowed on Arm64 Windows too (`x64compatible`, it runs
  emulated); the arm64 one only there.
- Per-user (`PrivilegesRequired=lowest`), fixed folder `{localappdata}\Programs\claude-rc-windows`
  (no folder page; `UsePreviousAppDir=no`), Windows 10 1809 or later, `SetupLogging=yes` (a log
  in `%TEMP%` for support).
- **Start menu shortcut** `{autoprograms}\claude-rc-windows` without an `AppUserModelID` of its
  own, as M2 12 asks: the toast toolkit keeps the exe's path-based AUMID, the same whether the app
  starts from the shortcut, at logon or from a toast click, and takes the shortcut's name and icon
  for the toast header.
- **The installer builds nothing itself.** The task, the host and the folders are handled by the
  installed ClaudeRc.exe (section 3), so the task XML exists once (M4's `HostTaskDefinition`) and
  the toast registration is removed by the exe whose path it was made for.
  - `[Run]`: `ClaudeRc.exe --install` (waits), then the tray (`postinstall`, not skipped in silent
    mode, so a silent upgrade brings the tray back too).
  - `PrepareToInstall`: when `{app}\ClaudeRc.exe` exists (an upgrade), it runs
    `--stop-for-update` before any file is replaced.
  - `[UninstallRun]`: `ClaudeRc.exe --uninstall` (waits).
- `CloseApplications=no`: the Restart Manager cannot end the windowless host and would offer to
  restart the apps itself; `--stop-for-update` ends both. If a file is still locked, Inno's own
  retry dialog appears.
- **Upgrade**: the host has to stop (its exe is replaced), so the ON folders' sessions end and
  start again when `--install` runs the task. The tray is quit and started again.
- **Uninstall data**: asked at `usUninstall` (before anything is removed): "Keep your folder list,
  settings and logs?" Yes keeps `%APPDATA%\claude-rc-windows` and
  `%LOCALAPPDATA%\claude-rc-windows\logs`; No deletes both folders. The WebView2 folder always
  goes. A silent uninstall keeps the data (`SuppressibleMsgBox` default Yes). SPEC 14 asked only
  about the data folder; the logs follow the same answer, as keeping a log folder for an app whose
  list was deleted (or the other way round) makes no sense. `.claude.json` is never touched.
- The tray is not started at logon (no `Run` key): SPEC never asked for it, the host is what runs
  at logon. The README says to open the app from the Start menu. A "start the tray at sign-in"
  option would be a spec change (notifications only show while the tray runs).

## 3. The installer's steps in ClaudeRc.exe (`Hosting/AppSetup.cs`)

`TrayOptions` parses `--install`, `--stop-for-update` and `--uninstall`, exactly one, with nothing
but `--instance` beside it (no `--host-exe`, `--host-arg`, `--for-user`, `--set-start-mode`,
`--restart-host`, `--unregister-notifications`, `-ToastActivated`). The host is always the
`ClaudeRcHost.exe` next to ClaudeRc.exe (with a test instance's own derived arguments), the task
the fixed `\claude-rc-windows[-instance]\Host`, the user the current one (the installer runs
unelevated as the user). So like M4's helper, nothing on the command line can point it at another
program, path or account. `App.OnStartup` runs the step before the single-instance check, with no
tray icon or window, and exits with its code (0 ok, 1 something failed and was shown, 2 bad
arguments). Every problem is collected and shown in one message at the end; each step still runs.

- **`--install`**: (1) quit the running tray; (2) set up the task: a `startup` task that already
  runs this host with these arguments is kept (registering it again would need UAC and the
  password; an upgrade keeps the user's choice); a `startup` task of another copy becomes this
  copy's `startup` task through `StartModeSwitcher` (the elevated helper, UAC and the password
  prompt); anything else (none, `logon`, changed by hand) becomes this copy's `logon` task (an
  upgrade so gets the new version's definition; Task Scheduler refusing an administrator-made task
  hands it to the helper, as in M4); (3) stop the host; (4) run the task. A task that was not set
  up (cancelled, failed) stops there with the reason and "choose a start mode in Settings"; a host
  that still runs stops it before (4), since a new host would only meet the old one's mutex.
- **`--stop-for-update`**: quit the tray, stop the host. Nothing is registered, run or deleted.
- **`--uninstall`**: if the task names another `ClaudeRcHost.exe` (the user moved to the portable
  zip), that task, its host and its folders are left alone. Otherwise: every saved ON folder is
  turned OFF through the host (`set` with `on: false`; unsaved and OFF rows are not touched, and
  nothing is started, removed or taken over), then the setup waits up to 15 s for no row to be
  `starting`, `running` or `waiting` (each session gets the normal Ctrl+C and up to 5 s); then the
  tray quits, the host is stopped and the task deleted. Then, in every case, the toast
  registration is removed (`ToastNotificationManagerCompat.Uninstall()`, as
  `--unregister-notifications`). With no host answering (3 tries), nothing runs and the saved list
  is left as it is (only the host writes `folders.json`).
- **Stopping the host**: `ITaskService.Stop` (which waits for the process, M4 6), then every
  process still running this copy's `ClaudeRcHost.exe` is terminated (`WindowsProcessStopper`:
  processes by name, each opened with terminate + query + synchronize access, its image path read
  with `QueryFullProcessImageName` and compared case-insensitively, terminated through that handle,
  never this process). That covers a stop Task Scheduler refused (an administrator-made `startup`
  task) and a host started by hand. Its sessions end with it (Job Objects, SPEC 6.5).
- **Quitting the tray**: the tray now creates a second named event,
  `Local\claude-rc-windows-tray-quit-<SID>[-instance]` (`TrayOptions.QuitEventName`), beside the
  show event, and calls its normal Quit when it is set. `WindowsRunningTray` opens the tray's
  mutex (none: no tray), sets the event and waits up to 10 s for the mutex (an abandoned one counts
  as gone). Then this copy's `ClaudeRc.exe` processes other than itself get 3 s and are ended, so
  the exe is free to be replaced. A portable tray at another path is asked to quit too (same user,
  same mutex), so the installer's new tray does not just show the old one's window.
- **Dialogs from any thread**: `RunAsync` resumes on the caller's context after each step, and
  `WpfDialogs` now marshals a message box to the dispatcher when it is called from another thread
  (it reads `Application.Current.Windows`, which only the UI thread may). M4's helper showed its
  "not restarted" error after a `ConfigureAwait(false)` await, which would have thrown there.
- **Not done**: deleting a `startup` task through the elevated helper. Task Scheduler may refuse an
  unelevated delete of the administrator-made task; the uninstall then says so and points at Task
  Scheduler. The task would otherwise try to start a missing exe at boot.

## 4. Portable zip

`claude-rc-windows-<version>-<rid>-portable.zip` holds one folder, `claude-rc-windows\`, with the
same files as the installer: the two exes, the native DLLs, `README.md`, `LICENSE`,
`THIRD-PARTY-NOTICES.md`. The README tells portable users to run `ClaudeRc.exe --uninstall` to
remove it (the same steps as the uninstaller, without deleting files).

## 5. Release workflow (`.github/workflows/release.yml`)

- `on: push: tags: ['v*']` and `workflow_dispatch` with `dry_run` (boolean, default true).
- **build** (windows-latest, `contents: read`): checkout, .NET 10, version, restore, build
  `-warnaserror`, test (as CI), install Inno Setup, `build/publish.ps1`, upload
  `artifacts/release/*` as the artifact `claude-rc-windows-<version>` (14 days).
- **Inno Setup on the runner**: the official 6.7.3 installer from the jrsoftware GitHub release,
  pinned by URL and SHA-256 (the hash in winget's manifest, which winget verified on this machine),
  installed `/VERYSILENT /CURRENTUSER /DIR=$RUNNER_TEMP\InnoSetup`. Chocolatey was the other
  choice; the hash pin is stronger than a package version.
- **release** (ubuntu-latest), only on a tag ref, and on a hand run only with `dry_run` unticked;
  the only job with `contents: write`. It downloads the artifact, checks `SHA256SUMS.txt`, and runs
  `gh release create <tag> --verify-tag` with every file and short notes (installer vs portable,
  the checksums, SmartScreen). A version with a `-` is marked prerelease. `gh` is on the runner,
  so there is no third-party release action.
- **Pinned actions**, by commit SHA with the tag in a comment: `actions/checkout` v4.4.0,
  `actions/setup-dotnet` v4.3.1, `actions/upload-artifact` v4.6.2, `actions/download-artifact`
  v4.3.0: the newest v4 releases, the major `ci.yml` already uses (newer majors exist; moving to
  them is a separate change for both workflows).

## 6. Tests (59 more in the tray tests, 457 there in all)

- `TrayOptionsSetupTests`: each step parsed; the fixed task and the host next to the exe; a test
  instance's task and folders; every forbidden combination refused; the quit event name.
- `AppSetupTests` (fakes for Task Scheduler, the switcher, the host pipe, the tray and the process
  stopper, one journal to check the order): install with no task (logon, then stop, then run, also
  through the real `StartModeSwitcher`), over a logon task, keeping a startup task of this host,
  another copy's startup task, a switch that failed or was cancelled (no host started), a running
  host stopped before the run, a host that does not end (task not run), a refused stop still ending
  this host, a tray that does not quit, a tray exe that still runs; stop-for-update changes
  nothing; uninstall (only saved ON folders, only OFF, before the tray/host/task, waiting for the
  sessions, giving up after 15 s), no host, no task, another copy's task left alone, a task that
  can't be deleted, a folder the host won't turn off, errors shown once together.
- `WindowsRunningTrayTests` (a stand-in tray on another thread with a random mutex and event): no
  tray, a tray that quits, one that ends without releasing its mutex, one that ignores the event.
- `WindowsProcessStopperTests` (copies of `cmd.exe` under `%TEMP%` waiting on their redirected
  input, no window): only the processes of that exact path end, also with the path in another case;
  the grace time; no process. Every copy is killed and deleted in `Dispose`.
- `HostTaskDefinitionTests`: the host read back from our XML and from Task Scheduler's (quoted or
  not), arguments must match, no host for anything else.

All ran against stubs first (47 failing; the refusal cases passed only because the options did not
exist yet) and passed after the implementation.

## 7. Checked here, and what is left

- Inno Setup 6.7.3 installed per-user with winget (logged in `~/CHANGES.md`); both installers
  compiled; ISCC's file list and the zip listing match the publish folder. **The installers were
  not run** (they would install into the real profile and create the real task).
- `actionlint` 1.7.12 (release binary, checksum verified, in the scratch folder): no errors in
  `release.yml` and `ci.yml`. The release job's shell step was run locally against the built files
  with a stub `gh`: the checksums verify and the `gh release create` line has every file.
- `workflow_dispatch` can only start a workflow file that exists on the default branch, and
  `release.yml` is only on `impl/v1`, so the dry run on GitHub waits for the merge to `main`.
- Manual (PLAN 5): run the installer; upgrade over it (sessions stop and come back); the console
  viewer from the installed single-file exe (WebView2 loader beside the exe); a toast and its
  header name; uninstall with and without keeping the data; `startup` mode before an upgrade and an
  uninstall.
