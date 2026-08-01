# Requires elevation to remove the Windows service.
#Requires -RunAsAdministrator
[CmdletBinding()]
param()

$ErrorActionPreference = 'SilentlyContinue'

function Stop-ProcessAtPath([string]$ProcessName, [string]$ExecutablePath) {
    $targetPath = [IO.Path]::GetFullPath($ExecutablePath)
    foreach ($process in @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)) {
        try {
            $candidatePath = $process.MainModule.FileName
            if ([string]::Equals([IO.Path]::GetFullPath($candidatePath), $targetPath, [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $process.Id -Force -ErrorAction Stop
                $process.WaitForExit(5000) | Out-Null
            }
        } finally {
            $process.Dispose()
        }
    }
}

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
$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$trayExe = Join-Path $programFiles 'Disk Activity Monitor Dev\tray\DiskActivityMonitor.Tray.exe'
Stop-ProcessAtPath 'DiskActivityMonitor.Tray' $trayExe
$startup = [Environment]::GetFolderPath('Startup')
$lnkPath = Join-Path $startup 'Disk Activity Monitor.lnk'
if (Test-Path $lnkPath) { Remove-Item $lnkPath -Force; Write-Host "Removed startup shortcut: $lnkPath" -ForegroundColor Green }

Write-Host 'Uninstall complete. Collected data in %ProgramData%\DiskActivityMonitor was left intact.' -ForegroundColor Green
Write-Host 'Delete that folder manually if you want to remove the history and settings.' -ForegroundColor Gray
