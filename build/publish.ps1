<#
.SYNOPSIS
  Builds the release files of claude-rc-windows: self-contained single-file publishes of
  ClaudeRc.exe and ClaudeRcHost.exe per architecture, the portable zips, the installers and
  SHA256SUMS.txt (SPEC 14). Used locally and by .github/workflows/release.yml.

.DESCRIPTION
  For each runtime (win-x64, win-arm64):
    artifacts/publish/<rid>/   ClaudeRc.exe, ClaudeRcHost.exe and the native DLLs WPF and WebView2
                               load from beside the exe (see docs/m5-decisions.md), plus README.md,
                               LICENSE and THIRD-PARTY-NOTICES.md
    artifacts/release/claude-rc-windows-<version>-<rid>-portable.zip
    artifacts/release/claude-rc-windows-<version>-<rid>-setup.exe   (unless -NoInstaller)
  and artifacts/release/SHA256SUMS.txt over everything in artifacts/release.

  Nothing is installed or run apart from one check: ClaudeRcHost.exe of the machine's own
  architecture is started with an unknown option and must exit at once with code 2 (it parses its
  command line before it touches anything), which proves the single-file bundle starts.

.PARAMETER Version
  The version, e.g. 1.2.0 or 1.2.0-rc.1 (no leading v). Default: from the git tag on HEAD
  (v1.2.0 -> 1.2.0), else 0.0.0-dev.

.PARAMETER Runtimes
  Runtime identifiers to build. Default: win-x64, win-arm64.

.PARAMETER NoInstaller
  Skip the Inno Setup installers (no ISCC.exe needed).

.PARAMETER Iscc
  Path of ISCC.exe. Default: on PATH, else the usual per-user and per-machine Inno Setup 6 folders.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string[]]$Runtimes = @('win-x64', 'win-arm64'),
    [string]$OutDir,
    [switch]$NoInstaller,
    [string]$Iscc
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $root 'artifacts' }
$publishRoot = Join-Path $OutDir 'publish'
$releaseDir = Join-Path $OutDir 'release'

