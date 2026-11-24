<#
.SYNOPSIS
    Starts Windows Customizer as your shell, with a failsafe: whenever it ends, Explorer starts.

.DESCRIPTION
    When "Use Windows Customizer as shell" is on, Windows runs THIS script at sign-in instead of
    Explorer (it is set as the Shell value in your user's Winlogon key).

    It starts the app and waits for it for the whole session. If the app ends for ANY reason
    (it crashes at startup or later, is killed, or you choose Exit), Explorer is started at once,
    so you are never left without a taskbar and desktop.

    It also logs what happened to %AppData%\WindowsCustomizer\shell-launcher.log.

.PARAMETER App
    The program to start. Default: WindowsCustomizer.exe next to this script.

.PARAMETER FallbackShell
    What to start when the app ends. Default: explorer.exe.
#>
param(
    [string]$App = (Join-Path $PSScriptRoot 'WindowsCustomizer.exe'),
    [string]$FallbackShell = 'explorer.exe'
)

$log = Join-Path $env:APPDATA 'WindowsCustomizer\shell-launcher.log'
function Write-Log([string]$text) {
    try {
        New-Item -ItemType Directory -Force -Path (Split-Path $log) | Out-Null
        Add-Content -Path $log -Value ('{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $text)
    } catch { }
}

try {
    if (-not (Test-Path $App)) { throw "App not found: $App" }
    Write-Log "Starting $App as shell"
    # --shell: run as the shell (desktop, startup programs); --startup: start the startup programs once.
    $process = Start-Process -FilePath $App -ArgumentList '--shell', '--startup' -PassThru
    $process.WaitForExit()
    Write-Log "App ended (exit code $($process.ExitCode)); starting $FallbackShell"
}
catch {
    Write-Log "Could not run the app: $($_.Exception.Message); starting $FallbackShell"
}

# Never leave the user without a shell.
Start-Process -FilePath $FallbackShell
