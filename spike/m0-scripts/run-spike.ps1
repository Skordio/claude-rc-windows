param(
  [Parameter(Mandatory)] [string] $Exe,
  [Parameter(Mandatory)] [string] $Cwd,
  [Parameter(Mandatory)] [string] $Out,
  [double] $Timeout = 60,
  [double] $AutoStop = -1,
  [double] $StopGrace = 5,
  [switch] $NoWait,
  [string[]] $ChildArgs = @()
)
$spike = 'C:\Users\Steven\Github\claude-rc-windows\spike\ConPtySpike\bin\Release\net10.0-windows\ConPtySpike.exe'
$psi = [System.Diagnostics.ProcessStartInfo]::new($spike)
$psi.UseShellExecute = $false
foreach ($a in @('--exe', $Exe, '--cwd', $Cwd, '--out', $Out, '--timeout', "$Timeout", '--auto-stop', "$AutoStop", '--stop-grace', "$StopGrace", '--')) { $psi.ArgumentList.Add($a) }
foreach ($a in $ChildArgs) { $psi.ArgumentList.Add($a) }
$p = [System.Diagnostics.Process]::Start($psi)
Add-Content -Path "$PSScriptRoot\started-pids.txt" -Value "$(Get-Date -Format o) spike pid=$($p.Id) out=$Out"
"spike pid=$($p.Id)"
if (-not $NoWait) { $p.WaitForExit(); "spike exit=$($p.ExitCode)" }
