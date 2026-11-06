param([int] $Seconds = 30, [string] $Out)
Add-Type @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class W {
  delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(P p, IntPtr l);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  public static List<string> List() {
    var r = new List<string>();
    EnumWindows((h, l) => { if (IsWindowVisible(h)) { uint pid; GetWindowThreadProcessId(h, out pid);
      var t = new StringBuilder(256); GetWindowText(h, t, 256); var c = new StringBuilder(256); GetClassName(h, c, 256);
      r.Add(h.ToInt64().ToString("X") + " pid=" + pid + " class=" + c + " title=" + t); } return true; }, IntPtr.Zero);
    return r;
  }
}
'@
$base = [W]::List()
"$(Get-Date -Format HH:mm:ss.fff) baseline: $($base.Count) visible top-level windows" | Out-File $Out
$end = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $end) {
  Start-Sleep -Milliseconds 500
  $now = [W]::List()
  foreach ($w in $now) { if ($base -notcontains $w) { "$(Get-Date -Format HH:mm:ss.fff) NEW $w" | Out-File $Out -Append; $base += $w } }
}
"$(Get-Date -Format HH:mm:ss.fff) done" | Out-File $Out -Append
