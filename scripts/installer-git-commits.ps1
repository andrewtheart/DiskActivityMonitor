function Get-DamGitChangedPaths {
  [CmdletBinding()]
  param([Parameter(Mandatory)][string]$RepoRoot)

  $unstaged = @(& git --no-pager -C $RepoRoot diff --name-only --no-renames)
  if ($LASTEXITCODE -ne 0) { throw "git diff failed (exit $LASTEXITCODE)." }

  $staged = @(& git --no-pager -C $RepoRoot diff --cached --name-only --no-renames)
  if ($LASTEXITCODE -ne 0) { throw "git diff --cached failed (exit $LASTEXITCODE)." }

  $untracked = @(& git --no-pager -C $RepoRoot ls-files --others --exclude-standard)
  if ($LASTEXITCODE -ne 0) { throw "git ls-files failed (exit $LASTEXITCODE)." }

  return @($unstaged + $staged + $untracked |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Sort-Object -Unique)
}

function Get-DamTrackedChangedPaths {
  [CmdletBinding()]
  param([Parameter(Mandatory)][string]$RepoRoot)

  $unstaged = @(& git --no-pager -C $RepoRoot diff --name-only --no-renames)
  if ($LASTEXITCODE -ne 0) { throw "git diff failed (exit $LASTEXITCODE)." }

  $staged = @(& git --no-pager -C $RepoRoot diff --cached --name-only --no-renames)
  if ($LASTEXITCODE -ne 0) { throw "git diff --cached failed (exit $LASTEXITCODE)." }

  return @($unstaged + $staged |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Sort-Object -Unique)
}

function Invoke-DamGitPathBatches {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$RepoRoot,
    [Parameter(Mandatory)][string[]]$Paths,
    [Parameter(Mandatory)][scriptblock]$Action
  )

  for ($offset = 0; $offset -lt $Paths.Count; $offset += 100) {
    $last = [Math]::Min($offset + 99, $Paths.Count - 1)
    [string[]]$batch = @($Paths[$offset..$last])
    & $Action $RepoRoot $batch
    if ($LASTEXITCODE -ne 0) { throw "git path operation failed (exit $LASTEXITCODE)." }
  }
}

function Invoke-DamReviewedStagedCommit {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$RepoRoot,
    [Parameter(Mandatory)][string]$Message
  )

  if ([string]::IsNullOrWhiteSpace($Message)) {
    throw 'A non-empty commit message is required.'
  }

  $stagedPaths = @(& git --no-pager -C $RepoRoot diff --cached --name-only --no-renames)
  if ($LASTEXITCODE -ne 0) { throw "git diff --cached failed (exit $LASTEXITCODE)." }
  if ($stagedPaths.Count -eq 0) { throw 'No staged changes were selected.' }

  & git --no-pager -C $RepoRoot commit -m $Message
  if ($LASTEXITCODE -ne 0) { throw "git commit failed (exit $LASTEXITCODE)." }

  $committedPaths = @(& git --no-pager -C $RepoRoot diff-tree --no-commit-id --name-only -r HEAD)
  if ($LASTEXITCODE -ne 0) { throw "git diff-tree failed (exit $LASTEXITCODE)." }

  $stagedNorm = @($stagedPaths | ForEach-Object { $_.Replace('\\', '/') } | Sort-Object -Unique)
  $committedNorm = @($committedPaths | ForEach-Object { $_.Replace('\\', '/') } | Sort-Object -Unique)
  $unexpected = @($committedNorm | Where-Object { $stagedNorm -notcontains $_ })
  $missing = @($stagedNorm | Where-Object { $committedNorm -notcontains $_ })
  if ($unexpected.Count -gt 0 -or $missing.Count -gt 0) {
    throw 'The committed path set does not exactly match the reviewed staged path set.'
  }
}

