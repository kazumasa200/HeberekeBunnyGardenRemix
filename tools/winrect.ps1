# Prints the game window's rectangle and style (dev helper).
Add-Type @'
using System; using System.Runtime.InteropServices;
public class GameWin {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  public struct RECT { public int L, T, R, B; }
}
'@
$p = Get-Process "HEBEREKE BUNNY GARDEN"
$r = New-Object GameWin+RECT
[GameWin]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
"rect {0},{1} {2}x{3} style 0x{4:X}" -f $r.L, $r.T, ($r.R - $r.L), ($r.B - $r.T), [GameWin]::GetWindowLong($p.MainWindowHandle, -16)
