<h1 align="center">
  <img src="assets/app-icon.png" alt="Disk Activity Monitor icon" width="96" align="middle">
  &nbsp;Disk Activity Monitor
</h1>

A lightweight Windows tool that tracks how much data is **written to and read from your
drives over time**, so you can spot processes that hammer an SSD and protect its limited
write endurance. It focuses on **aggregate trends** (MB/GB per hour, day and week) rather
than noisy instantaneous values.

---

## Why this exists

Consumer SSDs are rated for a finite **TBW** (terabytes written). A misbehaving app that
writes continuously can chew through that budget far faster than normal use. This tool makes
that visible: it answers *"how much am I writing per day, and what is responsible?"* and
warns you when a drive or process crosses a threshold you set.

---

## Features

<table>
  <tr>
    <td width="33%" align="center">
      <h3>📊 Disk Trends</h3>
      Track physical reads and writes per drive across 24 hours, 30 days, or 12 weeks.
    </td>
    <td width="33%" align="center">
      <h3>🔎 Process Attribution</h3>
      Find the applications generating write pressure with ETW-based logical file I/O tracking.
    </td>
    <td width="33%" align="center">
      <h3>💾 SSD Endurance</h3>
      Combine rated TBW, lifetime writes, SMART wear, and current activity into a lifespan projection.
    </td>
  </tr>
  <tr>
    <td width="33%" align="center">
      <h3>🔔 Proactive Alerts</h3>
      Detect heavy writes and repeated Windows Disk event 11 controller errors with tunable thresholds.
    </td>
    <td width="33%" align="center">
      <h3>🩺 Live SMART Diagnostics</h3>
      Run read-only health scans with Windows reliability data and direct NVMe telemetry when available.
    </td>
    <td width="33%" align="center">
      <h3>⏸️ Safe Auto-Suspend</h3>
      Confirm or automatically freeze runaway writers, then resume only the exact recorded process.
    </td>
  </tr>
  <tr>
    <td width="33%" align="center">
      <h3>🤖 Private TBW Lookup</h3>
      Search endurance evidence with Serper; verify locally with Foundry or use deterministic parsing.
    </td>
    <td width="33%" align="center">
      <h3>🗂️ Alert History</h3>
      Review, dismiss, restore, and snooze retained alerts while the tray icon reflects current severity.
    </td>
    <td width="33%" align="center">
      <h3>🔐 Local and Self-Contained</h3>
      Keep monitoring data on-device in SQLite and protect per-user API keys with Windows DPAPI.
    </td>
  </tr>
</table>

---

## Screenshots

<p align="center">
  <img src="assets/dashboard-overview.png" alt="Full Disk Activity Monitor dashboard showing drive totals, SSD endurance, trends, process writes, and alerts" width="940">
</p>

<p align="center"><em>Full dashboard with per-drive totals, endurance telemetry, trends, process attribution, and active alerts.</em></p>

---

## Download

