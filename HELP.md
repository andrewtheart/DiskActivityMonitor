# Disk Activity Monitor Help Guide

This guide covers installation, everyday use, alerts, drive-health diagnostics, rated-TBW lookup, the command-line interface, maintenance, troubleshooting, and release builds.

In the tray dashboard, select the top **?** button to open this guide in a modal. The build converts this Markdown source into a styled, navigable `HELP.html`; external links open in the default browser, and `Esc` closes the modal.

For architecture and contributor-oriented details, see [README.md](README.md). For installer-specific notes, see [installer/README.md](installer/README.md).

## Contents

- [What Disk Activity Monitor measures](#what-disk-activity-monitor-measures)
- [Install and start](#install-and-start)
- [Dashboard tour](#dashboard-tour)
- [SSD endurance and Drive Life Used](#ssd-endurance-and-drive-life-used)
- [Rated-TBW lookup](#rated-tbw-lookup)
- [Alerts, dismissal, and snoozing](#alerts-dismissal-and-snoozing)
- [Live SMART scan](#live-smart-scan)
- [Top application write requests](#top-application-write-requests)
- [Auto-suspend rules](#auto-suspend-rules)
- [Settings reference](#settings-reference)
- [Command-line guide](#command-line-guide)
- [Files, privacy, and backup](#files-privacy-and-backup)
- [Troubleshooting](#troubleshooting)
- [Uninstall and reset](#uninstall-and-reset)
- [Developer and release workflows](#developer-and-release-workflows)

---

## What Disk Activity Monitor measures

Disk Activity Monitor combines several Windows data sources:

- **Physical-disk performance counters** provide actual read/write traffic reaching each disk.
- **Kernel ETW FileIO events** attribute logical file-I/O requests to processes.
- **Windows storage/WMI data** identifies disks, media type, volume mappings, health, and reliability information.
- **SMART/NVMe telemetry** provides lifetime writes, wear, temperature, spare capacity, media errors, unsafe shutdowns, and related fields when supported.
- **Windows System log Disk event 11** reveals recurring controller-path errors that can indicate cable, port, power, enclosure, or controller instability.

Two numbers that look similar have different meanings:

1. **Per-disk writes** are physical-device traffic and are the authoritative input for SSD endurance tracking.
2. **Per-process writes** are logical file-write requests made by software. Caching, coalescing, overwritten data, and multi-disk activity can make these totals differ from physical disk writes.

The collector aggregates samples into one-minute SQLite buckets. The dashboard then rolls those buckets into hourly, daily, and weekly views.

---

## Install and start

### Recommended: self-contained installer

Download the appropriate installer from the [latest GitHub release](https://github.com/andrewtheart/DiskActivityMonitor/releases/latest):

- `DiskActivityMonitor-Setup-<version>-x64.exe` for normal 64-bit Windows PCs.
- `DiskActivityMonitor-Setup-<version>-x86.exe` for 32-bit Windows.

The installer is self-contained; the target computer does not need a separately installed .NET runtime. It installs:

- The `DiskActivityMonitor` Windows service, running as LocalSystem and starting automatically.
- The tray dashboard.
- Start Menu and optional sign-in startup integration.

### Reinstalling or upgrading

Setup stops the running tray, stops and unregisters the existing service, waits until Windows has
really removed the registration, replaces the files, and registers the service again. If the
installation is aborted after the service was unregistered, setup restores the registration from
whatever service binary is still on disk.

You are asked two questions when previous data is present. Both offer the same two buttons:
**Delete existing data** on the left and **Keep existing data** on the right, with keeping
focused as the default.

1. **Settings** - keep your existing thresholds, preferences, and API credentials, or start from
   defaults.
2. **Database** - keep your monitoring history or start a new, empty database. The prompt shows the
   database path, its size, when collection started, and when it was last updated. If you choose to
   delete, the existing `diskactivity.db` and its `-wal`/`-shm` journal files are renamed to
   `diskactivity-replaced-<timestamp>.db` in the same folder, so nothing is actually erased and you
   can remove or re-inspect the archive yourself.

Both prompts are skipped when setup runs with `/SUPPRESSMSGBOXES`, in which case the defaults
(keep settings, keep database) apply.

### Development launch

From the repository root:

```powershell
.\run.ps1
```

Force a build first:

```powershell
.\run.ps1 -Build
```

Or start service and tray development processes separately:

```powershell
.\scripts\run-dev.ps1
```

Running the collector without elevation is supported for development, but ETW process attribution and some SMART/reliability data may be unavailable. The collector then falls back to broader Win32 process I/O counters.

### Scripted service installation

From an elevated PowerShell prompt in the repository root:

```powershell
.\scripts\install.ps1
```

This publishes staging files under the repository's `publish` directory, copies runnable files to the ACL-protected `%ProgramFiles%\Disk Activity Monitor Dev` directory, recreates the service from that protected path, starts it, adds a Startup shortcut, and opens the dashboard.

---

## Dashboard tour

### Header

- **Disk selector:** chooses the physical disk displayed by summary, endurance, trends, and throughput sections. SSDs are listed first.
- **Refresh:** re-reads current data immediately.
- **Settings:** switches between the dashboard and editable configuration.

### Summary cards

The cards show:

- Writes today.
- Writes in the trailing 24 hours.
- Writes in the trailing seven days.
- Projected time to the configured TBW rating at the recent write rate.

### SSD endurance panel

This panel displays:

- Rated TBW or the configured rating range.
- Drive Life Used.
- Lifetime bytes written/read when the drive exposes them.
- Projected lifespan.
- Recent average writes per hour, day, and week.

### Trend chart

Choose:

- **24h:** hourly buckets.
- **30d:** daily buckets.
- **12w:** weekly buckets.

The current bucket is highlighted.

### Throughput

The throughput card shows average, median, and busiest-minute MB/s for 1 hour, 24 hours, 7 days, or 30 days.

Average and median use only minutes when the collector was actually monitoring. A monitored minute
with no disk activity counts as zero; a minute missed because the service or computer was stopped
does not. The card shows monitoring coverage for the selected period. When coverage reaches the
configured high-coverage threshold, it also shows a calendar-time average. Below that threshold,
the calendar average is withheld rather than treating missing time as idle.

### Processes and Alert center

- **Top application write requests** ranks process-level logical writes for the selected time range.
- **Alert center** shows non-dismissed alerts from the last hour.
- **Search alerts** filters visible Alert center rows by title or message; matching is case-insensitive.
- **All alerts** opens complete retained alert history, including dismissed records.

### Tray behavior

Closing the dashboard hides it; collection continues. Use the tray icon to reopen it. The tray menu includes:

- Open dashboard.
- Open data folder.
- Exit.

Exiting the tray does not stop the Windows service.

---

## SSD endurance and Drive Life Used

### Rated TBW

TBW means **terabytes written**, the manufacturer's write-endurance rating. Configure a rating for each SSD whenever possible. When the rating is unknown, the dashboard uses and labels a conservative **150 to 600 TBW estimate**.

A rating can be:

- A single value, such as `600 TBW`.
- A lower/upper range when the exact model or capacity is uncertain.

Settings provides an explicit **Single TBW** / **TBW Range** choice. Range mode requires a maximum greater than the minimum. Dashboard ranges are always shown from low to high, for example `Drive Life Used: 3% to 10%`.

### Drive Life Used precision

When lifetime-write telemetry and a TBW rating are both available, Drive Life Used is calculated as:

$$
\text{Drive Life Used} = \frac{\text{Lifetime Bytes Written}}{\text{Rated TBW in Bytes}} \times 100
$$

The dashboard shows up to two decimal places. For example, it may show `~10.38%`.

Many drives expose their native SMART wear field only as a whole integer. In that case the precise lifetime-writes/TBW calculation is the headline and the whole-percent SMART value is shown as supporting context. If lifetime writes are unavailable, the dashboard falls back to the drive's SMART whole-percent wear value.

### Projection caveats

The projected years-to-TBW value uses the recent average per monitored time and is shown only when
recent monitoring coverage reaches the configured high-coverage threshold. This prevents a long
service shutdown from making drive life look artificially long. It is not a failure prediction or
warranty calculation. Workload changes can materially change the projection.

---

## Rated-TBW lookup

### Guided setup

If no Serper key is configured, normal tray startup offers optional online lookup setup:

- **Configure:** opens the Serper setup step.
- **Not now:** closes the prompt; it returns on the next startup.
- **Don't show this again:** suppresses the startup prompt.

You can reopen guided setup from Settings at any time.

### Choose an analysis method

Settings offers two distinct choices:

- **Foundry Local verification:** uses an on-device model to extract claims, then validates every claim against the source evidence.
- **Serper-only evidence parsing:** degraded mode that never installs or starts Foundry. It deterministically accepts only TBW values explicitly linked to the requested model capacity in Serper titles/snippets.

Both modes require an explicit **Apply single** or **Apply range** action before changing the drive rating. When multiple source-backed values disagree, applying the full range preserves that uncertainty.

### Install Foundry Local in-app

When the lookup modal reports that Foundry Local is missing, select **Install Foundry Local**.
The app then:

1. Uses Windows Package Manager to install the exact official `Microsoft.FoundryLocal` package
  from the `winget` source.
2. Verifies the installation with `foundry --version`.
3. Checks for a compatible on-device model.
4. Offers the separate model download when one is still required, then resumes the lookup.

Installation begins only after you select the button. Package and source agreements are accepted
for that requested installation, and Windows may request approval. If `winget` is unavailable,
install or update **App Installer** from Microsoft Store and try again. Foundry Local's current
Microsoft prerequisites include Windows 11 version 24H2 or later and DirectX 12-capable GPU
hardware; unsupported systems receive the package-manager error in the modal.

### Privacy and data flow

A Foundry lookup uses two stages:

1. **Serper web search:** sends the drive model and search terms to Serper.
2. **Foundry Local verification:** analyzes returned evidence locally on the computer.

The app rejects candidate values that are not explicitly supported by source text or do not match the requested drive capacity.

Serper-only mode performs the same web search but replaces stage 2 with deterministic parsing. It is clearly labeled as degraded and does not claim local-AI verification.

### API-key storage

Google and Serper API keys are stored per Windows user in:

`%LOCALAPPDATA%\DiskActivityMonitor\ai-secrets.json`

Keys are encrypted using Windows DPAPI with `CurrentUser` scope. A key encrypted for one Windows account cannot be decrypted by another account. Legacy plaintext key fields are migrated to protected fields when loaded.

Environment-variable alternatives are also supported:

- `SERPER_API_KEY`
- `GOOGLE_API_KEY`
- `GOOGLE_CSE_ID`

Serper is the default and recommended provider for new users. Google Custom Search remains available only for existing/grandfathered customers.

### Run a lookup

1. Select the SSD.
2. Right-click the **TBW rated/estimated** pill.
3. Choose **Look up rated TBW**.
4. Review the sources, source agreement, and candidate values.
5. Select **Apply single** for one value or **Apply range** to retain conflicting supported values.

Nothing changes until a candidate is explicitly applied.

---

## Alerts, dismissal, and snoozing

### Alert types

The collector can raise alerts for:

- High SSD writes in one hour.
- High or critical SSD writes in 24 hours.
- High process-level writes in one hour.
- High combined process writes.
- SMART wear reaching the configured threshold.
- Projected time to TBW falling below warning or critical thresholds.
- Repeated Windows Disk event 11 controller errors.

The cooldown prevents the same rule and scope from creating alerts continuously.

### Alert center

The main Alert center shows one grouped row per rule for non-dismissed occurrences in the last hour. Repeats are summarized with a count.

### Dismiss an alert

Use either:

- The **x** button on a row.
- Right-click and choose **Dismiss from Alert center**.

Because a row can represent several repeated occurrences, dismissing the row dismisses every occurrence represented by that row. Dismissal persists across restarts.

Dismissal does **not** disable the alert rule. A future occurrence can appear again.

### View or restore dismissed alerts

1. Select **All alerts** at the top-right of the Alert center.
2. Find a record marked **Dismissed**.
3. Select **Restore** to make that occurrence eligible for the main Alert center again (if it is still inside the one-hour window).

Dismissed records remain stored until normal retention pruning removes them.

### Snooze versus dismiss

- **Dismiss** hides recorded alert occurrences.
- **Snooze process** suppresses new alerts for one process until a chosen time.
- **Snooze all** suppresses all new alert evaluation until a chosen time.

Use dismissal when you have reviewed a notification. Use snooze when you expect a known workload and temporarily do not want new alerts.

### Tray icon

The tray icon reflects the worst non-dismissed alert in the recent one-hour window:

- Green: no visible warning/critical alert.
- Amber: warning.
- Red: critical.

---

## Live SMART scan

Controller-error alerts have a context action named **Run live SMART scan**.

The scan is read-only. It combines available evidence from:

- `MSFT_PhysicalDisk` health and operational status.
- `MSFT_StorageReliabilityCounter`.
- `Win32_DiskDrive` identity/status.
- Direct NVMe SMART/Health log page `0x02` when available.
- The controller-error count represented by the alert.

Possible result grades include healthy, attention, critical, limited, and unavailable.

### USB, RAID, and virtual-disk limitations

Many USB bridges, RAID controllers, virtual disks, and some storage drivers do not pass SMART commands through. **Limited** means telemetry could not be read; it does not prove the disk or connection is healthy.

For recurring controller errors:

1. Back up important data.
2. Try a different cable and port.
3. Avoid unpowered hubs.
4. Check enclosure power and cooling.
5. Compare behavior through a direct SATA/NVMe connection when possible.
6. Inspect Windows System events for recurrence.

---

## Top application write requests

The process table answers which software is requesting file writes. It does not assign every physical disk write to a process.

Selectable windows range from the last minute through the past year. `svchost` entries include hosted service names when Windows exposes them.

For accurate ETW-based attribution, run the collector as the installed LocalSystem service or run development collection elevated. If ETW startup fails, the collector logs a warning and uses cumulative Win32 process I/O counters, which also include pipe/device I/O and therefore over-count disk-related activity.

### Why so much is attributed to "System"

`System` is the Windows kernel itself (PID 4), not an application. Its writes are issued by kernel
components on behalf of the whole machine:

- the **cache manager** flushing pages that other applications dirtied,
- **NTFS metadata and journal** updates (`$Mft`, `$LogFile`, `$Bitmap`, `$UsnJrnl`),
- the **memory manager** writing `pagefile.sys`,
- kernel-mode **drivers** and filter drivers.

So the process name cannot identify the work - the target file can.

### Which files a process wrote

Hover over a process bar or click any row in **Top application write requests** to open the per-file breakdown for that
process. Each file shows its size share, a classification (NTFS metadata, paging file, registry
hive, virtual disk, search index, Defender, event log, shadow copy, temporary, log, database,
network path) and one line explaining what causes that kind of write. Opening it for `System`
also shows the kernel explanation above.

After closing a hover-opened breakdown with Enter, Escape, or Close, it stays closed until the
pointer leaves that bar. This prevents a stationary pointer from immediately reopening it.

The same data is available from the CLI:

```powershell
dam files System --minutes 60 --count 15
```

Per-file attribution requires the ETW collector and is controlled in Settings:

| Setting | Default | Meaning |
|---|---|---|
| Record which files each process writes | on | Master switch for per-file attribution. |
| Files kept per process / minute | 15 | Busiest files stored for each process each minute. |
| Minimum per file (KB/min) | 256 | Files below this in a minute are not stored. |
| File history (days) | 30 | Per-file rows are far more numerous, so they expire sooner than the process rollup. Configurable from 1 to 365 days. |
| Files tracked in memory | 20000 | Upper bound on distinct files tracked between samples. |

Because of the size floor, the listed files usually account for most - not all - of a process's
logical writes; the modal states the exact share.

### Analytics in the per-file breakdown

The **Analytics** button in the breakdown header toggles a chart band above the file list:

- **Share of writes by process** - a donut showing every process that wrote during the selected
  window, with the process you opened pulled out and highlighted. The centre shows the total, so
  you can see immediately whether this process is the main writer or a minor one.
- **Write volume over time** - the process's own writes bucketed across the window (at most 48
  intervals). A single tall bar is a burst; an even row is sustained background writing. The
  busiest interval is highlighted.
- **Bytes written by file type** - the tracked files grouped by extension. This answers "is this
  logs, databases, or temporary files?" without reading every path.

### Row actions

Each file row carries four actions on the right and the same copy command on its right-click menu:

| Icon | Action | Notes |
|---|---|---|
| Copy | Copy full path | Copies the exact path to the clipboard. Also available as **Copy full path** on right-click. |
| Eye | Live tail | Streams new lines as they are written. Disabled for binary extensions. |
| Lock | Find open handles | Lists which processes currently hold that file open. |
| Bin | Delete | Asks for confirmation, then reports exactly why a delete failed. |

**Live tail** opens the file with shared read/write/delete access, so it never blocks the process
that is writing. It seeds the view with the last lines, then polls once a second. *Follow* keeps
the newest line in view and *Pause* freezes the display without stopping the writer. If the file
is truncated or rotated, the view restarts and says so. Files whose extension is on the binary
list are not opened as text; the viewer explains why instead.

Tail safety is based on bounded work, not total file size. The reader seeks directly to the end and
decodes only the configured trailing window, so a multi-gigabyte log does not require reading the
whole file. Controlled sparse-file tests from 1 MiB through 8 GiB showed effectively constant read
time at a 512 KiB window (about 1-5 ms after warm-up, roughly 3 MiB temporary managed allocation).
A 4 MiB window allocated about 24 MiB per read, so 512 KiB remains the conservative default. The
display has a separate 1 MiB decoded-text ceiling for minified files with extremely long lines.

**Delete** never uses the Recycle Bin. When it fails, the reason is classified rather than shown
as a raw error:

| Result | Meaning |
|---|---|
| Read-only | The file carries the read-only attribute. Clear it and retry. |
| Access denied | Permission, ownership, or elevation problem. |
| In use by another process | Another process holds a conflicting handle. |

For the last two, a **Find what is locking it** button runs Sysinternals Handle and names the
holding processes with their PIDs. Without administrator rights, handles owned by other accounts
are invisible, and the app says so rather than reporting a misleading "nothing found".

### Sysinternals Handle

**Trace open handles** in the breakdown footer lists every file, key, and object the process
currently has open. Both this and the per-file lock lookup need `handle.exe`, which the app finds
in this order:

1. Beside the monitoring database in `%ProgramData%\DiskActivityMonitor`.
2. Any directory on `PATH`.

If it is not present, the app explains what it is and offers to download it. The download comes
only from the official Sysinternals HTTPS endpoint
(`https://download.sysinternals.com/files/Handle.zip`); redirects to any other host are rejected,
only the Handle executables and EULA are extracted, and the result is stored next to the database.
Nothing is installed system-wide and no elevation is required.

Handle enumerates handles owned by other users only when Disk Activity Monitor itself runs
elevated. Every trace states whether it ran elevated so an empty result is never misread.

### Live tail settings

| Setting | Default | Meaning |
|---|---|---|
| Binary file extensions | Yagu's binary, media, archive, and database defaults | Extensions never opened as text by the eye icon. Semicolon-separated. |
| Lines shown on open | 200 | Trailing lines seeded when the tail opens. |
| Maximum lines buffered | 5000 | Ceiling on retained lines, so a busy log cannot grow without bound. |
| Maximum decoded per poll (KiB) | 512 | Bounds each read regardless of total file size or append burst size. |
| Maximum displayed text (KiB) | 1024 | Bounds retained decoded text even when lines are extremely long. |

**Restore default extensions** puts the shipped list back. Clearing the box entirely keeps the
previous list rather than making every file tailable.

### Database size and compaction

Disk Activity Monitor watches its own database and warns when it grows past a threshold.

| Setting | Default | Meaning |
|---|---|---|
| Warn above (GB) | 1 | Total of the database, its write-ahead log, and index. Set to 0 to disable. |
| Repeat warning after (hours) | 12 | Minimum gap between repeat warnings. |

The warning offers **Compact now**, which checkpoints the write-ahead log and runs a SQLite
`VACUUM`. Deleting expired history only marks pages reusable, so a database that has pruned a lot
of data stays large on disk until it is rebuilt; compaction returns that space. No history is
lost, but the rebuild briefly blocks the collector and can take minutes on a large file.

If the dashboard is minimised, in the tray, or not focused when the threshold is crossed, the
warning also arrives as a Windows notification with a **Compact now** button. Compaction always
runs in the dashboard rather than in the short-lived notification process. You can also compact at
any time from **Settings -> Database size -> Compact database now**.

---

## Auto-suspend rules

Auto-suspend freezes all threads in matching processes after their rolling-hour logical write requests cross a threshold.

### Create a rule

1. Open Settings.
2. Pick a process already seen by the collector for a name-wide rule, or browse to an
  executable to bind the rule to that exact path.
3. Set the GB/hour threshold.
4. Choose behavior:
   - **Confirm:** ask through a toast before suspending.
  - **Auto:** suspend an exact-path rule immediately and notify afterward. Name-wide rules are
    confirmation-only.
5. Save rules.

### Suspend from an alert

An alert about a single heavy-writing process offers a **Suspend `<process>`** button and a
**Suspend for** picker: 5 minutes, 15 minutes, 30 minutes, 1 hour, or until you resume it.
The pre-selected value comes from **Suspend for (minutes)** in Settings and defaults to 30
minutes; set it to `0` to make suspensions last until you resume them. The confirmation toast
raised by an auto-suspend rule offers the same picker.

When the chosen interval elapses, the app resumes the process automatically and posts a
notification. Suspensions chosen as "until I resume it" are never released automatically.

### Resume a process

The dashboard's **Suspended processes** card lists every process this app suspended, whether an
auto-suspend rule or you suspended it. Each row shows the origin, when it was suspended, and
whether it resumes on a schedule or waits for you, with a **Resume** button; **Resume all**
releases them together. The same list appears under Currently suspended in Settings, and
suspension toasts carry a **Resume** action.

### Safety notes

- Rules created from the seen-process list match the executable image name and can affect
  multiple processes with that name. Browsed rules match only the selected executable path.
- Resume actions validate the recorded process ID, creation time, and executable path so a
  recycled process ID or same-name executable is not resumed accidentally.
- If an older or damaged suspended record lacks exact identities, resume fails closed and keeps
  the record instead of selecting a process by name.
- Suspending system, security, storage, or shell processes can destabilize Windows or interrupt data operations.
- The tray can control ordinary processes owned by the current user. Elevated or other-user processes may require an elevated tray instance.
- Prefer Confirm mode until a rule has been validated.

---

## Settings reference

Machine-wide collector settings are stored in `%ProgramData%\DiskActivityMonitor\config.json`. The service watches this file and reloads valid changes. Malformed files and files larger than 1 MiB are ignored, preserving the last known-good settings.

| Setting | Default | Meaning |
|---|---:|---|
| `sampleIntervalSeconds` | `5` | Physical-disk sampling interval. |
| `dashboardRefreshSeconds` | `15` | Dashboard refresh interval. |
| `retentionDays` | `365` | Age at which minute samples and alert history are pruned. |
| `processMinMbPerMinute` | `0.5` | Process noise floor. |
| `ssdWarnGbPerHour` | `10` | SSD one-hour warning threshold. |
| `ssdWarnGbPerDay` | `100` | SSD 24-hour warning threshold. |
| `ssdCriticalGbPerDay` | `250` | SSD 24-hour critical threshold. |
| `processWarnGbPerHour` | `5` | Single-process logical-write threshold. |
| `allProcessesWarnGbPerHour` | `20` | Combined logical-write threshold. |
| `alertCooldownMinutes` | `5` | Minimum repeat interval for one rule/scope. |
| `enableControllerErrorAlerts` | `true` | Monitor System log provider `disk`, event 11. |
| `controllerErrorWindowDays` | `14` | Event 11 aggregation window. |
| `controllerErrorWarnCount` | `3` | Controller warning count. |
| `controllerErrorCriticalCount` | `10` | Controller critical count. |
| `defaultSsdTbw` | `150` | Lower bound of the estimate used when TBW is unknown. |
| `defaultSsdTbwUpper` | `600` | Upper bound of the estimate used when TBW is unknown. |
| `diskTbwRatings` | empty | Per-disk lower/exact TBW values. |
| `diskTbwRatingsUpper` | empty | Per-disk upper TBW values. |
| `tbwProjectionWarnYears` | `2` | Projection warning threshold. |
| `tbwProjectionCriticalYears` | `1` | Projection critical threshold. |
| `highCoveragePercent` | `90` | Minimum monitoring coverage required for calendar-rate and endurance projection claims. |
| `ssdWearWarnPercent` | `90` | SMART wear warning threshold. |
| `trackFileTargets` | `true` | Master switch for per-file attribution. |
| `fileTargetsPerProcessPerMinute` | `15` | Busiest files stored per process per minute. |
| `fileTargetMinKbPerMinute` | `64` | Per-file size floor for a minute to be stored. |
| `fileTargetRetentionDays` | `30` | Age at which per-file rows are pruned (1-365). |
| `fileTargetTrackingLimit` | `20000` | Distinct files tracked in memory between samples. |
| `databaseSizeWarnGb` | `1` | Warn when the database, WAL, and index together exceed this size. `0` disables. |
| `databaseSizeAlertCooldownHours` | `12` | Minimum gap between repeat database-size warnings. |
| `binaryExtensions` | see below | Extensions never opened as text by the live tail. |
| `tailInitialLines` | `200` | Trailing lines seeded when a live tail opens. |
| `tailMaxLines` | `5000` | Maximum lines the live tail retains. |
| `tailMaxReadKb` | `512` | Maximum KiB decoded by one initial or incremental tail read. |
| `tailMaxBufferKb` | `1024` | Approximate maximum KiB of decoded UTF-16 text retained in the viewer. |
| `liveGraphRetentionMinutes` | `15` | Granular physical-disk samples retained for the live graph (1-120). |

`binaryExtensions` is a semicolon-separated list seeded from Yagu's binary, skip, and archive
extension defaults, so executables, media, fonts, documents, databases, dumps, and archives are all
excluded from live tailing. Edit it in **Settings -> Live file tail**, where **Restore default
extensions** returns the shipped list.

Prefer the dashboard or CLI over hand-editing. If hand-editing JSON, keep valid JSON syntax and preserve numeric/boolean types.

Session behavior and network-use preferences are stored per Windows user in `%LOCALAPPDATA%\DiskActivityMonitor\user-settings.json`.

| Setting | Default | Meaning |
|---|---:|---|
| `enableNotifications` | `true` | Show alert desktop notifications for this user. |
| `enableTbwWebLookup` | `true` | Enable rated-TBW web lookup for this user. |
| `suppressTbwOnlineSetupPrompt` | `false` | Suppress this user's guided lookup startup prompt. |
| `webSearchProvider` | `serper` | This user's `serper` or `google` backend. |
| `tbwLookupMethod` | `FoundryLocal` | `FoundryLocal` verification or degraded `SerperOnly` parsing. |
| `tbwLookupModel` | automatic | Optional per-user Foundry Local model override. |
| `autoSuspendRules` | empty | Rules allowed to suspend processes in this user's session. |

On the first launch after upgrading from a machine-wide settings version, notification and online-lookup preferences are copied to the current user's file. Legacy auto-suspend rules are not imported; recreate them deliberately in each user's dashboard.

---

## Command-line guide

Use the repository helper during development:

```powershell
.\dam.ps1 <command> [options]
```

Installed or published builds can call `dam.exe` directly.

### Status and inventory

```powershell
.\dam.ps1 status
.\dam.ps1 disks
.\dam.ps1 summary --all
.\dam.ps1 summary --disk 0
```

### Process activity

```powershell
.\dam.ps1 top --minutes 60 --count 15
.\dam.ps1 process Code
.\dam.ps1 trends --range hour --count 24 --disk 0
```

Quote process names containing spaces.

### Endurance

```powershell
.\dam.ps1 endurance --all
.\dam.ps1 endurance --disk 0
```

### Alerts

```powershell
# Outstanding/non-dismissed alerts
.\dam.ps1 alerts

# Include acknowledged/dismissed alerts
.\dam.ps1 alerts --all --count 100 --full

# Acknowledge/dismiss selected records
.\dam.ps1 ack 120 121

# Acknowledge all records
.\dam.ps1 ack --all
```

The CLI retains the historical term **acknowledge** for the database state used by dashboard dismissal.

### Snooze

```powershell
.\dam.ps1 snooze list
.\dam.ps1 snooze process Code 1h
.\dam.ps1 snooze all 30m
.\dam.ps1 snooze clear Code
.\dam.ps1 snooze clear --global
.\dam.ps1 snooze clear --all
```

Duration examples: `30m`, `1h`, `1d`, `1w`.

### Configuration

```powershell
.\dam.ps1 config get
.\dam.ps1 config get ssdWarnGbPerHour
.\dam.ps1 config set ssdWarnGbPerHour 20
```

Change notification, online lookup, and auto-suspend preferences in the dashboard; they are not machine-wide CLI settings.

### Live terminal dashboard

```powershell
.\dam.ps1 watch --interval 5
```

Press `Ctrl+C` to stop watching.

---

## Files, privacy, and backup

### Machine-wide files

`%ProgramData%\DiskActivityMonitor\`

| File | Purpose |
|---|---|
| `diskactivity.db` | SQLite database containing disks, minute aggregates, alerts, snoozes, and suspended-process state. |
| `diskactivity.db-wal` / `diskactivity.db-shm` | SQLite WAL support files that may exist while processes are active. |
| `config.json` | Machine-wide configuration. |
| `toast-error.log` | Best-effort tray/toast errors, created only when needed. |
| `logs\` | Reserved application log directory. |

### Per-user files

| File | Purpose |
|---|---|
| `%LOCALAPPDATA%\DiskActivityMonitor\user-settings.json` | Per-user notification, lookup, and auto-suspend preferences. |
| `%LOCALAPPDATA%\DiskActivityMonitor\ai-secrets.json` | DPAPI-protected API key values and the non-secret Google CSE identifier. |

The installer pins service binaries to its expected Program Files directory. It opens directories with no-follow handles, rejects reparse points from handle metadata, holds the directory identity while applying protected ownership and ACLs, and grants standard users read/execute access only to service code. Install and uninstall stop only the tray executable at that exact path. Foundry Local requests accept loopback endpoints only and do not follow HTTP redirects or use a system proxy. Credentialed search requests also reject redirects so API-key headers cannot cross origins. The shared `%ProgramData%` database and machine configuration still support tray/CLI writes and are not a tamper-evident boundary between mutually untrusted local accounts: another local user can alter telemetry or deny service, but cannot modify protected service code or read another user's DPAPI secrets. Security-sensitive process-control and network-use preferences therefore live only in the per-user file.

### Backup

For a consistent database copy:

1. Stop the `DiskActivityMonitor` service.
2. Exit the tray app.
3. Copy the entire `%ProgramData%\DiskActivityMonitor` directory.
4. Start the service and tray again.

Also back up the per-user settings and secrets files if those preferences should move with the same Windows account. DPAPI-protected keys generally cannot be moved to a different Windows account; re-enter keys there instead.

---

## Troubleshooting

### Dashboard says no disks are detected

- Confirm the service is running:

  ```powershell
  Get-Service DiskActivityMonitor
  ```

- Open `%ProgramData%\DiskActivityMonitor` and confirm `diskactivity.db` exists.
- Wait through at least one sampling interval.
- Restart the service if storage hardware was added or removed and does not appear after rescanning.

### Charts are empty

One-minute buckets need time to accumulate. Wait several minutes, select the expected disk, and use Refresh. Verify service state with `dam status`.

### Process totals are absent or unexpectedly high

- Install/run the collector as the Windows service for elevated ETW access.
- When ETW is unavailable, fallback counters include non-file handles such as pipes and devices.
- Process totals are logical requests, not physical disk writes.

### SMART data is unavailable

- Ensure the service runs elevated/LocalSystem.
- USB, RAID, Storage Spaces, and virtual-disk layers may hide SMART data.
- Try a direct storage connection if practical.
- Treat Limited/Unavailable as missing evidence, not a healthy result.

### Controller-error alert names the wrong volume

Windows event 11 records identify `HarddiskN`. Removable/USB disk numbers can change after reconnection. The alert preserves the original event device path but shows the **current** volume mapping. Verify the disk number and event timestamps before acting.

### Online TBW lookup cannot run

Check:

1. **Enable rated-TBW web lookup** is selected in this user's dashboard settings.
2. The selected provider is **serper** for new setups.
3. A Serper key is saved in guided setup or `SERPER_API_KEY` is set.
4. For **Foundry Local verification**, Foundry Local and a suitable model are installed. If Foundry
  is missing, use **Install Foundry Local** in the lookup modal; this requires Windows Package Manager (`winget`).
5. For **Serper-only evidence parsing**, the backend is locked to Serper; Foundry and a local model are not required.
6. Internet access permits the selected search API.

If no verified result is found, do not assume the nearest model's rating. Enter the manufacturer's rating manually when known.

### Notifications do not appear

- Confirm notifications are enabled in this user's dashboard settings.
- Check Windows notification settings and Focus Assist/Do Not Disturb.
- Confirm the tray app is running.
- Inspect `%ProgramData%\DiskActivityMonitor\toast-error.log` if it exists.

### Auto-suspend reports access denied

The target likely runs elevated or under another account. Run the tray elevated only if process control is required and the security impact is understood.

### Database is busy or WAL is large

SQLite WAL mode permits the service and tray to share data. The collector checkpoints periodically and on shutdown. Close readers and restart the service to allow a clean checkpoint. Do not delete only the WAL while processes are using the database.

### Service diagnostics

Useful checks:

```powershell
Get-Service DiskActivityMonitor
sc.exe query DiskActivityMonitor
.\dam.ps1 status
Get-WinEvent -LogName System -MaxEvents 100 |
  Where-Object ProviderName -eq 'disk'
```

When run interactively, the collector writes structured logs to its console. As a Windows Service, hosting/lifecycle messages can also be inspected through Windows Event Viewer where available.

---

## Uninstall and reset

### Packaged uninstaller

The packaged uninstaller asks whether to keep settings for a future reinstall. Choosing not to
keep them removes machine `config.json` and the invoking Windows account's `user-settings.json`;
monitoring history and DPAPI-protected API keys remain. A later installer detects retained
machine or current-account settings and asks whether to reuse them. Existing values are
preserved, while settings introduced by the newer version receive their current defaults.

### Scripted uninstall

Run elevated:

```powershell
.\scripts\uninstall.ps1
```

The script removes the service and Startup shortcut and stops the tray. It intentionally leaves monitoring history and settings.

### Remove all data

After uninstalling and confirming service/tray processes are stopped, delete:

- `%ProgramData%\DiskActivityMonitor`
- `%LOCALAPPDATA%\DiskActivityMonitor` for each user whose lookup secrets should be removed

Deleting the data directory permanently removes history, alerts, snoozes, configuration, and stored suspension state.

---

## Developer and release workflows

### Build and test

```powershell
dotnet build .\DiskActivityMonitor.slnx -c Release
dotnet test .\tests\DiskActivityMonitor.Tests\DiskActivityMonitor.Tests.csproj -c Release
```

### Build installers

Build x64 and x86 with one version and a consolidated summary:

```powershell
.\scripts\build-all-installers.ps1 -Version 1.5.0
```

Preview without changing files:

```powershell
.\scripts\build-all-installers.ps1 -Version 1.5.0 -WhatIf
```

Build selected architecture:

```powershell
.\scripts\build-all-installers.ps1 -Variant x64 -Version 1.5.0
```

### Commit, push, and release

```powershell
.\scripts\build-all-installers.ps1 -Version 1.5.0 -Commit
.\scripts\build-all-installers.ps1 -Push
```

`-Commit` and `-Push` attempt focused commits by functional area. The script preserves any
already-staged changes as the first commit, then groups remaining changes by whole file. It never
creates partial-file hunks automatically. Possible renames or other ambiguous path expansion are
kept together in one residual release commit.

With `-Push`, omitting `-Version` increments the patch number of the latest stable local tag,
origin tag, or GitHub release, including drafts (for example, `v1.5.0` becomes `v1.5.1`). Pass
`-Version` to override that selection. The script then asks whether the GitHub release should be:

- **Draft:** upload for review without official publication.
- **Published:** publish officially and mark latest.

For unattended operation:

```powershell
.\scripts\build-all-installers.ps1 -Push -ReleaseMode Draft
.\scripts\build-all-installers.ps1 -Push -ReleaseMode Published
.\scripts\build-all-installers.ps1 -Push -SkipRelease
```

The lower-level builder remains available at `installer\build-installer.ps1` for direct x64/x86 packaging.

### Publish the authenticated download site

The Azure publication workflow is separate from GitHub Releases:

```powershell
.\scripts\publish-to-azure.ps1 -Version 1.5.0
```

It builds x64/x86 installers, creates an x64 portable ZIP, uploads artifacts, and surgically updates only the Disk Activity Monitor card on the existing static site.
