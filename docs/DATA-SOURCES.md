# Disk Activity Data Sources

This document maps every graph and disk-activity card in the tray dashboard to the data it
uses, the calculation applied to that data, and the important limits on its interpretation.
It describes the implementation in `MainWindow.xaml` and `MainWindow.xaml.cs`, including the
graphs in the per-process file drill-down.

## The four kinds of data

The application deliberately keeps these sources separate:

| Kind | Primary Windows source | What it means |
|---|---|---|
| Physical disk activity | `PhysicalDisk\Disk Read Bytes/sec` and `PhysicalDisk\Disk Write Bytes/sec` performance counters | Bytes observed at each physical disk. These values drive disk totals, throughput, trends, and write-rate endurance projections. |
| Process activity | Kernel ETW `FileIO` read/write events | Logical file-I/O bytes requested by processes. Windows can cache, merge, defer, or eliminate requests before they reach a physical disk. |
| Drive lifetime and wear | NVMe SMART/Health log page `0x02`, ATA SMART attributes 241/242, and `MSFT_StorageReliabilityCounter.Wear` | Device-reported lifetime host bytes and wear. These values survive application installs and are not reconstructed from monitoring history. |
| Controller errors | Windows System log, provider `disk`, event ID 11 | Storage-path errors involving a device, cable, port, enclosure, power, or controller. This is evidence about reliability, not a byte count. |

Physical-disk and process values are not expected to match. Process values identify software
requesting I/O; physical-disk values are the authority for actual disk traffic and SSD write
pressure.

## Collection and storage pipeline

### Physical disk samples

1. `DiskPerformanceSampler` enumerates `PhysicalDisk` performance-counter instances, excluding
   `_Total`.
2. On each configured sample interval, it reads `Disk Read Bytes/sec` and
   `Disk Write Bytes/sec` for each instance.
3. It converts each rate to an interval byte count:

   `interval bytes = counter bytes/second * actual elapsed seconds`

4. `CollectorWorker` stores each interval in `disk_live` for the rolling live graph and
   accumulates the intervals into one-minute rows in `disk_minute`.
5. A row in `collector_minute` records each completed minute so the UI can distinguish missing
   monitoring time from an idle monitored minute.

The configured sample interval is clamped to 1 through 60 seconds. Live samples are retained for
the configured live-graph window, clamped to 1 through 120 minutes. Minute history is retained for
the configured general retention period.

### Process samples

The preferred process source is a real-time kernel ETW session named
`DiskActivityMonitorKernel`. It enables `FileIO` and `FileIOInit`, listens to read and write
events, and sums each event's `IoSize` by process name. Unnamed objects, named pipes, and mailslots
are excluded. `svchost` is decorated with its hosted Windows service name when that mapping is
available.

Starting the kernel ETW session normally requires elevation. The installed LocalSystem service
has that access. If ETW cannot start, the collector falls back to Win32
`GetProcessIoCounters`. Its transfer counts combine files, pipes, and device I/O, so fallback
values are an upper-bound proxy and per-file attribution is unavailable.

Process deltas are accumulated into one-minute `proc_minute` rows. Rows below the configurable
per-process minimum are omitted. When per-file tracking is enabled, ETW targets are normalized to
drive-letter or UNC paths and written to `proc_file_minute`. The busiest configured number of
targets per process and minute are stored individually; the rest are folded into
`(all other files)` so their bytes are not silently discarded.

### Disk identity, lifetime, and wear

`DiskDetector` correlates the numeric `PhysicalDisk` counter instance with
`MSFT_PhysicalDisk` in `root\Microsoft\Windows\Storage`. It stores the friendly name, volume
letters, media type, size, serial number, and bus type in `disks`.

Lifetime telemetry is read directly from `\\.\PhysicalDriveN`:

- NVMe uses `IOCTL_STORAGE_QUERY_PROPERTY` to read SMART/Health log page `0x02`.
  `Data Units Written` and `Data Units Read` are multiplied by 512,000 bytes. The same log can
  provide `Percentage Used`.
- Internal ATA/SATA disks can use `IOCTL_ATA_PASS_THROUGH` SMART READ DATA. Attribute 241 is
  interpreted as total LBAs written and 242 as total LBAs read, at 512 bytes per LBA.
