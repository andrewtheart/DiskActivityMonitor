<#
.SYNOPSIS
  Builds one or more Disk Activity Monitor installer variants.

.DESCRIPTION
  Orchestrates installer\build-installer.ps1 for the supported self-contained
  Windows installers:

    x64    64-bit Windows (win-x64)
    x86    32-bit Windows (win-x86)

  All selected variants use the same version. Builds are written to
  installer\Output. A failed variant is reported in the final summary and
  prevents commit, push, and release operations.

.PARAMETER Variant
  Which variant(s) to build: x64, x86, or all (the default). Accepts an array;
  duplicates are normalized and canonical build order is preserved.

.PARAMETER Version
  Installer version, without a leading "v". When omitted, uses the newest local
  Git tag matching v<version>, or 1.0.0 when no version tag exists. Pass an
  explicit version when preparing a new release.

.PARAMETER Configuration
  .NET build configuration passed to build-installer.ps1. Defaults to Release.

.PARAMETER Commit
  After every requested installer succeeds, stage all pending repository changes
  and commit them as "Build installers v<version> (<variants>)". Does nothing
  when the working tree is clean.

.PARAMETER Push
    Implies Commit, pushes the current branch, and prompts whether the GitHub
    release should remain a draft or be published officially unless SkipRelease
    is specified. Re-running an existing release version refreshes its installer
    assets with --clobber.

.PARAMETER ReleaseMode
    GitHub release publication mode used with Push: Prompt (the default), Draft,
    or Published. Prompt asks interactively after a successful push. Draft and
    Published are useful for unattended runs.

.PARAMETER SkipRelease
  With Push, skip GitHub release creation or asset refresh.

.EXAMPLE
  .\build-all-installers.ps1
  Builds x64 and x86 using the version from the newest local Git tag.

.EXAMPLE
  .\build-all-installers.ps1 -Version 1.5.0
  Builds x64 and x86 installers for version 1.5.0.

.EXAMPLE
  .\build-all-installers.ps1 -Variant x64 -Version 1.5.0
  Builds only the x64 installer.

.EXAMPLE
  .\build-all-installers.ps1 -Version 1.5.0 -WhatIf
  Prints the complete build plan without changing files.

.EXAMPLE
  .\build-all-installers.ps1 -Version 1.5.0 -Commit
  Builds both installers, then stages and commits all pending changes.

.EXAMPLE
  .\build-all-installers.ps1 -Version 1.5.0 -Push
    Builds both installers, commits, pushes, then asks whether to create a draft
    or officially published GitHub release.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('x64', 'x86', 'all')]
    [string[]]$Variant = @('all'),
    [string]$Version,
    [string]$Configuration = 'Release',
    [switch]$Commit,
    [switch]$Push,
    [switch]$SkipRelease,
    [ValidateSet('Prompt', 'Draft', 'Published')]
    [string]$ReleaseMode = 'Prompt'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$buildInstaller = Join-Path $repoRoot 'installer\build-installer.ps1'
$installerOutput = Join-Path $repoRoot 'installer\Output'

if (-not (Test-Path -LiteralPath $buildInstaller)) {
    throw "Installer build script not found: $buildInstaller"
}

function Resolve-ReleaseMode {
    if ($ReleaseMode -ne 'Prompt') { return $ReleaseMode }

    while ($true) {
        Write-Host ''
        Write-Host 'How should the GitHub release be created?' -ForegroundColor Cyan
        Write-Host '  [D] Draft - upload it for review without publishing'
        Write-Host '  [P] Publish - publish it officially and mark it latest'
        $choice = (Read-Host 'Choose D or P').Trim().ToLowerInvariant()
        switch ($choice) {
            { $_ -in @('d', 'draft') } { return 'Draft' }
            { $_ -in @('p', 'publish', 'published') } { return 'Published' }
            default { Write-Warning "Invalid choice '$choice'. Enter D for Draft or P for Publish." }
        }
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $null
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $latestTag = (& git -C $repoRoot tag --list 'v[0-9]*' --sort=-version:refname 2>$null | Select-Object -First 1)
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace("$latestTag")) {
            $Version = "$latestTag".Trim() -replace '^v', ''
        }
    }
    if ([string]::IsNullOrWhiteSpace($Version)) { $Version = '1.0.0' }
}
else {
    $Version = $Version.Trim() -replace '^v', ''
}

if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "Invalid version '$Version'. Use a semantic version such as 1.5.0 (without a leading v)."
}

$variantSpecs = [ordered]@{
    'x64' = @{ Runtime = 'win-x64'; Suffix = 'x64' }
    'x86' = @{ Runtime = 'win-x86'; Suffix = 'x86' }
}

