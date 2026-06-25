# Requires elevation to create the Windows service.
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root        = Split-Path -Parent $PSScriptRoot
$publish     = Join-Path $root 'publish'
$serviceProj = Join-Path $root 'src\DiskActivityMonitor.Service\DiskActivityMonitor.Service.csproj'
$trayProj    = Join-Path $root 'src\DiskActivityMonitor.Tray\DiskActivityMonitor.Tray.csproj'
$serviceOut  = Join-Path $publish 'service'
$trayOut     = Join-Path $publish 'tray'

Write-Host 'Publishing collector service...' -ForegroundColor Cyan
dotnet publish $serviceProj -c $Configuration -o $serviceOut --nologo
Write-Host 'Publishing tray app...' -ForegroundColor Cyan
dotnet publish $trayProj -c $Configuration -o $trayOut --nologo

$serviceExe = Join-Path $serviceOut 'DiskActivityMonitor.Service.exe'
$trayExe    = Join-Path $trayOut 'DiskActivityMonitor.Tray.exe'

if (-not (Test-Path $serviceExe)) { throw "Service executable not found at $serviceExe" }
if (-not (Test-Path $trayExe))    { throw "Tray executable not found at $trayExe" }

# (Re)install the Windows service.
$existing = Get-Service -Name 'DiskActivityMonitor' -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host 'Removing existing service...' -ForegroundColor Yellow
    if ($existing.Status -ne 'Stopped') { Stop-Service 'DiskActivityMonitor' -Force -ErrorAction SilentlyContinue }
    & sc.exe delete DiskActivityMonitor | Out-Null
    Start-Sleep -Seconds 1
}

Write-Host 'Installing service "DiskActivityMonitor"...' -ForegroundColor Cyan
New-Service -Name 'DiskActivityMonitor' `
    -BinaryPathName "`"$serviceExe`"" `
    -DisplayName 'Disk Activity Monitor' `
    -Description 'Collects SSD/HDD read-write trends to protect drive endurance.' `
    -StartupType Automatic | Out-Null

Start-Service 'DiskActivityMonitor'
Write-Host 'Service started.' -ForegroundColor Green

# Launch the tray app at logon via a Startup-folder shortcut.
$startup = [Environment]::GetFolderPath('Startup')
$lnkPath = Join-Path $startup 'Disk Activity Monitor.lnk'
$shell   = New-Object -ComObject WScript.Shell
$lnk     = $shell.CreateShortcut($lnkPath)
$lnk.TargetPath       = $trayExe
$lnk.WorkingDirectory = Split-Path $trayExe
$lnk.Description       = 'Disk Activity Monitor tray'
$lnk.Save()
Write-Host "Startup shortcut created: $lnkPath" -ForegroundColor Green

Start-Process -FilePath $trayExe -ArgumentList '--show'
Write-Host 'Install complete. The tray icon is now running.' -ForegroundColor Green
