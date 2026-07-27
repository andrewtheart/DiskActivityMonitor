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

## Building locally

Run from the repository root:

```powershell
.\installer\build-installer.ps1 -Version 1.2.0             # x64 only (default)
.\installer\build-installer.ps1 -All -Version 1.2.0        # both architectures
.\installer\build-installer.ps1 -All -Version 1.2.0 -Push  # build, commit + push, publish a GitHub release as latest
```

Build output lands in `installer/Output/`, which is git-ignored. Requires
[Inno Setup 6](https://jrsoftware.org/isinfo.php) and the .NET SDK; `-Push` also requires the
authenticated GitHub CLI (`gh`).
