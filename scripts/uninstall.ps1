# Requires elevation to remove the Windows service.
#Requires -RunAsAdministrator
[CmdletBinding()]
param()

$ErrorActionPreference = 'SilentlyContinue'

Write-Host 'Stopping and removing the service...' -ForegroundColor Cyan
$svc = Get-Service -Name 'DiskActivityMonitor' -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') { Stop-Service 'DiskActivityMonitor' -Force }
    & sc.exe delete DiskActivityMonitor | Out-Null
    Write-Host 'Service removed.' -ForegroundColor Green
} else {
    Write-Host 'Service was not installed.' -ForegroundColor Yellow
}

# Stop the tray app and remove its Startup shortcut.
Get-Process -Name 'DiskActivityMonitor.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force
$startup = [Environment]::GetFolderPath('Startup')
$lnkPath = Join-Path $startup 'Disk Activity Monitor.lnk'
if (Test-Path $lnkPath) { Remove-Item $lnkPath -Force; Write-Host "Removed startup shortcut: $lnkPath" -ForegroundColor Green }

Write-Host 'Uninstall complete. Collected data in %ProgramData%\DiskActivityMonitor was left intact.' -ForegroundColor Green
Write-Host 'Delete that folder manually if you want to remove the history and settings.' -ForegroundColor Gray
