<#
.SYNOPSIS
  Builds one or more Disk Activity Monitor installer variants.

.DESCRIPTION
  Canonical installer workflow for Disk Activity Monitor.

  This workflow never invokes a Git pager; every git command is run with
  explicit --no-pager.

  Supported variants:

    x64    64-bit Windows (win-x64)
    x86    32-bit Windows (win-x86)

  Build-only runs call installer\build-installer.ps1 without push/release behavior.
  Commit/push runs organize pending changes before build via reviewed whole-file
  atomic Copilot planning, then commit only allowlisted post-build generated tracked files.

.PARAMETER Variant
  Which variant(s) to build: x64, x86, or all (default). Accepts arrays;
  duplicates are removed and canonical order is preserved.

.PARAMETER Version
  Installer version without a leading v. If omitted with -Push, the script
  auto-increments the latest stable patch version discovered from tags/releases.

.PARAMETER Configuration
  .NET publish configuration passed through to installer\build-installer.ps1.
  Defaults to Release.

.PARAMETER Commit
  Before build, run strict git preflight and reviewed whole-file atomic Copilot
  planning for pending changes. After build, commit only allowlisted generated tracked files.

.PARAMETER Push
  Implies -Commit, pushes the current branch, then creates or refreshes the
  GitHub release unless -SkipRelease is supplied.

.PARAMETER SkipRelease
  With -Push, skip GitHub release create/refresh and verification.

.PARAMETER ReleaseMode
  GitHub release mode used with -Push: Prompt (default), Draft, or Published.

.PARAMETER CopilotPath
  Optional explicit path to the native Copilot CLI executable. Required only when a new
  release must be created and comprehensive notes are generated. If supplied, it
  is also used by pre-build whole-file planning.

.EXAMPLE
  .\build-all-installers.ps1
  Builds x64 and x86 installers for the resolved stable version.

.EXAMPLE
  .\build-all-installers.ps1 -Version 1.6.0 -Variant x64
  Builds only the x64 installer.

.EXAMPLE
  .\build-all-installers.ps1 -Push
  Auto-increments patch version, runs reviewed pre-build whole-file commits, builds both
  variants, commits allowlisted generated files, pushes, and creates/refreshes
  the release in prompt-selected mode unless -SkipRelease is set.

.EXAMPLE
  .\build-all-installers.ps1 -Push -SkipRelease
  Runs the same commit/build/push flow but skips release actions entirely.

.EXAMPLE
  .\build-all-installers.ps1 -Version 1.6.0 -WhatIf
  Prints version, whole-file commit strategy, exact build commands, and release plan
  without requiring gh/Copilot authentication or changing/building anything.
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
  [string]$ReleaseMode = 'Prompt',
  [string]$CopilotPath
)

$ErrorActionPreference = 'Stop'

if ($Push) { $Commit = $true }

$repoRoot = Split-Path -Parent $PSScriptRoot
$buildInstaller = Join-Path $repoRoot 'installer\build-installer.ps1'
$installerOutput = Join-Path $repoRoot 'installer\Output'
$gitHelperPath = Join-Path $repoRoot 'scripts\installer-git-commits.ps1'

if (-not (Test-Path -LiteralPath $buildInstaller)) {
  throw "Installer build script not found: $buildInstaller"
}
if (($Commit -or $Push) -and -not (Test-Path -LiteralPath $gitHelperPath)) {
  throw "Installer Git helper script not found: $gitHelperPath"
}

if ($Commit -or $Push) {
  . $gitHelperPath
}

function Resolve-ReleaseMode {
  if ($ReleaseMode -ne 'Prompt') { return $ReleaseMode }

  while ($true) {
    Write-Host ''
    Write-Host 'How should the GitHub release be created?' -ForegroundColor Cyan
    Write-Host '  [D] Draft - upload for review without publishing'
    Write-Host '  [P] Publish - publish officially and mark latest'
    $choice = (Read-Host 'Choose D or P').Trim().ToLowerInvariant()
    switch ($choice) {
      { $_ -in @('d', 'draft') } { return 'Draft' }
      { $_ -in @('p', 'publish', 'published') } { return 'Published' }
      default { Write-Warning "Invalid choice '$choice'. Enter D or P." }
    }
  }
}

