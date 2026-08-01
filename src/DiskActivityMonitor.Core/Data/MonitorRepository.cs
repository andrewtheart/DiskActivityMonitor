using System.Text.Json;
using Microsoft.Data.Sqlite;
using DiskActivityMonitor.Core.Collection;
using DiskActivityMonitor.Core.Models;

namespace DiskActivityMonitor.Core.Data;

public sealed record SuspendedProcessState(
    string Name,
    DateTime SuspendedUtc,
    string? ExecutablePath,
    IReadOnlyList<ProcessControl.ProcessIdentity> ProcessIdentities);

/// <summary>
/// Thin SQLite repository shared by the collector (writer) and tray app (reader).
/// Uses WAL mode so a single writer and multiple readers coexist without blocking.
/// All timestamps are stored as Unix seconds (UTC).
/// </summary>
public sealed class MonitorRepository
{
    private readonly string _connectionString;
    private readonly bool _readOnly;

    public MonitorRepository(string? databasePath = null, bool readOnly = false)
    {
        Paths.EnsureCreated();
        _readOnly = readOnly;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath ?? Paths.DatabasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        };
        _connectionString = builder.ToString();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = _readOnly
            ? "PRAGMA query_only=ON; PRAGMA busy_timeout=5000;"
            // wal_autocheckpoint folds the WAL back into the main DB every ~1000 pages on write;
            // the collector also runs an explicit TRUNCATE checkpoint periodically (see Checkpoint).
            : "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA wal_autocheckpoint=1000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public void EnsureSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS disks(
                disk_id TEXT PRIMARY KEY,
                instance_name TEXT,
                friendly_name TEXT,
                volumes TEXT,
                media_type INTEGER,
                size_bytes INTEGER,
                serial TEXT,
                first_seen_utc INTEGER,
                last_seen_utc INTEGER
            );

            CREATE TABLE IF NOT EXISTS disk_minute(
                ts_min INTEGER NOT NULL,
                disk_id TEXT NOT NULL,
                read_bytes INTEGER NOT NULL,
                write_bytes INTEGER NOT NULL,
                PRIMARY KEY(ts_min, disk_id)
            );
            CREATE INDEX IF NOT EXISTS ix_disk_minute_disk_ts ON disk_minute(disk_id, ts_min);

            CREATE TABLE IF NOT EXISTS proc_minute(
                ts_min INTEGER NOT NULL,
                name TEXT NOT NULL,
                read_bytes INTEGER NOT NULL,
                write_bytes INTEGER NOT NULL,
                PRIMARY KEY(ts_min, name)
            );
            CREATE INDEX IF NOT EXISTS ix_proc_minute_ts ON proc_minute(ts_min);

            CREATE TABLE IF NOT EXISTS alerts(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts_utc INTEGER NOT NULL,
                severity INTEGER NOT NULL,
                rule_key TEXT NOT NULL,
                title TEXT NOT NULL,
                message TEXT NOT NULL,
                value REAL NOT NULL,
                threshold REAL NOT NULL,
                acknowledged INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_alerts_ts ON alerts(ts_utc);
            CREATE INDEX IF NOT EXISTS ix_alerts_rule ON alerts(rule_key, ts_utc);

            CREATE TABLE IF NOT EXISTS process_snoozes(
                name TEXT PRIMARY KEY,
                until_utc INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS global_snooze(
                id INTEGER PRIMARY KEY,
                until_utc INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS suspended_processes(
                name TEXT PRIMARY KEY,
                suspended_utc INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // Migrate older databases that predate later columns.
        EnsureColumn(conn, "disks", "wear_percent", "INTEGER");
        EnsureColumn(conn, "disks", "life_write_bytes", "INTEGER");
        EnsureColumn(conn, "disks", "life_read_bytes", "INTEGER");
        EnsureColumn(conn, "suspended_processes", "executable_path", "TEXT");
        EnsureColumn(conn, "suspended_processes", "process_identities", "TEXT");
    }

    private static void EnsureColumn(SqliteConnection conn, string table, string column, string definition)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table});";
        using (var r = check.ExecuteReader())
        {
            while (r.Read())
                if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return;
        }
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
    private static DateTime FromUnix(long s) => DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime;

    // ---------------------------------------------------------------- Disks

    public void UpsertDisks(IEnumerable<DiskInfo> disks)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO disks(disk_id, instance_name, friendly_name, volumes, media_type, size_bytes, serial, wear_percent, life_write_bytes, life_read_bytes, first_seen_utc, last_seen_utc)
            VALUES($id, $inst, $name, $vol, $media, $size, $serial, $wear, $lifew, $lifer, $now, $now)
            ON CONFLICT(disk_id) DO UPDATE SET
                instance_name = excluded.instance_name,
                friendly_name = excluded.friendly_name,
                volumes       = excluded.volumes,
                media_type    = excluded.media_type,
                size_bytes    = excluded.size_bytes,
                serial        = excluded.serial,
                wear_percent  = excluded.wear_percent,
                life_write_bytes = COALESCE(excluded.life_write_bytes, disks.life_write_bytes),
                life_read_bytes  = COALESCE(excluded.life_read_bytes, disks.life_read_bytes),
                last_seen_utc = excluded.last_seen_utc;
            """;
        var pId = cmd.CreateParameter(); pId.ParameterName = "$id"; cmd.Parameters.Add(pId);
        var pInst = cmd.CreateParameter(); pInst.ParameterName = "$inst"; cmd.Parameters.Add(pInst);
        var pName = cmd.CreateParameter(); pName.ParameterName = "$name"; cmd.Parameters.Add(pName);
        var pVol = cmd.CreateParameter(); pVol.ParameterName = "$vol"; cmd.Parameters.Add(pVol);
        var pMedia = cmd.CreateParameter(); pMedia.ParameterName = "$media"; cmd.Parameters.Add(pMedia);
        var pSize = cmd.CreateParameter(); pSize.ParameterName = "$size"; cmd.Parameters.Add(pSize);
        var pSerial = cmd.CreateParameter(); pSerial.ParameterName = "$serial"; cmd.Parameters.Add(pSerial);
        var pWear = cmd.CreateParameter(); pWear.ParameterName = "$wear"; cmd.Parameters.Add(pWear);
        var pLifeW = cmd.CreateParameter(); pLifeW.ParameterName = "$lifew"; cmd.Parameters.Add(pLifeW);
        var pLifeR = cmd.CreateParameter(); pLifeR.ParameterName = "$lifer"; cmd.Parameters.Add(pLifeR);
        var pNow = cmd.CreateParameter(); pNow.ParameterName = "$now"; cmd.Parameters.Add(pNow);
        pNow.Value = ToUnix(DateTime.UtcNow);

        foreach (var d in disks)
        {
            pId.Value = d.DiskId;
            pInst.Value = d.InstanceName;
            pName.Value = d.FriendlyName;
            pVol.Value = d.Volumes;
            pMedia.Value = (int)d.MediaType;
            pSize.Value = d.SizeBytes;
            pSerial.Value = d.SerialNumber;
            pWear.Value = (object?)d.WearPercent ?? DBNull.Value;
            pLifeW.Value = (object?)d.LifetimeBytesWritten ?? DBNull.Value;
            pLifeR.Value = (object?)d.LifetimeBytesRead ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<DiskInfo> GetDisks()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT disk_id, instance_name, friendly_name, volumes, media_type, size_bytes, serial, wear_percent, life_write_bytes, life_read_bytes FROM disks ORDER BY disk_id;";
        var list = new List<DiskInfo>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new DiskInfo
            {
                DiskId = r.GetString(0),
                InstanceName = r.IsDBNull(1) ? "" : r.GetString(1),
                FriendlyName = r.IsDBNull(2) ? "" : r.GetString(2),
                Volumes = r.IsDBNull(3) ? "" : r.GetString(3),
                MediaType = (DiskMediaType)(r.IsDBNull(4) ? 0 : r.GetInt32(4)),
                SizeBytes = r.IsDBNull(5) ? 0 : r.GetInt64(5),
                SerialNumber = r.IsDBNull(6) ? "" : r.GetString(6),
                WearPercent = r.IsDBNull(7) ? null : r.GetInt32(7),
                LifetimeBytesWritten = r.IsDBNull(8) ? null : r.GetInt64(8),
                LifetimeBytesRead = r.IsDBNull(9) ? null : r.GetInt64(9),
            });
        }
        return list;
    }

    // ---------------------------------------------------------------- Samples

    public void AddDiskSamples(IReadOnlyCollection<DiskSample> samples)
    {
        if (samples.Count == 0) return;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO disk_minute(ts_min, disk_id, read_bytes, write_bytes)
            VALUES($ts, $id, $r, $w)
            ON CONFLICT(ts_min, disk_id) DO UPDATE SET
                read_bytes  = read_bytes  + excluded.read_bytes,
                write_bytes = write_bytes + excluded.write_bytes;
            """;
        var pTs = cmd.CreateParameter(); pTs.ParameterName = "$ts"; cmd.Parameters.Add(pTs);
        var pId = cmd.CreateParameter(); pId.ParameterName = "$id"; cmd.Parameters.Add(pId);
        var pR = cmd.CreateParameter(); pR.ParameterName = "$r"; cmd.Parameters.Add(pR);
        var pW = cmd.CreateParameter(); pW.ParameterName = "$w"; cmd.Parameters.Add(pW);
        foreach (var s in samples)
        {
            pTs.Value = ToUnix(s.TimestampUtc);
            pId.Value = s.DiskId;
            pR.Value = s.ReadBytes;
            pW.Value = s.WriteBytes;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void AddProcessSamples(IReadOnlyCollection<ProcessIoSample> samples)
    {
        if (samples.Count == 0) return;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO proc_minute(ts_min, name, read_bytes, write_bytes)
            VALUES($ts, $name, $r, $w)
            ON CONFLICT(ts_min, name) DO UPDATE SET
                read_bytes  = read_bytes  + excluded.read_bytes,
                write_bytes = write_bytes + excluded.write_bytes;
            """;
        var pTs = cmd.CreateParameter(); pTs.ParameterName = "$ts"; cmd.Parameters.Add(pTs);
        var pName = cmd.CreateParameter(); pName.ParameterName = "$name"; cmd.Parameters.Add(pName);
        var pR = cmd.CreateParameter(); pR.ParameterName = "$r"; cmd.Parameters.Add(pR);
        var pW = cmd.CreateParameter(); pW.ParameterName = "$w"; cmd.Parameters.Add(pW);
        foreach (var s in samples)
        {
            pTs.Value = ToUnix(s.TimestampUtc);
            pName.Value = s.ProcessName;
            pR.Value = s.ReadBytes;
            pW.Value = s.WriteBytes;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ---------------------------------------------------------------- Trend queries

    /// <summary>
    /// Returns hour-aligned (UTC) read/write totals for one disk between two instants.
    /// Callers roll these into day/week buckets in local time.
    /// </summary>
    public List<(DateTime HourStartUtc, long Read, long Write)> GetHourlyDiskTotals(string diskId, DateTime fromUtc, DateTime toUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT (ts_min / 3600) * 3600 AS hour, SUM(read_bytes), SUM(write_bytes)
            FROM disk_minute
            WHERE disk_id = $id AND ts_min >= $from AND ts_min < $to
            GROUP BY hour
            ORDER BY hour;
            """;
        cmd.Parameters.AddWithValue("$id", diskId);
        cmd.Parameters.AddWithValue("$from", ToUnix(fromUtc));
        cmd.Parameters.AddWithValue("$to", ToUnix(toUtc));
        var list = new List<(DateTime, long, long)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((FromUnix(r.GetInt64(0)), r.GetInt64(1), r.GetInt64(2)));
        return list;
    }

    public (long Read, long Write) GetDiskTotals(string diskId, DateTime fromUtc, DateTime toUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(read_bytes),0), COALESCE(SUM(write_bytes),0)
            FROM disk_minute
            WHERE disk_id = $id AND ts_min >= $from AND ts_min < $to;
            """;
        cmd.Parameters.AddWithValue("$id", diskId);
        cmd.Parameters.AddWithValue("$from", ToUnix(fromUtc));
        cmd.Parameters.AddWithValue("$to", ToUnix(toUtc));
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt64(0), r.GetInt64(1));
    }

    /// <summary>
    /// Per-minute total I/O (read + write bytes) for a disk over a period. One entry per recorded
    /// minute; minutes with no activity are simply absent (callers treat them as zero).
    /// </summary>
    public List<long> GetDiskMinuteTotals(string diskId, DateTime fromUtc, DateTime toUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT read_bytes + write_bytes
            FROM disk_minute
            WHERE disk_id = $id AND ts_min >= $from AND ts_min < $to;
            """;
        cmd.Parameters.AddWithValue("$id", diskId);
        cmd.Parameters.AddWithValue("$from", ToUnix(fromUtc));
        cmd.Parameters.AddWithValue("$to", ToUnix(toUtc));
        var list = new List<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    /// <summary>Earliest minute recorded for a disk (used to scope "since monitoring began").</summary>
    public DateTime? GetEarliestSample(string diskId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MIN(ts_min) FROM disk_minute WHERE disk_id = $id;";
        cmd.Parameters.AddWithValue("$id", diskId);
        var val = cmd.ExecuteScalar();
        if (val is null || val is DBNull) return null;
        return FromUnix(Convert.ToInt64(val));
    }

    public List<ProcessRank> GetTopProcesses(DateTime fromUtc, DateTime toUtc, int topN)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT name, SUM(write_bytes) AS w, SUM(read_bytes) AS r
            FROM proc_minute
            WHERE ts_min >= $from AND ts_min < $to
            GROUP BY name
            ORDER BY w DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$from", ToUnix(fromUtc));
        cmd.Parameters.AddWithValue("$to", ToUnix(toUtc));
        cmd.Parameters.AddWithValue("$n", topN);
        var list = new List<ProcessRank>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ProcessRank { ProcessName = r.GetString(0), WriteBytes = r.GetInt64(1), ReadBytes = r.GetInt64(2) });
        return list;
    }

    /// <summary>Total write bytes attributed to one process within [fromUtc, toUtc).</summary>
    public long GetProcessWrite(string name, DateTime fromUtc, DateTime toUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(write_bytes), 0) FROM proc_minute WHERE name = $n AND ts_min >= $from AND ts_min < $to;";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$from", ToUnix(fromUtc));
        cmd.Parameters.AddWithValue("$to", ToUnix(toUtc));
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>Total write bytes across ALL processes within [fromUtc, toUtc).</summary>
    public long GetAllProcessesWrite(DateTime fromUtc, DateTime toUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(write_bytes), 0) FROM proc_minute WHERE ts_min >= $from AND ts_min < $to;";
        cmd.Parameters.AddWithValue("$from", ToUnix(fromUtc));
        cmd.Parameters.AddWithValue("$to", ToUnix(toUtc));
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    // ---------------------------------------------------------------- Alerts

    public long InsertAlert(AlertRecord alert)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO alerts(ts_utc, severity, rule_key, title, message, value, threshold, acknowledged)
            VALUES($ts, $sev, $rule, $title, $msg, $val, $thr, 0);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$ts", ToUnix(alert.TimestampUtc));
        cmd.Parameters.AddWithValue("$sev", (int)alert.Severity);
        cmd.Parameters.AddWithValue("$rule", alert.RuleKey);
        cmd.Parameters.AddWithValue("$title", alert.Title);
        cmd.Parameters.AddWithValue("$msg", alert.Message);
        cmd.Parameters.AddWithValue("$val", alert.Value);
        cmd.Parameters.AddWithValue("$thr", alert.Threshold);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public DateTime? GetLastAlertTime(string ruleKey)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(ts_utc) FROM alerts WHERE rule_key = $rule;";
        cmd.Parameters.AddWithValue("$rule", ruleKey);
        var val = cmd.ExecuteScalar();
        if (val is null || val is DBNull) return null;
        return FromUnix(Convert.ToInt64(val));
    }

    public List<AlertRecord> GetRecentAlerts(int limit, bool unacknowledgedOnly = false, DateTime? sinceUtc = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var clauses = new List<string>();
        if (unacknowledgedOnly) clauses.Add("acknowledged = 0");
        if (sinceUtc is not null) clauses.Add("ts_utc >= $since");
        var where = clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : "";
        cmd.CommandText = $"""
            SELECT id, ts_utc, severity, rule_key, title, message, value, threshold, acknowledged
            FROM alerts
            {where}
            ORDER BY ts_utc DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$n", limit);
        if (sinceUtc is not null) cmd.Parameters.AddWithValue("$since", ToUnix(sinceUtc.Value));
        var list = new List<AlertRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AlertRecord
            {
                Id = r.GetInt64(0),
                TimestampUtc = FromUnix(r.GetInt64(1)),
                Severity = (AlertSeverity)r.GetInt32(2),
                RuleKey = r.GetString(3),
                Title = r.GetString(4),
                Message = r.GetString(5),
                Value = r.GetDouble(6),
                Threshold = r.GetDouble(7),
                Acknowledged = r.GetInt32(8) != 0,
            });
        }
        return list;
    }

    public void AcknowledgeAlerts(IEnumerable<long>? ids = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        if (ids is null)
        {
            cmd.CommandText = "UPDATE alerts SET acknowledged = 1 WHERE acknowledged = 0;";
            cmd.ExecuteNonQuery();
            return;
        }
        cmd.CommandText = "UPDATE alerts SET acknowledged = 1 WHERE id = $id;";
        var p = cmd.CreateParameter(); p.ParameterName = "$id"; cmd.Parameters.Add(p);
        using var tx = conn.BeginTransaction();
        cmd.Transaction = tx;
        foreach (var id in ids) { p.Value = id; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    /// <summary>Permanently hides the selected alert records from the main Alert center.</summary>
    public void DismissAlerts(IEnumerable<long> ids) => AcknowledgeAlerts(ids);

    /// <summary>Restores dismissed alert records so they can appear in the main Alert center again.</summary>
    public void RestoreAlerts(IEnumerable<long> ids)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE alerts SET acknowledged = 0 WHERE id = $id;";
        var p = cmd.CreateParameter(); p.ParameterName = "$id"; cmd.Parameters.Add(p);
        using var tx = conn.BeginTransaction();
        cmd.Transaction = tx;
        foreach (var id in ids) { p.Value = id; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    /// <summary>Acknowledges all outstanding alerts raised for a specific process (rule proc-1h).</summary>
    public void AcknowledgeProcessAlerts(string processName)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE alerts SET acknowledged = 1 WHERE acknowledged = 0 AND rule_key = $rule;";
        cmd.Parameters.AddWithValue("$rule", $"proc-1h:{processName}");
        cmd.ExecuteNonQuery();
    }

    /// <summary>Acknowledges all outstanding alerts sharing a rule key (e.g. all repeats of one condition).</summary>
    public void AcknowledgeAlertsByRule(string ruleKey)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE alerts SET acknowledged = 1 WHERE acknowledged = 0 AND rule_key = $rule;";
        cmd.Parameters.AddWithValue("$rule", ruleKey);
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- Snoozes

    /// <summary>Suppresses per-process alerts for <paramref name="name"/> until <paramref name="untilUtc"/>.</summary>
    public void SnoozeProcess(string name, DateTime untilUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO process_snoozes(name, until_utc) VALUES($n, $u)
            ON CONFLICT(name) DO UPDATE SET until_utc = excluded.until_utc;
            """;
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$u", ToUnix(untilUtc));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Removes any snooze for a process (re-enables its alerts immediately).</summary>
    public void ClearProcessSnooze(string name)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM process_snoozes WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns the set of process names whose snooze is still active at <paramref name="nowUtc"/>.</summary>
    public HashSet<string> GetActiveProcessSnoozes(DateTime nowUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM process_snoozes WHERE until_utc > $now;";
        cmd.Parameters.AddWithValue("$now", ToUnix(nowUtc));
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    /// <summary>Active per-process snoozes with their expiry, for display.</summary>
    public List<(string Name, DateTime UntilUtc)> GetProcessSnoozes(DateTime nowUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, until_utc FROM process_snoozes WHERE until_utc > $now ORDER BY until_utc DESC;";
        cmd.Parameters.AddWithValue("$now", ToUnix(nowUtc));
        var list = new List<(string, DateTime)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), FromUnix(r.GetInt64(1))));
        return list;
    }

    /// <summary>Suppresses ALL alerts (every process and disk) until <paramref name="untilUtc"/>.</summary>
    public void SnoozeAllAlerts(DateTime untilUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO global_snooze(id, until_utc) VALUES(1, $u)
            ON CONFLICT(id) DO UPDATE SET until_utc = excluded.until_utc;
            """;
        cmd.Parameters.AddWithValue("$u", ToUnix(untilUtc));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Removes the global snooze (re-enables all alerts immediately).</summary>
    public void ClearGlobalSnooze()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM global_snooze WHERE id = 1;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>True when a global "snooze all alerts" is still in effect at <paramref name="nowUtc"/>.</summary>
    public bool IsGlobalSnoozeActive(DateTime nowUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM global_snooze WHERE id = 1 AND until_utc > $now;";
        cmd.Parameters.AddWithValue("$now", ToUnix(nowUtc));
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>The global snooze expiry if one is active at <paramref name="nowUtc"/>, else null.</summary>
    public DateTime? GetGlobalSnoozeUntil(DateTime nowUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT until_utc FROM global_snooze WHERE id = 1 AND until_utc > $now;";
        cmd.Parameters.AddWithValue("$now", ToUnix(nowUtc));
        var val = cmd.ExecuteScalar();
        return val is null or DBNull ? null : FromUnix(Convert.ToInt64(val));
    }

    // ---------------------------------------------------------------- Auto-suspend

    /// <summary>Distinct process names ever recorded, for populating the auto-suspend picker.</summary>
    public List<string> GetKnownProcessNames()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT name FROM proc_minute ORDER BY name COLLATE NOCASE;";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>Records the exact processes currently suspended by the app.</summary>
    public void AddSuspendedProcess(
        string name,
        DateTime suspendedUtc,
        string? executablePath = null,
        IReadOnlyList<ProcessControl.ProcessIdentity>? processIdentities = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO suspended_processes(
                name, suspended_utc, executable_path, process_identities)
            VALUES($n, $u, $p, $i)
            ON CONFLICT(name) DO UPDATE SET
                suspended_utc = excluded.suspended_utc,
                executable_path = excluded.executable_path,
                process_identities = excluded.process_identities;
            """;
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$u", ToUnix(suspendedUtc));
        cmd.Parameters.AddWithValue("$p", (object?)executablePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "$i",
            processIdentities is { Count: > 0 }
                ? JsonSerializer.Serialize(processIdentities)
                : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Clears the suspended record for a process (call after resuming it).</summary>
    public void RemoveSuspendedProcess(string name)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM suspended_processes WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Processes the app currently considers suspended, with the time each was suspended.</summary>
    public List<(string Name, DateTime SuspendedUtc)> GetSuspendedProcesses()
        => GetSuspendedProcessStates()
            .Select(state => (state.Name, state.SuspendedUtc))
            .ToList();

    public List<SuspendedProcessState> GetSuspendedProcessStates()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT name, suspended_utc, executable_path, process_identities
            FROM suspended_processes
            ORDER BY suspended_utc DESC;
            """;
        var list = new List<SuspendedProcessState>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SuspendedProcessState(
                r.GetString(0),
                FromUnix(r.GetInt64(1)),
                r.IsDBNull(2) ? null : r.GetString(2),
                DeserializeProcessIdentities(r.IsDBNull(3) ? null : r.GetString(3))));
        }
        return list;
    }

    public SuspendedProcessState? GetSuspendedProcessState(string name)
        => GetSuspendedProcessStates()
            .FirstOrDefault(state => string.Equals(state.Name, name, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<ProcessControl.ProcessIdentity> DeserializeProcessIdentities(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 64 * 1024)
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<ProcessControl.ProcessIdentity>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Set of process names the app currently considers suspended (case-insensitive).</summary>
    public HashSet<string> GetSuspendedProcessNames()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM suspended_processes;";
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    // ---------------------------------------------------------------- Maintenance

    /// <summary>
    /// Forces a write-ahead-log checkpoint, folding the WAL back into the main database file
    /// and truncating it. Because the repository pools connections, the WAL is never truncated
    /// by the usual "last connection closed" path, so this should be called periodically to keep
    /// the -wal file from growing. Best-effort: a concurrent reader/writer can make the
    /// checkpoint partial, in which case it simply runs again next time.
    /// </summary>
    public void Checkpoint()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }

    public int PruneOlderThan(DateTime cutoffUtc)
    {
        using var conn = Open();
        var cutoff = ToUnix(cutoffUtc);
        int removed = 0;
        foreach (var (table, col) in new[] { ("disk_minute", "ts_min"), ("proc_minute", "ts_min"), ("alerts", "ts_utc") })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {table} WHERE {col} < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            removed += cmd.ExecuteNonQuery();
        }
        return removed;
    }
}