- If direct telemetry does not provide wear, the collector tries the related
  `MSFT_StorageReliabilityCounter.Wear` value.

ATA pass-through is attempted only for internal ATAPI, ATA, or SATA buses. USB, SD, virtual, and
other unsupported paths degrade to unavailable data instead of being treated as zero. The
repository preserves the last known lifetime byte totals when a later scan cannot read them.

## Dashboard source map

The disk selector determines the scope of physical-disk cards. A single selection queries one
`disk_id`; **All disks** sums the selected interval across every detected disk. The live graph is
the exception: All disks keeps separate read and write series per disk. Per-drive SMART wear,
rated TBW, and lifespan are never combined into a synthetic all-disk percentage.

### Written today

| Display | Source and calculation |
|---|---|
| Main value | Sum of `disk_minute.write_bytes` from local midnight, converted to UTC, through the current time. |
| `read` subtitle | Sum of `disk_minute.read_bytes` over the same interval. |

This is physical disk traffic recorded by the application. It is not a drive-lifetime SMART
total, and unmonitored time is not estimated.

### Last 24 hours

| Display | Source and calculation |
|---|---|
| Main value | Sum of `disk_minute.write_bytes` over the trailing 24 hours. |
| `read` subtitle | Sum of `disk_minute.read_bytes` over the trailing 24 hours. |

This is a rolling interval, unlike Written today's local calendar-day boundary.

### Last 7 days

| Display | Source and calculation |
|---|---|
| Main value | Sum of physical `disk_minute.write_bytes` over the trailing seven days. |
| Average subtitle | The displayed seven-day write total divided by 7. |

The subtitle is a calendar-day average of the recorded total. It does not fill missing monitoring
time and is therefore lower when the collector was stopped.

### SSD endurance summary

For one selected disk, the headline is the same projected lifespan calculated by the detailed
SSD endurance card. Its inputs are the effective rated TBW, the direct lifetime-written total
when available, and the recent physical write rate from `disk_minute`.

For All disks, the card shows the number of disks and their combined recent physical writes per
day. It intentionally says that lifespan remains drive-specific.

### SSD endurance detail

| Display | Source and calculation |
|---|---|
| Rated TBW badge | Per-disk TBW or TBW range in configuration. If none is confirmed, the configured default SSD rating is used and labeled estimated. A web lookup changes this input only after the user applies a candidate. |
| Drive Life Used | Preferred: direct lifetime bytes written divided by effective rated TBW. Fallback: direct or WMI SMART wear percentage. |
| Wear bar | The same percentage as Drive Life Used, clamped visually to 0 through 100 percent. For a TBW range, the lower rating determines the conservative fill. |
| SMART context | `Percentage Used` from NVMe log page `0x02`, or `MSFT_StorageReliabilityCounter.Wear`, when available. |
| Lifetime written/read | Direct NVMe Data Units Written/Read or ATA SMART 241/242. These are whole-drive lifetime values, not sums from SQLite. |
| Projected lifespan | Remaining rated bytes divided by the recent physical write rate. The display uses a small 0.1 percent-of-TBW remaining floor and is withheld when the write rate is zero or monitoring coverage is below the configured high-coverage threshold. |
| Average per hour/day/week | Physical writes from the most recent seven days, or all history when less than seven days exists, divided by monitored minutes. Day and week values are scaled from the monitored-hour rate. |
| Recorded-by-this-app footnote | Sum of `disk_minute.write_bytes` from the disk's earliest retained sample through now. |
| Monitoring coverage | Distinct completed minutes in `collector_minute` or `disk_minute`, divided by requested calendar minutes. |

For a configured TBW range, the consumed percentage and projected lifespan are displayed as
ranges. The lower TBW rating gives the higher consumed percentage and shorter projected life.
The card uses decimal bytes for manufacturer TBW ratings: `1 TBW = 1,000,000,000,000 bytes`.

In All disks mode, lifetime written/read totals are summed only across drives that expose each
value. SMART wear, TBW percentages, and projections remain labeled per drive.

### Live disk activity graph

