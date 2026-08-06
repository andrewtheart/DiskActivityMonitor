# Generates assets/app.ico from the same glyph drawn by TrayIconFactory.Create,
# so the executable/installer/shortcut icon matches the system-tray and taskbar icon.
# Multi-resolution ICO with PNG-compressed frames (16..256 px).
[CmdletBinding()]
param(
    [string]$OutPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\app.ico'),
    [string]$WizardAssetDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'installer\assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Add-RoundedRect {
    param($Path, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r)
    $d = $r * 2
    $Path.AddArc($x, $y, $d, $d, 180, 90)
    $Path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $Path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $Path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $Path.CloseFigure()
}

function New-GlyphPng {
    param([int]$Size)
    $s = $Size / 32.0
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Drive body (rounded rectangle).
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-RoundedRect $path (3 * $s) (7 * $s) (26 * $s) (18 * $s) (4 * $s)
    $fill = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(0x33, 0x37, 0x3B))
    $bodyPen = New-Object System.Drawing.Pen -ArgumentList ([System.Drawing.Color]::FromArgb(0x6A, 0x70, 0x76)), ([float](1.5 * $s))
    $g.FillPath($fill, $path)
    $g.DrawPath($bodyPen, $path)

    # Activity lanes.
    $lane = New-Object System.Drawing.Pen -ArgumentList ([System.Drawing.Color]::FromArgb(0x8A, 0x92, 0x9A)), ([float](1.4 * $s))
    $g.DrawLine($lane, [float](7 * $s), [float](13 * $s), [float](25 * $s), [float](13 * $s))
    $g.DrawLine($lane, [float](7 * $s), [float](17 * $s), [float](22 * $s), [float](17 * $s))
    $g.DrawLine($lane, [float](7 * $s), [float](21 * $s), [float](19 * $s), [float](21 * $s))

    # Status dot (healthy green, matching TrayIconFactory.Ok).
    $dot = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(0x3F, 0xB9, 0x50))
    $ring = New-Object System.Drawing.Pen -ArgumentList ([System.Drawing.Color]::FromArgb(0x20, 0x20, 0x20)), ([float](1.2 * $s))
    $g.FillEllipse($dot, [float](19 * $s), [float](17 * $s), [float](11 * $s), [float](11 * $s))
    $g.DrawEllipse($ring, [float](19 * $s), [float](17 * $s), [float](11 * $s), [float](11 * $s))

    $g.Dispose()
    $msImg = New-Object System.IO.MemoryStream
    $bmp.Save($msImg, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $msImg.ToArray()
}

function New-Canvas {
    param([int]$Width, [int]$Height)

    New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
}

function New-Brush {
    param([int]$Red, [int]$Green, [int]$Blue)

    New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb($Red, $Green, $Blue))
}

function Get-AlphaBounds {
    param([System.Drawing.Bitmap]$Bitmap)

    $left = $Bitmap.Width
    $top = $Bitmap.Height
    $right = -1
    $bottom = -1
    for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            if ($Bitmap.GetPixel($x, $y).A -eq 0) { continue }
            if ($x -lt $left) { $left = $x }
            if ($x -gt $right) { $right = $x }
            if ($y -lt $top) { $top = $y }
            if ($y -gt $bottom) { $bottom = $y }
        }
    }
    if ($right -lt $left -or $bottom -lt $top) {
        throw 'The generated disk glyph is fully transparent.'
    }
    New-Object System.Drawing.Rectangle($left, $top, ($right - $left + 1), ($bottom - $top + 1))
}

