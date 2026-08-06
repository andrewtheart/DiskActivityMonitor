---
name: dam-dashboard-screenshot
description: "Capture and stitch the complete Disk Activity Monitor dashboard into the primary README image. Use when: update dashboard screenshot, refresh README screenshot, capture full dashboard, stitch scrolled dashboard, dashboard-overview.png."
---

# Disk Activity Monitor Dashboard Screenshot

Use the bundled script to regenerate the full-height dashboard image from the running installed tray app.

## Preconditions

- Build and install the current application before capture so the screenshot matches the current UI.
- Open **Disk Activity Monitor** and leave it on the main Dashboard view.
- Close modal overlays and popups.
- Keep the installed collector service running long enough for live charts to contain representative data.
- Do not terminate tray or service processes for capture.

## Capture

Run from the repository root:

```powershell
pwsh -File .\.github\skills\dam-dashboard-screenshot\scripts\capture-dashboard.ps1
```

The script must:

1. Find the visible `DiskActivityMonitor.Tray` window.
2. Verify the Dashboard view and locate `BodyScroller` through UI Automation.
3. Preserve the original window geometry and scroll percentage.
4. Capture overlapping top/middle/bottom frames with `PrintWindow(PW_RENDERFULLCONTENT)`.
5. Refine expected overlaps by comparing frame pixels.
6. Keep the fixed title/header once, remove the repeated scrollbar, and stitch the scrolling body once.
7. Validate dimensions, overlap error, nonblank content, and the README reference.
8. Atomically replace `assets/dashboard-overview.png` and restore the app state.

## Options

```powershell
# Keep source frames and the preview stitch for diagnosis
pwsh -File .\.github\skills\dam-dashboard-screenshot\scripts\capture-dashboard.ps1 -KeepFrames

# Use a specific visible tray process
pwsh -File .\.github\skills\dam-dashboard-screenshot\scripts\capture-dashboard.ps1 -ProcessId 12345

# Validate the method without replacing the README asset
pwsh -File .\.github\skills\dam-dashboard-screenshot\scripts\capture-dashboard.ps1 -OutputPath .\TestResults\dashboard-preview.png -SkipReadmeUpdate
```

## Verification

After capture:

- Open `assets/dashboard-overview.png` and inspect every seam at full resolution.
- Confirm the fixed header appears once.
- Confirm all dashboard cards from summary through Suspended processes appear once.
- Confirm no repeated scrollbar thumbs, clipped cards, blank bands, or duplicated chart rows.
- Confirm `README.md` references `assets/dashboard-overview.png`.
- Run `git diff --check` and inspect the asset diff metadata.

If alignment validation fails, rerun with `-KeepFrames`; inspect the emitted frames and alignment report rather than manually stitching screenshots.