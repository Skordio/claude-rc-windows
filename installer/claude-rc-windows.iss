; claude-rc-windows installer (SPEC 14): per-user, no administrator rights, one installer per
; architecture. Built by build/publish.ps1, which passes:
;   ISCC /DAppVersion=1.2.3 /DNumericVersion=1.2.3.0 /DArch=x64|arm64
;        /DSourceDir=<artifacts\publish\win-ARCH> /DOutputDir=<artifacts\release> claude-rc-windows.iss
;
; The scheduled task, the host's start and stop, turning the folders OFF and the toast
; registration are done by the installed ClaudeRc.exe itself (--install, --stop-for-update,
; --uninstall; src/ClaudeRc.Tray/Hosting/AppSetup.cs), so the installer never builds the task
; or talks to the host on its own. See docs/m5-decisions.md.

#ifndef AppVersion
  #error Pass /DAppVersion=<version> (build/publish.ps1 does)
#endif
#ifndef NumericVersion
  #error Pass /DNumericVersion=<a.b.c.d>
#endif
#ifndef Arch
  #error Pass /DArch=x64 or /DArch=arm64
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<the publish folder of that architecture>
#endif
#ifndef OutputDir
  #define OutputDir "Output"
#endif

#if Arch == "x64"
  #define ArchAllowed "x64compatible"
#elif Arch == "arm64"
  #define ArchAllowed "arm64"
#else
  #error Arch must be x64 or arm64
#endif

#define AppName "claude-rc-windows"
#define AppExe "ClaudeRc.exe"

[Setup]
; One AppId for both architectures: installing one over the other upgrades in place.
AppId={{5AEDD3C4-2546-429C-83EF-9654EC7A9898}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName} (unofficial; not affiliated with Anthropic)
AppPublisherURL=https://github.com/Skordio/claude-rc-windows
AppSupportURL=https://github.com/Skordio/claude-rc-windows/issues
AppUpdatesURL=https://github.com/Skordio/claude-rc-windows/releases
AppComments=Keeps Claude Code's Remote Control server (claude rc) running in the folders you choose. Unofficial; not affiliated with or endorsed by Anthropic.
AppCopyright=Copyright (c) 2026 Steven Wells. MIT License.
VersionInfoVersion={#NumericVersion}
VersionInfoProductVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription={#AppName} {#Arch} setup
; Per-user (SPEC 14): %LOCALAPPDATA%\Programs\claude-rc-windows, no UAC prompt.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=no
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchAllowed}
; Windows 10 1809 (ConPTY, SPEC 13).
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-win-{#Arch}-setup
SetupIconFile=..\src\ClaudeRc.Tray\Assets\tray.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The running tray and host are ended by ClaudeRc.exe --stop-for-update (PrepareToInstall below),
; not by the Restart Manager, which can't end the windowless host and would offer to restart them.
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; No AppUserModelID of its own (docs/m2-decisions.md 12): the toast library uses the exe's
; path-based ID, the same whether the app starts from here, at logon or from a toast click.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Comment: "Keeps claude rc running in the folders you choose"

[Run]
; Sets up the logon task for this copy (a startup task that already runs it is kept), stops any
; running host and runs the task. Errors are shown by ClaudeRc.exe itself.
Filename: "{app}\{#AppExe}"; Parameters: "--install"; StatusMsg: "Setting up the scheduled task and starting the host..."; Flags: waituntilterminated
; The tray app. Not skipped in silent mode, so a silent upgrade brings it back too.
Filename: "{app}\{#AppExe}"; Description: "Open {#AppName}"; Flags: nowait postinstall

[UninstallRun]
; Every folder OFF (stops the sessions), tray and host ended, task deleted, toast registration removed.
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall"; RunOnceId: "Cleanup"; Flags: waituntilterminated

[Code]
var
  KeepData: Boolean;

{ An upgrade: the installed ClaudeRc.exe ends the running tray and host, so their files can be
  replaced. The ON folders start again when --install runs the task afterwards. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
  Code: Integer;
begin
  Result := '';
  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) then
  begin
    if not Exec(Exe, '--stop-for-update', ExpandConstant('{app}'), SW_SHOWNORMAL, ewWaitUntilTerminated, Code) then
      Log('Could not run ' + Exe + ' --stop-for-update: ' + SysErrorMessage(Code))
    else
      Log(Exe + ' --stop-for-update ended with code ' + IntToStr(Code));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    { Asked before anything is removed. Silent uninstalls keep the data. Never touches .claude.json. }
    KeepData := SuppressibleMsgBox(
      'Keep your folder list, settings and logs?' + #13#10#13#10 +
      ExpandConstant('{userappdata}\{#AppName}') + #13#10 +
      ExpandConstant('{localappdata}\{#AppName}\logs') + #13#10#13#10 +
      'Yes keeps them for a later install. No deletes them.',
      mbConfirmation, MB_YESNO, IDYES) = IDYES;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    { The console viewer's WebView2 data always goes (SPEC 14). }
    DelTree(ExpandConstant('{localappdata}\{#AppName}\webview2'), True, True, True);
    if KeepData then
      RemoveDir(ExpandConstant('{localappdata}\{#AppName}'))
    else
    begin
      DelTree(ExpandConstant('{userappdata}\{#AppName}'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\{#AppName}'), True, True, True);
    end;
  end;
end;