function Save-WizardAssets {
    param([byte[]]$GlyphPng, [string]$OutputDirectory)

    if (-not (Test-Path $OutputDirectory)) {
        New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
    }

    $glyphStream = New-Object System.IO.MemoryStream -ArgumentList (,$GlyphPng)
    $glyph = [System.Drawing.Image]::FromStream($glyphStream)
    try {
        $background = [System.Drawing.Color]::FromArgb(0x10, 0x14, 0x19)
        $panel = [System.Drawing.Color]::FromArgb(0x1C, 0x22, 0x29)
        $grid = [System.Drawing.Color]::FromArgb(0x24, 0x2C, 0x35)
        $accent = [System.Drawing.Color]::FromArgb(0x3F, 0xB9, 0x50)
        $primary = [System.Drawing.Color]::FromArgb(0xF2, 0xF5, 0xF7)
        $secondary = [System.Drawing.Color]::FromArgb(0xA9, 0xB2, 0xBC)

        $large = New-Canvas -Width 492 -Height 942
        $graphics = [System.Drawing.Graphics]::FromImage($large)
        try {
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.Clear($background)

            $gridPen = New-Object System.Drawing.Pen -ArgumentList $grid, 2
            try {
                for ($x = 54; $x -lt 492; $x += 54) { $graphics.DrawLine($gridPen, $x, 0, $x, 942) }
                for ($y = 54; $y -lt 942; $y += 54) { $graphics.DrawLine($gridPen, 0, $y, 492, $y) }
            }
            finally { $gridPen.Dispose() }

            $accentBrush = New-Brush -Red 0x3F -Green 0xB9 -Blue 0x50
            $panelBrush = New-Brush -Red 0x1C -Green 0x22 -Blue 0x29
            $primaryBrush = New-Brush -Red 0xF2 -Green 0xF5 -Blue 0xF7
            $secondaryBrush = New-Brush -Red 0xA9 -Green 0xB2 -Blue 0xBC
            $brandFont = New-Object System.Drawing.Font('Segoe UI Semibold', 25, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            $subBrandFont = New-Object System.Drawing.Font('Segoe UI', 17, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
            $taglineFont = New-Object System.Drawing.Font('Segoe UI', 17, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
            try {
                $graphics.FillRectangle($accentBrush, 0, 0, 9, 942)
                $graphics.DrawString('DISK ACTIVITY', $brandFont, $primaryBrush, 60, 64)
                $graphics.DrawString('MONITOR', $subBrandFont, $accentBrush, 61, 104)
                $graphics.FillRectangle($panelBrush, 58, 218, 376, 390)
                $graphics.FillRectangle($accentBrush, 58, 218, 376, 7)
                $graphics.DrawImage($glyph, 116, 284, 260, 260)
                $graphics.DrawString('See what writes. Protect your drives.', $taglineFont, $secondaryBrush, 60, 704)
                $graphics.DrawString('Local monitoring', $taglineFont, $primaryBrush, 60, 758)
            }
            finally {
                $taglineFont.Dispose()
                $subBrandFont.Dispose()
                $brandFont.Dispose()
                $secondaryBrush.Dispose()
                $primaryBrush.Dispose()
                $panelBrush.Dispose()
                $accentBrush.Dispose()
            }
        }
        finally { $graphics.Dispose() }
        try {
            $large.Save((Join-Path $OutputDirectory 'wizard-dark.png'), [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $large.Dispose() }

        $small = New-Canvas -Width 256 -Height 256
        $graphics = [System.Drawing.Graphics]::FromImage($small)
        try {
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $glyphBitmap = New-Object System.Drawing.Bitmap($glyph)
            try {
                $source = Get-AlphaBounds -Bitmap $glyphBitmap
                $destination = New-Object System.Drawing.Rectangle(8, 24, 240, 208)
                $graphics.DrawImage(
                    $glyphBitmap,
                    $destination,
                    $source.X,
                    $source.Y,
                    $source.Width,
                    $source.Height,
                    [System.Drawing.GraphicsUnit]::Pixel)
            }
            finally { $glyphBitmap.Dispose() }
        }
        finally { $graphics.Dispose() }
        try {
            $small.Save((Join-Path $OutputDirectory 'wizard-small-dark.png'), [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $small.Dispose() }
    }
    finally {
        $glyph.Dispose()
        $glyphStream.Dispose()
    }
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = foreach ($sz in $sizes) { , (New-GlyphPng -Size $sz) }

$outDir = Split-Path -Parent $OutPath
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ms
$bw.Write([uint16]0)            # reserved
$bw.Write([uint16]1)            # type = icon
$bw.Write([uint16]$sizes.Count) # image count

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $data = $frames[$i]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim)        # width  (0 => 256)
    $bw.Write([byte]$dim)        # height (0 => 256)
    $bw.Write([byte]0)           # palette count
    $bw.Write([byte]0)           # reserved
    $bw.Write([uint16]1)         # color planes
    $bw.Write([uint16]32)        # bits per pixel
    $bw.Write([uint32]$data.Length)
    $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $frames) { $bw.Write($data) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($OutPath, $ms.ToArray())
$bw.Dispose()

Save-WizardAssets -GlyphPng $frames[-1] -OutputDirectory $WizardAssetDirectory

Write-Host "Wrote $OutPath ($([math]::Round((Get-Item $OutPath).Length/1KB,1)) KB, $($sizes.Count) sizes)"
Write-Host "Wrote dark installer artwork to $WizardAssetDirectory"