$requested = if ($Variant -contains 'all') {
    @($variantSpecs.Keys)
}
else {
    @($variantSpecs.Keys | Where-Object { $Variant -contains $_ })
}

if ($requested.Count -eq 0) {
    throw 'No installer variants selected. Choose x64, x86, or all.'
}

Write-Host "Disk Activity Monitor installer build - version $Version - variants: $($requested -join ', ')" -ForegroundColor Cyan

if ($WhatIfPreference) {
    Write-Host 'WhatIf: the following installer builds would run:' -ForegroundColor Yellow
    foreach ($name in $requested) {
        $runtime = $variantSpecs[$name].Runtime
        Write-Host ("  {0,-4} -> installer\build-installer.ps1 -Runtime {1} -Version {2} -Configuration {3}" -f $name, $runtime, $Version, $Configuration)
    }
    if ($Commit -or $Push) {
        Write-Host ("  post-build: git add -A + commit{0}" -f $(if ($Push) { ' + push' } else { '' })) -ForegroundColor Yellow
    }
    if ($Push -and -not $SkipRelease) {
        $plannedMode = if ($ReleaseMode -eq 'Prompt') { 'prompt for Draft or Published' } else { $ReleaseMode }
        Write-Host "  post-push: create or refresh GitHub release v$Version ($plannedMode) with selected installers" -ForegroundColor Yellow
    }
    return
}

$results = New-Object System.Collections.Generic.List[object]
foreach ($name in $requested) {
    $spec = $variantSpecs[$name]
    Write-Host ''
    Write-Host '############################################################' -ForegroundColor Cyan
    Write-Host "# Building $name installer ($($spec.Runtime))" -ForegroundColor Cyan
    Write-Host '############################################################' -ForegroundColor Cyan

    $success = $true
    $errorMessage = $null
    $installerPath = Join-Path $installerOutput "DiskActivityMonitor-Setup-$Version-$($spec.Suffix).exe"

    try {
        & $buildInstaller -Runtime $spec.Runtime -Version $Version -Configuration $Configuration
        if (-not (Test-Path -LiteralPath $installerPath)) {
            throw "Expected installer was not produced: $installerPath"
        }
    }
    catch {
        $success = $false
        $errorMessage = $_.Exception.Message
        Write-Warning "Variant '$name' FAILED: $errorMessage"
    }

    $results.Add([pscustomobject]@{
        Variant = $name
        Success = $success
        InstallerPath = $installerPath
        Error = $errorMessage
    })
}

Write-Host ''
Write-Host '==================== Build summary ====================' -ForegroundColor Cyan
foreach ($result in $results) {
    if ($result.Success) {
        $file = Get-Item -LiteralPath $result.InstallerPath
        Write-Host ("  [OK]   {0,-4} -> {1} ({2} MB)" -f $result.Variant, $file.Name, [math]::Round($file.Length / 1MB, 1)) -ForegroundColor Green
    }
    else {
        Write-Host ("  [FAIL] {0,-4} -> {1}" -f $result.Variant, $result.Error) -ForegroundColor Red
    }
}
Write-Host '=======================================================' -ForegroundColor Cyan

$failed = @($results | Where-Object { -not $_.Success })
if ($failed.Count -gt 0) {
    throw "$($failed.Count) of $($results.Count) installer variant(s) failed: $(@($failed.Variant) -join ', ')."
}

Write-Host "All $($results.Count) installer variant(s) built successfully." -ForegroundColor Green

