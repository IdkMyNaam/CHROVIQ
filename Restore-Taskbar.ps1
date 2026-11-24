<#
.SYNOPSIS
    Brings the Windows taskbar back if Windows Customizer hid it (for example after a crash).

.DESCRIPTION
    1. Closes Windows Customizer if it is running (otherwise it would hide the taskbar again).
    2. Starts Explorer if it is not running.
    3. Turns taskbar auto-hide off (unless -KeepAutoHide is given).
    4. Shows the taskbar on every monitor.

    Works on its own: it does not need the app to start.

.EXAMPLE
    Right-click this file > Run with PowerShell

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Restore-Taskbar.ps1 -KeepAutoHide
    Use -KeepAutoHide if you had auto-hide turned on yourself before using the app.
#>
param(
    [switch]$KeepAutoHide
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
}
'@

# 1. The app would hide the taskbar again, so close it first.
$app = Get-Process -Name 'WindowsCustomizer' -ErrorAction SilentlyContinue
if ($app) {
    Write-Host 'Closing Windows Customizer...'
    $app | Stop-Process -Force
    Start-Sleep -Seconds 1
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

Write-Host 'Taskbar restored.' -ForegroundColor Green
