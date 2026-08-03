# Builds the Disk Activity Monitor Inno Setup installer.
# Publishes the service and tray as self-contained (no .NET runtime needed on target),
# regenerates the app icon, then compiles installer\DiskActivityMonitor.iss with ISCC.
#
# Examples:
#   .\build-installer.ps1                            # x64 only (default)
#   .\build-installer.ps1 -Runtime win-x86           # x86 only
#   .\build-installer.ps1 -All                       # both x64 and x86
#   .\build-installer.ps1 -All -Version 1.2.0 -Push  # delegate to scripts\build-all-installers.ps1 canonical workflow
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version = '1.0.0',
    [string]$Runtime = 'win-x64',
    [switch]$All,
    # Delegates commit/push/release behavior to scripts\build-all-installers.ps1.
    [switch]$Push,
    [switch]$SkipRelease,
    [ValidateSet('Prompt', 'Draft', 'Published')]
    [string]$ReleaseMode = 'Prompt',
    [string]$CopilotPath
)

$ErrorActionPreference = 'Stop'
$installerDir = $PSScriptRoot
$root         = Split-Path -Parent $installerDir
$serviceProj  = Join-Path $root 'src\DiskActivityMonitor.Service\DiskActivityMonitor.Service.csproj'
$trayProj     = Join-Path $root 'src\DiskActivityMonitor.Tray\DiskActivityMonitor.Tray.csproj'
$publishRoot  = Join-Path $installerDir 'publish'
$serviceOut   = Join-Path $publishRoot 'service'
$trayOut      = Join-Path $publishRoot 'tray'

$normalizedVersion = $Version.Trim() -replace '^v', ''
if ($normalizedVersion -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "Invalid version '$Version'. Use semantic versioning like 1.6.0."
}
$Version = $normalizedVersion
$fileVersion = (($Version -split '[-+]')[0] + '.0')

if ($Push) {
    $canonicalScript = Join-Path $root 'scripts\build-all-installers.ps1'
    if (-not (Test-Path -LiteralPath $canonicalScript)) {
        throw "Canonical script not found: $canonicalScript"
    }

    $variant = if ($All) {
        @('all')
    }
    elseif ($Runtime -eq 'win-x86') {
        @('x86')
    }
    else {
        @('x64')
    }

    $delegateArgs = @{
        Variant = $variant
        Configuration = $Configuration
        Push = $true
        ReleaseMode = $ReleaseMode
        SkipRelease = $SkipRelease
    }
    if ($PSBoundParameters.ContainsKey('Version')) {
        $delegateArgs['Version'] = $Version
    }
    if (-not [string]::IsNullOrWhiteSpace($CopilotPath)) {
        $delegateArgs['CopilotPath'] = $CopilotPath
    }
    if ($WhatIfPreference) {
        $delegateArgs['WhatIf'] = $true
    }

    Write-Host 'Delegating -Push workflow to scripts\build-all-installers.ps1...' -ForegroundColor Cyan
    & $canonicalScript @delegateArgs
    return
}
# Locate the Inno Setup compiler.
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'ISCC.exe (Inno Setup 6) not found. Install Inno Setup 6 first.' }

# 1. Regenerate the shared app icon.
Write-Host 'Generating app icon...' -ForegroundColor Cyan
& (Join-Path $root 'scripts\make-icon.ps1')

$runtimes = if ($All) { @('win-x64', 'win-x86') } else { @($Runtime) }
$builtInstallers = @()

foreach ($rid in $runtimes) {
    $arch = $rid -replace 'win-', ''
    Write-Host "`n=== Building $arch installer ===" -ForegroundColor Magenta

    # 2. Clean previous publish output.
    if (Test-Path $publishRoot) { Remove-Item -Recurse -Force $publishRoot }

    # 3. Publish both apps self-contained.
    Write-Host "Publishing collector service (self-contained, $rid)..." -ForegroundColor Cyan
    dotnet publish $serviceProj -c $Configuration -r $rid --self-contained true `
        -p:PublishSingleFile=false -p:Version=$Version -p:InformationalVersion=$Version -p:FileVersion=$fileVersion -o $serviceOut --nologo
    if ($LASTEXITCODE -ne 0) { throw "Service publish failed ($rid)." }

    Write-Host "Publishing tray dashboard (self-contained, $rid)..." -ForegroundColor Cyan
    dotnet publish $trayProj -c $Configuration -r $rid --self-contained true `
        -p:PublishSingleFile=false -p:Version=$Version -p:InformationalVersion=$Version -p:FileVersion=$fileVersion -o $trayOut --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tray publish failed ($rid)." }

    # 4. Compile the installer.
    $iss = Join-Path $installerDir 'DiskActivityMonitor.iss'
    Write-Host "Compiling $arch installer with $iscc ..." -ForegroundColor Cyan
    & $iscc "/DAppVersion=$Version" "/DAppArch=$arch" $iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC compilation failed ($rid)." }

    $setup = Join-Path $installerDir "Output\DiskActivityMonitor-Setup-$Version-$arch.exe"
    if (Test-Path $setup) {
        Write-Host "Installer built: $setup ($([math]::Round((Get-Item $setup).Length/1MB,1)) MB)" -ForegroundColor Green
        $builtInstallers += $setup
    }
    else {
        throw "Installer was not produced at $setup"
    }
}
# Publishing returns through scripts\build-all-installers.ps1 before this build-only path runs.
