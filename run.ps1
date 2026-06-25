# Launches the collector service and the tray UI. No elevation required.
#
#   .\run.ps1            # launch (builds only if the binaries are missing)
#   .\run.ps1 -Build     # force a rebuild first (close the running apps so their exe isn't locked)
#   .\run.ps1 -Release   # use the Release build output
[CmdletBinding()]
param(
    [switch]$Build,
    [switch]$Release
)

$ErrorActionPreference = 'Stop'
$root          = $PSScriptRoot
$configuration = if ($Release) { 'Release' } else { 'Debug' }
$serviceExe    = Join-Path $root "src\DiskActivityMonitor.Service\bin\$configuration\net10.0-windows\DiskActivityMonitor.Service.exe"
$trayExe       = Join-Path $root "src\DiskActivityMonitor.Tray\bin\$configuration\net10.0-windows10.0.19041.0\DiskActivityMonitor.Tray.exe"

$serviceRunning = [bool](Get-Process -Name 'DiskActivityMonitor.Service' -ErrorAction SilentlyContinue)
$trayRunning    = [bool](Get-Process -Name 'DiskActivityMonitor.Tray' -ErrorAction SilentlyContinue)

# Build when requested, or automatically when the binaries do not exist yet.
$needBuild = $Build -or -not (Test-Path $serviceExe) -or -not (Test-Path $trayExe)
if ($needBuild) {
    if ($Build -and $trayRunning) {
        throw "The tray app is running and locks its .exe. Close it (tray icon > Exit) before using -Build."
    }
    Write-Host "Building solution ($configuration)..." -ForegroundColor Cyan
    dotnet build (Join-Path $root 'DiskActivityMonitor.slnx') -c $configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

if (-not (Test-Path $serviceExe)) { throw "Service executable not found at $serviceExe." }
if (-not (Test-Path $trayExe))    { throw "Tray executable not found at $trayExe." }

# Collector service: only one instance (it is the single writer to the database).
if ($serviceRunning) {
    Write-Host "Collector service is already running." -ForegroundColor Yellow
}
else {
    Write-Host "Starting collector service..." -ForegroundColor Cyan
    Start-Process -FilePath $serviceExe
}

# Tray UI: the app enforces a single instance itself; --show opens the dashboard.
if ($trayRunning) {
    Write-Host "Tray app is already running." -ForegroundColor Yellow
}
else {
    Write-Host "Starting tray app..." -ForegroundColor Cyan
    Start-Process -FilePath $trayExe -ArgumentList '--show'
}

Write-Host "Done. The collector needs a minute or two of history before the hourly chart fills in." -ForegroundColor Green
