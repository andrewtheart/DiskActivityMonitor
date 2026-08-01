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
    Installer version, without a leading "v". When omitted with Push, increments
    the patch number of the newest stable local tag, origin tag, or GitHub release
    (including drafts). Otherwise uses the newest stable local tag, or 1.0.0 when
    no version tag exists.

.PARAMETER Configuration
  .NET build configuration passed to build-installer.ps1. Defaults to Release.

.PARAMETER Commit
    After every requested installer succeeds, attempt to commit pending changes in
    focused functional groups. Existing staged changes are preserved as the first
    commit. Automatic grouping uses whole files only; mixed files and ambiguous
    changes fall back to one residual release commit. Does nothing when clean.

.PARAMETER Push
    Implies Commit, pushes the current branch, and prompts whether the GitHub
    release should remain a draft or be published officially unless SkipRelease
    is specified. When Version is omitted, automatically increments the patch
    version. An explicit existing release version refreshes its installer assets
    with --clobber.

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
    Builds both installers, then attempts focused, low-risk commits for pending changes.

.EXAMPLE
    .\build-all-installers.ps1 -Push
        Increments the latest release patch version, builds both installers, commits,
        pushes, then asks whether to create a draft or officially published release.
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

function Get-FunctionalCommitGroup {
    param([Parameter(Mandatory = $true)][string]$Path)

    $normalized = $Path.Replace('\', '/')
    if ($normalized -match '^(docs/|README\.md$|HELP\.md$|installer/README\.md$|src/DiskActivityMonitor\.Tray/HELP\.html$)') {
        return [pscustomobject]@{ Key = 'documentation'; Message = 'Update documentation'; Order = 50 }
    }
    if ($normalized -match '^(installer/|scripts/)') {
        return [pscustomobject]@{ Key = 'installer'; Message = 'Update installer and release tooling'; Order = 40 }
    }
    if ($normalized -match '^src/DiskActivityMonitor\.Core/Ai/' -or
        $normalized -match '^tests/DiskActivityMonitor\.Tests/(AiSecretsStore|TbwLookup)Tests\.cs$') {
        return [pscustomobject]@{ Key = 'ai'; Message = 'Update AI lookup and credential handling'; Order = 20 }
    }
    if ($normalized -match '^src/DiskActivityMonitor\.Core/(AtomicFile\.cs|Configuration/)' -or
        $normalized -eq 'src/DiskActivityMonitor.Cli/CliRunner.cs' -or
        $normalized -match '^tests/DiskActivityMonitor\.Tests/(AppConfig|ConfigStore|UserSettingsStore)Tests\.cs$') {
        return [pscustomobject]@{ Key = 'configuration'; Message = 'Update configuration persistence'; Order = 10 }
    }
    if ($normalized -match '^src/DiskActivityMonitor\.Core/(Collection/ProcessControl\.cs|Data/MonitorRepository\.cs)$' -or
        $normalized -match '^src/DiskActivityMonitor\.Tray/(App\.xaml\.cs|AutoSuspendManager\.cs|MainWindow\.xaml(?:\.cs)?|TrayController\.cs)$' -or
        $normalized -match '^tests/DiskActivityMonitor\.Tests/(ProcessControl|MonitorRepository|MainWindowCoverage)Tests\.cs$') {
        return [pscustomobject]@{ Key = 'process-control'; Message = 'Update process monitoring and control'; Order = 30 }
    }
    if ($normalized -match '^tests/') {
        return [pscustomobject]@{ Key = 'tests'; Message = 'Update regression coverage'; Order = 70 }
    }
    if ($normalized -match '^src/') {
        return [pscustomobject]@{ Key = 'application'; Message = 'Update application functionality'; Order = 60 }
    }
    return [pscustomobject]@{ Key = 'release'; Message = "Prepare release v$Version"; Order = 80 }
}

function Get-GitPaths {
    param([switch]$Staged)

    $arguments = @('-C', $repoRoot, 'diff', '--name-only', '--no-renames')
    if ($Staged) { $arguments += '--cached' }
    $arguments += @('HEAD', '--')
    $tracked = @(& git @arguments)
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect pending Git paths (exit $LASTEXITCODE)." }

    if (-not $Staged) {
        $untracked = @(& git -C $repoRoot ls-files --others --exclude-standard --)
        if ($LASTEXITCODE -ne 0) { throw "Could not inspect untracked Git paths (exit $LASTEXITCODE)." }
        $tracked += $untracked
    }

    return @($tracked | ForEach-Object { "$($_)".Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique)
}

function Get-FunctionalCommitPlan {
    param([Parameter(Mandatory = $true)][string[]]$Paths)

    $entries = foreach ($path in $Paths) {
        $group = Get-FunctionalCommitGroup -Path $path
        [pscustomobject]@{
            Path = $path
            Key = $group.Key
            Message = $group.Message
            Order = $group.Order
        }
    }

    return @($entries |
        Group-Object Key |
        ForEach-Object {
            $first = $_.Group | Select-Object -First 1
            [pscustomobject]@{
                Key = $first.Key
                Message = $first.Message
                Order = $first.Order
                Paths = @($_.Group.Path | Sort-Object -Unique)
            }
        } |
        Sort-Object Order, Key)
}

function Test-SamePathSet {
    param(
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string[]]$Actual
    )

    $left = @($Expected | Sort-Object -Unique)
    $right = @($Actual | Sort-Object -Unique)
    if ($left.Count -ne $right.Count) { return $false }
    return (Compare-Object -ReferenceObject $left -DifferenceObject $right).Count -eq 0
}

function Test-HasAmbiguousRename {
    $deleted = @(& git -C $repoRoot diff --name-only --diff-filter=D HEAD --)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect deleted paths for rename safety.' }
    if ($deleted.Count -eq 0) { return $false }

    $untracked = @(& git -C $repoRoot ls-files --others --exclude-standard --)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect untracked paths for rename safety.' }
    return $untracked.Count -gt 0
}

function Invoke-StagedCommit {
    param([Parameter(Mandatory = $true)][string]$Message)

    & git -C $repoRoot diff --cached --check
    if ($LASTEXITCODE -ne 0) { throw 'Staged changes failed git diff --cached --check.' }

    & git -C $repoRoot diff --cached --quiet
    $stagedExit = $LASTEXITCODE
    if ($stagedExit -gt 1) { throw "git diff --cached failed (exit $stagedExit)." }
    if ($stagedExit -eq 0) { return $false }

    Write-Host "Committing: $Message" -ForegroundColor Cyan
    & git -C $repoRoot commit -m $Message
    if ($LASTEXITCODE -ne 0) { throw "git commit failed (exit $LASTEXITCODE)." }
    Write-Host 'Committed.' -ForegroundColor Green
    return $true
}

function Show-FunctionalCommitPlan {
    $staged = @(Get-GitPaths -Staged)
    $pending = @(Get-GitPaths)

    if ($staged.Count -gt 0) {
        Write-Host "  commit 1: preserve $($staged.Count) already-staged path(s)" -ForegroundColor Yellow
        $pending = @($pending | Where-Object { $staged -notcontains $_ })
    }

    if ($pending.Count -gt 0 -and (Test-HasAmbiguousRename)) {
        Write-Host "  commit: Prepare release v$Version [$($pending.Count) residual path(s); possible rename]" -ForegroundColor Yellow
    }
    else {
        if ($pending.Count -gt 0) {
            foreach ($group in @(Get-FunctionalCommitPlan -Paths $pending)) {
                Write-Host "  commit: $($group.Message) [$($group.Paths.Count) path(s)]" -ForegroundColor Yellow
            }
        }
    }
    if ($staged.Count -eq 0 -and $pending.Count -eq 0) {
        Write-Host '  no pending changes to commit' -ForegroundColor DarkGray
    }
    Write-Host '  safety: whole-file groups only; mixed-file hunks remain together' -ForegroundColor DarkGray
}

function Invoke-FunctionalCommits {
    $committed = 0
    $staged = @(Get-GitPaths -Staged)
    if ($staged.Count -gt 0) {
        $stagedPlan = @(Get-FunctionalCommitPlan -Paths $staged)
        $message = if ($stagedPlan.Count -eq 1) {
            $stagedPlan[0].Message
        }
        else {
            "Preserve staged changes for v$Version"
        }
        Write-Host "Preserving the existing index as its own commit ($($staged.Count) path(s))." -ForegroundColor Cyan
        if (Invoke-StagedCommit -Message $message) { $committed++ }
    }

    $pending = @(Get-GitPaths)
    if ($pending.Count -gt 0 -and (Test-HasAmbiguousRename)) {
        Write-Warning 'A deletion and an untracked path may be an unstaged rename. Keeping all residual changes in one release commit.'
        & git -C $repoRoot add -A
        if ($LASTEXITCODE -ne 0) { throw "git add -A failed (exit $LASTEXITCODE)." }
        if (Invoke-StagedCommit -Message "Prepare release v$Version") { $committed++ }
        $pending = @()
    }

    $plan = if ($pending.Count -gt 0) { @(Get-FunctionalCommitPlan -Paths $pending) } else { @() }
    foreach ($group in $plan) {
        if ($group.Paths.Count -eq 0) { continue }
        Write-Host "Staging functional group '$($group.Key)' ($($group.Paths.Count) path(s))..." -ForegroundColor Cyan
        $addArguments = @('-C', $repoRoot, 'add', '-A', '--') + @($group.Paths)
        & git @addArguments
        if ($LASTEXITCODE -ne 0) { throw "git add failed for group '$($group.Key)' (exit $LASTEXITCODE)." }

        $actual = @(Get-GitPaths -Staged)
        if (-not (Test-SamePathSet -Expected $group.Paths -Actual $actual)) {
            Write-Warning "Git expanded group '$($group.Key)' beyond its planned paths (for example, a rename). Falling back to one residual release commit."
            & git -C $repoRoot reset --quiet HEAD
            if ($LASTEXITCODE -ne 0) { throw 'Could not restore the index before the residual commit.' }
            break
        }

        if (Invoke-StagedCommit -Message $group.Message) { $committed++ }
    }

    $residual = @(Get-GitPaths)
    if ($residual.Count -gt 0) {
        Write-Host "Staging $($residual.Count) residual path(s) without splitting file hunks..." -ForegroundColor Cyan
        & git -C $repoRoot add -A
        if ($LASTEXITCODE -ne 0) { throw "git add -A failed (exit $LASTEXITCODE)." }
        if (Invoke-StagedCommit -Message "Prepare release v$Version") { $committed++ }
    }

    if ($committed -eq 0) {
        Write-Host 'Nothing to commit - working tree already clean.' -ForegroundColor DarkGray
    }
    else {
        Write-Host "Created $committed focused commit(s)." -ForegroundColor Green
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $versionTags = New-Object System.Collections.Generic.List[string]
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $localTags = @(& git -C $repoRoot tag --list 'v[0-9]*' 2>$null)
        if ($LASTEXITCODE -eq 0) {
            foreach ($tag in $localTags) { $versionTags.Add("$tag") }
        }

        if ($Push) {
            $remoteTags = @(& git -C $repoRoot ls-remote --tags origin 'refs/tags/v*' 2>$null)
            if ($LASTEXITCODE -eq 0) {
                foreach ($tag in $remoteTags) { $versionTags.Add("$tag") }
            }
            else {
                Write-Warning 'Could not read release tags from origin; auto-incrementing from local tags only.'
            }

            $gh = Get-Command gh -ErrorAction SilentlyContinue
            $originUrl = (& git -C $repoRoot remote get-url origin 2>$null)
            if ($gh -and "$originUrl" -match 'github\.com[:/](?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$') {
                $repoSlug = "$($Matches.owner)/$($Matches.repo)"
                $releaseJson = "$(& $gh.Source release list --limit 100 --json tagName --repo $repoSlug 2>$null)"
                if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($releaseJson)) {
                    try {
                        foreach ($release in @($releaseJson | ConvertFrom-Json)) {
                            $versionTags.Add("$($release.tagName)")
                        }
                    }
                    catch {
                        Write-Warning 'Could not parse GitHub release versions; continuing with Git tags only.'
                    }
                }
            }
        }
    }

    $stableVersions = @(
        foreach ($tag in $versionTags) {
            if ($tag -match '(?:^|refs/tags/)v(?<version>\d+\.\d+\.\d+)(?:\^\{\})?$') {
                [version]$Matches.version
            }
        }
    )
    $latestVersion = $stableVersions | Sort-Object -Descending | Select-Object -First 1

    if ($Push -and $null -ne $latestVersion) {
        $Version = '{0}.{1}.{2}' -f $latestVersion.Major, $latestVersion.Minor, ($latestVersion.Build + 1)
    }
    elseif ($null -ne $latestVersion) {
        $Version = $latestVersion.ToString(3)
    }
    else {
        $Version = '1.0.0'
    }
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
        Write-Host ("  post-build: conservative functional commits{0}" -f $(if ($Push) { ' + push' } else { '' })) -ForegroundColor Yellow
        if (Get-Command git -ErrorAction SilentlyContinue) {
            Show-FunctionalCommitPlan
        }
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
        Write-Host 'Planning conservative functional commits...' -ForegroundColor Cyan
        Invoke-FunctionalCommits

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
