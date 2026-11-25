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
    [string]$FallbackShell = 'explorer.exe',
    # For a test while already signed in: don't start the startup programs (they're running already).
    [switch]$NoStartupPrograms
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
    $arguments = if ($NoStartupPrograms) { @('--shell') } else { @('--shell', '--startup') }
    $process = Start-Process -FilePath $App -ArgumentList $arguments -PassThru
    $process.WaitForExit()
    Write-Log "App ended (exit code $($process.ExitCode)); starting $FallbackShell"
}
catch {
    Write-Log "Could not run the app: $($_.Exception.Message); starting $FallbackShell"
}

# Never leave the user without a shell. (If Explorer runs already, starting it again would only open a folder window.)
# Only this sign-in counts: with "Switch user", other accounts have their own Explorer running.
$session = (Get-Process -Id $PID).SessionId
if (Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($FallbackShell)) -ErrorAction SilentlyContinue |
        Where-Object SessionId -eq $session) {
    Write-Log "$FallbackShell is already running; not starting it again"
}
else { Start-Process -FilePath $FallbackShell }
