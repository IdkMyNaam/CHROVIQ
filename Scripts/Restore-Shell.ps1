<#
.SYNOPSIS
    Makes Windows Explorer your shell again (undoes "Use Windows Customizer as shell").

.DESCRIPTION
    1. Saves a backup of your current Winlogon settings (a .reg file in
       %AppData%\WindowsCustomizer\Backups).
    2. Removes the "Shell" value from YOUR user's Winlogon key (HKCU). Windows then uses the
       normal shell (Explorer) again from the next sign-in. Other users were never changed.
    3. Starts Explorer right now if it isn't running, so you get your taskbar and desktop back
       without signing out.

    Works on its own: it does not need Windows Customizer to start.

    From a black screen: press Ctrl+Alt+Del > Task Manager > File > Run new task, and type:
        powershell -ExecutionPolicy Bypass -File "<path to this file>"
    or simply:  explorer.exe   (that gives you a desktop for now; run this script afterwards).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Restore-Shell.ps1 -DryRun
    Shows what would happen, changes nothing.
#>
param(
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$key = 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon'
$regKey = 'HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon'
$backups = Join-Path $env:APPDATA 'WindowsCustomizer\Backups'

$current = (Get-ItemProperty -Path $key -Name 'Shell' -ErrorAction SilentlyContinue).Shell
if ($current) { Write-Host "Your user's shell is set to: $current" }
else { Write-Host "Your user has no own shell setting (Windows uses Explorer)." }

# 1. Backup.
$backupFile = Join-Path $backups ("winlogon-before-restore-{0:yyyyMMdd-HHmmss}.reg" -f (Get-Date))
if ($DryRun) { Write-Host "[dry run] Would save a backup to $backupFile" }
else {
    New-Item -ItemType Directory -Force -Path $backups | Out-Null
    & reg.exe export $regKey $backupFile /y | Out-Null
    Write-Host "Backup saved: $backupFile"
}

# 2. Remove our shell setting.
if ($current) {
    if ($DryRun) { Write-Host "[dry run] Would remove the Shell value from $regKey" }
    else {
        Remove-ItemProperty -Path $key -Name 'Shell'
        Write-Host 'Your shell is Explorer again from the next sign-in.'
    }
}

# 3. Explorer now.
if (Get-Process -Name 'explorer' -ErrorAction SilentlyContinue) {
    Write-Host 'Explorer is already running.'
}
elseif ($DryRun) { Write-Host '[dry run] Would start explorer.exe' }
else {
    Start-Process 'explorer.exe'
    Write-Host 'Explorer started.'
}

if ($DryRun) { Write-Host 'Dry run: nothing was changed.' -ForegroundColor Yellow }
else { Write-Host 'Done.' -ForegroundColor Green }