function Invoke-Native([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

# ---- version ----
if (-not $Version) {
    $tag = git -C $root describe --tags --exact-match --match 'v[0-9]*' 2>$null
    $Version = if ($LASTEXITCODE -eq 0 -and $tag) { $tag.Trim().Substring(1) } else { '0.0.0-dev' }
    $global:LASTEXITCODE = 0
}
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "Version must look like 1.2.3 or 1.2.3-rc.1: $Version"
}
$numericVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
Write-Host "claude-rc-windows $Version ($numericVersion)"

# ---- Inno Setup ----
if (-not $NoInstaller) {
    if (-not $Iscc) {
        $candidates = @(
            (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source),
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
        ) | Where-Object { $_ -and (Test-Path $_) }
        $Iscc = $candidates | Select-Object -First 1
    }
    if (-not $Iscc -or -not (Test-Path $Iscc)) { throw 'ISCC.exe (Inno Setup 6) not found: install it, pass -Iscc, or use -NoInstaller.' }
}

if (Test-Path $publishRoot) { Remove-Item -Recurse -Force $publishRoot }
if (Test-Path $releaseDir) { Remove-Item -Recurse -Force $releaseDir }
New-Item -ItemType Directory -Force $publishRoot, $releaseDir | Out-Null

$docs = @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md') | ForEach-Object { Join-Path $root $_ }
$hostArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()

foreach ($rid in $Runtimes) {
    $arch = $rid -replace '^win-', ''
    if ($arch -notin @('x64', 'arm64')) { throw "unsupported runtime: $rid" }
    $out = Join-Path $publishRoot $rid
    Write-Host "== publish $rid -> $out"
    foreach ($project in @('src\ClaudeRc.Tray\ClaudeRc.Tray.csproj', 'src\ClaudeRc.Host\ClaudeRc.Host.csproj')) {
        Invoke-Native dotnet @(
            'publish', (Join-Path $root $project),
            '-c', 'Release', '-r', $rid, '--self-contained', 'true',
            '-p:PublishSingleFile=true',
            # WPF's and WebView2's native DLLs stay beside the exe (not extracted to %TEMP% at each start).
            '-p:IncludeNativeLibrariesForSelfExtract=false',
            # Not compressed: the host runs all the time, and a compressed bundle is held in memory.
            '-p:EnableCompressionInSingleFile=false',
            '-p:DebugType=none',
            '-p:PublishDocumentationFile=false', '-p:PublishReferencesDocumentationFiles=false',
            "-p:Version=$Version",
            '-o', $out)
    }
    Copy-Item $docs $out

    # What the app needs beside the exes; nothing else (no .dll of our own, no .pdb, no .xml).
    $expected = @('ClaudeRc.exe', 'ClaudeRcHost.exe', 'WebView2Loader.dll', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')
    foreach ($f in $expected) {
        if (-not (Test-Path (Join-Path $out $f))) { throw "missing in ${rid}: $f" }
    }
    $managed = Get-ChildItem $out -Recurse -File | Where-Object { $_.Name -like 'ClaudeRc*.dll' -or $_.Extension -in '.pdb', '.xml' }
    if ($managed) { throw "unexpected files in ${rid}: $($managed.Name -join ', ')" }
    foreach ($exe in 'ClaudeRc.exe', 'ClaudeRcHost.exe') {
        $info = (Get-Item (Join-Path $out $exe)).VersionInfo
        if ($info.FileVersion -ne $numericVersion -or -not $info.ProductVersion.StartsWith($Version)) {
            throw "$exe in $rid has version $($info.FileVersion) / $($info.ProductVersion), expected $numericVersion / $Version"
        }
    }
    if ($arch -eq $hostArch) {
        # Starts the bundle without doing anything: an unknown option ends the host with code 2.
        $p = Start-Process (Join-Path $out 'ClaudeRcHost.exe') -ArgumentList '--release-smoke-test' -PassThru -Wait -WindowStyle Hidden
        if ($p.ExitCode -ne 2) { throw "ClaudeRcHost.exe ($rid) smoke test: exit code $($p.ExitCode), expected 2" }
        Write-Host "   ClaudeRcHost.exe starts (exit code 2 for an unknown option)"
    }

    # ---- portable zip: one folder, claude-rc-windows\ ----
    $zip = Join-Path $releaseDir "claude-rc-windows-$Version-$rid-portable.zip"
    $stage = Join-Path $OutDir "stage-$rid"
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Force (Join-Path $stage 'claude-rc-windows') | Out-Null
    Copy-Item (Join-Path $out '*') (Join-Path $stage 'claude-rc-windows') -Recurse
    Compress-Archive -Path (Join-Path $stage 'claude-rc-windows') -DestinationPath $zip -CompressionLevel Optimal
    Remove-Item -Recurse -Force $stage
    Write-Host "   $zip"

    # ---- installer ----
    if (-not $NoInstaller) {
        Invoke-Native $Iscc @(
            '/Q',
            "/DAppVersion=$Version",
            "/DNumericVersion=$numericVersion",
            "/DArch=$arch",
            "/DSourceDir=$out",
            "/DOutputDir=$releaseDir",
            (Join-Path $root 'installer\claude-rc-windows.iss'))
        $setup = Join-Path $releaseDir "claude-rc-windows-$Version-$rid-setup.exe"
        if (-not (Test-Path $setup)) { throw "ISCC did not write $setup" }
        Write-Host "   $setup"
    }
}

# ---- checksums (sha256sum format: "<hash>  <name>") ----
$sums = Get-ChildItem $releaseDir -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
[System.IO.File]::WriteAllText((Join-Path $releaseDir 'SHA256SUMS.txt'), (($sums -join "`n") + "`n"))
Write-Host "== $releaseDir"
Get-ChildItem $releaseDir | ForEach-Object { Write-Host ("   {0,12:N0}  {1}" -f $_.Length, $_.Name) }
