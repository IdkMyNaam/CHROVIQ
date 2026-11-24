<#
.SYNOPSIS
    Brings the Windows taskbar back if Windows Customizer hid it (for example after a crash).

.DESCRIPTION
    1. Closes Windows Customizer if it is running (otherwise it would hide the taskbar again).
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
}
'@

if ($WaitForProcessId -gt 0) {
    # Watchdog mode: wait for the app to end (normally the app stops this watchdog itself first).
    Wait-Process -Id $WaitForProcessId -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    # Started again meanwhile? Then the new instance manages the taskbar; leave it alone.
    if (Get-Process -Name 'WindowsCustomizer' -ErrorAction SilentlyContinue) { exit 0 }
}
else {
    # 1. The app would hide the taskbar again, so close it first.
    $app = Get-Process -Name 'WindowsCustomizer' -ErrorAction SilentlyContinue
    if ($app) {
        Write-Host 'Closing Windows Customizer...'
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