function Invoke-DamGitPreflight {
  [CmdletBinding()]
  param([Parameter(Mandatory)][string]$RepoRoot)

  if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "Cannot prepare commits because 'git' is not available on PATH."
  }

  $inside = ("$(& git --no-pager -C $RepoRoot rev-parse --is-inside-work-tree 2>$null)").Trim()
  if ($LASTEXITCODE -ne 0 -or $inside -ne 'true') {
    throw "'$RepoRoot' is not a git working tree."
  }

  $branch = ("$(& git --no-pager -C $RepoRoot rev-parse --abbrev-ref HEAD)").Trim()
  if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch) -or $branch -eq 'HEAD') {
    throw 'Commit/push is not allowed from a detached HEAD.'
  }

  foreach ($operationMarker in @('MERGE_HEAD', 'CHERRY_PICK_HEAD', 'REVERT_HEAD', 'rebase-merge', 'rebase-apply')) {
    $markerPath = ("$(& git --no-pager -C $RepoRoot rev-parse --git-path $operationMarker)").Trim()
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect Git operation state (exit $LASTEXITCODE)." }
    if (-not [System.IO.Path]::IsPathRooted($markerPath)) {
      $markerPath = Join-Path $RepoRoot $markerPath
    }
    if (Test-Path -LiteralPath $markerPath) {
      throw "Finish the in-progress Git operation ($operationMarker) before running commit/push automation."
    }
  }

  $conflicts = @(& git --no-pager -C $RepoRoot diff --name-only --diff-filter=U)
  if ($LASTEXITCODE -ne 0) { throw "git conflict check failed (exit $LASTEXITCODE)." }
  $conflicts += @(& git --no-pager -C $RepoRoot diff --cached --name-only --diff-filter=U)
  if ($LASTEXITCODE -ne 0) { throw "git staged conflict check failed (exit $LASTEXITCODE)." }
  $conflicts = @($conflicts | Sort-Object -Unique)
  if ($conflicts.Count -gt 0) {
    throw "Resolve merge conflicts before publishing: $($conflicts -join ', ')"
  }

  $renameStatus = @(& git --no-pager -C $RepoRoot diff --name-status --find-renames --find-copies)
  if ($LASTEXITCODE -ne 0) { throw "git rename/copy check failed (exit $LASTEXITCODE)." }
  $renameStatus += @(& git --no-pager -C $RepoRoot diff --cached --name-status --find-renames --find-copies)
  if ($LASTEXITCODE -ne 0) { throw "git staged rename/copy check failed (exit $LASTEXITCODE)." }
  $renames = @($renameStatus | Where-Object { $_ -match '^[RC][0-9]*\s' })
  if ($renames.Count -gt 0) {
    throw 'Renamed/copied paths must be handled manually before commit/push automation.'
  }
}

function Resolve-DamCopilotExecutable {
  [CmdletBinding()]
  param([string]$ProvidedPath)

  if (-not [string]::IsNullOrWhiteSpace($ProvidedPath)) {
    if (-not (Test-Path -LiteralPath $ProvidedPath)) {
      throw "Copilot CLI was not found at $ProvidedPath"
    }
    if ([System.IO.Path]::GetExtension($ProvidedPath) -eq '.exe') {
      return (Resolve-Path -LiteralPath $ProvidedPath).Path
    }
  }

  $nativeCopilot = Get-Command copilot.exe -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
  if ($nativeCopilot -and (Test-Path -LiteralPath $nativeCopilot.Source)) {
    return $nativeCopilot.Source
  }

  if (-not [string]::IsNullOrWhiteSpace($ProvidedPath)) {
    throw "Copilot CLI path '$ProvidedPath' is a wrapper. Native copilot.exe is required for captured-output automation."
  }

  throw 'Native copilot.exe is required for whole-file commit planning. Install it or provide -CopilotExecutable.'
}

