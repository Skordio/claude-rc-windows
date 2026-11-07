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
; running host and runs the task. A fresh install (--fresh-install) also sets the app to start at
; sign-in; an upgrade keeps the user's choice. Errors are shown by ClaudeRc.exe itself; in a silent
; install (--quiet) they go to %LOCALAPPDATA%\claude-rc-windows\logs\setup.log instead.
Filename: "{app}\{#AppExe}"; Parameters: "--install{code:QuietArg}{code:FreshArg}"; StatusMsg: "Setting up the scheduled task and starting the host..."; Flags: waituntilterminated
; The tray app. Not skipped in silent mode, so a silent upgrade brings it back too.
Filename: "{app}\{#AppExe}"; Description: "Open {#AppName}"; Flags: nowait postinstall

; No [UninstallRun]: ClaudeRc.exe --uninstall is run from CurUninstallStepChanged below, so its
; exit code is known before the keep-data question.

[Code]
const
  { ClaudeRc.exe --uninstall: the task runs another copy (the portable zip), left alone (SetupExitCodes.OtherCopy). }
  SetupOtherCopy = 3;
  TOKEN_QUERY = $0008;
  { TOKEN_INFORMATION_CLASS TokenElevationType and its TokenElevationTypeFull. }
  TokenElevationTypeClass = 18;
  TokenElevationTypeFull = 2;

var
  KeepData: Boolean;
  KeepWebView: Boolean;
  { An earlier install is there (set in PrepareToInstall, before the files are copied). }
  Upgrade: Boolean;

function OpenProcessToken(ProcessHandle: THandle; DesiredAccess: DWORD; var TokenHandle: THandle): BOOL;
  external 'OpenProcessToken@advapi32.dll stdcall';
function GetTokenInformation(TokenHandle: THandle; TokenInformationClass: Integer; var TokenInformation: DWORD;
  TokenInformationLength: DWORD; var ReturnLength: DWORD): BOOL;
  external 'GetTokenInformation@advapi32.dll stdcall';
function GetCurrentProcess: THandle;
  external 'GetCurrentProcess@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

{ Setup runs with an elevated UAC token ("Run as administrator", an elevated terminal, winget from
  one). [Run] entries and Exec() would inherit it, and runasoriginaluser can't drop it when Setup did
  not elevate itself: the task would be registered from an administrator's token (or for another
  account), so changing it later would need UAC. With UAC off there is no split token and nothing to
  drop, so IsAdmin alone does not stop Setup. }
function RunsElevated: Boolean;
var
  Token: THandle;
  ElevationType, Size: DWORD;
begin
  Result := False;
  if not IsAdmin() then
    Exit;
  if not OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, Token) then
  begin
    Result := True;
    Exit;
  end;
  try
    ElevationType := 0;
    if GetTokenInformation(Token, TokenElevationTypeClass, ElevationType, 4, Size) then
      Result := ElevationType = TokenElevationTypeFull
    else
      Result := True;
  finally
    CloseHandle(Token);
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if RunsElevated() then
  begin
    Log('Setup runs elevated (an administrator token); a per-user install must run as the user. Stopped.');
    if not WizardSilent() then
      MsgBox('{#AppName} installs for your own account and needs no administrator rights, ' +
        'but this Setup was started as an administrator (Run as administrator, or from an elevated ' +
        'terminal). Its scheduled task would then be made with administrator rights.' + #13#10#13#10 +
        'Run the installer again normally (double-click it).', mbError, MB_OK);
    Result := False;
  end;
end;

{ --quiet in a silent install: ClaudeRc.exe writes its errors to the setup log instead of showing a box. }
function QuietArg(Param: String): String;
begin
  if WizardSilent() then
    Result := ' --quiet'
  else
    Result := '';
end;

{ --fresh-install when no earlier install was there: ClaudeRc.exe --install then sets the app to
  start at sign-in. An upgrade keeps whatever the user chose (the Run value, or none). }
function FreshArg(Param: String): String;
begin
  if Upgrade then
    Result := ''
  else
    Result := ' --fresh-install';
end;

{ An upgrade: the installed ClaudeRc.exe ends the running tray and host, so their files can be
  replaced. The ON folders start again when --install runs the task afterwards. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
  Code: Integer;
begin
  Result := '';
  Exe := ExpandConstant('{app}\{#AppExe}');
  Upgrade := FileExists(Exe);
  if Upgrade then
  begin
    if not Exec(Exe, '--stop-for-update' + QuietArg(''), ExpandConstant('{app}'), SW_SHOWNORMAL, ewWaitUntilTerminated, Code) then
      Log('Could not run ' + Exe + ' --stop-for-update: ' + SysErrorMessage(Code))
    else
      Log(Exe + ' --stop-for-update ended with code ' + IntToStr(Code));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Exe, Params: String;
  Code: Integer;
  OtherCopy: Boolean;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Before anything is removed: every saved ON folder OFF (stops the sessions; the list keeps
      them OFF), tray and host ended, task deleted, the sign-in start (if it starts this copy) and
      the toast registration removed. }
    OtherCopy := False;
    Exe := ExpandConstant('{app}\{#AppExe}');
    Params := '--uninstall';
    if UninstallSilent() then
      Params := Params + ' --quiet';
    if not Exec(Exe, Params, ExpandConstant('{app}'), SW_SHOWNORMAL, ewWaitUntilTerminated, Code) then
      Log('Could not run ' + Exe + ' ' + Params + ': ' + SysErrorMessage(Code))
    else
    begin
      Log(Exe + ' ' + Params + ' ended with code ' + IntToStr(Code));
      OtherCopy := Code = SetupOtherCopy;
    end;

    { Never touches .claude.json. The task runs another copy (the portable zip): that copy still
      uses the data, so it is kept without asking. A silent uninstall keeps it too. }
    KeepWebView := OtherCopy;
    if OtherCopy then
    begin
      Log('The task runs another copy of {#AppName}; its folder list, settings, logs and console viewer data are kept.');
      KeepData := True;
    end
    else if UninstallSilent() then
      KeepData := True
    else
      KeepData := MsgBox(
        'Keep your folder list, settings and logs?' + #13#10#13#10 +
        ExpandConstant('{userappdata}\{#AppName}') + #13#10 +
        ExpandConstant('{localappdata}\{#AppName}\logs') + #13#10#13#10 +
        'Yes keeps them for a later install. Every folder in the list stays OFF until you turn it ' +
        'on again. No deletes them.',
        mbConfirmation, MB_YESNO) = IDYES;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    { The console viewer's WebView2 data goes (SPEC 14), unless another copy still uses it. }
    if not KeepWebView then
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
