# Disk Activity Monitor Installer

Compiled installers are **not** committed to this repository. They are published as
assets on the GitHub Releases page:

- **Latest release:** https://github.com/andrewtheart/DiskActivityMonitor/releases/latest
- **All releases:** https://github.com/andrewtheart/DiskActivityMonitor/releases

## What each release contains

Self-contained installers (no .NET runtime required on the target machine):

| Asset | Description |
| --- | --- |
| `DiskActivityMonitor-Setup-<version>-x64.exe` | 64-bit installer |
| `DiskActivityMonitor-Setup-<version>-x86.exe` | 32-bit installer |

The installer registers the collector as a Windows service (LocalSystem, auto-start) and
installs the tray dashboard per user.

When an existing `config.json` is found, setup asks whether to reuse it. Retained values are
combined with defaults for settings introduced by the new version. Choosing fresh settings
removes only the old configuration; monitoring history remains. The packaged uninstaller also
asks whether settings should be kept for a future reinstall.

## Building locally

Run from the repository root:

```powershell
.\scripts\build-all-installers.ps1 -Version 1.5.0                  # x64 + x86 with a summary
.\scripts\build-all-installers.ps1 -Variant x64 -Version 1.5.0     # selected architecture
.\scripts\build-all-installers.ps1 -Version 1.5.0 -WhatIf          # print the plan only
.\scripts\build-all-installers.ps1 -Version 1.5.0 -Commit          # build, stage, and commit
.\scripts\build-all-installers.ps1 -Version 1.5.0 -Push            # build, commit, push, prompt for draft/publish
.\scripts\build-all-installers.ps1 -Version 1.5.0 -Push -ReleaseMode Draft  # unattended override

# Lower-level installer builder:
.\installer\build-installer.ps1 -Version 1.2.0             # x64 only (default)
.\installer\build-installer.ps1 -All -Version 1.2.0        # both architectures
.\installer\build-installer.ps1 -All -Version 1.2.0 -Push  # build, commit + push, publish a GitHub release as latest
```

Build output lands in `installer/Output/`, which is git-ignored. Requires
[Inno Setup 6](https://jrsoftware.org/isinfo.php) and the .NET SDK; `-Push` also requires the
authenticated GitHub CLI (`gh`). After pushing, the root build-all script asks whether the release
should remain a **draft** for review or be **published officially** as the latest release.
