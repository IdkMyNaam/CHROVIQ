<#
.SYNOPSIS
    Brings the Windows taskbar back if CHROVIQ hid it (for example after a crash).

.DESCRIPTION
    1. Closes CHROVIQ if it is running (otherwise it would hide the taskbar again).
    2. Starts Explorer if it is not running.
    3. Turns taskbar auto-hide off (unless -KeepAutoHide is given).
    4. Shows the taskbar on every monitor and puts it back on top.
    5. Asks all programs to add their tray icons again, so they show in the Windows tray
       (the custom taskbar's Tray module collects them while it runs).

    Works on its own: it does not need the app to start.

.EXAMPLE
    Right-click this file > Run with PowerShell

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Restore-Taskbar.ps1 -KeepAutoHide
    Use -KeepAutoHide if you had auto-hide turned on yourself before using the app.

.EXAMPLE
    Restore-Taskbar.ps1 -WaitForProcessId 1234 -KeepAutoHide
    Watchdog mode (started by the app itself, hidden): waits until the app's process ends and then
    restores the taskbar, for when the app was killed (Task Manager, Visual Studio's stop button) and
    couldn't clean up. Doesn't close anything, and does nothing if the app was started again meanwhile.
#>
param(
    [switch]$KeepAutoHide,
    [int]$WaitForProcessId = 0
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class TaskbarRestore
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    public static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string title);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    public static extern bool SendNotifyMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hwnd);

    /// <summary>True if a visible window of that process is "not responding".</summary>
    public static bool IsFrozen(int processId)
    {
        bool frozen = false;
        EnumWindows((hwnd, l) =>
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == processId && IsWindowVisible(hwnd) && IsHungAppWindow(hwnd)) { frozen = true; return false; }
            return true;
        }, IntPtr.Zero);
        return frozen;
    }
}
'@

function Show-WindowsTaskbar {
    $tray = [TaskbarRestore]::FindWindowEx([IntPtr]::Zero, [IntPtr]::Zero, 'Shell_TrayWnd', $null)
    if ($tray -eq [IntPtr]::Zero) { return }
    if (-not $KeepAutoHide) {
        $data = New-Object TaskbarRestore+APPBARDATA
        $data.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($data)
        $data.hWnd = $tray
        $data.lParam = [IntPtr]2
        [void][TaskbarRestore]::SHAppBarMessage(10, [ref]$data)
    }
    [void][TaskbarRestore]::ShowWindow($tray, 5)
    $other = [IntPtr]::Zero
    while (($other = [TaskbarRestore]::FindWindowEx([IntPtr]::Zero, $other, 'Shell_SecondaryTrayWnd', $null)) -ne [IntPtr]::Zero) {
        [void][TaskbarRestore]::ShowWindow($other, 5)
    }
    [void][TaskbarRestore]::SetWindowPos($tray, [IntPtr](-1), 0, 0, 0, 0, 0x13)
}

function Write-Trace([string]$text) {
    try { Add-Content -Path (Join-Path $env:APPDATA 'WindowsCustomizer\trace.log') -Value ('{0:HH:mm:ss.fff} [watchdog] {1}' -f (Get-Date), $text) } catch { }
}

if ($WaitForProcessId -gt 0) {
    # Watchdog mode: wait for the app to end (normally the app stops this watchdog itself first).
    # Meanwhile: if the app stops responding for 8 seconds, show the Windows taskbar, so you are never left
    # without one. (If the app recovers, it hides it again by itself.)
    $frozenFor = 0
    $shownForFreeze = $false
    while (Get-Process -Id $WaitForProcessId -ErrorAction SilentlyContinue) {
        Start-Sleep -Seconds 2
        if ([TaskbarRestore]::IsFrozen($WaitForProcessId)) { $frozenFor += 2 }
        else {
            if ($shownForFreeze) { Write-Trace 'CHROVIQ responds again' }
            $frozenFor = 0
            $shownForFreeze = $false
        }
        if ($frozenFor -ge 8 -and -not $shownForFreeze) {
            Write-Trace 'CHROVIQ has not responded for 8 s: showing the Windows taskbar'
            Show-WindowsTaskbar
            $shownForFreeze = $true
        }
    }
    Start-Sleep -Seconds 1
    # Started again meanwhile? Then the new instance manages the taskbar; leave it alone.
    if (Get-Process -Name 'WindowsCustomizer' -ErrorAction SilentlyContinue) { exit 0 }
}
else {
    # 1. The app would hide the taskbar again, so close it first.
    $app = Get-Process -Name 'WindowsCustomizer' -ErrorAction SilentlyContinue
    if ($app) {
        Write-Host 'Closing CHROVIQ...'
        $app | Stop-Process -Force
        Start-Sleep -Seconds 1
    }
}

# 2. No Explorer means no taskbar at all.
if (-not (Get-Process -Name 'explorer' -ErrorAction SilentlyContinue)) {
    Write-Host 'Explorer is not running, starting it...'
    Start-Process 'explorer.exe'
    Start-Sleep -Seconds 4
}

$primary = [TaskbarRestore]::FindWindowEx([IntPtr]::Zero, [IntPtr]::Zero, 'Shell_TrayWnd', $null)
if ($primary -eq [IntPtr]::Zero) {
    Write-Host 'The taskbar window was not found. Sign out and back in, or restart Explorer from Task Manager.' -ForegroundColor Yellow
    exit 1
}

# 3. Auto-hide off (ABM_SETSTATE = 10, ABS_ALWAYSONTOP = 2 means "not auto-hide").
if (-not $KeepAutoHide) {
    $data = New-Object TaskbarRestore+APPBARDATA
    $data.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($data)
    $data.hWnd = $primary
    $data.lParam = [IntPtr]2
    [void][TaskbarRestore]::SHAppBarMessage(10, [ref]$data)
    Write-Host 'Auto-hide turned off.'
}

# 4. Show the main taskbar and the taskbars on other monitors (SW_SHOW = 5).
[void][TaskbarRestore]::ShowWindow($primary, 5)
$secondary = [IntPtr]::Zero
while (($secondary = [TaskbarRestore]::FindWindowEx([IntPtr]::Zero, $secondary, 'Shell_SecondaryTrayWnd', $null)) -ne [IntPtr]::Zero) {
    [void][TaskbarRestore]::ShowWindow($secondary, 5)
}

# Back on top (HWND_TOPMOST = -1; SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE = 0x13).
[void][TaskbarRestore]::SetWindowPos($primary, [IntPtr](-1), 0, 0, 0, 0, 0x13)

# 5. "TaskbarCreated" is what Explorer sends when it starts: every program then adds its tray icons again.
$taskbarCreated = [TaskbarRestore]::RegisterWindowMessage('TaskbarCreated')
[void][TaskbarRestore]::SendNotifyMessage([IntPtr]0xFFFF, $taskbarCreated, [IntPtr]::Zero, [IntPtr]::Zero)
Write-Host 'Asked programs to show their tray icons again.'

Write-Host 'Taskbar restored.' -ForegroundColor Green
