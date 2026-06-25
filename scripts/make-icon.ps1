# Generates assets/app.ico from the same glyph drawn by TrayIconFactory.Create,
# so the executable/installer/shortcut icon matches the system-tray and taskbar icon.
# Multi-resolution ICO with PNG-compressed frames (16..256 px).
[CmdletBinding()]
param(
    [string]$OutPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\app.ico')
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

Write-Host "Wrote $OutPath ($([math]::Round((Get-Item $OutPath).Length/1KB,1)) KB, $($sizes.Count) sizes)"
