<#
.SYNOPSIS
  Builds both installer architectures and launches the x64 installer.

.DESCRIPTION
  Runs the canonical installer workflow in build-only mode. It never requests
  commit, push, or release behavior, and it does not increment the version.
  If Version is omitted, the canonical workflow reuses the latest stable version.

.PARAMETER Version
  Optional exact installer version. A leading v is accepted.

.PARAMETER Configuration
  .NET publish configuration. Defaults to Release.

.EXAMPLE
  .\build-installers-launch-nopush.ps1

.EXAMPLE
  .\build-installers-launch-nopush.ps1 -Version 1.5.0
#>
[CmdletBinding()]
param(
  [string]$Version,
  [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$canonicalScript = Join-Path $PSScriptRoot 'build-all-installers.ps1'
$installerOutput = Join-Path $repoRoot 'installer\Output'

if (-not (Test-Path -LiteralPath $canonicalScript)) {
  throw "Canonical installer script not found: $canonicalScript"
}

$buildArgs = @{
  Variant = @('all')
  Configuration = $Configuration
}
if (-not [string]::IsNullOrWhiteSpace($Version)) {
  $buildArgs['Version'] = $Version
}

$buildStartedUtc = [DateTime]::UtcNow
Write-Host 'Building x64 and x86 installers without commit, push, release, or version increment...' -ForegroundColor Cyan
& $canonicalScript @buildArgs

$x64Installer = Get-ChildItem -LiteralPath $installerOutput -Filter 'DiskActivityMonitor-Setup-*-x64.exe' -File |
  Where-Object { $_.LastWriteTimeUtc -ge $buildStartedUtc.AddSeconds(-2) } |
  Sort-Object LastWriteTimeUtc -Descending |
  Select-Object -First 1

if (-not $x64Installer) {
  throw "A fresh x64 installer was not found in $installerOutput"
}

Write-Host "Launching installer: $($x64Installer.FullName)" -ForegroundColor Green
Start-Process -FilePath $x64Installer.FullName