function Resolve-RepositorySlug {
  param(
    [Parameter(Mandatory)][string]$RepoRoot,
    [Parameter(Mandatory)][string]$GitHubCli
  )

  $originUrl = (& git --no-pager -C $RepoRoot remote get-url origin 2>$null)
  if ("$originUrl" -match 'github\.com[:/](?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$') {
    return "$($Matches.owner)/$($Matches.repo)"
  }

  $resolved = ("$(& $GitHubCli repo view --json nameWithOwner --jq .nameWithOwner 2>$null)").Trim()
  if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($resolved)) {
    return $resolved
  }

  throw 'Could not resolve the GitHub owner/repository.'
}

function Get-ResolvedVersion {
  param(
    [Parameter(Mandatory)][string]$RepoRoot,
    [string]$InputVersion,
    [switch]$ForPush,
    [string]$GitHubCli,
    [string[]]$RepoArgs
  )

  if (-not [string]::IsNullOrWhiteSpace($InputVersion)) {
    $normalized = $InputVersion.Trim() -replace '^v', ''
    if ($normalized -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
      throw "Invalid version '$InputVersion'. Use semantic versioning like 1.6.0."
    }
    return $normalized
  }

  $tags = New-Object System.Collections.Generic.List[string]

  $localTags = @(& git --no-pager -C $RepoRoot tag --list 'v[0-9]*' 2>$null)
  if ($LASTEXITCODE -eq 0) {
    foreach ($tag in $localTags) { $tags.Add("$tag") }
  }

  if ($ForPush) {
    $remoteTags = @(& git --no-pager -C $RepoRoot ls-remote --tags origin 'refs/tags/v*' 2>$null)
    if ($LASTEXITCODE -eq 0) {
      foreach ($tag in $remoteTags) { $tags.Add("$tag") }
    }
  }

  if (-not [string]::IsNullOrWhiteSpace($GitHubCli) -and $RepoArgs.Count -gt 0) {
    $releaseJson = & $GitHubCli release list --limit 100 --json tagName @RepoArgs 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace((@($releaseJson) -join ''))) {
      try {
        foreach ($release in @((@($releaseJson) -join [Environment]::NewLine) | ConvertFrom-Json)) {
          $tags.Add("$($release.tagName)")
        }
      }
      catch {
        Write-Warning 'Could not parse GitHub release tags for version auto-selection.'
      }
    }
  }

  $stableVersions = @(
    foreach ($tag in $tags) {
      if ($tag -match '(?:^|refs/tags/)v(?<version>\d+\.\d+\.\d+)(?:\^\{\})?$') {
        [version]$Matches.version
      }
    }
  )

  $latest = $stableVersions | Sort-Object -Descending | Select-Object -First 1
  if ($null -eq $latest) {
    return '1.0.0'
  }

  if ($ForPush) {
    return ('{0}.{1}.{2}' -f $latest.Major, $latest.Minor, ($latest.Build + 1))
  }
  return $latest.ToString(3)
}

function Resolve-CopilotCliPath {
  param([string]$ProvidedPath)

  return Resolve-DamCopilotExecutable -ProvidedPath $ProvidedPath
}

function Assert-CopilotAuthenticated {
  param([Parameter(Mandatory)][string]$CopilotCliPath)

  & $CopilotCliPath --version *> $null
  if ($LASTEXITCODE -ne 0) {
    throw 'Copilot CLI availability check failed. Install/authenticate it before creating a new release.'
  }
}

function Invoke-CopilotMarkdown {
  param(
    [Parameter(Mandatory)][string]$CopilotCliPath,
    [Parameter(Mandatory)][string]$PromptText
  )

  $result = Invoke-DamCopilotPrompt -RepoRoot $repoRoot -CopilotExecutable $CopilotCliPath -Prompt $PromptText
  if ($result.ExitCode -eq 0) {
    $text = $result.StdOut.Trim()
    if (-not [string]::IsNullOrWhiteSpace($text)) {
      return $text
    }
  }

  $errorDetail = $result.StdErr.Trim()
  throw "Copilot CLI did not return release notes using the documented non-interactive prompt interface (exit $($result.ExitCode)). $errorDetail"
}

