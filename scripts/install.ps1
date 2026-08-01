# Requires elevation to create the Windows service.
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$InstallRoot
)

$ErrorActionPreference = 'Stop'
$root        = Split-Path -Parent $PSScriptRoot
$publish     = Join-Path $root 'publish'
$serviceProj = Join-Path $root 'src\DiskActivityMonitor.Service\DiskActivityMonitor.Service.csproj'
$trayProj    = Join-Path $root 'src\DiskActivityMonitor.Tray\DiskActivityMonitor.Tray.csproj'
$serviceOut  = Join-Path $publish 'service'
$trayOut     = Join-Path $publish 'tray'
$secureDirectoryScript = Join-Path $PSScriptRoot 'secure-directory.ps1'

$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$expectedInstallRoot = Join-Path $programFiles 'Disk Activity Monitor Dev'
if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = $expectedInstallRoot
}
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$expectedInstallRoot = [IO.Path]::GetFullPath($expectedInstallRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (-not [string]::Equals($InstallRoot, $expectedInstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "InstallRoot must be exactly: $expectedInstallRoot"
}
$commonApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
$dataRoot = Join-Path $commonApplicationData 'DiskActivityMonitor'

function Assert-NotReparsePoint([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to install through a reparse point: $Path"
        }
    }
}

function Set-SecureInstallAcl([string]$Path) {
    & $secureDirectoryScript -Path $Path -Profile Install
}

function Set-SecureDataAcl([string]$Path) {
    Assert-NotReparsePoint $Path
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    & $secureDirectoryScript -Path $Path -Profile Data
}

function Stop-ProcessAtPath([string]$ProcessName, [string]$ExecutablePath) {
    $targetPath = [IO.Path]::GetFullPath($ExecutablePath)
    foreach ($process in @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)) {
        try {
            $candidatePath = $process.MainModule.FileName
            if ([string]::Equals([IO.Path]::GetFullPath($candidatePath), $targetPath, [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $process.Id -Force -ErrorAction Stop
                if (-not $process.WaitForExit(5000)) {
                    throw "Timed out stopping tray process $($process.Id)."
                }
            }
        } finally {
            $process.Dispose()
        }
    }
}

& $secureDirectoryScript -Path $programFiles -Profile Validate
& $secureDirectoryScript -Path $commonApplicationData -Profile Validate

Write-Host 'Publishing collector service...' -ForegroundColor Cyan
dotnet publish $serviceProj -c $Configuration -o $serviceOut --nologo
Write-Host 'Publishing tray app...' -ForegroundColor Cyan
dotnet publish $trayProj -c $Configuration -o $trayOut --nologo

$stagedServiceExe = Join-Path $serviceOut 'DiskActivityMonitor.Service.exe'
$stagedTrayExe    = Join-Path $trayOut 'DiskActivityMonitor.Tray.exe'

if (-not (Test-Path -LiteralPath $stagedServiceExe)) { throw "Service executable not found at $stagedServiceExe" }
if (-not (Test-Path -LiteralPath $stagedTrayExe))    { throw "Tray executable not found at $stagedTrayExe" }

# (Re)install the Windows service.
$existing = Get-Service -Name 'DiskActivityMonitor' -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host 'Removing existing service...' -ForegroundColor Yellow
    if ($existing.Status -ne 'Stopped') {
        Stop-Service 'DiskActivityMonitor' -Force -ErrorAction SilentlyContinue
        $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
    }
    & sc.exe delete DiskActivityMonitor | Out-Null
    $existing.Dispose()
}

$serviceInstall = Join-Path $InstallRoot 'service'
$trayInstall = Join-Path $InstallRoot 'tray'
$trayExe = Join-Path $trayInstall 'DiskActivityMonitor.Tray.exe'
Stop-ProcessAtPath 'DiskActivityMonitor.Tray' $trayExe
Assert-NotReparsePoint $InstallRoot
New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
Assert-NotReparsePoint $InstallRoot
Set-SecureInstallAcl $InstallRoot
Set-SecureDataAcl $dataRoot

# Once the root is no longer user-writable, reject pre-existing link targets and replace only
# this script's two payload directories.
Assert-NotReparsePoint $serviceInstall
Assert-NotReparsePoint $trayInstall
foreach ($directory in @($serviceInstall, $trayInstall)) {
    if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
    New-Item -ItemType Directory -Path $directory | Out-Null
    Set-SecureInstallAcl $directory
}
Copy-Item -Path (Join-Path $serviceOut '*') -Destination $serviceInstall -Recurse -Force
Copy-Item -Path (Join-Path $trayOut '*') -Destination $trayInstall -Recurse -Force

$serviceExe = Join-Path $serviceInstall 'DiskActivityMonitor.Service.exe'

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