| Display | Source and calculation |
|---|---|
| Read line | `disk_live.read_bytes / elapsed_seconds / 1,000,000` for each physical disk. |
| Write line | `disk_live.write_bytes / elapsed_seconds / 1,000,000` for each physical disk. |
| `Now` values | The last plotted read and write point for each disk. |

The unit is decimal MB/s. The graph reads granular samples from the selected rolling window and
downsamples them to at most 120 plotted points. Downsampling sums bytes and elapsed time inside
each plotted bucket, so it preserves average throughput rather than averaging averages.

All disks mode shows a distinct read and write line for every disk. It does not use ETW process
events, SMART lifetime totals, or the performance-counter `_Total` instance.

### Total written over time graph

The graph uses physical `disk_minute.write_bytes` rows. It groups them into fixed buckets based on
the selected duration and builds a cumulative line:

| Selected duration | Bucket size |
|---|---|
| Up to 2 hours | 1 minute |
| Up to 2 days | 15 minutes |
| Up to 14 days | 2 hours |
| Up to 62 days | 12 hours |
| Longer custom ranges | At least 1 day, targeting about 96 buckets |

When a direct lifetime-written value exists, the line is anchored by subtracting physical writes
recorded after the range start from the current drive lifetime total. This gives the line a
device-lifetime baseline while its changes still come only from recorded physical samples.

Without a lifetime value, the baseline is the physical write total recorded before the selected
range, so the cumulative series starts with writes observed since monitoring began. Missing
monitoring time is never spread across the graph. The **Increase** and average-rate text use the
recorded change between the first and last plotted cumulative points.

All disks mode sums bucket values and baselines across disks. Lifetime anchoring is applied only
to disks that expose it, which the graph caption states.

### Disk throughput graph and metrics

This card queries `disk_minute` and first sums `read_bytes + write_bytes` for each UTC minute
across the selected disks. It therefore reports total physical I/O, not writes alone.

| Display | Calculation |
|---|---|
| Average | Total read-plus-write bytes divided by the number of monitored minutes, then divided by 60 and 1,000,000 to produce decimal MB/s. |
| Median | Median per-minute read-plus-write byte total, converted to decimal MB/s. Monitored minutes with no activity contribute zero. |
| Peak | The busiest one-minute read-plus-write total, divided by 60 and 1,000,000. |
| Bar graph | The same Average, Median, and Peak values shown above it. |
| Coverage caption | Completed monitored minutes versus requested calendar minutes. A calendar-time average is shown only at or above the configured high-coverage threshold. |

The 1-hour, 24-hour, 7-day, and 30-day controls change only the query interval. They do not change
the physical counter source.

### Top application write requests card

This card ranks the eight largest `SUM(proc_minute.write_bytes)` values in the selected completed
minute window. Bars are scaled relative to the largest process in that result. Although read bytes
are stored and queried, the visible rank and value are based on writes.

Under normal installed-service operation, these are logical file-write request bytes from kernel
ETW, grouped by process or resolved service name. They are not physical disk writes and cannot be
assigned exactly to one physical disk. Under the non-elevated fallback, they are Win32 process I/O
transfer deltas that can also include pipe and device traffic.

The current partial minute is excluded because `proc_minute` is persisted when a minute closes.
Very small process rows can be absent because collection applies the configured minimum bytes per
minute.

### Alert center card

The card reads persisted rows from `alerts`; it does not sample a new data source itself. It shows
non-dismissed alerts from the trailing hour, grouped by stable rule key. Repeated occurrences show
the newest message and an occurrence count. **All alerts** reads the complete retained alert table,
including dismissed rows.

Alert inputs are:

| Alert family | Underlying source |
|---|---|
| SSD 1-hour and 24-hour write alerts | Physical `disk_minute.write_bytes`. |
| Endurance alerts | Direct lifetime bytes or SMART wear, configured TBW, recent physical `disk_minute` write rate, and monitoring coverage. |
| Per-process and all-process write alerts | Logical `proc_minute.write_bytes`, or fallback Win32 process I/O counters. Messages also include separate per-drive physical writes for comparison. |
| Controller-error alerts | Windows System log, provider `disk`, event ID 11, counted over the configured trailing-day window and correlated to the current physical-disk map. |

Thresholds, cooldowns, snoozes, and dismissal state affect whether an alert row appears; they do
not change the underlying byte measurements.

