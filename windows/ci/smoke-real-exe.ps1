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
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    public static bool Cloaked(IntPtr h) { int v; return DwmGetWindowAttribute(h, 14, out v, 4) == 0 && v != 0; }
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

# Screenshot of a window; returns the share of near-white pixels (a blank window is ~100%).
function Snap([IntPtr] $h, [string] $name, [string] $label) {
    $r = New-Object Win+RECT
    [Win]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = [Math]::Max(1, $r.Right - $r.Left); $hgt = [Math]::Max(1, $r.Bottom - $r.Top)
    $bmp = New-Object System.Drawing.Bitmap $w, $hgt
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $g.Dispose()
    $bmp.Save((Join-Path $Screens $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $white = 0; $total = 0
    for ($y = 0; $y -lt $hgt; $y += 6) {
        for ($x = 0; $x -lt $w; $x += 6) {
            $c = $bmp.GetPixel($x, $y); $total++
            if ($c.R -ge 250 -and $c.G -ge 250 -and $c.B -ge 250) { $white++ }
        }
    }
    $bmp.Dispose()
    $share = [Math]::Round(100.0 * $white / [Math]::Max(1, $total), 1)
    Write-Host "window '$label' at $($r.Left),$($r.Top) ${w}x${hgt}: $share% white -> $name"
    return $share
}

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
        $share = Snap $h "windows-real-exe-$i.png" $title
        if ($share -gt 97) { $problems += "window '$title' is blank ($share% white)" }
        # The taskbar button draws the window's icon: both sizes must be set.
        if ([Win]::SendMessage($h, 0x7F, [IntPtr]1, [IntPtr]::Zero) -eq [IntPtr]::Zero) { $problems += "window '$title' has no icon" }
        $i++
    }

    # A taskbar button for the main window (UI Automation on the taskbar; reported only where there is one).
    $main = $windows | Where-Object { [Win]::Title($_) -eq 'Midfielder' } | Select-Object -First 1
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $tray = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'Shell_TrayWnd')))
        if ($null -eq $tray) { Write-Host 'taskbar: none on this runner (not checked)' }
        else {
            $buttons = $tray.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
                Where-Object { $_.Current.Name -like '*Midfielder*' }
            if (@($buttons).Count -gt 0) { Write-Host "taskbar: button found ($(@($buttons)[0].Current.Name))" }
            else { $problems += 'no taskbar button for Midfielder' }
        }
    }
    catch { Write-Host "taskbar: couldn't check ($($_.Exception.Message))" }

    # Reopen like a user does: close the window (it hides; the app stays in the tray), let it go idle (efficiency
    # mode), then start Midfielder again: the running one must show its window at once, painted.
    if ($null -ne $main) {
        # The pairing window (mostly white, on top) would cover the main window in the screenshots: close it.
        [Win]::Windows([uint32]$proc.Id) | Where-Object { [Win]::Title($_) -eq 'Pair your phone' } |
            ForEach-Object { [Win]::PostMessage($_, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
        [Win]::PostMessage($main, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        Start-Sleep -Seconds 2
        $proc.Refresh()
        if ($proc.HasExited) { $problems += 'closing the window exited the app (it should stay in the tray)' }
        elseif (@([Win]::Windows([uint32]$proc.Id) | Where-Object { $_ -eq $main }).Count -gt 0) { $problems += 'closing the window did not hide it' }
        else {
            Start-Sleep -Seconds 15
            $clock = [Diagnostics.Stopwatch]::StartNew()
            Start-Process -FilePath $Exe | Out-Null
            $shown = $null
            while ($clock.ElapsedMilliseconds -lt 5000 -and $null -eq $shown) {
                Start-Sleep -Milliseconds 25
                # On screen = visible and not cloaked (the app reveals the window once it has drawn it).
                $shown = [Win]::Windows([uint32]$proc.Id) | Where-Object { [Win]::Title($_) -eq 'Midfielder' -and -not [Win]::Cloaked($_) } | Select-Object -First 1
            }
            $ms = $clock.ElapsedMilliseconds
            if ($null -eq $shown) { $problems += 'starting Midfielder again did not show the window' }
            else {
                # The first frame on screen (would catch a white flash), then the settled one.
                $early = Snap $shown 'windows-real-exe-reopen-early.png' 'reopen (early)'
                Start-Sleep -Milliseconds 600
                $late = Snap $shown 'windows-real-exe-reopen.png' 'reopen'
                Write-Host "Reopen: window visible after $ms ms (includes starting the second exe)"
                if ($ms -gt 2500) { $problems += "reopening took $ms ms" }
                if ($early -gt 90 -or $late -gt 90) { $problems += "the reopened window was blank ($early% / $late% white)" }
            }
        }
    }
}
finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    $log = Join-Path $env:LOCALAPPDATA 'Midfielder\midfielder.log'
    if (Test-Path $log) {
        $lines = Get-Content $log
        $start = ($lines | Select-String -SimpleMatch 'Midfielder starting' | Select-Object -Last 1).LineNumber
        $run = if ($start) { $lines[($start - 1)..($lines.Count - 1)] } else { $lines }
        Write-Host '--- midfielder.log (this run) ---'; $run | Select-Object -First 400 | Write-Host
        $rendered = $run | Select-String -Pattern 'Main window rendered in (\d+) ms' | Select-Object -First 1
        if (-not $rendered) { $problems += 'the log never says "Main window rendered"' }
        else {
            $ms = [int]$rendered.Matches[0].Groups[1].Value
            Write-Host "Main window first frame: $ms ms"
            if ($ms -gt 3000) { $problems += "the main window took $ms ms to draw its first frame (limit 3000)" }
        }
        # Any error the app logged on a clean start is a bug (e.g. the pairing window failing to open).
        $errors = @($run | Select-String -SimpleMatch ' ERROR ')
        if ($errors.Count -gt 0) { $problems += "the app logged $($errors.Count) error(s): $($errors[0].Line)" }
    }
    else { $problems += 'no log file' }
}
if ($problems.Count -gt 0) { throw ('Real exe check failed: ' + ($problems -join '; ')) }
Write-Host 'Real exe check passed.'