Self-contained installers (no .NET runtime required on the target machine) are published as
[GitHub Release assets](https://github.com/andrewtheart/DiskActivityMonitor/releases):

| Architecture | Installer |
|---|---|
| **x64** (64-bit Intel/AMD) | Download `DiskActivityMonitor-Setup-<version>-x64.exe` from the [latest release](https://github.com/andrewtheart/DiskActivityMonitor/releases/latest). |
| **x86** (32-bit) | Download `DiskActivityMonitor-Setup-<version>-x86.exe` from the [latest release](https://github.com/andrewtheart/DiskActivityMonitor/releases/latest). |

The installer registers a Windows Service (auto-start), adds a Start Menu shortcut, and
optionally creates a startup entry so the tray dashboard launches at sign-in.
Service binaries are pinned to the expected Program Files directory. Setup rejects reparse
points and applies protected ownership and ACLs through no-follow directory handles.

---

## Requirements

### Installed/self-contained release

- Windows 10 or Windows 11.
- No separate .NET runtime is required.

### Development/build machine

- .NET 10 SDK.
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) to build installers.
- Authenticated GitHub CLI (`gh`) only when using installer `-Push` release automation.
- Foundry Local plus a suitable local model only for the optional local-verification lookup mode. When Foundry
  Local is missing, the lookup modal can install Microsoft's official `Microsoft.FoundryLocal`
  package through Windows Package Manager (`winget`) and then continue setup in-app. The degraded
  Serper-only mode does not install, start, or require Foundry Local.

---

## Quick start (development, no admin)

```powershell
# From the repo root — builds, then launches the collector + dashboard:
.\run.ps1

# Or force a rebuild first:
.\run.ps1 -Build

# Or use the scripts\run-dev.ps1 helper that launches both in separate windows:
.\scripts\run-dev.ps1
```

This builds everything, starts the collector in one window, and opens the dashboard. Give the
collector a couple of minutes so the per-minute buckets accumulate; charts fill in as history
grows.

## Install as a Windows Service (recommended)

Run an **elevated** PowerShell:

```powershell
.\scripts\install.ps1
```

This:
1. Publishes staging output to `.\publish\`.
2. Copies the runnable service and tray files to `%ProgramFiles%\Disk Activity Monitor Dev\`, where standard users have read/execute access only.
3. Installs and starts the **DiskActivityMonitor** Windows service (auto-start at boot).
4. Adds a Startup shortcut so the tray app launches at logon.
5. Launches the tray app immediately.

To remove it (elevated):

```powershell
.\scripts\uninstall.ps1
```

Collected history and settings are left in `%ProgramData%\DiskActivityMonitor\`; delete that
folder if you also want to discard the data. Per-user preferences and DPAPI-protected lookup
keys remain under `%LOCALAPPDATA%\DiskActivityMonitor\`.

---

## Using the dashboard

- **Disk selector** (top right): SSDs are listed first and tagged. Trends and endurance apply
  to the selected disk.
- **Summary cards**: written today, last 24h, last 7 days, and an endurance/projection card.
- **Rated-TBW pill**: right-click the **TBW rated/estimated** badge in SSD endurance and choose
  **Look up rated TBW**. This forces a fresh lookup even if the drive already has a configured
  rating or cached result. A dedicated modal shows Serper search progress, evidence analysis,
  empty/error states, source agreement, and explicit **Apply single** / **Apply range** actions.
  Choose **Foundry Local verification** for local-model analysis or **Serper-only evidence parsing**
  for a degraded deterministic path that accepts only explicit, capacity-matched snippet values.
  Nothing changes until an Apply action is selected.
  If Foundry Local is absent, choose **Install Foundry Local** in the modal. The app installs the
  exact Microsoft package through `winget`, verifies the CLI, and then offers the separate local
  model download when needed. Windows may request installation approval.
  Google's Custom Search JSON API is closed to new customers; its 100 free daily queries apply
  only to existing customers until the service is retired. New setups should select the supported
  **Serper** backend in Settings.
- **Trend chart**: toggle **24h / 30d / 12w**. Bars are bytes written to the device in each
  bucket; the current bucket is highlighted.
- **Top application write requests**: logical file-write bytes requested by each process over
  the selected window. This identifies software generating write pressure, but is not a physical
  SSD-wear figure. Service-host processes are labeled with the hosted service when known (for
  example, `svchost (SDRSVC: Windows Backup)`).
- **Alert center**: a rolling one-hour view of non-dismissed alerts. Repeated controller errors
  appear here alongside write/endurance alerts and can also raise desktop notifications. Click the
  **x** on a row (or use its context menu) to dismiss that occurrence permanently from the main
  view. **All alerts** opens the complete retained history, includes dismissed records, and lets
  you restore them. A future occurrence of the same condition can still appear. The tray icon
  color resets to green when no non-dismissed alerts remain in the window.
- **Live SMART scan**: right-click a controller-error alert and choose **Run live SMART scan**.
  The result panel shows Windows health/operational status, SMART access, alert-window controller
  errors, temperature, model/firmware/serial, lifetime I/O, and actionable findings. The scan is
  read-only and does not start a destructive or long-running ATA/NVMe self-test.
- **Settings**: thresholds (in GB), sample interval, desktop notifications, and the selected
  drive's explicit **Single TBW** or **TBW Range**. Unknown drives use a clearly labeled
  **150 to 600 TBW estimate** until a per-drive rating is saved. Click **Save settings**; the
  collector applies changes live.
- **Online lookup setup**: the startup prompt explains what is sent online, links to Serper signup,
  accepts the API key, and stores it with Windows DPAPI. Choosing **Not now** shows the prompt again
  at the next startup unless **Don't show this again** is checked. Guided setup can be reopened from
  Settings at any time.

The tray icon's right-click menu offers *Open dashboard*,
*Open data folder*, and *Exit*. Closing the dashboard window hides it to the tray; use *Exit*
to quit the app.

### Auto-suspending heavy writers

The **Auto-suspend rules** card lets you stop a runaway writer before it wears the disk:

- **Add a rule** for a process already seen by the monitor (pick it from the dropdown) to match
  all processes with that executable image name, or **Browse for an `.exe`** to bind the rule
  to that exact executable path.
- Set a **limit** in GB written per rolling hour. When a process exceeds it, the app reacts.
- Each rule is either **confirm** (a toast asks before suspending - the default for every new
  rule) or **Auto** (suspend immediately, then notify with a *Resume* button).
- Suspended processes appear under **Currently suspended** with a **Resume** button, and the
  auto-suspend toasts carry *Suspend now* / *Resume* actions. Resume validates the recorded
  process ID, creation time, and executable path before acting.

Name-wide rules always require confirmation; automatic suspension is available only to rules
bound to an exact executable path. If exact resume identity is unavailable, the app leaves the
suspended record in place instead of guessing by process name.

Suspending freezes every thread in the target. The dashboard runs in your user session, so it
can suspend your own processes; suspending an elevated/other-user process requires the app to
run elevated and otherwise reports “access denied”.

---

## Command-line interface (`dam`)

A full CLI (`dam.exe`) reads and writes the same database and `config.json` as the service and
tray, so you can inspect activity, manage alerts/snoozes, and change settings from a terminal.

```powershell
# From the repo root (builds on first run):
.\dam.ps1 status
.\dam.ps1 top --minutes 30 --count 15

# Or run the built exe directly:
src\DiskActivityMonitor.Cli\bin\Debug\net10.0-windows\dam.exe status
```

| Command | What it does |
|---------|--------------|
| `status` | Service/DB status, disk count, non-dismissed alerts, snoozes |
| `disks` | List monitored disks (media, size, SMART wear) |
| `summary [--disk ID] [--all]` | Writes today / last 24h / last 7d |
| `top [--minutes N] [--count N]` | Top processes by writes in a window (default 60m, 10) |
| `process <name>` | Writes for a process across 1m/5m/15m/30m/1h/24h |
| `trends [--range hour\|day\|week] [--count N] [--disk ID]` | Write trend with bar chart |
| `endurance [--disk ID] [--all]` | SSD TBW usage, SMART wear, projection |
| `watch [--interval N]` | Live auto-refreshing dashboard (Ctrl+C to exit) |
| `alerts [--all] [--count N] [--full]` | List alerts (unacknowledged by default) |
| `ack <id>... ` / `ack --all` | Acknowledge alerts |
| `snooze list` | Show active snoozes |
| `snooze process <name> <dur>` | Snooze a process (e.g. `30m`, `1h`, `1d`, `1w`) |
| `snooze all <dur>` | Snooze every alert for a duration |
| `snooze clear <name>\|--global\|--all` | Clear snoozes |
| `config get [key]` | Print all config, or one key |
| `config set <key> <value>` | Change a threshold/setting (the service reloads live) |
| `help` / `version` | Usage / version |

---

## How it works

Disk Activity Monitor has two cooperating components:

| Component | What it does |
|-----------|--------------|
| **Collector service** (`DiskActivityMonitor.Service`) | A background worker that samples Windows performance counters, aggregates them into one-minute buckets, stores them in SQLite, and raises alerts. It runs as a Windows Service or console app. |
| **Tray app** (`DiskActivityMonitor.Tray`) | A WPF system-tray dashboard that visualizes trends, ranks noisy processes, shows alerts, and manages thresholds. |

Both share a database under `%ProgramData%\DiskActivityMonitor\`.

```mermaid
flowchart LR
    PC["PhysicalDisk\nperf counters"] --> COL
    PIO["ETW kernel\nFileIO writes"] --> COL
    WMI["MSFT_PhysicalDisk\n(SSD/HDD)"] --> COL
    EVT["Windows System log\nDisk event 11"] --> COL
    COL["Collector service\n(1-min aggregation)"] --> DB[("SQLite\n%ProgramData%")]
    COL --> AL["Alert engine"]
    AL --> DB
    DB --> TRAY["Tray dashboard"]
    CFG["config.json"] <--> COL
    CFG <--> TRAY
```

- **Disk volume** is read from the `PhysicalDisk\Disk Read/Write Bytes/sec` counters and
  integrated over each sampling interval. These reflect bytes that actually reached the
  device, which is what matters for SSD wear.
- **Per-process** numbers come from the Windows kernel ETW `FileIO` provider: the byte size of
  every logical file read/write request is attributed to the process that issued it, aggregated
  by process/service name. `svchost` PIDs are resolved to their hosted Windows service(s) when
  known. Because only genuine file-system writes raise these events, named-pipe, device-ioctl
  and other non-disk I/O are excluded — so a process stuck in an IPC/UI loop no longer shows up
  as a heavy disk writer. The ETW session needs elevation; the collector runs as LocalSystem.
  When ETW is unavailable (e.g. run un-elevated during development) it falls back to cumulative
  `GetProcessIoCounters` deltas, which are an *upper bound* that mixes file, pipe and device I/O.
- Samples are aggregated into **one-minute buckets** and rolled up to hour/day/week for
  charting in local time.
- Once per minute, the collector reads System log provider `disk`, event ID `11`, counts records
  by `\Device\HarddiskN` across the configured window, and maps that disk number to the currently
  detected volume/model. USB/removable disk numbers can change after reconnecting hardware, so
  each alert retains the original Windows device path and describes the volume mapping as current.

### Accuracy notes

- Per-process numbers are *logical file-write requests*: bytes an application asked Windows to
  write above the cache/storage stack. Windows may cache, coalesce, overwrite or eliminate some
  requests before they become physical I/O, and the requests may target several disks. Therefore
  these numbers identify the software creating pressure but can be higher than actual device
  writes. The per-disk physical totals remain authoritative for SSD endurance. (Under the
  un-elevated fallback reader, per-process numbers over-count further because they also include
  pipe/device I/O.)
- TBW projections assume your recent observed average write rate continues; they are estimates,
  not guarantees. Consumed endurance uses drive-reported lifetime writes when available and falls
  back to writes observed since monitoring started when lifetime telemetry is unavailable.
- Reading some processes owned by other accounts requires the collector to run with
  sufficient privileges (the Windows Service runs as LocalSystem and sees everything).
- Many USB enclosures do not pass SMART telemetry through to Windows. In that case the live scan
  explicitly reports **Limited** rather than treating `Healthy/Online` as proof that the disk,
  cable, port, power supply, or enclosure is fault-free.

---

## Configuration

Machine-wide collector settings live at `%ProgramData%\DiskActivityMonitor\config.json` and can
be edited from the dashboard or by hand (the collector watches the file). Invalid or oversized
replacements are ignored in favor of the last known-good settings. Thresholds are in GB.

| Key | Meaning | Default |
|-----|---------|---------|
| `sampleIntervalSeconds` | Counter sampling cadence | 5 |
| `dashboardRefreshSeconds` | How often the dashboard re-reads the DB and redraws tables, graphs and stats | 15 |
| `retentionDays` | Minute-level history kept before pruning | 365 |
| `processMinMbPerMinute` | Ignore processes writing less than this per minute | 0.5 |
| `ssdWarnGbPerHour` | Warn above this many GB written to an SSD in 1h | 10 |
| `ssdWarnGbPerDay` / `ssdCriticalGbPerDay` | 24h warn / critical thresholds | 100 / 250 |
| `processWarnGbPerHour` | Warn when one process writes this much in 1h | 5 |
| `allProcessesWarnGbPerHour` | Warn when all processes combined write this much in 1h | 20 |
| `alertCooldownMinutes` | Minimum gap between repeats of the same alert | 5 |
| `enableControllerErrorAlerts` | Monitor System log Disk event 11 controller errors | true |
| `controllerErrorWindowDays` | Trailing event-count window | 14 |
| `controllerErrorWarnCount` | Warning threshold inside the event-count window | 3 |
| `controllerErrorCriticalCount` | Critical threshold inside the event-count window | 10 |
| `defaultSsdTbw` / `defaultSsdTbwUpper` | Estimated SSD TBW range when the rating is unknown | 150 / 600 |
| `diskTbwRatings` | Per-disk TBW endurance rating (TB) | none |
| `diskTbwRatingsUpper` | Optional per-disk upper TBW; when set, endurance %/projection are shown as a range | none |
| `tbwProjectionWarnYears` / `tbwProjectionCriticalYears` | Projected-years warning / critical thresholds | 2 / 1 |
| `ssdWearWarnPercent` | SMART wear warning threshold | 90 |

Action-bearing preferences are isolated per Windows user in
`%LOCALAPPDATA%\DiskActivityMonitor\user-settings.json`:

| Key | Meaning | Default |
|-----|---------|---------|
| `enableNotifications` | Show desktop notifications | true |
| `enableTbwWebLookup` | Enable online TBW evidence search | true |
| `suppressTbwOnlineSetupPrompt` | Suppress guided lookup setup at startup | false |
| `webSearchProvider` | Search backend (`serper` recommended; `google` for existing customers) | serper |
| `tbwLookupMethod` | Evidence analysis (`FoundryLocal` or degraded `SerperOnly`) | FoundryLocal |
| `tbwLookupModel` | Optional Foundry Local model override | automatic |
| `autoSuspendRules` | Per-process confirm/automatic suspension rules | none |

Upgrades copy legacy notification and online-lookup preferences into the current user's file,
but do not import legacy machine-wide auto-suspend rules.

See [HELP.md](HELP.md#settings-reference) for the full operational explanation of each setting.

---

## Project layout

```
src/
  DiskActivityMonitor.Core/      Shared models, SQLite repo, sampling, detection, alerts
  DiskActivityMonitor.Service/   Background collector (Windows Service / console)
  DiskActivityMonitor.Tray/      WPF system-tray dashboard
  DiskActivityMonitor.Cli/       Command-line client (dam.exe)
tests/
  DiskActivityMonitor.Tests/     xUnit + real STA WPF tests for Core, service, tray, and workflows
installer/
  build-installer.ps1            Publish + compile the Inno Setup installer
  DiskActivityMonitor.iss        Inno Setup script
scripts/
  build-all-installers.ps1       Build x64/x86, optionally commit/push/release
  install.ps1 / uninstall.ps1    Service install/removal (elevated)
  publish-to-azure.ps1           Build/upload installers + portable ZIP and update site card
  run-dev.ps1                    Run both from source (separate windows)
  make-icon.ps1                  Regenerate assets/app.ico from code
HELP.md                          Comprehensive user, CLI, troubleshooting, and release guide
run.ps1                          Build-if-needed + launch service & tray
dam.ps1                          CLI convenience wrapper
```

## Documentation and security

- For usage and troubleshooting, see the comprehensive [HELP.md](HELP.md) guide. The tray app
  also compiles it to HTML and displays it from the top **?** button.
- For the threat model, implemented controls, validation evidence, known limitations, and
  roadmap, see [docs/security/security.md](docs/security/security.md).

## Building

```powershell
# Build the full solution
dotnet build .\DiskActivityMonitor.slnx -c Release

# Run the full test suite
dotnet test .\tests\DiskActivityMonitor.Tests\DiskActivityMonitor.Tests.csproj -c Release

# Build both self-contained installer architectures with one version
.\scripts\build-all-installers.ps1 -Version 1.5.0

# Preview the build / commit / push / release plan without changing files
.\scripts\build-all-installers.ps1 -Version 1.5.0 -Push -WhatIf

# Build, commit, push, then choose Draft or Published release mode
.\scripts\build-all-installers.ps1 -Version 1.5.0 -Push
```

Output lands in `installer\Output\` as architecture-suffixed executables. See
[installer/README.md](installer/README.md) and [HELP.md](HELP.md#developer-and-release-workflows)
for selected-variant, unattended release-mode, and Azure publication examples.
