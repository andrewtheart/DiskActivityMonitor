# Builds the Disk Activity Monitor Inno Setup installer.
# Publishes the service and tray as self-contained win-x64 (no .NET runtime needed on target),
# regenerates the app icon, then compiles installer\DiskActivityMonitor.iss with ISCC.
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version = '1.0.0',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$installerDir = $PSScriptRoot
$root         = Split-Path -Parent $installerDir
$serviceProj  = Join-Path $root 'src\DiskActivityMonitor.Service\DiskActivityMonitor.Service.csproj'
$trayProj     = Join-Path $root 'src\DiskActivityMonitor.Tray\DiskActivityMonitor.Tray.csproj'
$publishRoot  = Join-Path $installerDir 'publish'
$serviceOut   = Join-Path $publishRoot 'service'
$trayOut      = Join-Path $publishRoot 'tray'

# Locate the Inno Setup compiler.
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'ISCC.exe (Inno Setup 6) not found. Install Inno Setup 6 first.' }

# 1. Regenerate the shared app icon.
Write-Host 'Generating app icon...' -ForegroundColor Cyan
& (Join-Path $root 'scripts\make-icon.ps1')

# 2. Clean previous publish output.
if (Test-Path $publishRoot) { Remove-Item -Recurse -Force $publishRoot }

# 3. Publish both apps self-contained.
Write-Host 'Publishing collector service (self-contained)...' -ForegroundColor Cyan
dotnet publish $serviceProj -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=false -o $serviceOut --nologo
if ($LASTEXITCODE -ne 0) { throw 'Service publish failed.' }

Write-Host 'Publishing tray dashboard (self-contained)...' -ForegroundColor Cyan
dotnet publish $trayProj -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=false -o $trayOut --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tray publish failed.' }

# 4. Compile the installer.
$iss = Join-Path $installerDir 'DiskActivityMonitor.iss'
Write-Host "Compiling installer with $iscc ..." -ForegroundColor Cyan
& $iscc "/DAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) { throw 'ISCC compilation failed.' }

$setup = Join-Path $installerDir "Output\DiskActivityMonitor-Setup-$Version.exe"
if (Test-Path $setup) {
    Write-Host "Installer built: $setup ($([math]::Round((Get-Item $setup).Length/1MB,1)) MB)" -ForegroundColor Green
}
else {
    throw "Installer was not produced at $setup"
}
