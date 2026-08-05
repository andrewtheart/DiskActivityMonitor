using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DiskActivityMonitor.Core;
using DiskActivityMonitor.Core.Collection;
using DiskActivityMonitor.Core.Configuration;
using DiskActivityMonitor.Core.Data;
using DiskActivityMonitor.Core.Models;

namespace DiskActivityMonitor.Cli;

/// <summary>Dispatches CLI commands and implements them against the shared repository/config.</summary>
internal static class CliRunner
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0) { PrintUsage(); return 0; }

            var cmd = args[0].ToLowerInvariant().TrimStart('-');
            var a = CliArgs.Parse(args.Skip(1).ToArray());

            return cmd switch
            {
                "help" or "h" or "?" => Help(a),
                "version" => Version(),
                "status" => Status(),
                "disks" => Disks(),
                "summary" or "today" => Summary(a),
                "top" => Top(a),
                "files" or "file" => FilesCmd(a),
                "process" or "proc" => ProcessCmd(a),
                "trends" or "trend" => TrendsCmd(a),
                "endurance" or "ssd" => Endurance(a),
                "alerts" => Alerts(a),
                "ack" => Ack(a),
                "snooze" => Snooze(a),
                "config" or "cfg" => Config(a),
                "watch" => Watch(a),
                _ => Unknown(cmd),
            };
        }
        catch (Exception ex)
        {
            Out.Error("Error: " + ex.Message);
            return 1;
        }
    }

    private static int Unknown(string cmd)
    {
        Out.Error($"Unknown command: {cmd}");
        Console.Error.WriteLine("Run 'dam help' for usage.");
        return 2;
    }

    // ---------------------------------------------------------------- helpers

    private static MonitorRepository OpenRepo()
    {
        var repo = new MonitorRepository();
        repo.EnsureSchema();
        return repo;
    }

    private static bool IsRunning(string processName)
    {
        try { return Process.GetProcessesByName(processName).Length > 0; }
        catch { return false; }
    }

    private static DiskInfo? ResolveDisk(MonitorRepository repo, CliArgs a)
    {
        var disks = repo.GetDisks();
        var id = a.Opt("disk", "d");
        if (id is not null)
            return disks.FirstOrDefault(x => string.Equals(x.DiskId, id, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No disk with id '{id}'. Run 'dam disks'.");
        return disks.FirstOrDefault(x => x.IsSsd) ?? disks.FirstOrDefault();
    }

    private static string MediaTag(DiskInfo d) => d.MediaType switch
    {
        DiskMediaType.Ssd => "SSD",
        DiskMediaType.Scm => "Optane",
        DiskMediaType.Hdd => "HDD",
        _ => "unknown",
    };

    private static DateTime MinuteFloorUtc(DateTime nowUtc)
        => new(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, nowUtc.Minute, 0, DateTimeKind.Utc);

    private static string SevText(AlertSeverity s) => s switch
    {
        AlertSeverity.Critical => "CRIT",
        AlertSeverity.Warning => "WARN",
        _ => "INFO",
    };

    private static ConsoleColor SevColor(AlertSeverity s) => s switch
    {
        AlertSeverity.Critical => ConsoleColor.Red,
        AlertSeverity.Warning => ConsoleColor.Yellow,
        _ => ConsoleColor.Green,
    };

    private static string FormatYears(double years)
    {
        if (double.IsInfinity(years) || double.IsNaN(years) || years <= 0) return "an unknown time";
        if (years >= 1) return $"{years:0.0} years";
        int months = Math.Max(1, (int)Math.Round(years * 12));
        return months == 1 ? "1 month" : $"{months} months";
    }

    // ---------------------------------------------------------------- commands

    private static int Status()
    {
        var repo = OpenRepo();
        var nowUtc = DateTime.UtcNow;
        var disks = repo.GetDisks();
        var earliest = disks.Select(d => repo.GetEarliestSample(d.DiskId)).Where(t => t is not null).Select(t => t!.Value).DefaultIfEmpty().Min();
        var unacked = repo.GetRecentAlerts(500, unacknowledgedOnly: true).Count;
        var procSnoozes = repo.GetProcessSnoozes(nowUtc).Count;
        var globalUntil = repo.GetGlobalSnoozeUntil(nowUtc);

        Out.Header("Disk Activity Monitor — status");
        var svc = IsRunning("DiskActivityMonitor.Service");
        var tray = IsRunning("DiskActivityMonitor.Tray");
        Console.WriteLine($"  Collector service : {(svc ? "running" : "NOT running")}");
        Console.WriteLine($"  Tray app          : {(tray ? "running" : "not running")}");
        Console.WriteLine($"  Data directory    : {Paths.BaseDirectory}");
        if (File.Exists(Paths.DatabasePath))
        {
            var fi = new FileInfo(Paths.DatabasePath);
            Console.WriteLine($"  Database          : {fi.Length / 1024.0 / 1024.0:0.0} MB, updated {fi.LastWriteTime:g} ({LocalTimeDisplay.ZoneId()})");
        }
        else
        {
            Console.WriteLine("  Database          : (none yet)");
        }
        Console.WriteLine($"  Monitoring since  : {(earliest == default ? "(no samples yet)" : LocalTimeDisplay.FormatUtcWithZone(earliest, "g"))}");
        Console.WriteLine($"  Disks             : {disks.Count} ({disks.Count(d => d.IsSsd)} SSD)");
        Console.WriteLine($"  Unacked alerts    : {unacked}");
        Console.WriteLine($"  Snoozes           : {(globalUntil is null ? "no global" : $"GLOBAL until {LocalTimeDisplay.FormatUtcWithZone(globalUntil.Value, "g")}")}, {procSnoozes} process(es)");
        return 0;
    }

    private static int Disks()
    {
        var repo = OpenRepo();
        var disks = repo.GetDisks();
        if (disks.Count == 0) { Out.Dim("No disks recorded yet — start the collector service."); return 0; }

        var rows = disks.Select(d => (IReadOnlyList<string>)new[]
        {
            d.DiskId,
            string.IsNullOrWhiteSpace(d.Volumes) ? (string.IsNullOrWhiteSpace(d.FriendlyName) ? $"Disk {d.DiskId}" : d.FriendlyName) : d.Volumes.Trim(),
            MediaTag(d),
            d.SizeBytes > 0 ? ByteFormat.Humanize(d.SizeBytes) : "-",
            d.WearPercent is int w ? $"{w}%" : "-",
        }).ToList();
        Out.Table(new[] { "ID", "Disk", "Media", "Size", "Wear" }, rows, new[] { false, false, false, true, true });
        return 0;
    }

    private static int Summary(CliArgs a)
    {
        var repo = OpenRepo();
        var disks = a.Flag("all") ? repo.GetDisks() : (ResolveDisk(repo, a) is { } d ? new List<DiskInfo> { d } : new());
        if (disks.Count == 0) { Out.Dim("No disks recorded yet."); return 0; }

        var nowUtc = DateTime.UtcNow;
        var midnightUtc = DateTime.Today.ToUniversalTime();
        foreach (var disk in disks)
        {
            Out.Header($"{disk.DisplayName}");
            var rows = new List<IReadOnlyList<string>>();
            void Add(string label, DateTime fromUtc)
            {
                var t = repo.GetDiskTotals(disk.DiskId, fromUtc, nowUtc);
                rows.Add(new[] { label, ByteFormat.Humanize(t.Write), ByteFormat.Humanize(t.Read) });
            }
            Add("Today", midnightUtc);
            Add("Last 24h", nowUtc.AddHours(-24));
            Add("Last 7d", nowUtc.AddDays(-7));
            Out.Table(new[] { "Window", "Written", "Read" }, rows, new[] { false, true, true });
            Console.WriteLine();
        }
        return 0;
    }

    private static int Top(CliArgs a)
    {
        var repo = OpenRepo();
        int minutes = a.IntOpt(new[] { "minutes", "m" }, 60);
        int count = a.IntOpt(new[] { "count", "n" }, 10);
        var nowUtc = DateTime.UtcNow;
        var procs = repo.GetTopProcesses(nowUtc.AddMinutes(-minutes), nowUtc, count);

        Out.Header($"Top {count} processes by logical file-write requests — last {minutes} min");
        Out.Dim("Application-requested file I/O; physical disk writes and SSD wear may be lower.");
        if (procs.Count == 0) { Out.Dim("No process activity recorded in this window."); return 0; }
        var rows = procs.Select(p => (IReadOnlyList<string>)new[]
        {
            p.ProcessName, ByteFormat.Humanize(p.WriteBytes), ByteFormat.Humanize(p.ReadBytes),
        }).ToList();
        Out.Table(new[] { "Process / service", "Logical writes", "Logical reads" }, rows, new[] { false, true, true });
        return 0;
    }

    /// <summary>
    /// Lists the individual files a process wrote to. This is what explains an opaque writer such
    /// as the kernel <c>System</c> process, whose writes are issued for the whole machine.
    /// </summary>
    private static int FilesCmd(CliArgs a)
    {
        var name = a.Positional(0);
        if (name is null) { Out.Error("Usage: dam files <process> [--minutes 60] [--count 15]"); return 2; }

        var repo = OpenRepo();
        int minutes = a.IntOpt(new[] { "minutes", "m" }, 60);
        int count = a.IntOpt(new[] { "count", "n" }, 15);
        var end = MinuteFloorUtc(DateTime.UtcNow);
        var start = end.AddMinutes(-minutes);

        var targets = repo.GetTopFileTargets(name, start, end, count);
        long processWrite = repo.GetProcessWrite(name, start, end);
        long attributed = repo.GetFileTargetWriteTotal(name, start, end);

        Out.Header($"Files written by '{name}' - last {minutes} min");
        var note = FileTargetNormalizer.ExplainProcess(name);
        if (note is not null) Out.Dim(note);

        if (targets.Count == 0)
        {
            Out.Dim("No per-file records in this window. Per-file attribution needs the ETW collector "
                + "(the installed service) and trackFileTargets enabled in config.json.");
            return 0;
        }

        var rows = targets.Select(t => (IReadOnlyList<string>)new[]
        {
            t.Path, FileTargetNormalizer.Label(t.Kind), ByteFormat.Humanize(t.WriteBytes), ByteFormat.Humanize(t.ReadBytes),
        }).ToList();
        Out.Table(new[] { "File", "Kind", "Logical writes", "Logical reads" }, rows, new[] { false, false, true, true });

        if (processWrite > 0 && attributed > 0)
            Out.Dim($"Listed rows cover {Math.Min(1, (double)attributed / processWrite):P0} of this process's logical writes.");
        return 0;
    }

    private static int ProcessCmd(CliArgs a)
    {
        var name = a.Positional(0);
        if (name is null) { Out.Error("Usage: dam process <name>"); return 2; }
        var repo = OpenRepo();
        var end = MinuteFloorUtc(DateTime.UtcNow);
        (string Label, int Minutes)[] windows = { ("1m", 1), ("5m", 5), ("15m", 15), ("30m", 30), ("1h", 60), ("24h", 1440) };

        Out.Header($"'{name}' — logical file-write requests by window");
        Out.Dim("Application-requested file I/O; physical disk writes and SSD wear may be lower.");
        var rows = windows.Select(w => (IReadOnlyList<string>)new[]
        {
            w.Label, ByteFormat.Humanize(repo.GetProcessWrite(name, end.AddMinutes(-w.Minutes), end)),
        }).ToList();
        Out.Table(new[] { "Window", "Logical writes" }, rows, new[] { false, true });
        return 0;
    }

    private static int TrendsCmd(CliArgs a)
    {
        var repo = OpenRepo();
        var disk = ResolveDisk(repo, a);
        if (disk is null) { Out.Dim("No disks recorded yet."); return 0; }

        var range = (a.Opt("range", "r") ?? "hour").ToLowerInvariant();
        var bucket = range switch
        {
            "day" or "d" => Trends.Bucket.Day,
            "week" or "w" => Trends.Bucket.Week,
            _ => Trends.Bucket.Hour,
        };
        int count = a.IntOpt(new[] { "count", "n" }, bucket switch { Trends.Bucket.Hour => 24, Trends.Bucket.Day => 30, _ => 12 });
        var nowLocal = DateTime.Now;
        int days = bucket switch { Trends.Bucket.Hour => (count / 24) + 2, Trends.Bucket.Day => count + 1, _ => count * 7 + 1 };
        var fromUtc = DateTime.UtcNow.AddDays(-days);
        var hourly = repo.GetHourlyDiskTotals(disk.DiskId, fromUtc, DateTime.UtcNow);
        var buckets = Trends.Build(hourly, bucket, count, nowLocal);

        Out.Header($"{disk.DisplayName} — writes per {bucket.ToString().ToLowerInvariant()} (last {count})");
        Out.Dim(LocalTimeDisplay.ZoneLabel());
        double max = Math.Max(1, buckets.Max(b => (double)b.WriteBytes));
        foreach (var b in buckets)
        {
            int barLen = (int)Math.Round(b.WriteBytes / max * 30);
            Console.WriteLine($"  {Trends.Label(b.BucketStartLocal, bucket),-8} {ByteFormat.Humanize(b.WriteBytes),12}  {new string('#', barLen)}");
        }
        return 0;
    }

    private static int Endurance(CliArgs a)
    {
        var repo = OpenRepo();
        var cfg = new ConfigStore().Current;
        var disks = a.Flag("all") ? repo.GetDisks().Where(d => d.IsSsd).ToList()
                                  : (ResolveDisk(repo, a) is { } d ? new List<DiskInfo> { d } : new());
        if (disks.Count == 0) { Out.Dim("No SSD recorded yet."); return 0; }

        var nowUtc = DateTime.UtcNow;
        foreach (var disk in disks)
        {
            Out.Header($"{disk.DisplayName} — SSD endurance");
            if (!disk.IsSsd) { Out.Dim("  Not an SSD; endurance tracking does not apply."); Console.WriteLine(); continue; }

            double tbwLow = cfg.EffectiveTbw(disk.DiskId);
            double? tbwHigh = cfg.EffectiveTbwUpper(disk.DiskId);
            bool ranged = tbwHigh.HasValue;
            bool estimatedTbw = !cfg.DiskTbwRatings.ContainsKey(disk.DiskId);
            double tbwLowBytes = tbwLow * 1e12;
            double tbwHighBytes = (tbwHigh ?? tbwLow) * 1e12;
            string tbwLabel = ranged ? $"{tbwLow:0.#} to {tbwHigh:0.#} TBW" : $"{tbwLow:0.#} TBW";
            var earliest = repo.GetEarliestSample(disk.DiskId);
            long writtenObserved = earliest is null ? 0 : repo.GetDiskTotals(disk.DiskId, earliest.Value, nowUtc).Write;
            long consumed = disk.LifetimeBytesWritten ?? writtenObserved;

            Console.WriteLine($"  {(estimatedTbw ? "TBW estimate" : "TBW rating"),-18}: {tbwLabel}");
            string usedPct = ranged ? $"~{consumed / tbwHighBytes * 100:0.#}% to {consumed / tbwLowBytes * 100:0.#}%" : $"~{consumed / tbwLowBytes * 100:0.#}%";
            string wearText = disk.WearPercent is int w
                ? $"{w}% used"
                : disk.LifetimeBytesWritten is not null && tbwLowBytes > 0
                    ? $"{usedPct} used (estimated from lifetime / TBW)"
                    : "not reported by this drive (or collector not elevated)";
            Console.WriteLine($"  SMART wear        : {wearText}");
            if (disk.LifetimeBytesWritten is long lifeW)
            {
                string pctText = ranged ? $"{lifeW / tbwHighBytes * 100:0.###}% to {lifeW / tbwLowBytes * 100:0.###}%" : $"{lifeW / tbwLowBytes * 100:0.###}%";
                string readPart = disk.LifetimeBytesRead is long lifeR ? $", {ByteFormat.Humanize(lifeR)} read" : "";
                Console.WriteLine($"  Lifetime (drive)  : {ByteFormat.Humanize(lifeW)} written{readPart}  ({pctText} of TBW)");
            }
            string trackedPct = ranged
                ? $"{writtenObserved / tbwHighBytes * 100:0.###}% to {writtenObserved / tbwLowBytes * 100:0.###}%"
                : $"{writtenObserved / tbwLowBytes * 100:0.###}%";
            Console.WriteLine($"  Written (tracked) : {ByteFormat.Humanize(writtenObserved)}  ({trackedPct} of TBW since {(earliest is null ? "n/a" : LocalTimeDisplay.FormatUtcWithZone(earliest.Value, "d"))})");

            if (earliest is not null)
            {
                double observedDays = (nowUtc - earliest.Value).TotalDays;
                double avgPerDay = observedDays >= 7
                    ? repo.GetDiskTotals(disk.DiskId, nowUtc.AddDays(-7), nowUtc).Write / 7.0
                    : writtenObserved / Math.Max(1.0 / 24, observedDays);
                double yearsLow = avgPerDay > 0 ? Math.Max(tbwLowBytes - (disk.LifetimeBytesWritten ?? 0), tbwLowBytes * 0.001) / (avgPerDay * 365.0) : double.PositiveInfinity;
                double yearsHigh = avgPerDay > 0 ? Math.Max(tbwHighBytes - (disk.LifetimeBytesWritten ?? 0), tbwHighBytes * 0.001) / (avgPerDay * 365.0) : double.PositiveInfinity;
                string projText = ranged ? $"{FormatYears(yearsLow)} to {FormatYears(yearsHigh)}" : FormatYears(yearsLow);
                Console.WriteLine($"  Recent average    : {ByteFormat.Humanize(avgPerDay)}/day");
                Console.WriteLine($"  Projected to TBW  : ~{projText} at the recent rate");
            }
            Console.WriteLine();
        }
        return 0;
    }

    private static int Alerts(CliArgs a)
    {
        var repo = OpenRepo();
        bool all = a.Flag("all");
        int count = a.IntOpt(new[] { "count", "n" }, 15);
        var alerts = repo.GetRecentAlerts(count, unacknowledgedOnly: !all);

        Out.Header(all ? $"Recent alerts (last {count})" : $"Unacknowledged alerts (last {count})");
        if (alerts.Count == 0) { Out.Dim("No alerts."); return 0; }
        bool full = a.Flag("full");
        foreach (var al in alerts)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = SevColor(al.Severity);
            Console.Write($"  #{al.Id,-4} {LocalTimeDisplay.FormatUtcWithZone(al.TimestampUtc, "MMM d HH:mm")}  {SevText(al.Severity),-4} ");
            Console.ForegroundColor = prev;
            Console.WriteLine($"{(al.Acknowledged ? "[ack] " : "")}{al.Title}");
            if (full) Out.Dim($"        {al.Message}");
        }
        if (!full) Out.Dim("  (use --full to show messages with the per-window breakdown)");
        return 0;
    }

    private static int Ack(CliArgs a)
    {
        var repo = OpenRepo();
        if (a.Flag("all"))
        {
            repo.AcknowledgeAlerts();
            Console.WriteLine("Acknowledged all alerts.");
            return 0;
        }
        var ids = a.Positionals.Select(p => long.TryParse(p, out var v) ? v : -1).Where(v => v > 0).ToList();
        if (ids.Count == 0) { Out.Error("Usage: dam ack <id> [<id>...]  |  dam ack --all"); return 2; }
        repo.AcknowledgeAlerts(ids);
        Console.WriteLine($"Acknowledged {ids.Count} alert(s): {string.Join(", ", ids)}");
        return 0;
    }

    private static int Snooze(CliArgs a)
    {
        var repo = OpenRepo();
        var nowUtc = DateTime.UtcNow;
        var sub = (a.Positional(0) ?? "list").ToLowerInvariant();

        switch (sub)
        {
            case "list":
                var g = repo.GetGlobalSnoozeUntil(nowUtc);
                Out.Header("Active snoozes");
                Console.WriteLine($"  Global : {(g is null ? "none" : $"all alerts until {LocalTimeDisplay.FormatUtcWithZone(g.Value, "g")}")}");
                var ps = repo.GetProcessSnoozes(nowUtc);
                if (ps.Count == 0) Console.WriteLine("  Process: none");
                else foreach (var (name, until) in ps)
                    Console.WriteLine($"  Process: {name,-24} until {LocalTimeDisplay.FormatUtcWithZone(until, "g")}");
                return 0;

            case "process" or "proc":
            {
                var name = a.Positional(1);
                var dur = Duration.Parse(a.Positional(2));
                if (name is null || dur is null) { Out.Error("Usage: dam snooze process <name> <duration>  (e.g. 30m, 1h, 1d, 1w)"); return 2; }
                repo.SnoozeProcess(name, nowUtc + dur.Value);
                repo.AcknowledgeProcessAlerts(name);
                Console.WriteLine($"Snoozed '{name}' for {Duration.Humanize(dur.Value)} (until {LocalTimeDisplay.FormatUtcWithZone(nowUtc + dur.Value, "g")}).");
                return 0;
            }

            case "all":
            {
                var dur = Duration.Parse(a.Positional(1));
                if (dur is null) { Out.Error("Usage: dam snooze all <duration>  (e.g. 30m, 1h, 1d, 1w)"); return 2; }
                repo.SnoozeAllAlerts(nowUtc + dur.Value);
                repo.AcknowledgeAlerts();
                Console.WriteLine($"Snoozed ALL alerts for {Duration.Humanize(dur.Value)} (until {LocalTimeDisplay.FormatUtcWithZone(nowUtc + dur.Value, "g")}).");
                return 0;
            }

            case "clear":
            {
                if (a.Flag("global")) { repo.ClearGlobalSnooze(); Console.WriteLine("Cleared global snooze."); return 0; }
                if (a.Flag("all"))
                {
                    repo.ClearGlobalSnooze();
                    foreach (var (name, _) in repo.GetProcessSnoozes(nowUtc)) repo.ClearProcessSnooze(name);
                    Console.WriteLine("Cleared all snoozes.");
                    return 0;
                }
                var target = a.Positional(1);
                if (target is null) { Out.Error("Usage: dam snooze clear <name> | --global | --all"); return 2; }
                repo.ClearProcessSnooze(target);
                Console.WriteLine($"Cleared snooze for '{target}'.");
                return 0;
            }

            default:
                Out.Error($"Unknown snooze subcommand: {sub}");
                Console.Error.WriteLine("Use: list | process <name> <dur> | all <dur> | clear <name>|--global|--all");
                return 2;
        }
    }

    private static int Config(CliArgs a)
    {
        var store = new ConfigStore();
        var cfg = store.Current;
        var sub = (a.Positional(0) ?? "get").ToLowerInvariant();

        if (sub == "get")
        {
            var key = a.Positional(1);
            if (key is null)
            {
                Console.WriteLine(JsonSerializer.Serialize(cfg, AppConfig.SerializerOptions));
                return 0;
            }
            var prop = FindScalarProp(key);
            if (prop is null) { Out.Error($"Unknown config key: {key}"); return 2; }
            Console.WriteLine($"{prop.Name} = {prop.GetValue(cfg)}");
            return 0;
        }

        if (sub == "set")
        {
            var key = a.Positional(1);
            var val = a.Positional(2);
            if (key is null || val is null) { Out.Error("Usage: dam config set <key> <value>"); return 2; }
            var prop = FindScalarProp(key);
            if (prop is null) { Out.Error($"Unknown or non-settable config key: {key}. Run 'dam config get' to list keys."); return 2; }
            object converted;
            try { converted = ConvertValue(prop.PropertyType, val); }
            catch { Out.Error($"Cannot convert '{val}' to {prop.PropertyType.Name} for {prop.Name}."); return 2; }
            store.Update(config => prop.SetValue(config, converted));
            Console.WriteLine($"Set {prop.Name} = {converted}  (saved; the service reloads automatically).");
            return 0;
        }

        Out.Error($"Unknown config subcommand: {sub}. Use 'get' or 'set'.");
        return 2;
    }

    private static PropertyInfo? FindScalarProp(string key)
        => typeof(AppConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.CanWrite && p.CanRead
                && string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)
                && IsScalar(p.PropertyType));

    private static bool IsScalar(Type t) => t == typeof(double) || t == typeof(int) || t == typeof(bool) || t == typeof(string);

    private static object ConvertValue(Type t, string val)
    {
        if (t == typeof(double)) return double.Parse(val, CultureInfo.InvariantCulture);
        if (t == typeof(int)) return int.Parse(val, CultureInfo.InvariantCulture);
        if (t == typeof(string)) return val;
        var v = val.ToLowerInvariant();
        return v is "1" or "true" or "yes" or "on";
    }

    private static int Watch(CliArgs a)
    {
        var repo = OpenRepo();
        var cfg = new ConfigStore().Current;
        int interval = a.IntOpt(new[] { "interval", "i" }, Math.Max(2, cfg.DashboardRefreshSeconds));
        bool stop = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop = true; };

        while (!stop)
        {
            try
            {
                Console.Clear();
                var nowUtc = DateTime.UtcNow;
                Out.Header($"Disk Activity Monitor — live  ({LocalTimeDisplay.FormatUtcWithZone(nowUtc, "HH:mm:ss")}, every {interval}s, Ctrl+C to exit)");
                Console.WriteLine();

                var disk = repo.GetDisks().FirstOrDefault(d => d.IsSsd) ?? repo.GetDisks().FirstOrDefault();
                if (disk is not null)
                {
                    var today = repo.GetDiskTotals(disk.DiskId, DateTime.Today.ToUniversalTime(), nowUtc).Write;
                    var hour = repo.GetDiskTotals(disk.DiskId, nowUtc.AddHours(-1), nowUtc).Write;
                    Console.WriteLine($"{disk.DisplayName}");
                    Console.WriteLine($"  written today: {ByteFormat.Humanize(today)}   last hour: {ByteFormat.Humanize(hour)}");
                    Console.WriteLine();
                }

                var procs = repo.GetTopProcesses(nowUtc.AddMinutes(-60), nowUtc, 8);
                var rows = procs.Select(p => (IReadOnlyList<string>)new[] { p.ProcessName, ByteFormat.Humanize(p.WriteBytes) }).ToList();
                Out.Table(new[] { "Top process (1h)", "Written" }, rows, new[] { false, true });

                var unacked = repo.GetRecentAlerts(500, unacknowledgedOnly: true).Count;
                if (unacked > 0) { Console.WriteLine(); Out.Error($"  {unacked} unacknowledged alert(s) — run 'dam alerts'"); }
            }
            catch { /* transient DB lock — try again next tick */ }

            for (int i = 0; i < interval * 10 && !stop; i++) Thread.Sleep(100);
        }
        return 0;
    }

    private static int Help(CliArgs a) { PrintUsage(); return 0; }

    private static int Version()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        Console.WriteLine($"dam (Disk Activity Monitor CLI) {v}");
        return 0;
    }

    private static void PrintUsage()
    {
        Out.Header("dam — Disk Activity Monitor CLI");
        Console.WriteLine(@"
Usage: dam <command> [options]

Status & data
  status                       Service/DB status, disk count, alerts, snoozes
  disks                        List monitored disks (media, size, SMART wear)
  summary [--disk ID] [--all]  Writes today / last 24h / last 7d
  top [--minutes N] [--count N]  Top processes by writes in a window (default 60m, 10)
  process <name>               Writes for a process across 1m/5m/15m/30m/1h/24h
  files <name> [--minutes N] [--count N]  Files a process wrote to (explains ""System"")
  trends [--range hour|day|week] [--count N] [--disk ID]   Write trend with bars
  endurance [--disk ID] [--all]  SSD TBW usage, SMART wear, projection
  watch [--interval N]         Live auto-refreshing dashboard (Ctrl+C to exit)

Alerts
  alerts [--all] [--count N] [--full]   List alerts (unacked by default)
  ack <id> [<id>...]           Acknowledge alerts by id
  ack --all                    Acknowledge all alerts

Snooze
  snooze list                  Show active snoozes
  snooze process <name> <dur>  Snooze a process (dur: 30m, 1h, 1d, 1w)
  snooze all <dur>             Snooze ALL alerts for a duration
  snooze clear <name>          Clear a process snooze
  snooze clear --global        Clear the global snooze
  snooze clear --all           Clear every snooze

Config
  config get [key]             Print all config, or one key
  config set <key> <value>     Change a threshold/setting (service reloads live)

  version                      Print version
  help                         Show this help");
    }
}