function Get-PreviousPublishedReleaseTag {
  param(
    [Parameter(Mandatory)][string]$GitHubCli,
    [Parameter(Mandatory)][string[]]$RepoArgs,
    [Parameter(Mandatory)][string]$CurrentTag
  )

  $releaseJson = & $GitHubCli release list --limit 100 --json tagName,isDraft,publishedAt @RepoArgs
  if ($LASTEXITCODE -ne 0) {
    throw 'Could not determine previous published release from GitHub.'
  }

  $releases = @((@($releaseJson) -join [Environment]::NewLine) | ConvertFrom-Json)
  $previous = $releases |
    Where-Object { -not $_.isDraft -and $_.tagName -ne $CurrentTag } |
    Sort-Object { $_.publishedAt } -Descending |
    Select-Object -First 1

  if ($previous) { return [string]$previous.tagName }
  return $null
}

function Get-RemoteReleaseTagTarget {
  param(
    [Parameter(Mandatory)][string]$RepoRoot,
    [Parameter(Mandatory)][string]$Tag
  )

  $tagOutput = & git --no-pager -C $RepoRoot ls-remote --tags origin "refs/tags/$Tag" "refs/tags/$Tag^{}"
  if ($LASTEXITCODE -ne 0 -or -not $tagOutput) {
    throw "Could not resolve remote tag $Tag."
  }

  $lines = @($tagOutput)
  $line = $lines | Where-Object { "$_" -match '\^\{\}$' } | Select-Object -First 1
  if (-not $line) { $line = $lines | Select-Object -First 1 }
  return (("$line" -split '\s+')[0]).Trim()
}

function New-AssetTableMarkdown {
  param([Parameter(Mandatory)][System.IO.FileInfo[]]$Assets)

  $lines = @(
    '| File | Size (bytes) | Size (MB) | SHA-256 |',
    '| --- | ---: | ---: | --- |'
  )
  foreach ($asset in $Assets) {
    $hash = (Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $lines += "| $($asset.Name) | $($asset.Length) | $([math]::Round($asset.Length / 1MB, 2)) | $hash |"
  }
  return ($lines -join "`r`n")
}

function New-ValidationMarkdown {
  param(
    [Parameter(Mandatory)][System.IO.FileInfo[]]$Assets,
    [Parameter(Mandatory)][string]$HeadSha,
    [Parameter(Mandatory)][string]$Configuration,
    [Parameter(Mandatory)][string[]]$Variants
  )

  $assetList = @($Assets | ForEach-Object { $_.Name }) -join ', '
  $variantList = $Variants -join ', '
  $lines = @(
    "- Built variants: $variantList",
    "- Build configuration: $Configuration",
    "- Commit target: $HeadSha",
    "- Assets verified locally: $assetList"
  )
  return ($lines -join "`r`n")
}

function Set-MarkedSection {
  param(
    [AllowEmptyString()][string]$Body,
    [Parameter(Mandatory)][string]$Marker,
    [Parameter(Mandatory)][string]$Title,
    [Parameter(Mandatory)][string]$Content
  )

  $start = "<!-- $Marker START -->"
  $end = "<!-- $Marker END -->"
  $section = "$start`r`n## $Title`r`n$Content`r`n$end"

  $text = if ($null -eq $Body) { '' } else { $Body.Trim() }
  $pattern = [regex]::Escape($start) + '[\s\S]*?' + [regex]::Escape($end)
  if ([regex]::IsMatch($text, $pattern)) {
    return [regex]::Replace($text, $pattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $section })
  }

  if ([string]::IsNullOrWhiteSpace($text)) {
    return $section
  }
  return "$text`r`n`r`n$section"
}

