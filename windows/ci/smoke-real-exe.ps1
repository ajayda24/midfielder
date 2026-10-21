# Starts the published Midfielder.exe the way a user does (double-click: tray + main window) and checks the
# real windows on screen: the process keeps responding and no window is a blank white rectangle. The unit
# tests draw the WPF tree offscreen, so only this catches a window that never paints.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $Screens,
    [int] $WaitSeconds = 12
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class Win {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static List<IntPtr> Windows(uint pid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h)) {
                var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
                if (sb.Length > 0) list.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
    public static string Title(IntPtr h) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }
}
'@
[Win]::SetProcessDPIAware() | Out-Null
New-Item -ItemType Directory -Force $Screens | Out-Null

$proc = Start-Process -FilePath $Exe -PassThru
$problems = @()
try {
    Start-Sleep -Seconds $WaitSeconds
    $proc.Refresh()
    if ($proc.HasExited) { throw "Midfielder.exe exited early (code $($proc.ExitCode))" }
    if (-not $proc.Responding) {
        $problems += 'the process is not responding'
        # Where is the UI thread stuck? Managed stacks of every thread (works on single-file, self-contained).
        dotnet tool install --global dotnet-stack 2>&1 | Out-Null
        $stack = Join-Path $env:USERPROFILE '.dotnet\tools\dotnet-stack.exe'
        if (Test-Path $stack) { Write-Host '--- managed stacks ---'; & $stack report --process-id $proc.Id }
        else { Write-Host 'dotnet-stack could not be installed' }
    }

    $windows = [Win]::Windows([uint32]$proc.Id)
    if ($windows.Count -eq 0) { $problems += 'no visible window' }
    $i = 0
    foreach ($h in $windows) {
        $title = [Win]::Title($h)
        [Win]::SetForegroundWindow($h) | Out-Null
        Start-Sleep -Milliseconds 800
        $r = New-Object Win+RECT
        [Win]::GetWindowRect($h, [ref]$r) | Out-Null
        $w = [Math]::Max(1, $r.Right - $r.Left); $hgt = [Math]::Max(1, $r.Bottom - $r.Top)
        $bmp = New-Object System.Drawing.Bitmap $w, $hgt
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
        $g.Dispose()
        $name = "windows-real-exe-$i.png"
        $bmp.Save((Join-Path $Screens $name), [System.Drawing.Imaging.ImageFormat]::Png)
        # Share of (near-)white pixels, sampled on a grid.
        $white = 0; $total = 0
        for ($y = 0; $y -lt $hgt; $y += 6) {
            for ($x = 0; $x -lt $w; $x += 6) {
                $c = $bmp.GetPixel($x, $y); $total++
                if ($c.R -ge 250 -and $c.G -ge 250 -and $c.B -ge 250) { $white++ }
            }
        }
        $bmp.Dispose()
        $share = [Math]::Round(100.0 * $white / [Math]::Max(1, $total), 1)
        Write-Host "window '$title' at $($r.Left),$($r.Top) ${w}x${hgt}: $share% white -> $name"
        if ($share -gt 97) { $problems += "window '$title' is blank ($share% white)" }
        $i++
    }
}
finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    $log = Join-Path $env:LOCALAPPDATA 'Midfielder\midfielder.log'
    if (Test-Path $log) {
        $lines = Get-Content $log
        $start = ($lines | Select-String -SimpleMatch 'Midfielder starting' | Select-Object -Last 1).LineNumber
        $run = if ($start) { $lines[($start - 1)..($lines.Count - 1)] } else { $lines }
        Write-Host '--- midfielder.log (this run) ---'; $run | Select-Object -Last 60 | Write-Host
        if (-not ($run -match 'Main window rendered')) { $problems += 'the log never says "Main window rendered"' }
    }
    else { $problems += 'no log file' }
}
if ($problems.Count -gt 0) { throw ('Real exe check failed: ' + ($problems -join '; ')) }
Write-Host 'Real exe check passed.'
