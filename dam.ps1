# Convenience wrapper for the Disk Activity Monitor CLI (dam.exe).
# Usage:  .\dam.ps1 <command> [options]      e.g.  .\dam.ps1 status
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'src\DiskActivityMonitor.Cli\bin\Debug\net10.0-windows\dam.exe'
if (-not (Test-Path $exe)) {
    Write-Host 'Building CLI (first run)...' -ForegroundColor DarkGray
    dotnet build (Join-Path $PSScriptRoot 'src\DiskActivityMonitor.Cli\DiskActivityMonitor.Cli.csproj') -c Debug --nologo -v q | Out-Null
}
& $exe @args
exit $LASTEXITCODE