function Build-DeterministicReleaseBody {
  param(
    [AllowEmptyString()][string]$BaseBody,
    [Parameter(Mandatory)][System.IO.FileInfo[]]$Assets,
    [Parameter(Mandatory)][string]$HeadSha,
    [Parameter(Mandatory)][string]$Configuration,
    [Parameter(Mandatory)][string[]]$Variants,
    [Parameter(Mandatory)][string]$RepositorySlug,
    [string]$PreviousTag,
    [Parameter(Mandatory)][string]$Tag
  )

  $body = if ($null -eq $BaseBody) { '' } else { $BaseBody.Trim() }
  $assetsTable = New-AssetTableMarkdown -Assets $Assets
  $validation = New-ValidationMarkdown -Assets $Assets -HeadSha $HeadSha -Configuration $Configuration -Variants $Variants

  $body = Set-MarkedSection -Body $body -Marker 'DAM_ASSETS' -Title 'Assets' -Content $assetsTable
  $body = Set-MarkedSection -Body $body -Marker 'DAM_VALIDATION' -Title 'Validation' -Content $validation

  if (-not [string]::IsNullOrWhiteSpace($PreviousTag)) {
    $compareUrl = "https://github.com/$RepositorySlug/compare/$PreviousTag...$Tag"
    $changelog = "[Compare $PreviousTag...$Tag]($compareUrl)"
    $body = Set-MarkedSection -Body $body -Marker 'DAM_CHANGELOG' -Title 'Full changelog' -Content $changelog
  }

  return $body.Trim()
}

function New-ReleaseNotesFromCopilot {
  param(
    [Parameter(Mandatory)][string]$RepoRoot,
    [Parameter(Mandatory)][string]$CopilotCliPath,
    [string]$PreviousTag,
    [Parameter(Mandatory)][string]$HeadSha,
    [Parameter(Mandatory)][string]$Tag
  )

  $range = if ([string]::IsNullOrWhiteSpace($PreviousTag)) { $HeadSha } else { "$PreviousTag..$HeadSha" }
  $commits = @(& git --no-pager -C $RepoRoot log $range --no-merges --date=short --pretty=format:'%h %ad %s')
  if ($LASTEXITCODE -ne 0) {
    throw 'Could not gather commit history for release notes generation.'
  }
  $diffStat = @(& git --no-pager -C $RepoRoot diff --stat $range)
  if ($LASTEXITCODE -ne 0) {
    throw 'Could not gather diff statistics for release notes generation.'
  }

  $commitText = if ($commits.Count -gt 0) { ($commits -join "`n") } else { 'No commits available in range.' }
  $statText = if ($diffStat.Count -gt 0) { ($diffStat -join "`n") } else { 'No diff stat available in range.' }

  $prompt = @"
You are writing release notes for Disk Activity Monitor.

IMPORTANT:
- Use ONLY the provided git evidence.
- Do NOT invent changes.
- Output Markdown only.
- Include these exact sections:
  ## What's changed
  ## Installation

Context:
- Release tag: $Tag
- Comparison base: $PreviousTag
- Comparison head: $HeadSha

Commit log:
$commitText

Diff stat:
$statText
"@

  $notes = Invoke-CopilotMarkdown -CopilotCliPath $CopilotCliPath -PromptText $prompt

  if ($notes -notmatch '(?im)^##\s+What''s changed\s*$') {
    throw 'Generated release notes are missing the required "## What''s changed" section.'
  }
  if ($notes -notmatch '(?im)^##\s+Installation\s*$') {
    throw 'Generated release notes are missing the required "## Installation" section.'
  }

  return $notes.Trim()
}

function Write-ReleaseNotesFile {
  param([Parameter(Mandatory)][string]$Notes)

  $path = Join-Path ([System.IO.Path]::GetTempPath()) ("dam-release-notes-" + [guid]::NewGuid().ToString('N') + '.md')
  [System.IO.File]::WriteAllText($path, $Notes, (New-Object System.Text.UTF8Encoding($false)))
  return $path
}

