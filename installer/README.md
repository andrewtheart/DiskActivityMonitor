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
It pins service code to the expected Program Files directory and applies protected ownership
and ACLs through no-follow directory handles that reject reparse points.

When existing machine settings or current-account preferences are found, setup asks whether to
reuse them. Retained values are combined with defaults for settings introduced by the new
version. Choosing fresh settings removes machine `config.json` and the current account's
`user-settings.json` and DPAPI-protected `ai-secrets.json`; monitoring history remains. The
packaged uninstaller offers the same settings choice.

## Building locally

Run from the repository root:

```powershell
.\scripts\build-all-installers.ps1 -Version 1.5.0                  # x64 + x86 with a summary
.\scripts\build-all-installers.ps1 -Variant x64 -Version 1.5.0     # selected architecture
.\scripts\build-all-installers.ps1 -Version 1.5.0 -WhatIf          # print the plan only
.\scripts\build-all-installers.ps1 -Version 1.5.0 -Commit          # build, stage, and commit
.\scripts\build-all-installers.ps1 -Push                           # increment patch, build, commit, push, prompt
.\scripts\build-all-installers.ps1 -Push -ReleaseMode Draft        # increment patch, unattended override
.\scripts\build-all-installers.ps1 -Version 2.0.0 -Push            # explicitly override the next version

# Lower-level installer builder:
.\installer\build-installer.ps1 -Version 1.2.0             # x64 only (default)
.\installer\build-installer.ps1 -All -Version 1.2.0        # both architectures
.\installer\build-installer.ps1 -All -Version 1.2.0 -Push  # build, commit + push, publish a GitHub release as latest
```

Every non-`WhatIf` canonical run builds the full solution first and runs the full test suite
second. A failure stops the workflow before commits, publishing, or installer compilation.

Build output lands in `installer/Output/`, which is git-ignored. Requires
[Inno Setup 6](https://jrsoftware.org/isinfo.php) and the .NET SDK; `-Push` also requires the
authenticated GitHub CLI (`gh`). After pushing, the root build-all script asks whether the release
should remain a **draft** for review or be **published officially** as the latest release. When
`-Version` is omitted, `-Push` increments the patch number of the newest stable local tag, origin
tag, or GitHub release, including drafts. `-Commit` and `-Push` preserve already-staged changes as
the first commit, then attempt focused whole-file commits by functional area. The script does not
split file hunks automatically and keeps possible renames or other ambiguous changes together.
