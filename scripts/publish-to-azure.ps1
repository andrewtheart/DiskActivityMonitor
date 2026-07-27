<#
.SYNOPSIS
    Builds Disk Activity Monitor, packages a portable ZIP, and publishes
    the installers + ZIP to the Azure Blob static site at
    https://installmonitordl.z13.web.core.windows.net/

.DESCRIPTION
    1. Builds x64 and x86 self-contained installers (via build-installer.ps1 -All)
    2. Creates a portable ZIP (self-contained x64 publish + README)
    3. Uploads installers and ZIP to the private 'downloads' container
    4. Downloads the live index.html, surgically inserts/updates ONLY the
       Disk Activity Monitor card, and re-uploads it.

.EXAMPLE
    .\scripts\publish-to-azure.ps1
    .\scripts\publish-to-azure.ps1 -Version 1.1.0
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version = '1.0.0',
    [string]$StorageAccount = 'installmonitordl',
    [string]$StaticContainer = '$web',
    [string]$DownloadsContainer = 'downloads'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot   # repo root (scripts\ is one level down)
$serviceProj = Join-Path $root 'src\DiskActivityMonitor.Service\DiskActivityMonitor.Service.csproj'
$trayProj    = Join-Path $root 'src\DiskActivityMonitor.Tray\DiskActivityMonitor.Tray.csproj'
$cliProj     = Join-Path $root 'src\DiskActivityMonitor.Cli\DiskActivityMonitor.Cli.csproj'
$installerScript = Join-Path $root 'installer\build-installer.ps1'

# ── temp dirs ────────────────────────────────────────────────────────────────
$tempRoot   = Join-Path ([IO.Path]::GetTempPath()) ("dam-release-" + [Guid]::NewGuid().ToString('N'))
$publishDir = Join-Path $tempRoot 'publish'
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

