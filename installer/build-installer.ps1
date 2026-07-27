# Builds the Disk Activity Monitor Inno Setup installer.
# Publishes the service and tray as self-contained (no .NET runtime needed on target),
# regenerates the app icon, then compiles installer\DiskActivityMonitor.iss with ISCC.
#
# Examples:
#   .\build-installer.ps1                            # x64 only (default)
#   .\build-installer.ps1 -Runtime win-x86           # x86 only
#   .\build-installer.ps1 -All                       # both x64 and x86
#   .\build-installer.ps1 -All -Version 1.2.0 -Push  # build both, commit+push, draft a GitHub release, publish as latest
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version = '1.0.0',
    [string]$Runtime = 'win-x64',
    [switch]$All,
    # After building, commit and push any pending changes, then create a draft GitHub release
    # (tag vVersion) with the installer(s) attached and publish it as the latest release.
    # Requires the GitHub CLI (gh) to be authenticated.
    [switch]$Push,
    # Commit message used when -Push commits pending changes before releasing.
    [string]$CommitMessage = "Release v$Version"
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
        -p:PublishSingleFile=false -o $serviceOut --nologo
    if ($LASTEXITCODE -ne 0) { throw "Service publish failed ($rid)." }

    Write-Host "Publishing tray dashboard (self-contained, $rid)..." -ForegroundColor Cyan
    dotnet publish $trayProj -c $Configuration -r $rid --self-contained true `
        -p:PublishSingleFile=false -o $trayOut --nologo
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

# 5. Optionally commit + push the current code, then publish the installer(s) as a GitHub release.
if ($Push) {
    Write-Host "`n=== Publishing GitHub release ===" -ForegroundColor Magenta

    if (-not $builtInstallers) { throw 'No installers were built to publish.' }
    if (-not (Get-Command gh  -ErrorAction SilentlyContinue)) {
        throw 'GitHub CLI (gh) not found. Install it and run "gh auth login" before using -Push.'
    }
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'git was not found on PATH.' }

    # Don't let a non-zero native exit code auto-throw (PowerShell 7.4+ maps native failures to
    # terminating errors under $ErrorActionPreference = 'Stop'); we check $LASTEXITCODE ourselves.
    $PSNativeCommandUseErrorActionPreference = $false

    # Resolve owner/repo from the git remote so gh targets the right repository regardless of CWD.
    $repoUrl = (git -C $root remote get-url origin 2>$null)
    if ($repoUrl -match 'github\.com[:/](.+?)(?:\.git)?/?$') { $repo = $Matches[1] }
    else { throw "Could not determine the GitHub repository from remote '$repoUrl'." }

    $tag   = "v$Version"
    $title = "Disk Activity Monitor $tag"

    # Refuse to clobber an existing release for this version.
    gh release view $tag --repo $repo 1>$null 2>$null
    if ($LASTEXITCODE -eq 0) {
        throw "A release tagged '$tag' already exists on $repo. Bump -Version or delete the existing release first (gh release delete $tag --repo $repo)."
    }

    $branch = (git -C $root rev-parse --abbrev-ref HEAD).Trim()

    # Commit any pending changes so the release tag captures exactly what shipped.
    if (git -C $root status --porcelain) {
        Write-Host "Committing pending changes on '$branch' (`"$CommitMessage`")..." -ForegroundColor Cyan
        git -C $root add -A;                        if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }
        git -C $root commit -m $CommitMessage;       if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }
    }
    else {
        Write-Host 'Working tree is clean; nothing to commit.' -ForegroundColor DarkGray
    }

    # Push the branch so the release tag can point at a commit that exists on the remote.
    Write-Host "Pushing '$branch' to origin..." -ForegroundColor Cyan
    git -C $root push origin $branch
    if ($LASTEXITCODE -ne 0) { throw "git push failed for branch '$branch'." }
    $sha = (git -C $root rev-parse HEAD).Trim()

    # Compose commit-based release notes: asset table + commit log since the previous tag.
    $prevTag = (git -C $root describe --tags --abbrev=0 2>$null)
    $range   = if ($prevTag) { "$prevTag..HEAD" } else { 'HEAD' }
    $commits = @(git -C $root log $range --no-merges --pretty=format:'- %s (%h)')

    $notesLines = @(
        "Self-contained installers for Disk Activity Monitor $tag (no .NET runtime required on the target machine).",
        '',
        '| Asset | Size |',
        '| --- | --- |'
    )
    foreach ($f in $builtInstallers) {
        $notesLines += "| $([System.IO.Path]::GetFileName($f)) | $([math]::Round((Get-Item $f).Length/1MB,1)) MB |"
    }
    $notesLines += @('', '## Changes')
    $notesLines += if ($commits) { $commits } else { '- No commits since the previous release.' }
    if ($prevTag) { $notesLines += @('', "**Full Changelog**: https://github.com/$repo/compare/$prevTag...$tag") }
    $notes = $notesLines -join "`n"

    # 1. Create a DRAFT release, tagged at the pushed commit, with the installer(s) attached.
    Write-Host "Creating draft release $tag on $repo (target $($sha.Substring(0,7)), $($builtInstallers.Count) asset(s))..." -ForegroundColor Cyan
    gh release create $tag @builtInstallers --repo $repo --target $sha --draft --title $title --notes $notes
    if ($LASTEXITCODE -ne 0) { throw "Failed to create draft release $tag." }

    # 2. Publish the draft and mark it as the latest release.
    Write-Host "Publishing draft $tag as the latest release..." -ForegroundColor Cyan
    gh release edit $tag --repo $repo --draft=false --latest
    if ($LASTEXITCODE -ne 0) {
        throw "Draft $tag was created but publishing failed. Publish it manually: gh release edit $tag --draft=false --latest --repo $repo"
    }

    $url = (gh release view $tag --repo $repo --json url --jq .url 2>$null)
    Write-Host "Release published as latest: $url" -ForegroundColor Green
}
