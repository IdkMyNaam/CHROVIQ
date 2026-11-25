<#
.SYNOPSIS
    Builds the CHROVIQ setup: publishes the app (with .NET included) and packs it with Inno Setup.

.DESCRIPTION
    1. dotnet publish with the Release profile (bin\publish).
    2. Inno Setup's compiler (iscc.exe) packs it into bin\installer\CHROVIQ-Setup-<version>.exe.
    The version comes from <Version> in WindowsCustomizer.csproj.

    Needs Inno Setup 6 (https://jrsoftware.org/isinfo.php). Run from anywhere:
        powershell -ExecutionPolicy Bypass -File Scripts\Build-Installer.ps1
#>
param(
    # Path to iscc.exe, if Inno Setup isn't in a standard place.
    [string]$Iscc
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'WindowsCustomizer.csproj'

# The version from the project file.
[xml]$xml = Get-Content $project
$version = ($xml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw "No <Version> in WindowsCustomizer.csproj" }
Write-Host "CHROVIQ $version"

# Inno Setup's compiler.
if (-not $Iscc) {
    $Iscc = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Iscc -or -not (Test-Path $Iscc)) { throw "Inno Setup 6 (ISCC.exe) wasn't found. Install it from https://jrsoftware.org/isinfo.php or pass -Iscc." }

# 1. Publish (a clean folder, so no old files end up in the setup).
$publish = Join-Path $root 'bin\publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
Write-Host 'Publishing...'
dotnet publish $project -p:PublishProfile=Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# 2. The setup.
Write-Host 'Building the setup...'
& $Iscc "/DAppVersion=$version" (Join-Path $root 'Installer\WindowsCustomizer.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)" }

$setup = Join-Path $root "bin\installer\CHROVIQ-Setup-$version.exe"
Write-Host ''
Write-Host "Done: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