function ConvertTo-DamWindowsCommandLineArgument {
  [CmdletBinding()]
  param([AllowEmptyString()][string]$Argument)

  if ($Argument.Length -eq 0) { return '""' }
  if ($Argument -notmatch '[\s"]') { return $Argument }

  $quoted = New-Object System.Text.StringBuilder
  [void]$quoted.Append('"')
  $backslashes = 0
  foreach ($character in $Argument.ToCharArray()) {
    if ($character -eq '\') {
      $backslashes++
      continue
    }

    if ($character -eq '"') {
      [void]$quoted.Append(('\' * (($backslashes * 2) + 1)))
      [void]$quoted.Append('"')
      $backslashes = 0
      continue
    }

    if ($backslashes -gt 0) {
      [void]$quoted.Append(('\' * $backslashes))
      $backslashes = 0
    }
    [void]$quoted.Append($character)
  }

  if ($backslashes -gt 0) {
    [void]$quoted.Append(('\' * ($backslashes * 2)))
  }
  [void]$quoted.Append('"')
  return $quoted.ToString()
}

function Invoke-DamCapturedProcess {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][string[]]$Arguments,
    [Parameter(Mandatory)][string]$WorkingDirectory
  )

  $startInfo = New-Object System.Diagnostics.ProcessStartInfo
  $startInfo.FileName = $ExecutablePath
  $startInfo.WorkingDirectory = $WorkingDirectory
  $startInfo.UseShellExecute = $false
  $startInfo.RedirectStandardOutput = $true
  $startInfo.RedirectStandardError = $true
  $startInfo.CreateNoWindow = $true
  $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
  $startInfo.StandardOutputEncoding = $utf8NoBom
  $startInfo.StandardErrorEncoding = $utf8NoBom

  if ($null -ne $startInfo.PSObject.Properties['ArgumentList']) {
    foreach ($argument in $Arguments) {
      [void]$startInfo.ArgumentList.Add($argument)
    }
  }
  else {
    $startInfo.Arguments = (@($Arguments | ForEach-Object { ConvertTo-DamWindowsCommandLineArgument -Argument $_ }) -join ' ')
  }

  $process = New-Object System.Diagnostics.Process
  $process.StartInfo = $startInfo
  try {
    try {
      $started = $process.Start()
    }
    catch {
      throw "Could not start '$ExecutablePath': $($_.Exception.Message)"
    }
    if (-not $started) {
      throw "Could not start $ExecutablePath"
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()

    return [pscustomobject]@{
      ExitCode = $process.ExitCode
      StdOut = $stdout
      StdErr = $stderr
    }
  }
  finally {
    $process.Dispose()
  }
}

function Invoke-DamCopilotPrompt {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$RepoRoot,
    [Parameter(Mandatory)][string]$CopilotExecutable,
    [Parameter(Mandatory)][string]$Prompt
  )

  return Invoke-DamCapturedProcess -ExecutablePath $CopilotExecutable -WorkingDirectory $RepoRoot -Arguments @(
    '-C', $RepoRoot,
    '-p', $Prompt,
    '--silent',
    '--no-color',
    '--no-custom-instructions',
    '--no-ask-user',
    '--disable-builtin-mcps',
    '--allow-all-tools',
    '--deny-tool', 'shell',
    '--deny-tool', 'write'
  )
}

function Get-DamJsonObjectFromText {
  [CmdletBinding()]
  param([Parameter(Mandatory)][string]$Text)

  $trimmed = $Text.Trim()
  if ([string]::IsNullOrWhiteSpace($trimmed)) {
    throw 'Copilot whole-file commit planning is unavailable or returned no output.'
  }

  $candidates = New-Object System.Collections.Generic.List[string]
  $candidates.Add($trimmed)

  $fenced = [regex]::Matches($trimmed, '(?s)```(?:json)?\s*(\{.*?\})\s*```')
  foreach ($match in $fenced) {
    $candidates.Add([string]$match.Groups[1].Value)
  }

  $firstBrace = $trimmed.IndexOf('{')
  if ($firstBrace -ge 0) {
    $depth = 0
    $inString = $false
    $escaped = $false
    for ($i = $firstBrace; $i -lt $trimmed.Length; $i++) {
      $ch = $trimmed[$i]
      if ($inString) {
        if ($escaped) {
          $escaped = $false
        }
        elseif ($ch -eq '\\') {
          $escaped = $true
        }
        elseif ($ch -eq '"') {
          $inString = $false
        }
        continue
      }

      if ($ch -eq '"') {
        $inString = $true
        continue
      }

      if ($ch -eq '{') {
        $depth++
      }
      elseif ($ch -eq '}') {
        $depth--
        if ($depth -eq 0) {
          $candidates.Add($trimmed.Substring($firstBrace, ($i - $firstBrace + 1)))
          break
        }
      }
    }
  }

  foreach ($candidate in $candidates) {
    try {
      $obj = $candidate | ConvertFrom-Json -ErrorAction Stop
      if ($null -ne $obj) { return $obj }
    }
    catch {
    }
  }

  throw 'Copilot commit plan is invalid JSON.'
}

function Assert-DamExactPathSet {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string[]]$ExpectedPaths,
    [Parameter(Mandatory)][string[]]$ActualPaths,
    [Parameter(Mandatory)][string]$ErrorMessage
  )

  $expected = @($ExpectedPaths | ForEach-Object { $_.Replace('\\', '/').Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
  $actual = @($ActualPaths | ForEach-Object { $_.Replace('\\', '/').Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
  if ((Compare-Object -ReferenceObject $expected -DifferenceObject $actual).Count -ne 0) {
    throw $ErrorMessage
  }
}

function Assert-DamCompleteWholeFileCommitPlan {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)]$Plan,
    [Parameter(Mandatory)][string[]]$PendingPaths
  )

  $groups = @($Plan.groups)
  if ($groups.Count -eq 0) {
    throw 'Copilot commit plan is invalid: no groups were returned.'
  }

  $expected = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
  foreach ($path in $PendingPaths) {
    [void]$expected.Add($path.Replace('\\', '/').Trim())
  }

  $planned = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
  $normalizedGroups = New-Object System.Collections.Generic.List[object]

  for ($index = 0; $index -lt $groups.Count; $index++) {
    $group = $groups[$index]
    $message = [string]$group.message
    if ([string]::IsNullOrWhiteSpace($message)) {
      throw "Copilot commit plan is invalid: group $($index + 1) is missing a commit message."
    }

    if ($message -notmatch '^(build|chore|ci|docs|feat|fix|perf|refactor|revert|style|test)(\([a-z0-9._/-]+\))?: [^ ].+$') {
      throw "Copilot commit plan is invalid: group $($index + 1) must use a conservative conventional commit message."
    }

    $paths = @($group.paths)
    if ($paths.Count -eq 0) {
      throw "Copilot commit plan is invalid: group $($index + 1) has no paths."
    }

    $normalizedPaths = New-Object System.Collections.Generic.List[string]
    foreach ($rawPath in $paths) {
      $path = [string]$rawPath
      if ([string]::IsNullOrWhiteSpace($path)) {
        throw "Copilot commit plan is invalid: group $($index + 1) contains an empty path entry."
      }

      $normalizedPath = $path.Replace('\\', '/').Trim()
      if (-not $expected.Contains($normalizedPath)) {
        throw "Copilot commit plan is invalid: group $($index + 1) includes unexpected path '$normalizedPath'."
      }
      if (-not $planned.Add($normalizedPath)) {
        throw "Copilot commit plan is invalid: path '$normalizedPath' appears more than once."
      }

      $normalizedPaths.Add($normalizedPath)
    }

    $normalizedGroups.Add([pscustomobject]@{
        Message = $message.Trim()
        Paths = @($normalizedPaths | Sort-Object -Unique)
      })
  }

  if ($planned.Count -ne $expected.Count) {
    $missing = @($expected | Where-Object { -not $planned.Contains($_) } | Sort-Object)
    throw "Copilot commit plan is invalid: missing path coverage for $($missing -join ', ')."
  }

  return $normalizedGroups.ToArray()
}

function Invoke-DamCopilotWholeFilePlanner {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$RepoRoot,
    [Parameter(Mandatory)][string[]]$PendingPaths,
    [Parameter(Mandatory)][string]$CopilotExecutable
  )

  $status = @(& git --no-pager -C $RepoRoot status --short -- @PendingPaths)
  if ($LASTEXITCODE -ne 0) { throw "git status failed while preparing Copilot planning context (exit $LASTEXITCODE)." }

  $unstagedDiff = @(& git --no-pager -C $RepoRoot diff --no-color --unified=3 -- @PendingPaths)
  if ($LASTEXITCODE -ne 0) { throw "git diff failed while preparing Copilot planning context (exit $LASTEXITCODE)." }

  $stagedDiff = @(& git --no-pager -C $RepoRoot diff --cached --no-color --unified=3 -- @PendingPaths)
  if ($LASTEXITCODE -ne 0) { throw "git diff --cached failed while preparing Copilot planning context (exit $LASTEXITCODE)." }

  $untracked = @(& git --no-pager -C $RepoRoot ls-files --others --exclude-standard -- @PendingPaths)
  if ($LASTEXITCODE -ne 0) { throw "git ls-files failed while preparing Copilot planning context (exit $LASTEXITCODE)." }

  $untrackedPreview = New-Object System.Collections.Generic.List[string]
  foreach ($path in $untracked) {
    $absolutePath = Join-Path $RepoRoot $path
    if (-not (Test-Path -LiteralPath $absolutePath)) { continue }
    $preview = @()
    try {
      $preview = @(Get-Content -LiteralPath $absolutePath -TotalCount 120)
    }
    catch {
      $preview = @('<Unable to read file preview>')
    }

    $untrackedPreview.Add("--- BEGIN FILE $path ---")
    foreach ($line in $preview) { $untrackedPreview.Add([string]$line) }
    $untrackedPreview.Add("--- END FILE $path ---")
  }

  $pendingList = $PendingPaths | ForEach-Object { $_.Replace('\\', '/').Trim() }
  $context = @"
Repository root: $RepoRoot
Pending paths ($($pendingList.Count)):
$($pendingList -join "`n")

git status --short (pending paths only):
$(@($status) -join "`n")

Untracked file previews (first 120 lines each):
$(@($untrackedPreview) -join "`n")

git diff --no-color --unified=3 (pending paths only):
$(@($unstagedDiff) -join "`n")

git diff --cached --no-color --unified=3 (pending paths only):
$(@($stagedDiff) -join "`n")
"@

  $maxInlineContextChars = 16000
  if ($context.Length -gt $maxInlineContextChars) {
    $context = $context.Substring(0, $maxInlineContextChars) + @"

[Planning context was truncated to stay within the Windows command-line limit. Use read-only tools to inspect pending files when more detail is needed.]
"@
  }

  $prompt = @"
You are generating a whole-file atomic commit plan for Disk Activity Monitor.

Hard requirements:
- Return JSON only (no markdown fences, no prose).
- The JSON schema must be:
  {
    "groups": [
      { "message": "<conservative conventional commit message>", "paths": ["path/one", "path/two"] }
    ]
  }
- Include every pending path exactly once.
- Do not include any path that is not in the pending path list.
- Whole-file grouping only; no hunk-level planning.
- Use conservative conventional messages (feat/fix/chore/docs/refactor/test/build/ci/style/perf/revert).
- If uncertain, use a single group.
- Do not ask questions.

Planning context:
$context
"@

  $result = Invoke-DamCopilotPrompt -RepoRoot $RepoRoot -CopilotExecutable $CopilotExecutable -Prompt $prompt
  if ($result.ExitCode -eq 0) {
    $text = $result.StdOut.Trim()
    if (-not [string]::IsNullOrWhiteSpace($text)) {
      return Get-DamJsonObjectFromText -Text $text
    }
  }

  $errorDetail = $result.StdErr.Trim()
  throw "Copilot whole-file commit planning is unavailable (exit $($result.ExitCode)). $errorDetail Aborting without changing real staging."
}

function Test-DamWholeFilePlanWithTemporaryIndex {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$RepoRoot,
    [Parameter(Mandatory)][object[]]$Groups
  )

  $indexPath = ("$(& git --no-pager -C $RepoRoot rev-parse --git-path index)").Trim()
  if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($indexPath)) {
    throw 'Could not resolve git index path for temporary staging preflight.'
  }
  if (-not [System.IO.Path]::IsPathRooted($indexPath)) {
    $indexPath = Join-Path $RepoRoot $indexPath
  }
  if (-not (Test-Path -LiteralPath $indexPath)) {
    throw "Git index was not found at $indexPath"
  }

  $tempIndex = Join-Path ([System.IO.Path]::GetTempPath()) ("dam-git-index-" + [guid]::NewGuid().ToString('N'))
  Copy-Item -LiteralPath $indexPath -Destination $tempIndex -Force

  $previousIndex = $env:GIT_INDEX_FILE
  $hadPreviousIndex = $false
  if (Test-Path env:GIT_INDEX_FILE) {
    $hadPreviousIndex = $true
  }

  try {
    $env:GIT_INDEX_FILE = $tempIndex
    & git --no-pager -C $RepoRoot reset --mixed --quiet
    if ($LASTEXITCODE -ne 0) {
      throw "Temporary index reset failed (exit $LASTEXITCODE)."
    }

    for ($i = 0; $i -lt $Groups.Count; $i++) {
      $group = $Groups[$i]
      $paths = @($group.Paths)

      & git --no-pager -C $RepoRoot reset --mixed --quiet
      if ($LASTEXITCODE -ne 0) {
        throw "Temporary index reset failed before group $($i + 1) (exit $LASTEXITCODE)."
      }

      & git --no-pager -C $RepoRoot add -A -- @paths
      if ($LASTEXITCODE -ne 0) {
        throw "Temporary index staging failed for planned group $($i + 1) (exit $LASTEXITCODE)."
      }

      $stagedPaths = @(& git --no-pager -C $RepoRoot diff --cached --name-only --no-renames)
      if ($LASTEXITCODE -ne 0) {
        throw "Temporary index staged-path verification failed for group $($i + 1) (exit $LASTEXITCODE)."
      }

      Assert-DamExactPathSet -ExpectedPaths $paths -ActualPaths $stagedPaths -ErrorMessage "Temporary index preflight failed: staged path set does not exactly match planned group $($i + 1)."
    }
  }
  finally {
    if ($hadPreviousIndex) {
      $env:GIT_INDEX_FILE = $previousIndex
    }
    else {
      Remove-Item env:GIT_INDEX_FILE -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $tempIndex -Force -ErrorAction SilentlyContinue
  }
}

function Show-DamWholeFileCommitPlan {
  [CmdletBinding()]
  param([Parameter(Mandatory)][object[]]$Groups)

  Write-Host ''
  Write-Host 'Proposed whole-file commit plan:' -ForegroundColor Cyan
  for ($i = 0; $i -lt $Groups.Count; $i++) {
    $group = $Groups[$i]
    Write-Host ("  Group {0}: {1}" -f ($i + 1), [string]$group.Message) -ForegroundColor Yellow
    foreach ($path in @($group.Paths)) {
      Write-Host ("    - {0}" -f [string]$path) -ForegroundColor DarkGray
    }
  }
}

function Invoke-DamExecuteWholeFileCommitPlan {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$RepoRoot,
    [Parameter(Mandatory)][object[]]$Groups
  )

  foreach ($group in $Groups) {
    $paths = @($group.Paths)
    & git --no-pager -C $RepoRoot add -A -- @paths
    if ($LASTEXITCODE -ne 0) { throw "Path-scoped git add failed for planned group (exit $LASTEXITCODE)." }

    $stagedPaths = @(& git --no-pager -C $RepoRoot diff --cached --name-only --no-renames)
    if ($LASTEXITCODE -ne 0) { throw "git diff --cached failed while applying whole-file plan (exit $LASTEXITCODE)." }
    Assert-DamExactPathSet -ExpectedPaths $paths -ActualPaths $stagedPaths -ErrorMessage 'Staged path set does not exactly match planned group paths.'

    Invoke-DamReviewedStagedCommit -RepoRoot $RepoRoot -Message ([string]$group.Message)
  }
}

