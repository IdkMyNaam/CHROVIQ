<#
.SYNOPSIS
    Makes Windows Explorer the shell again (undoes "Use Windows Customizer as shell").

.DESCRIPTION
    1. Saves a backup of the Winlogon settings (a .reg file in that user's
       %AppData%\WindowsCustomizer\Backups).
    2. Removes the "Shell" value from that user's own Winlogon key (HKCU). Windows then uses the
       normal shell (Explorer) again from the next sign-in. Other users were never changed.
    3. For your own account: starts Explorer right now if it isn't running, so you get your taskbar
       and desktop back without signing out.

    Works on its own: it does not need Windows Customizer to start.

    From a black screen: press Ctrl+Alt+Del > Task Manager > File > Run new task, and type:
        powershell -ExecutionPolicy Bypass -File "<path to this file>"
    or simply:  explorer.exe   (that gives you a desktop for now; run this script afterwards).

    From ANOTHER administrator account (for example a backup admin), to fix a user who can't
    sign in properly: run PowerShell as administrator and use -ForUser <user name>.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Restore-Shell.ps1 -DryRun
    Shows what would happen, changes nothing.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Restore-Shell.ps1 -ForUser idkmy
    From a backup admin account (PowerShell "Run as administrator"): gives the user "idkmy"
    Explorer back. Works whether that user is signed in or not.
#>
param(
    [switch]$DryRun,
    [string]$ForUser
)

$ErrorActionPreference = 'Stop'
$winlogon = 'Software\Microsoft\Windows NT\CurrentVersion\Winlogon'

function Save-Backup([string]$regPath, [string]$folder) {
    $file = Join-Path $folder ("winlogon-before-restore-{0:yyyyMMdd-HHmmss}.reg" -f (Get-Date))
    if ($DryRun) { Write-Host "[dry run] Would save a backup to $file"; return }
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    & reg.exe export $regPath $file /y | Out-Null
    Write-Host "Backup saved: $file"
}

function Remove-ShellValue([string]$psPath, [string]$who) {
    $current = (Get-ItemProperty -Path $psPath -Name 'Shell' -ErrorAction SilentlyContinue).Shell
    if (-not $current) { Write-Host "$who has no own shell setting (Windows uses Explorer). Nothing to undo."; return }
    Write-Host "$who's shell is set to: $current"
    if ($DryRun) { Write-Host "[dry run] Would remove the Shell value"; return }
    Remove-ItemProperty -Path $psPath -Name 'Shell'
    Write-Host "$who gets Explorer again from the next sign-in."
}

if (-not $ForUser -or $ForUser -eq $env:USERNAME) {
    # ---------- Your own account ----------
    Save-Backup "HKCU\$winlogon" (Join-Path $env:APPDATA 'WindowsCustomizer\Backups')
    Remove-ShellValue "HKCU:\$winlogon" 'Your user'

    if (Get-Process -Name 'explorer' -ErrorAction SilentlyContinue) { Write-Host 'Explorer is already running.' }
    elseif ($DryRun) { Write-Host '[dry run] Would start explorer.exe' }
    else { Start-Process 'explorer.exe'; Write-Host 'Explorer started.' }
}
else {
    # ---------- Another user (run from an administrator account) ----------
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Host 'To change another user, run PowerShell as administrator (right-click > Run as administrator).' -ForegroundColor Red
        exit 1
    }

    try { $sid = (New-Object Security.Principal.NTAccount($ForUser)).Translate([Security.Principal.SecurityIdentifier]).Value }
    catch { Write-Host "There is no user called '$ForUser' on this PC." -ForegroundColor Red; exit 1 }
    $profilePath = (Get-CimInstance Win32_UserProfile | Where-Object SID -eq $sid).LocalPath
    if (-not $profilePath) { Write-Host "'$ForUser' has never signed in on this PC: nothing to undo."; exit 0 }
    $backupFolder = Join-Path $profilePath 'AppData\Roaming\WindowsCustomizer\Backups'

    if (Test-Path "Registry::HKEY_USERS\$sid") {
        # Signed in right now: their settings are loaded already.
        Save-Backup "HKU\$sid\$winlogon" $backupFolder
        Remove-ShellValue "Registry::HKEY_USERS\$sid\$winlogon" $ForUser
    }
    else {
        # Signed out: load their registry file for a moment, change it, unload it again.
        $mount = 'WC_RestoreShell'
        & reg.exe load "HKU\$mount" (Join-Path $profilePath 'NTUSER.DAT') | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Host "Could not open $ForUser's settings (is the account signed in elsewhere?)." -ForegroundColor Red; exit 1 }
        try {
            Save-Backup "HKU\$mount\$winlogon" $backupFolder
            Remove-ShellValue "Registry::HKEY_USERS\$mount\$winlogon" $ForUser
        }
        finally {
            [GC]::Collect(); [GC]::WaitForPendingFinalizers() # release PowerShell's handles before unloading
            & reg.exe unload "HKU\$mount" | Out-Null
        }
    }
}

if ($DryRun) { Write-Host 'Dry run: nothing was changed.' -ForegroundColor Yellow }
else { Write-Host 'Done.' -ForegroundColor Green }