### Suspended processes card

This is a state card, not an activity measurement. It reads `suspended_processes` and shows the
process name, suspension timestamp, source, and optional automatic-resume time for processes that
Disk Activity Monitor suspended.

An auto-suspend decision is based on the process's logical writes from `proc_minute` over the last
completed hour and the configured rule threshold. A user-triggered suspension can originate from
a process alert. The card does not imply that the process is currently producing physical disk
traffic.

## Per-process file drill-down graphs

Clicking or hovering a process row opens a drill-down based on the same completed process window.

### Writes by process donut

The donut reads `proc_minute`, using the top seven processes plus **Other processes**. The focused
process is added explicitly when it is outside the top seven. Its center total is the sum of
logical writes for all recorded processes in the window.

This is a share of recorded process-attributed logical write requests, not a share of physical
disk writes. Process rows omitted by the per-minute collection floor are not represented.

### Process write timeline

The timeline reads the focused process's per-minute `proc_minute.write_bytes`. It creates at most
48 equal-width time buckets, sums writes inside each bucket, and treats minutes without a row as
zero. The tallest bucket or buckets are highlighted as bursts.

### File-type bars and file rows

These displays use `proc_file_minute`, which exists only when ETW per-file tracking is enabled.

- The file list shows the top 20 paths by logical write bytes.
- The file-type bars query up to 200 paths, group them by extension, show the eight largest groups,
  and combine the rest as **Other**.
- The coverage footer compares all stored per-file logical writes with the process total from
  `proc_minute`.

The collector's per-minute target limit, minimum target size, in-memory tracking limit, and
per-file retention period are configurable. Bytes beyond the per-minute top-target list are
normally preserved in `(all other files)`, while new paths encountered after the in-memory limit
is reached cannot be attributed until the next sample snapshot.

## Other data-bearing surfaces

The disk selector uses the `disks` table populated from performance-counter instance names and
`MSFT_PhysicalDisk`. The live SMART scan is separate from dashboard activity collection: it reads
current `MSFT_PhysicalDisk` health and operational status, related
`MSFT_StorageReliabilityCounter` fields, `Win32_DiskDrive` identity/status, safe direct NVMe
SMART data, and the event-11 count carried by the selected controller alert.

The rated-TBW lookup modal uses web-search evidence and optional local-model analysis to propose a
manufacturer rating. Search results never become endurance inputs unless the user applies one,
which writes it to configuration. Settings cards are configuration inputs, not telemetry sources.

## Code map

| Responsibility | Implementation |
|---|---|
| Dashboard inventory and bindings | `src/DiskActivityMonitor.Tray/MainWindow.xaml` |
| Dashboard queries and calculations | `src/DiskActivityMonitor.Tray/MainWindow.xaml.cs` |
| File drill-down graphs | `src/DiskActivityMonitor.Tray/MainWindow.FileTools.cs` |
| Physical performance counters | `src/DiskActivityMonitor.Core/Collection/DiskPerformanceSampler.cs` |
| Disk identity and SMART correlation | `src/DiskActivityMonitor.Core/Collection/DiskDetector.cs` |
| Direct NVMe/ATA lifetime data | `src/DiskActivityMonitor.Core/Collection/SmartLifetimeReader.cs` |
| ETW process and file attribution | `src/DiskActivityMonitor.Service/EtwProcessIoReader.cs` |
| Process fallback counters | `src/DiskActivityMonitor.Core/Collection/ProcessIoReader.cs` |
| Sampling, minute rollups, and retention | `src/DiskActivityMonitor.Service/CollectorWorker.cs` |
| SQLite tables and aggregate queries | `src/DiskActivityMonitor.Core/Data/MonitorRepository.cs` |
| Alert derivation | `src/DiskActivityMonitor.Core/Alerts/AlertEngine.cs` |
| Controller event source | `src/DiskActivityMonitor.Service/DiskControllerErrorReader.cs` |
| Throughput math | `src/DiskActivityMonitor.Core/ThroughputStats.cs` |
| Monitoring-rate and coverage math | `src/DiskActivityMonitor.Core/MonitoringRateStats.cs` |
| Cumulative trend construction | `src/DiskActivityMonitor.Core/Trends.cs` |