function Invoke-DamFocusedPendingCommits {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$RepoRoot,
    [string]$CopilotExecutable
  )

  $restoreNativePreference = $false
  $savedNativePreference = $null
  if (Test-Path variable:PSNativeCommandUseErrorActionPreference) {
    $savedNativePreference = $PSNativeCommandUseErrorActionPreference
    $PSNativeCommandUseErrorActionPreference = $false
    $restoreNativePreference = $true
  }

  try {
    Invoke-DamGitPreflight -RepoRoot $RepoRoot

    $pending = @(Get-DamGitChangedPaths -RepoRoot $RepoRoot)
    if ($pending.Count -eq 0) {
      Write-Host 'No pre-build pending changes to organize.' -ForegroundColor DarkGray
      return
    }

    if ([Console]::IsInputRedirected -or -not [Environment]::UserInteractive -or "$env:CI" -match '^(1|true)$') {
      throw 'Dirty noninteractive/CI runs are blocked. Run the reviewed commit flow from an interactive terminal.'
    }

    Write-Host ''
    Write-Host 'Pending changes must be organized before build.' -ForegroundColor Cyan
    Write-Host 'Using reviewed whole-file atomic Copilot planning with strict validation and explicit approval.' -ForegroundColor Yellow

    & git --no-pager -C $RepoRoot diff --cached --quiet
    $stagedExit = $LASTEXITCODE
    if ($stagedExit -gt 1) { throw "git diff --cached failed (exit $stagedExit)." }
    if ($stagedExit -eq 1) {
      Write-Host ''
      Write-Host 'Existing staged changes:' -ForegroundColor Cyan
      & git --no-pager -C $RepoRoot diff --cached --stat
      if ($LASTEXITCODE -ne 0) { throw "git diff --cached --stat failed (exit $LASTEXITCODE)." }

      $choice = (Read-Host 'Commit this already-staged group now? [c]ommit/[a]bort').Trim().ToLowerInvariant()
      if ($choice -notin @('c', 'commit')) {
        throw 'Reviewed commit flow aborted; existing staged changes were preserved.'
      }

      $message = (Read-Host 'Commit message for existing staged group').Trim()
      if ([string]::IsNullOrWhiteSpace($message)) { throw 'A non-empty commit message is required.' }
      $confirm = (Read-Host "Commit staged changes as '$message'? [y/N]").Trim().ToLowerInvariant()
      if ($confirm -notin @('y', 'yes')) {
        throw 'Reviewed commit flow aborted; existing staged changes were preserved.'
      }
      Invoke-DamReviewedStagedCommit -RepoRoot $RepoRoot -Message $message
    }

    $pending = @(Get-DamGitChangedPaths -RepoRoot $RepoRoot)
    if ($pending.Count -eq 0) {
      Write-Host 'All pending changes were already committed from existing staged content.' -ForegroundColor Green
      return
    }

    $resolvedCopilotExecutable = Resolve-DamCopilotExecutable -ProvidedPath $CopilotExecutable
    $plan = Invoke-DamCopilotWholeFilePlanner -RepoRoot $RepoRoot -PendingPaths $pending -CopilotExecutable $resolvedCopilotExecutable
    $groups = @(Assert-DamCompleteWholeFileCommitPlan -Plan $plan -PendingPaths $pending)

    Test-DamWholeFilePlanWithTemporaryIndex -RepoRoot $RepoRoot -Groups $groups
    Show-DamWholeFileCommitPlan -Groups $groups

    $approval = (Read-Host 'Apply this whole-file commit plan? [yes/abort]').Trim().ToLowerInvariant()
    if ($approval -ne 'yes') {
      throw 'Whole-file commit plan declined; no plan was applied.'
    }

    Invoke-DamExecuteWholeFileCommitPlan -RepoRoot $RepoRoot -Groups $groups

    $remaining = @(Get-DamGitChangedPaths -RepoRoot $RepoRoot)
    if ($remaining.Count -gt 0) {
      throw "Whole-file commit plan finished with uncommitted paths: $($remaining -join ', ')."
    }

    <# DEFERRED: Interactive hunk workflow retained for possible future restoration.
    while (@(Get-DamGitChangedPaths -RepoRoot $RepoRoot).Count -gt 0) {
      Write-Host ''
      Write-Host 'Interactive hunk review for one functional group:' -ForegroundColor Cyan
      & git --no-pager -C $RepoRoot add --patch
      if ($LASTEXITCODE -ne 0) { throw "git add --patch failed (exit $LASTEXITCODE)." }

      & git --no-pager -C $RepoRoot diff --cached --quiet
      $stagedExit = $LASTEXITCODE
      if ($stagedExit -gt 1) { throw "git diff --cached failed (exit $stagedExit)." }
      if ($stagedExit -eq 0) {
        $retry = (Read-Host 'No hunks were staged. [r]etry/[a]bort').Trim().ToLowerInvariant()
        if ($retry -notin @('r', 'retry')) { throw 'Reviewed commit flow aborted.' }
        continue
      }

      Write-Host ''
      Write-Host 'Selected staged group:' -ForegroundColor Cyan
      & git --no-pager -C $RepoRoot diff --cached --stat
      if ($LASTEXITCODE -ne 0) { throw "git diff --cached --stat failed (exit $LASTEXITCODE)." }

      $message = (Read-Host 'Commit message for selected group').Trim()
      if ([string]::IsNullOrWhiteSpace($message)) {
        throw 'A non-empty commit message is required; selected hunks remain staged.'
      }
      $confirm = (Read-Host "Commit this group as '$message'? [y/N]").Trim().ToLowerInvariant()
      if ($confirm -notin @('y', 'yes')) {
        throw 'Reviewed commit flow aborted; selected hunks remain staged.'
      }

      Invoke-DamReviewedStagedCommit -RepoRoot $RepoRoot -Message $message
    }
    #>

    Write-Host 'All pre-build changes were committed via reviewed whole-file plan groups.' -ForegroundColor Green
  }
  finally {
    if ($restoreNativePreference) {
      $PSNativeCommandUseErrorActionPreference = $savedNativePreference
    }
  }
}