try {
    # ── Step 1: Build x64 + x86 installers ───────────────────────────────────
    Write-Host "`n=== Step 1: Build installers (x64 + x86) ===" -ForegroundColor Cyan
    & $installerScript -All -Version $Version -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

    $x64Installer = "DiskActivityMonitor-Setup-$Version-x64.exe"
    $x86Installer = "DiskActivityMonitor-Setup-$Version-x86.exe"
    $x64Path = Join-Path $root "installer\Output\$x64Installer"
    $x86Path = Join-Path $root "installer\Output\$x86Installer"
    if (-not (Test-Path $x64Path)) { throw "x64 installer not found: $x64Path" }
    if (-not (Test-Path $x86Path)) { throw "x86 installer not found: $x86Path" }
    $x64SizeMB = [math]::Round((Get-Item $x64Path).Length / 1MB, 1)
    $x86SizeMB = [math]::Round((Get-Item $x86Path).Length / 1MB, 1)

    # ── Step 2: Create portable ZIP (self-contained x64) ─────────────────────
    Write-Host "`n=== Step 2: Create portable ZIP ===" -ForegroundColor Cyan
    Write-Host 'Publishing service (self-contained x64)...'
    dotnet publish $serviceProj -c $Configuration -r win-x64 --self-contained -o (Join-Path $publishDir 'service') --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Service publish failed.' }

    Write-Host 'Publishing tray (self-contained x64)...'
    dotnet publish $trayProj -c $Configuration -r win-x64 --self-contained -o (Join-Path $publishDir 'tray') --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Tray publish failed.' }

    Write-Host 'Publishing CLI (self-contained x64)...'
    dotnet publish $cliProj -c $Configuration -r win-x64 --self-contained -o (Join-Path $publishDir 'cli') --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }

    # Include README and install/uninstall scripts
    foreach ($f in @('README.md', 'scripts\install.ps1', 'scripts\uninstall.ps1')) {
        $src = Join-Path $root $f
        if (Test-Path $src) { Copy-Item $src (Join-Path $publishDir (Split-Path $f -Leaf)) }
    }

    $zipName = "DiskActivityMonitor-$Version-x64-portable.zip"
    $zipPath = Join-Path $tempRoot $zipName
    Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -CompressionLevel Optimal
    $zipSizeMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Host "  $zipName - $zipSizeMB MB" -ForegroundColor Green

    # ── Step 3: Upload to private downloads container ────────────────────────
    Write-Host "`n=== Step 3: Upload to downloads container ===" -ForegroundColor Cyan
    foreach ($item in @(
        @{ Name = $x64Installer; File = $x64Path; Type = 'application/octet-stream' },
        @{ Name = $x86Installer; File = $x86Path; Type = 'application/octet-stream' },
        @{ Name = $zipName;      File = $zipPath; Type = 'application/zip' }
    )) {
        Write-Host "  Uploading $($item.Name)..."
        az storage blob upload `
            --account-name $StorageAccount `
            --container-name $DownloadsContainer `
            --name $item.Name `
            --file $item.File `
            --overwrite `
            --content-type $item.Type `
            --auth-mode key `
            -o none
        if ($LASTEXITCODE -ne 0) { throw "Upload failed: $($item.Name)" }
    }
    Write-Host '  All files uploaded.' -ForegroundColor Green

    # ── Step 4: Update the Disk Activity Monitor card in index.html ──────────
    Write-Host "`n=== Step 4: Update card in index.html ===" -ForegroundColor Cyan

    $htmlPath = 'D:\installationSite\index.html'

    # 4a. Download the live index.html
    az storage blob download `
        --account-name $StorageAccount `
        --container-name $StaticContainer `
        --name 'index.html' `
        --file $htmlPath `
        --auth-mode key `
        --overwrite `
        -o none
    if ($LASTEXITCODE -ne 0) { throw 'Failed to download live index.html' }

    $live = Get-Content -LiteralPath $htmlPath -Raw -Encoding UTF8

    # 4b. Build the card HTML
    $cst = [System.TimeZoneInfo]::FindSystemTimeZoneById('Central Standard Time')
    $nowCst = [System.TimeZoneInfo]::ConvertTimeFromUtc([DateTime]::UtcNow, $cst)
    $releasedDate = $nowCst.ToString('MMM d, yyyy h:mm tt') + ' CST'
    $ghRepo = 'https://github.com/andrewtheart/DiskActivityMonitor'

    $newCard = @"
        <div class="card">
            <h1>Disk Activity Monitor</h1>
            <p class="version">v$Version &middot; Windows x64 / x86</p>
            <p class="desc">Track SSD/HDD read-write trends, rank noisiest processes via ETW, project drive endurance, and get configurable alerts to protect SSD write life.</p>
            <a href="#" data-blob="$x64Installer" class="btn download-btn">Installer (x64)</a>
            <a href="#" data-blob="$x86Installer" class="btn download-btn">Installer (x86)</a>
            <a href="#" data-blob="$zipName" class="btn download-btn">Portable ZIP (x64)</a>
            <p class="size">x64 ~${x64SizeMB} MB &middot; x86 ~${x86SizeMB} MB &middot; ZIP ~${zipSizeMB} MB</p>
            <p class="note">Installers register a Windows Service + tray app. The portable ZIP includes service, tray, and CLI.<br>Self-contained &mdash; no .NET runtime required.</p>
            <p class="size" style="margin-top: 8px;"><a href="$ghRepo" style="color:#60a5fa;">GitHub</a> &middot; Released: $releasedDate</p>
        </div>
"@

    # 4c. Replace existing card or insert a new one
    # The card regex must match ALL nested </div> tags inside the card.
    # A card contains inner elements but no nested <div>, so we match up to
    # the LAST </div> that closes the card itself by being greedy within the
    # card boundary (from <div class="card"> with our <h1> to its </div> that
    # is followed by whitespace and either another <div class="card"> or the
    # grid-closing </div>).
    $cardRegex = '(?s)[ \t]*<div class="card">\s*<h1>Disk Activity Monitor</h1>.*?</div>\s*(?=</div>|<div class="card">)'

    if ($live -match $cardRegex) {
        $updated = [regex]::Replace($live, $cardRegex, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $newCard + "`r`n" }, 1)
        Write-Host '  Replaced existing Disk Activity Monitor card.'
    }
    elseif ($live -match '(?s)([ \t]*</div>\s*</div>\s*</div>\s*<script>)') {
        # No card yet - insert before the closing grid/content divs
        $updated = $live -replace '(?s)([ \t]*</div>\s*</div>\s*</div>\s*<script>)', ($newCard + "`r`n" + '$1')
        Write-Host '  Inserted new Disk Activity Monitor card.'
    }
    else {
        throw 'Could not locate a safe insertion point in index.html.'
    }

    Set-Content -LiteralPath $htmlPath -Value $updated -Encoding UTF8 -NoNewline

    # 4d. Upload the edited index.html
    az storage blob upload `
        --account-name $StorageAccount `
        --container-name $StaticContainer `
        --name 'index.html' `
        --file $htmlPath `
        --overwrite `
        --content-type 'text/html' `
        --auth-mode key `
        -o none
    if ($LASTEXITCODE -ne 0) { throw 'index.html upload failed' }
    Write-Host '  Uploaded index.html' -ForegroundColor Green

    # ── Done ─────────────────────────────────────────────────────────────────
    Write-Host ''
    Write-Host '=== Done! ===' -ForegroundColor Green
    Write-Host "Site:      https://installmonitordl.z13.web.core.windows.net/" -ForegroundColor Yellow
    Write-Host "GitHub:    $ghRepo" -ForegroundColor Yellow
    Write-Host "x64:       $x64Installer ($x64SizeMB MB)" -ForegroundColor Yellow
    Write-Host "x86:       $x86Installer ($x86SizeMB MB)" -ForegroundColor Yellow
    Write-Host "ZIP:       $zipName ($zipSizeMB MB)" -ForegroundColor Yellow
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
