# Runs the collector and the tray app from source for development (no admin required).
# Both run as normal foreground processes; close their windows / press Ctrl+C to stop.
[CmdletBinding()]
param(
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root        = Split-Path -Parent $PSScriptRoot
$serviceProj = Join-Path $root 'src\DiskActivityMonitor.Service\DiskActivityMonitor.Service.csproj'
$trayProj    = Join-Path $root 'src\DiskActivityMonitor.Tray\DiskActivityMonitor.Tray.csproj'

Write-Host 'Building solution...' -ForegroundColor Cyan
dotnet build (Join-Path $root 'DiskActivityMonitor.slnx') -c $Configuration --nologo | Out-Null

Write-Host 'Starting collector (new window)...' -ForegroundColor Cyan
Start-Process -FilePath 'dotnet' -ArgumentList "run --project `"$serviceProj`" -c $Configuration --no-build"

Start-Sleep -Seconds 2
Write-Host 'Starting tray app (new window)...' -ForegroundColor Cyan
Start-Process -FilePath 'dotnet' -ArgumentList "run --project `"$trayProj`" -c $Configuration --no-build -- --show"

Write-Host 'Both started. The collector needs a minute or two of history before charts fill in.' -ForegroundColor Green