function Invoke-DamReleaseGeneratedCommit {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$RepoRoot,
    [Parameter(Mandatory)][string[]]$AllowedTrackedPaths,
    [Parameter(Mandatory)][string]$Message
  )

  $restoreNativePreference = $false
  $savedNativePreference = $null
  if (Test-Path variable:PSNativeCommandUseErrorActionPreference) {
    $savedNativePreference = $PSNativeCommandUseErrorActionPreference
    $PSNativeCommandUseErrorActionPreference = $false
    $restoreNativePreference = $true
  }

  try {
    $allowed = @{}
    foreach ($path in $AllowedTrackedPaths) {
      $allowed[$path.Replace('\\', '/')] = $true
    }

    $trackedChanged = @(Get-DamTrackedChangedPaths -RepoRoot $RepoRoot)
    $unexpectedTracked = @($trackedChanged | Where-Object { -not $allowed.ContainsKey($_.Replace('\\', '/')) })
    if ($unexpectedTracked.Count -gt 0) {
      throw "Unexpected post-build tracked change(s): $($unexpectedTracked -join ', ')"
    }

    $releaseChanges = @($trackedChanged | Where-Object { $allowed.ContainsKey($_.Replace('\\', '/')) })
    if ($releaseChanges.Count -eq 0) {
      Write-Host 'No allowlisted release-generated tracked files changed.' -ForegroundColor DarkGray
      return
    }

    Write-Host "Staging allowlisted release-generated tracked files: $($releaseChanges -join ', ')" -ForegroundColor Cyan
    & git --no-pager -C $RepoRoot add -A -- @releaseChanges
    if ($LASTEXITCODE -ne 0) { throw "Path-scoped git add failed (exit $LASTEXITCODE)." }

    $stagedPaths = @(& git --no-pager -C $RepoRoot diff --cached --name-only --no-renames)
    if ($LASTEXITCODE -ne 0) { throw "git diff --cached failed (exit $LASTEXITCODE)." }
    $expectedStaged = @($releaseChanges | Sort-Object -Unique)
    $actualStaged = @($stagedPaths | Sort-Object -Unique)
    if ((Compare-Object -ReferenceObject $expectedStaged -DifferenceObject $actualStaged).Count -ne 0) {
      throw 'Staged path set does not exactly match allowlisted release-generated files.'
    }

    Invoke-DamReviewedStagedCommit -RepoRoot $RepoRoot -Message $Message

    $remainingTracked = @(Get-DamTrackedChangedPaths -RepoRoot $RepoRoot)
    $remainingUnexpected = @($remainingTracked | Where-Object { -not $allowed.ContainsKey($_.Replace('\\', '/')) })
    if ($remainingUnexpected.Count -gt 0) {
      throw "Unexpected tracked paths remain after release-generated commit: $($remainingUnexpected -join ', ')"
    }
  }
  finally {
    if ($restoreNativePreference) {
      $PSNativeCommandUseErrorActionPreference = $savedNativePreference
    }
  }
}