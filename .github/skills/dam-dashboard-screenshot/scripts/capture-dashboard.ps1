<#[
.SYNOPSIS
  Capture and stitch the complete Disk Activity Monitor dashboard.

.DESCRIPTION
  Uses UI Automation to scroll the main BodyScroller, PrintWindow to capture
  sharp frames even when the window is occluded, and pixel matching to refine
  overlap placement. Replaces assets/dashboard-overview.png only after the
  stitch passes validation.
#>
[CmdletBinding()]
param(
  [string]$OutputPath,
  [int]$ProcessId = 0,
  [ValidateRange(1200, 4096)]
  [int]$CaptureWidth = 1920,
  [ValidateRange(700, 2160)]
  [int]$CaptureHeight = 1032,
  [ValidateRange(20, 300)]
  [int]$SearchRadius = 90,
  [ValidateRange(0.001, 0.25)]
  [double]$MaxAlignmentError = 0.08,
  [switch]$KeepFrames,
  [switch]$SkipReadmeUpdate
)

$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
  $OutputPath = Join-Path $repoRoot 'assets\dashboard-overview.png'
}
elseif (-not [IO.Path]::IsPathRooted($OutputPath)) {
  $OutputPath = Join-Path $repoRoot $OutputPath
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ('DamDashboardCapture.Native' -as [type])) {
  $drawingDirectory = Split-Path -Parent ([Drawing.Bitmap].Assembly.Location)
  $gdiPlusAssembly = Join-Path $drawingDirectory 'System.Private.Windows.GdiPlus.dll'
  $windowsCoreAssembly = Join-Path $drawingDirectory 'System.Private.Windows.Core.dll'
  Add-Type -ReferencedAssemblies @(
    [Drawing.Bitmap].Assembly.Location,
    [Drawing.Rectangle].Assembly.Location,
    $gdiPlusAssembly,
    $windowsCoreAssembly
  ) -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DamDashboardCapture
{
    public sealed class AlignmentResult
    {
        public int Shift { get; set; }
        public double Error { get; set; }
        public int ComparedPixels { get; set; }
    }

    public sealed class WindowState
    {
        internal Native.WINDOWPLACEMENT Placement;
    }

    public static class Native
    {
        internal const uint PW_RENDERFULLCONTENT = 2;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int SW_RESTORE = 9;
        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_UPDATENOW = 0x0100;
        private const uint RDW_ALLCHILDREN = 0x0080;

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WINDOWPLACEMENT
        {
            public int Length;
            public int Flags;
            public int ShowCmd;
            public POINT MinPosition;
            public POINT MaxPosition;
            public RECT NormalPosition;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int command);

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(
            IntPtr hWnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr targetDc, uint flags);

        public static WindowState SaveWindowState(IntPtr hWnd)
        {
            var placement = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(hWnd, ref placement))
                throw new InvalidOperationException("GetWindowPlacement failed.");
            return new WindowState { Placement = placement };
        }

        public static void PrepareWindow(IntPtr hWnd, int width, int height)
        {
            ShowWindow(hWnd, SW_RESTORE);
            if (!SetWindowPos(hWnd, IntPtr.Zero, 0, 0, width, height, SWP_NOZORDER | SWP_NOACTIVATE))
                throw new InvalidOperationException("SetWindowPos failed.");
            RedrawWindow(hWnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN);
        }

        public static void RestoreWindowState(IntPtr hWnd, WindowState state)
        {
            var placement = state.Placement;
            placement.Length = Marshal.SizeOf<WINDOWPLACEMENT>();
            SetWindowPlacement(hWnd, ref placement);
            RedrawWindow(hWnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN);
        }

        public static int[] GetRect(IntPtr hWnd)
        {
            RECT rect;
            if (!GetWindowRect(hWnd, out rect))
                throw new InvalidOperationException("GetWindowRect failed.");
            return new[] { rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top };
        }

        public static Bitmap CaptureWindow(IntPtr hWnd)
        {
            int[] rect = GetRect(hWnd);
            var bitmap = new Bitmap(rect[2], rect[3], PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                IntPtr dc = graphics.GetHdc();
                try
                {
                    if (!PrintWindow(hWnd, dc, PW_RENDERFULLCONTENT))
                        throw new InvalidOperationException("PrintWindow failed.");
                }
                finally
                {
                    graphics.ReleaseHdc(dc);
                }
            }
            return bitmap;
        }

        public static AlignmentResult FindBestShift(
            Bitmap previous,
            Bitmap current,
            Rectangle viewport,
            int expectedShift,
            int searchRadius,
            int excludedX,
            int excludedWidth)
        {
            byte[] previousBytes;
            byte[] currentBytes;
            int previousStride;
            int currentStride;
            CopyPixels(previous, out previousBytes, out previousStride);
            CopyPixels(current, out currentBytes, out currentStride);

            int minimumShift = Math.Max(1, expectedShift - searchRadius);
            int maximumShift = Math.Min(viewport.Height - 80, expectedShift + searchRadius);
            var best = new AlignmentResult { Shift = expectedShift, Error = double.MaxValue };

            for (int shift = minimumShift; shift <= maximumShift; shift++)
            {
                int overlap = viewport.Height - shift;
                long difference = 0;
                int compared = 0;

                for (int y = 0; y < overlap; y += 3)
                {
                    int previousY = viewport.Y + shift + y;
                    int currentY = viewport.Y + y;
                    for (int x = 8; x < viewport.Width - 8; x += 7)
                    {
                        if (x >= excludedX - 2 && x < excludedX + excludedWidth + 2)
                            continue;

                        int absoluteX = viewport.X + x;
                        int previousOffset = previousY * previousStride + absoluteX * 4;
                        int currentOffset = currentY * currentStride + absoluteX * 4;
                        difference += Math.Abs(previousBytes[previousOffset] - currentBytes[currentOffset]);
                        difference += Math.Abs(previousBytes[previousOffset + 1] - currentBytes[currentOffset + 1]);
                        difference += Math.Abs(previousBytes[previousOffset + 2] - currentBytes[currentOffset + 2]);
                        compared++;
                    }
                }

                double error = compared == 0 ? double.MaxValue : difference / (compared * 3d * 255d);
                if (error < best.Error)
                {
                    best.Shift = shift;
                    best.Error = error;
                    best.ComparedPixels = compared;
                }
            }

            return best;
        }

        public static Bitmap Stitch(
            IList<Bitmap> frames,
            Rectangle viewport,
            int[] shifts,
            int scrollbarX,
            int scrollbarWidth,
            Color background)
        {
            if (frames == null || frames.Count == 0)
                throw new ArgumentException("At least one frame is required.");
            if (shifts.Length != frames.Count - 1)
                throw new ArgumentException("Shift count must be one less than frame count.");

            int headerHeight = viewport.Y;
            int footerHeight = frames[0].Height - (viewport.Y + viewport.Height);
            int bodyHeight = viewport.Height;
            foreach (int shift in shifts)
              bodyHeight += shift;
            var frameStarts = new int[frames.Count];
            for (int index = 1; index < frames.Count; index++)
              frameStarts[index] = frameStarts[index - 1] + shifts[index - 1];
            var boundaries = new int[frames.Count - 1];
            for (int index = 0; index < boundaries.Length; index++)
            {
              int overlapStart = frameStarts[index + 1];
              int overlapEnd = frameStarts[index] + viewport.Height;
              boundaries[index] = (overlapStart + overlapEnd) / 2;
            }
            var output = new Bitmap(frames[0].Width, headerHeight + bodyHeight + footerHeight, PixelFormat.Format32bppArgb);

            using (Graphics graphics = Graphics.FromImage(output))
            using (var backgroundBrush = new SolidBrush(background))
            {
                graphics.Clear(background);
                graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

                graphics.DrawImage(
                    frames[0],
                    new Rectangle(0, 0, frames[0].Width, headerHeight),
                    new Rectangle(0, 0, frames[0].Width, headerHeight),
                    GraphicsUnit.Pixel);
                for (int index = 0; index < frames.Count; index++)
                {
                  int segmentStart = index == 0 ? 0 : boundaries[index - 1];
                  int segmentEnd = index == frames.Count - 1 ? bodyHeight : boundaries[index];
                  int segmentHeight = segmentEnd - segmentStart;
                  int sourceY = viewport.Y + segmentStart - frameStarts[index];
                    graphics.DrawImage(
                        frames[index],
                    new Rectangle(viewport.X, headerHeight + segmentStart, viewport.Width, segmentHeight),
                    new Rectangle(viewport.X, sourceY, viewport.Width, segmentHeight),
                        GraphicsUnit.Pixel);
                }

                if (footerHeight > 0)
                {
                    Bitmap last = frames[frames.Count - 1];
                    graphics.DrawImage(
                        last,
                        new Rectangle(0, headerHeight + bodyHeight, last.Width, footerHeight),
                        new Rectangle(0, viewport.Y + viewport.Height, last.Width, footerHeight),
                        GraphicsUnit.Pixel);
                }

                if (scrollbarWidth > 0)
                    graphics.FillRectangle(backgroundBrush, scrollbarX, headerHeight, scrollbarWidth, bodyHeight);
            }

            return output;
        }

        public static double NonBackgroundFraction(Bitmap bitmap, Color background)
        {
            int different = 0;
            int sampled = 0;
            for (int y = 0; y < bitmap.Height; y += 12)
            {
                for (int x = 0; x < bitmap.Width; x += 12)
                {
                    Color color = bitmap.GetPixel(x, y);
                    if (Math.Abs(color.R - background.R) + Math.Abs(color.G - background.G) +
                        Math.Abs(color.B - background.B) > 18)
                        different++;
                    sampled++;
                }
            }
            return sampled == 0 ? 0 : different / (double)sampled;
        }

        private static void CopyPixels(Bitmap bitmap, out byte[] bytes, out int stride)
        {
            Rectangle rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            BitmapData data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                stride = Math.Abs(data.Stride);
                bytes = new byte[stride * bitmap.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
    }
}
'@
}

function Get-RelativeRectangle {
  param(
    [Parameter(Mandatory)]$AutomationRectangle,
    [Parameter(Mandatory)][int[]]$WindowRectangle
  )

  return [Drawing.Rectangle]::new(
    [int][math]::Round($AutomationRectangle.X - $WindowRectangle[0]),
    [int][math]::Round($AutomationRectangle.Y - $WindowRectangle[1]),
    [int][math]::Round($AutomationRectangle.Width),
    [int][math]::Round($AutomationRectangle.Height))
}

function Find-AutomationElementById {
  param(
    [Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$Root,
    [Parameter(Mandatory)][string]$AutomationId
  )

  $condition = [System.Windows.Automation.PropertyCondition]::new(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
    $AutomationId)
  return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

$process = if ($ProcessId -gt 0) {
  Get-Process -Id $ProcessId -ErrorAction Stop
}
else {
  Get-Process DiskActivityMonitor.Tray -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } |
    Sort-Object StartTime -Descending |
    Select-Object -First 1
}
if (-not $process -or $process.MainWindowHandle -eq 0) {
  throw 'Open the installed Disk Activity Monitor dashboard before capture.'
}

$windowHandle = [IntPtr]$process.MainWindowHandle
$windowState = [DamDashboardCapture.Native]::SaveWindowState($windowHandle)
$originalScrollPercent = 0.0
$workDirectory = Join-Path $env:TEMP ('dam-dashboard-capture-' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $workDirectory)
$frames = [Collections.Generic.List[Drawing.Bitmap]]::new()
$framePaths = [Collections.Generic.List[string]]::new()
$alignments = [Collections.Generic.List[object]]::new()

try {
  [DamDashboardCapture.Native]::PrepareWindow($windowHandle, $CaptureWidth, $CaptureHeight)
  $windowRectangle = [DamDashboardCapture.Native]::GetRect($windowHandle)
  $root = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)

  $dashboardMarker = $root.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.PropertyCondition]::new(
      [System.Windows.Automation.AutomationElement]::NameProperty,
      'Live disk activity'))
  if (-not $dashboardMarker) {
    throw 'The main Dashboard view is not active. Close overlays or return from Settings.'
  }

  $scroller = Find-AutomationElementById -Root $root -AutomationId 'BodyScroller'
  if (-not $scroller) { throw 'BodyScroller was not found through UI Automation.' }
  $patternObject = $null
  if (-not $scroller.TryGetCurrentPattern(
      [System.Windows.Automation.ScrollPattern]::Pattern,
      [ref]$patternObject)) {
    throw 'BodyScroller does not expose ScrollPattern.'
  }
  $scrollPattern = [System.Windows.Automation.ScrollPattern]$patternObject
  if (-not $scrollPattern.Current.VerticallyScrollable) {
    throw 'BodyScroller is not vertically scrollable; a stitched screenshot is unnecessary.'
  }

  $originalScrollPercent = $scrollPattern.Current.VerticalScrollPercent
  $viewport = Get-RelativeRectangle -AutomationRectangle $scroller.Current.BoundingRectangle -WindowRectangle $windowRectangle
  if ($viewport.Width -lt 800 -or $viewport.Height -lt 400) {
    throw "Unexpected BodyScroller viewport: $viewport"
  }

  $scrollbarCondition = [System.Windows.Automation.AndCondition]::new(
    [System.Windows.Automation.PropertyCondition]::new(
      [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
      [System.Windows.Automation.ControlType]::ScrollBar),
    [System.Windows.Automation.PropertyCondition]::new(
      [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
      'VerticalScrollBar'))
  $scrollbar = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $scrollbarCondition)
  $scrollbarRectangle = if ($scrollbar) {
    Get-RelativeRectangle -AutomationRectangle $scrollbar.Current.BoundingRectangle -WindowRectangle $windowRectangle
  }
  else {
    [Drawing.Rectangle]::new($viewport.Right - 10, $viewport.Y, 10, $viewport.Height)
  }

  $viewSize = $scrollPattern.Current.VerticalViewSize
  $estimatedContentHeight = [int][math]::Round($viewport.Height * 100.0 / $viewSize)
  $maximumScroll = [math]::Max(0, $estimatedContentHeight - $viewport.Height)
  $minimumOverlap = [math]::Min(220, [int]($viewport.Height * 0.25))
  $usableAdvance = [math]::Max(1, $viewport.Height - $minimumOverlap)
  $frameCount = [math]::Max(2, [int][math]::Ceiling($maximumScroll / $usableAdvance) + 1)
  $positions = for ($index = 0; $index -lt $frameCount; $index++) {
    if ($frameCount -eq 1) { 0.0 } else { 100.0 * $index / ($frameCount - 1) }
  }

  Write-Host ("Capturing {0} frames; viewport {1}x{2}; estimated content height {3}." -f
    $frameCount, $viewport.Width, $viewport.Height, $estimatedContentHeight) -ForegroundColor Cyan

  foreach ($position in $positions) {
    $scrollPattern.SetScrollPercent(
      [System.Windows.Automation.ScrollPattern]::NoScroll,
      $position)
    $bitmap = [DamDashboardCapture.Native]::CaptureWindow($windowHandle)
    if ($bitmap.Width -ne $windowRectangle[2] -or $bitmap.Height -ne $windowRectangle[3]) {
      $bitmap.Dispose()
      throw "Captured frame dimensions changed unexpectedly at $position percent."
    }
    $framePath = Join-Path $workDirectory ("frame-{0:000}.png" -f [int][math]::Round($position))
    $bitmap.Save($framePath, [Drawing.Imaging.ImageFormat]::Png)
    $frames.Add($bitmap)
    $framePaths.Add($framePath)
  }

  $shifts = [Collections.Generic.List[int]]::new()
  for ($index = 1; $index -lt $frames.Count; $index++) {
    $percentDelta = $positions[$index] - $positions[$index - 1]
    $expectedShift = [int][math]::Round($maximumScroll * $percentDelta / 100.0)
    $alignment = [DamDashboardCapture.Native]::FindBestShift(
      $frames[$index - 1],
      $frames[$index],
      $viewport,
      $expectedShift,
      $SearchRadius,
      $scrollbarRectangle.X - $viewport.X,
      $scrollbarRectangle.Width)
    if ($alignment.Error -gt $MaxAlignmentError) {
      throw ("Frame alignment {0}->{1} failed: shift {2}, normalized error {3:N5}." -f
        ($index - 1), $index, $alignment.Shift, $alignment.Error)
    }
    $shifts.Add($alignment.Shift)
    $alignments.Add([pscustomobject]@{
        Pair = "$(($index - 1))->$index"
        ExpectedShift = $expectedShift
        MeasuredShift = $alignment.Shift
        Overlap = $viewport.Height - $alignment.Shift
        NormalizedError = $alignment.Error
        ComparedPixels = $alignment.ComparedPixels
      })
  }

  $background = [Drawing.Color]::FromArgb(255, 21, 24, 27)
  $stitched = [DamDashboardCapture.Native]::Stitch(
    $frames,
    $viewport,
    $shifts.ToArray(),
    $scrollbarRectangle.X,
    $scrollbarRectangle.Width,
    $background)
  try {
    $minimumHeight = $viewport.Y + $estimatedContentHeight - ($SearchRadius * [math]::Max(1, $shifts.Count))
    $maximumHeight = $viewport.Y + $estimatedContentHeight + ($SearchRadius * [math]::Max(1, $shifts.Count)) +
      ($windowRectangle[3] - $viewport.Bottom)
    if ($stitched.Width -ne $windowRectangle[2] -or
        $stitched.Height -lt $minimumHeight -or
        $stitched.Height -gt $maximumHeight) {
      throw "Stitched dimensions $($stitched.Width)x$($stitched.Height) failed validation."
    }
    $nonBackground = [DamDashboardCapture.Native]::NonBackgroundFraction($stitched, $background)
    if ($nonBackground -lt 0.10) {
      throw ("Stitched image appears blank (non-background fraction {0:P2})." -f $nonBackground)
    }

    $candidatePath = Join-Path $workDirectory 'dashboard-overview-candidate.png'
    $stitched.Save($candidatePath, [Drawing.Imaging.ImageFormat]::Png)

    $outputDirectory = Split-Path -Parent $OutputPath
    if (-not (Test-Path -LiteralPath $outputDirectory)) {
      [void](New-Item -ItemType Directory -Path $outputDirectory -Force)
    }
    $temporaryOutput = Join-Path $outputDirectory ('.dashboard-overview.' + [guid]::NewGuid().ToString('N') + '.tmp.png')
    try {
      Copy-Item -LiteralPath $candidatePath -Destination $temporaryOutput
      if (Test-Path -LiteralPath $OutputPath) {
        Move-Item -LiteralPath $temporaryOutput -Destination $OutputPath -Force
      }
      else {
        Move-Item -LiteralPath $temporaryOutput -Destination $OutputPath
      }
    }
    finally {
      if (Test-Path -LiteralPath $temporaryOutput) {
        Remove-Item -LiteralPath $temporaryOutput -Force -ErrorAction SilentlyContinue
      }
    }

    if (-not $SkipReadmeUpdate) {
      $readmePath = Join-Path $repoRoot 'README.md'
      $readme = [IO.File]::ReadAllText($readmePath)
      if (-not $readme.Contains('assets/dashboard-overview.png')) {
        throw 'README.md does not reference assets/dashboard-overview.png.'
      }
    }

    if ($KeepFrames) {
      $reportPath = Join-Path $workDirectory 'alignment-report.json'
      [pscustomobject]@{
        OutputPath = $OutputPath
        Window = $windowRectangle
        Viewport = $viewport.ToString()
        ViewSizePercent = $viewSize
        EstimatedContentHeight = $estimatedContentHeight
        Positions = $positions
        Alignments = $alignments
        FinalWidth = $stitched.Width
        FinalHeight = $stitched.Height
        NonBackgroundFraction = $nonBackground
      } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding utf8
    }

    Write-Host "Updated dashboard screenshot: $OutputPath" -ForegroundColor Green
    Write-Host ("Final image: {0}x{1}; non-background {2:P1}." -f
      $stitched.Width, $stitched.Height, $nonBackground) -ForegroundColor Green
    foreach ($alignment in $alignments) {
      Write-Host ("  Pair {0}: shift {1} px, overlap {2} px, error {3:N5}" -f
        $alignment.Pair, $alignment.MeasuredShift, $alignment.Overlap, $alignment.NormalizedError) -ForegroundColor DarkGray
    }
    if ($KeepFrames) {
      Write-Host "Frames and alignment report: $workDirectory" -ForegroundColor Cyan
    }
  }
  finally {
    $stitched.Dispose()
  }
}
finally {
  try {
    if ($scrollPattern) {
      $scrollPattern.SetScrollPercent(
        [System.Windows.Automation.ScrollPattern]::NoScroll,
        $originalScrollPercent)
    }
  }
  catch { }
  [DamDashboardCapture.Native]::RestoreWindowState($windowHandle, $windowState)
  foreach ($frame in $frames) { $frame.Dispose() }
  if (-not $KeepFrames -and (Test-Path -LiteralPath $workDirectory)) {
    Remove-Item -LiteralPath $workDirectory -Recurse -Force -Confirm:$false
  }
}