function Assert-ReleaseMatchesBuild {
  param(
    [Parameter(Mandatory)][string]$GitHubCli,
    [Parameter(Mandatory)][string[]]$RepoArgs,
    [Parameter(Mandatory)][string]$RepoRoot,
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$HeadSha,
    [Parameter(Mandatory)][System.IO.FileInfo[]]$ExpectedAssets,
    [Parameter(Mandatory)][ValidateSet('Draft', 'Published')][string]$ExpectedMode
  )

  $releaseJson = & $GitHubCli release view $Tag --json body,isDraft,isPrerelease,tagName,url,assets @RepoArgs
  if ($LASTEXITCODE -ne 0 -or -not $releaseJson) {
    throw "Could not verify GitHub release $Tag after update."
  }

  $release = (@($releaseJson) -join [Environment]::NewLine) | ConvertFrom-Json
  if ($release.tagName -ne $Tag) {
    throw "Release verification failed: expected tag $Tag but found $($release.tagName)."
  }
  if ([bool]$release.isPrerelease) {
    throw 'Release verification failed: release is prerelease but a stable release is required.'
  }

  $shouldBeDraft = $ExpectedMode -eq 'Draft'
  if ([bool]$release.isDraft -ne $shouldBeDraft) {
    throw "Release verification failed: isDraft=$($release.isDraft), expected $shouldBeDraft."
  }

  $remoteTarget = Get-RemoteReleaseTagTarget -RepoRoot $RepoRoot -Tag $Tag
  if ($remoteTarget -ne $HeadSha) {
    throw "Release verification failed: remote tag $Tag targets $remoteTarget, expected $HeadSha."
  }

  $body = [string]$release.body
  if ([string]::IsNullOrWhiteSpace($body) -or $body.Trim().Length -lt 160) {
    throw 'Release verification failed: release notes are empty or not comprehensive enough.'
  }

  foreach ($expected in $ExpectedAssets) {
    $assetMatches = @($release.assets | Where-Object { $_.name -eq $expected.Name })
    if ($assetMatches.Count -ne 1) {
      throw "Release verification failed: expected exactly one asset named $($expected.Name)."
    }

    $remoteAsset = $assetMatches[0]
    if ([long]$remoteAsset.size -ne $expected.Length) {
      throw "Release verification failed: asset size mismatch for $($expected.Name)."
    }

    if (-not [string]::IsNullOrWhiteSpace([string]$remoteAsset.digest)) {
      $localDigest = 'sha256:' + (Get-FileHash -LiteralPath $expected.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
      if (-not [string]::Equals([string]$remoteAsset.digest, $localDigest, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release verification failed: SHA-256 mismatch for $($expected.Name)."
      }
    }
  }

  Write-Host "Verified release ${Tag}: state, tag target, notes, and assets." -ForegroundColor Green
  Write-Host ([string]$release.url) -ForegroundColor Green
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

$previewVersion = Get-ResolvedVersion -RepoRoot $repoRoot -InputVersion $Version -ForPush:$Push -GitHubCli $null -RepoArgs @()
if ($WhatIfPreference) {
  Write-Host 'WhatIf: canonical installer plan' -ForegroundColor Yellow
  Write-Host "  version: $previewVersion" -ForegroundColor Yellow
  Write-Host "  variants: $($requested -join ', ')" -ForegroundColor Yellow
  Write-Host '  pre-build commit strategy: strict git preflight + reviewed whole-file atomic Copilot plan (exact path coverage, temp-index preflight, explicit approval)' -ForegroundColor Yellow
  foreach ($name in $requested) {
    $runtime = $variantSpecs[$name].Runtime
    Write-Host ("  build command: installer\build-installer.ps1 -Runtime {0} -Version {1} -Configuration {2}" -f $runtime, $previewVersion, $Configuration) -ForegroundColor Yellow
  }
  if ($Push) {
    Write-Host '  post-build tracked allowlist commit: assets/app.ico only' -ForegroundColor Yellow
    Write-Host '  post-build push: git push origin <current-branch>' -ForegroundColor Yellow
    if ($SkipRelease) {
      Write-Host '  release plan: skipped (-SkipRelease)' -ForegroundColor Yellow
    }
    else {
      $modePlan = if ($ReleaseMode -eq 'Prompt') { 'Prompt (Draft or Published)' } else { $ReleaseMode }
      Write-Host "  release mode: $modePlan" -ForegroundColor Yellow
      Write-Host '  release action: create or refresh release; new releases require Copilot-generated comprehensive notes' -ForegroundColor Yellow
      Write-Host '  verification plan: gh release view -> tag/state/stability/body/assets(size+digest)/URL checks' -ForegroundColor Yellow
    }
  }
  return
}

$gh = $null
$repoSlug = $null
$repoArgs = @()

if ($Push -and -not $SkipRelease) {
  $gh = Get-Command gh -ErrorAction SilentlyContinue
  if (-not $gh) {
    throw 'GitHub CLI (gh) is required for -Push unless -SkipRelease is supplied.'
  }

  & $gh.Source auth status *> $null
  if ($LASTEXITCODE -ne 0) {
    throw 'gh is not authenticated. Run gh auth login before using -Push release flows.'
  }

  $repoSlug = Resolve-RepositorySlug -RepoRoot $repoRoot -GitHubCli $gh.Source
  if ([string]::IsNullOrWhiteSpace($repoSlug)) {
    throw 'Could not resolve repository owner/name for release operations.'
  }
  $repoArgs = @('--repo', $repoSlug)
}

$resolvedVersion = Get-ResolvedVersion -RepoRoot $repoRoot -InputVersion $Version -ForPush:$Push -GitHubCli $(if ($gh) { $gh.Source } else { $null }) -RepoArgs $repoArgs
$tag = "v$resolvedVersion"

$isExistingRelease = $false
$copilotCliPath = $null
if ($Push -and -not $SkipRelease) {
  & $gh.Source release view $tag @repoArgs *> $null
  $isExistingRelease = $LASTEXITCODE -eq 0

  if (-not $isExistingRelease) {
    $copilotCliPath = Resolve-CopilotCliPath -ProvidedPath $CopilotPath
    Assert-CopilotAuthenticated -CopilotCliPath $copilotCliPath
  }
}

Write-Host "Disk Activity Monitor installer build - version $resolvedVersion - variants: $($requested -join ', ')" -ForegroundColor Cyan

if ($Commit -or $Push) {
  $plannerCopilotExecutable = $null
  if (-not [string]::IsNullOrWhiteSpace($copilotCliPath)) {
    $plannerCopilotExecutable = $copilotCliPath
  }
  elseif (-not [string]::IsNullOrWhiteSpace($CopilotPath)) {
    $plannerCopilotExecutable = $CopilotPath
  }

  if ([string]::IsNullOrWhiteSpace($plannerCopilotExecutable)) {
    Invoke-DamFocusedPendingCommits -RepoRoot $repoRoot
  }
  else {
    Invoke-DamFocusedPendingCommits -RepoRoot $repoRoot -CopilotExecutable $plannerCopilotExecutable
  }
}

$results = New-Object System.Collections.Generic.List[object]
foreach ($name in $requested) {
  $spec = $variantSpecs[$name]
  $buildStartUtc = [DateTime]::UtcNow
  $installerPath = Join-Path $installerOutput "DiskActivityMonitor-Setup-$resolvedVersion-$($spec.Suffix).exe"

  Write-Host ''
  Write-Host '############################################################' -ForegroundColor Cyan
  Write-Host "# Building $name installer ($($spec.Runtime))" -ForegroundColor Cyan
  Write-Host '############################################################' -ForegroundColor Cyan

  $success = $true
  $errorMessage = $null
  $fileInfo = $null

  try {
    & $buildInstaller -Runtime $spec.Runtime -Version $resolvedVersion -Configuration $Configuration

    if (-not (Test-Path -LiteralPath $installerPath)) {
      throw "Expected installer was not produced: $installerPath"
    }

    $fileInfo = Get-Item -LiteralPath $installerPath
    if ($fileInfo.Length -le 0) {
      throw "Expected installer is empty: $installerPath"
    }

    if ($fileInfo.LastWriteTimeUtc -lt $buildStartUtc.AddSeconds(-2)) {
      throw "Expected installer is not fresh for this invocation: $installerPath"
    }
  }
  catch {
    $success = $false
    $errorMessage = $_.Exception.Message
    Write-Warning "Variant '$name' FAILED: $errorMessage"
  }

  $results.Add([pscustomobject]@{
      Variant = $name
      Runtime = $spec.Runtime
      Success = $success
      InstallerPath = $installerPath
      InstallerFile = $fileInfo
      Error = $errorMessage
      BuildStartUtc = $buildStartUtc
    })
}

Write-Host ''
Write-Host '==================== Build summary ====================' -ForegroundColor Cyan
foreach ($result in $results) {
  if ($result.Success) {
    $file = $result.InstallerFile
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
  Invoke-DamReleaseGeneratedCommit -RepoRoot $repoRoot -AllowedTrackedPaths @('assets/app.ico') -Message "Refresh release-generated assets for v$resolvedVersion"
}

if ($Push) {
  $branch = ("$(& git --no-pager -C $repoRoot rev-parse --abbrev-ref HEAD)").Trim()
  if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch) -or $branch -eq 'HEAD') {
    throw 'Cannot push from a detached HEAD or resolve current branch.'
  }

  Write-Host "Pushing '$branch' to origin..." -ForegroundColor Cyan
  & git --no-pager -C $repoRoot push origin $branch
  if ($LASTEXITCODE -ne 0) {
    throw "git push failed for branch '$branch' (exit $LASTEXITCODE)."
  }
  Write-Host 'Pushed.' -ForegroundColor Green

  if (-not $SkipRelease) {
    [System.IO.FileInfo[]]$releaseAssets = @($results | ForEach-Object { $_.InstallerFile })
    $headSha = ("$(& git --no-pager -C $repoRoot rev-parse HEAD)").Trim()
    $mode = Resolve-ReleaseMode
    $previousTag = Get-PreviousPublishedReleaseTag -GitHubCli $gh.Source -RepoArgs $repoArgs -CurrentTag $tag

    if ($isExistingRelease) {
      $remoteTarget = Get-RemoteReleaseTagTarget -RepoRoot $repoRoot -Tag $tag
      if ($remoteTarget -ne $headSha) {
        throw "Remote tag $tag targets $remoteTarget, not current pushed HEAD $headSha. Refusing --clobber refresh."
      }

      $existingBody = & $gh.Source release view $tag --json body --jq .body @repoArgs
      if ($LASTEXITCODE -ne 0) {
        throw "Could not read existing release body for $tag before refresh."
      }

      $refreshedBody = Build-DeterministicReleaseBody `
        -BaseBody ((@($existingBody) -join [Environment]::NewLine)) `
        -Assets $releaseAssets `
        -HeadSha $headSha `
        -Configuration $Configuration `
        -Variants $requested `
        -RepositorySlug $repoSlug `
        -PreviousTag $previousTag `
        -Tag $tag

      $notesPath = Write-ReleaseNotesFile -Notes $refreshedBody
      try {
        & $gh.Source release upload $tag @($releaseAssets.FullName) --clobber @repoArgs
        if ($LASTEXITCODE -ne 0) {
          throw "Release asset refresh failed for $tag (exit $LASTEXITCODE)."
        }

        if ($mode -eq 'Draft') {
          & $gh.Source release edit $tag --notes-file $notesPath --draft @repoArgs
        }
        else {
          & $gh.Source release edit $tag --notes-file $notesPath --draft=false --latest @repoArgs
        }
        if ($LASTEXITCODE -ne 0) {
          throw "Release metadata update failed for $tag (exit $LASTEXITCODE)."
        }
      }
      finally {
        Remove-Item -LiteralPath $notesPath -Force -ErrorAction SilentlyContinue
      }
    }
    else {
      $copilotNotes = New-ReleaseNotesFromCopilot -RepoRoot $repoRoot -CopilotCliPath $copilotCliPath -PreviousTag $previousTag -HeadSha $headSha -Tag $tag
      $finalBody = Build-DeterministicReleaseBody `
        -BaseBody $copilotNotes `
        -Assets $releaseAssets `
        -HeadSha $headSha `
        -Configuration $Configuration `
        -Variants $requested `
        -RepositorySlug $repoSlug `
        -PreviousTag $previousTag `
        -Tag $tag

      $notesPath = Write-ReleaseNotesFile -Notes $finalBody
      try {
        $createArgs = @('release', 'create', $tag,
          '--title', "Disk Activity Monitor $resolvedVersion",
          '--notes-file', $notesPath,
          '--target', $headSha)
        if ($mode -eq 'Draft') {
          $createArgs += '--draft'
        }
        else {
          $createArgs += '--latest'
        }
        $createArgs += @($releaseAssets.FullName) + $repoArgs
        & $gh.Source @createArgs
        if ($LASTEXITCODE -ne 0) {
          throw "Release creation failed for $tag (exit $LASTEXITCODE)."
        }
      }
      finally {
        Remove-Item -LiteralPath $notesPath -Force -ErrorAction SilentlyContinue
      }
    }

    Assert-ReleaseMatchesBuild -GitHubCli $gh.Source -RepoArgs $repoArgs -RepoRoot $repoRoot -Tag $tag -HeadSha $headSha -ExpectedAssets $releaseAssets -ExpectedMode $mode
  }
}