if ($Commit -or $Push) {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw '-Commit/-Push was requested but git is not available on PATH.'
    }

    $restoreNativePreference = $false
    $savedNativePreference = $null
    if (Test-Path variable:PSNativeCommandUseErrorActionPreference) {
        $savedNativePreference = $PSNativeCommandUseErrorActionPreference
        $PSNativeCommandUseErrorActionPreference = $false
        $restoreNativePreference = $true
    }

    try {
        $inside = (& git -C $repoRoot rev-parse --is-inside-work-tree 2>$null)
        if ($LASTEXITCODE -ne 0 -or "$inside".Trim() -ne 'true') {
            throw "Repository root is not a Git working tree: $repoRoot"
        }

        Write-Host ''
        Write-Host 'Staging all changes (git add -A)...' -ForegroundColor Cyan
        & git -C $repoRoot add -A
        if ($LASTEXITCODE -ne 0) { throw "git add -A failed (exit $LASTEXITCODE)." }

        & git -C $repoRoot diff --cached --quiet
        $stagedExit = $LASTEXITCODE
        if ($stagedExit -gt 1) { throw "git diff --cached failed (exit $stagedExit)." }

        if ($stagedExit -eq 1) {
            $commitMessage = "Build installers v$Version ($($requested -join ', '))"
            Write-Host "Committing: $commitMessage" -ForegroundColor Cyan
            & git -C $repoRoot commit -m $commitMessage
            if ($LASTEXITCODE -ne 0) { throw "git commit failed (exit $LASTEXITCODE)." }
            Write-Host 'Committed.' -ForegroundColor Green
        }
        else {
            Write-Host 'Nothing to commit - working tree already clean.' -ForegroundColor DarkGray
        }

        if ($Push) {
            $branch = ("$(& git -C $repoRoot rev-parse --abbrev-ref HEAD)").Trim()
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch) -or $branch -eq 'HEAD') {
                throw 'Cannot push from a detached HEAD or resolve the current branch.'
            }

            Write-Host "Pushing '$branch' to origin..." -ForegroundColor Cyan
            & git -C $repoRoot push origin $branch
            if ($LASTEXITCODE -ne 0) { throw "git push failed for branch '$branch' (exit $LASTEXITCODE)." }
            Write-Host 'Pushed.' -ForegroundColor Green

            if (-not $SkipRelease) {
                $gh = Get-Command gh -ErrorAction SilentlyContinue
                if (-not $gh) {
                    Write-Warning "GitHub release skipped: gh CLI was not found. Create it manually with: gh release create v$Version installer\Output\DiskActivityMonitor-Setup-$Version-*.exe --draft --generate-notes"
                }
                else {
                    $assets = @($results | ForEach-Object { $_.InstallerPath })
                    $tag = "v$Version"
                    $resolvedReleaseMode = Resolve-ReleaseMode
                    $originUrl = (& git -C $repoRoot remote get-url origin 2>$null)
                    $repoArgs = @()
                    $repoSlug = $null
                    if ("$originUrl" -match 'github\.com[:/](?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$') {
                        $repoSlug = "$($Matches.owner)/$($Matches.repo)"
                        $repoArgs = @('--repo', $repoSlug)
                    }
                    $headSha = ("$(& git -C $repoRoot rev-parse HEAD)").Trim()

                    & $gh.Source release view $tag @repoArgs *> $null
                    if ($LASTEXITCODE -eq 0) {
                        Write-Host "GitHub release $tag already exists - refreshing installer assets..." -ForegroundColor Cyan
                        & $gh.Source release upload $tag @assets --clobber @repoArgs
                        if ($LASTEXITCODE -ne 0) {
                            Write-Warning "Release asset upload failed (exit $LASTEXITCODE). Build and push succeeded; upload the installers manually."
                        }
                        else {
                            Write-Host "Refreshed assets on release $tag." -ForegroundColor Green
                            if ($resolvedReleaseMode -eq 'Draft') {
                                & $gh.Source release edit $tag --draft @repoArgs
                            }
                            else {
                                & $gh.Source release edit $tag --draft=false --latest @repoArgs
                            }
                            if ($LASTEXITCODE -ne 0) {
                                Write-Warning "Assets were refreshed, but applying $resolvedReleaseMode mode to release $tag failed (exit $LASTEXITCODE). Update it manually."
                            }
                            elseif ($resolvedReleaseMode -eq 'Draft') {
                                Write-Host "Release $tag is saved as a draft for review." -ForegroundColor Green
                            }
                            else { Write-Host "Release $tag is published officially as latest." -ForegroundColor Green }
                        }
                    }
                    else {
                        Write-Host "Creating $($resolvedReleaseMode.ToLowerInvariant()) GitHub release $tag with $($assets.Count) installer(s)..." -ForegroundColor Cyan
                        $createArgs = @(
                            'release', 'create', $tag,
                            '--title', "Disk Activity Monitor $Version",
                            '--generate-notes',
                            '--target', $headSha
                        )
                        if ($resolvedReleaseMode -eq 'Draft') { $createArgs += '--draft' }
                        else { $createArgs += '--latest' }
                        $createArgs += $assets + $repoArgs
                        & $gh.Source @createArgs
                        if ($LASTEXITCODE -ne 0) {
                            Write-Warning "Release creation failed (exit $LASTEXITCODE). Build and push succeeded; create the draft release manually."
                        }
                        else {
                            $releaseUrl = if ($repoSlug) { "https://github.com/$repoSlug/releases" } else { 'the GitHub Releases page' }
                            if ($resolvedReleaseMode -eq 'Draft') {
                                Write-Host "Draft release $tag created. Review and publish it at $releaseUrl" -ForegroundColor Green
                            }
                            else {
                                Write-Host "Release $tag published officially as latest: $releaseUrl" -ForegroundColor Green
                            }
                        }
                    }
                }
            }
        }
    }
    finally {
        if ($restoreNativePreference) {
            $PSNativeCommandUseErrorActionPreference = $savedNativePreference
        }
    }
}
