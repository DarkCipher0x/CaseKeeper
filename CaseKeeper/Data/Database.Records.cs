using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CaseKeeper.Models;
using Microsoft.Data.Sqlite;

namespace CaseKeeper.Data;

/// <summary>
/// A small typed record store for the case-management records (hearings, orders, directions,
/// contact sessions, costs, letters, templates and so on), plus the audit trail.
///
/// Each record class says which table it lives in. Tables are created on first use and gain new
/// columns automatically when a later version adds a property, so upgrading never needs a manual
/// migration. Every create, change and delete is written to the audit trail with what changed.
/// </summary>
public partial class Database
{
    /// <summary>Every record type the store manages. Tables for these are created at startup.</summary>
    public static readonly Type[] RecordTypes =
    {
        typeof(Hearing), typeof(CourtOrder), typeof(CaseAction), typeof(ContactSession), typeof(Expense),
        typeof(ScottItem), typeof(Letterhead), typeof(DocTemplate), typeof(LetterDraft), typeof(AiReport)
    };

    private sealed class EntityMap
    {
        public string Table, AuditKey, Label;
        public PropertyInfo IdProp;
        public List<PropertyInfo> Columns;
    }

    private static readonly ConcurrentDictionary<Type, EntityMap> Maps = new();

    private static EntityMap MapOf(Type t) => Maps.GetOrAdd(t, type =>
    {
        var attr = type.GetCustomAttribute<EntityAttribute>()
                   ?? throw new InvalidOperationException($"{type.Name} has no [Entity] attribute.");
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.Name != "Id" && p.GetCustomAttribute<NotStoredAttribute>() == null)
            .Where(p => SqlType(p.PropertyType) != null)
            .ToList();
        return new EntityMap
        {
            Table = attr.Table, AuditKey = attr.AuditKey, Label = attr.Label,
            IdProp = type.GetProperty("Id"), Columns = props
        };
    });

    private static string SqlType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(long) || t == typeof(int) || t == typeof(bool)) return "INTEGER";
        if (t == typeof(double) || t == typeof(decimal)) return "REAL";
        if (t == typeof(byte[])) return "BLOB";
        if (t == typeof(string) || t == typeof(DateTime)) return "TEXT";
        return null;
    }

    /// <summary>Timestamps (properties ending "At") keep the time; other dates are stored as plain days.</summary>
    private static bool IsTimestamp(PropertyInfo p) => p.Name.EndsWith("At", StringComparison.Ordinal);

    private static object ToDb(PropertyInfo p, object value)
    {
        switch (value)
        {
            case null: return DBNull.Value;
            case bool b: return b ? 1 : 0;
            case DateTime d: return IsTimestamp(p) ? d.ToString("o", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case decimal m: return (double)m;
            default: return value;
        }
    }

    private static object FromDb(PropertyInfo p, object raw)
    {
        var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        bool nullable = Nullable.GetUnderlyingType(p.PropertyType) != null || !p.PropertyType.IsValueType;
        if (raw == null || raw is DBNull)
            return nullable ? null : t == typeof(string) ? "" : Activator.CreateInstance(t);
        try
        {
            if (t == typeof(string)) return Convert.ToString(raw, CultureInfo.InvariantCulture);
            if (t == typeof(long)) return Convert.ToInt64(raw, CultureInfo.InvariantCulture);
            if (t == typeof(int)) return Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            if (t == typeof(bool)) return Convert.ToInt64(raw, CultureInfo.InvariantCulture) != 0;
            if (t == typeof(double)) return Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            if (t == typeof(decimal)) return Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
            if (t == typeof(byte[])) return raw as byte[];
            if (t == typeof(DateTime))
            {
                var s = Convert.ToString(raw, CultureInfo.InvariantCulture);
                if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)) return d;
                return nullable ? null : DateTime.MinValue;
            }
        }
        catch { /* a damaged value falls through to the default */ }
        return nullable ? null : Activator.CreateInstance(t);
    }

    /// <summary>Creates the record tables, adding any columns a newer version has introduced.</summary>
    private void EnsureRecordTables(SqliteConnection c)
    {
        foreach (var type in RecordTypes)
        {
            var map = MapOf(type);
            var cols = string.Join(", ", map.Columns.Select(p => $"[{p.Name}] {SqlType(p.PropertyType)}"));
            Exec(c, $"CREATE TABLE IF NOT EXISTS [{map.Table}](Id INTEGER PRIMARY KEY AUTOINCREMENT, {cols});");
            var existing = ColumnsOf(c, map.Table);
            foreach (var p in map.Columns.Where(p => !existing.Contains(p.Name)))
                Exec(c, $"ALTER TABLE [{map.Table}] ADD COLUMN [{p.Name}] {SqlType(p.PropertyType)}");
        }
    }

    private static HashSet<string> ColumnsOf(SqliteConnection c, string table)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    private void AddColumnIfMissing(SqliteConnection c, string table, string column, string type)
    {
        if (!ColumnsOf(c, table).Contains(column)) Exec(c, $"ALTER TABLE [{table}] ADD COLUMN [{column}] {type}");
    }

    private static T ReadEntity<T>(SqliteDataReader r, EntityMap map) where T : IEntity, new()
    {
        var e = new T { Id = r.GetInt64(r.GetOrdinal("Id")) };
        for (int i = 0; i < r.FieldCount; i++)
        {
            var name = r.GetName(i);
            var p = map.Columns.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (p != null) p.SetValue(e, FromDb(p, r.GetValue(i)));
        }
        return e;
    }

    // ------------------------------------------------------------------ public record API
    public List<T> All<T>() where T : IEntity, new()
    {
        var map = MapOf(typeof(T));
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT * FROM [{map.Table}] ORDER BY Id";
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(ReadEntity<T>(r, map));
        return list;
    }

    public T Find<T>(long id) where T : IEntity, new()
    {
        var map = MapOf(typeof(T));
        using var c = Open();
        return Find<T>(c, map, id);
    }

    private static T Find<T>(SqliteConnection c, EntityMap map, long id) where T : IEntity, new()
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT * FROM [{map.Table}] WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadEntity<T>(r, map) : default;
    }

    /// <summary>Inserts a new record (Id 0) or updates an existing one, and audits exactly what changed.</summary>
    public void Save<T>(T e) where T : IEntity, new()
    {
        var map = MapOf(typeof(T));
        using var c = Open();
        using var tx = c.BeginTransaction();
        if (e.Id == 0)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"INSERT INTO [{map.Table}]({string.Join(",", map.Columns.Select(p => $"[{p.Name}]"))}) " +
                              $"VALUES({string.Join(",", map.Columns.Select((_, i) => "$p" + i))}); SELECT last_insert_rowid();";
            for (int i = 0; i < map.Columns.Count; i++)
                cmd.Parameters.AddWithValue("$p" + i, ToDb(map.Columns[i], map.Columns[i].GetValue(e)));
            e.Id = (long)cmd.ExecuteScalar();
            AuditLocked(c, $"{map.AuditKey}.create", map.Label, e.Id, $"Added {map.Label}: {e.AuditName}",
                Describe(map, default(T), e));
        }
        else
        {
            var before = Find<T>(c, map, e.Id);
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"UPDATE [{map.Table}] SET {string.Join(",", map.Columns.Select((p, i) => $"[{p.Name}]=$p{i}"))} WHERE Id=$id";
            for (int i = 0; i < map.Columns.Count; i++)
                cmd.Parameters.AddWithValue("$p" + i, ToDb(map.Columns[i], map.Columns[i].GetValue(e)));
            cmd.Parameters.AddWithValue("$id", e.Id);
            cmd.ExecuteNonQuery();
            var diff = Describe(map, before, e);
            if (diff.Length > 0)
                AuditLocked(c, $"{map.AuditKey}.update", map.Label, e.Id, $"Changed {map.Label}: {e.AuditName}", diff);
        }
        tx.Commit();
    }

    public void Remove<T>(long id) where T : IEntity, new()
    {
        var map = MapOf(typeof(T));
        using var c = Open();
        using var tx = c.BeginTransaction();
        var before = Find<T>(c, map, id);
        if (before == null) return;
        Exec(c, $"DELETE FROM [{map.Table}] WHERE Id=$id", ("$id", id));
        AuditLocked(c, $"{map.AuditKey}.delete", map.Label, id, $"Deleted {map.Label}: {before.AuditName}",
            Describe(map, before, default(T)));
        tx.Commit();
    }

    /// <summary>"Field: old → new" for each changed property. Binary content is summarised, not dumped.</summary>
    private static string Describe<T>(EntityMap map, T before, T after) where T : IEntity
    {
        var lines = new List<string>();
        foreach (var p in map.Columns)
        {
            if (p.Name == "UpdatedAt") continue;
            var a = before == null ? null : p.GetValue(before);
            var b = after == null ? null : p.GetValue(after);
            if (p.PropertyType == typeof(byte[]))
            {
                int la = (a as byte[])?.Length ?? 0, lb = (b as byte[])?.Length ?? 0;
                if (la != lb || (la > 0 && !((byte[])a).AsSpan().SequenceEqual((byte[])b)))
                    lines.Add($"{p.Name}: {(la == 0 ? "none" : $"{la:N0} bytes")} → {(lb == 0 ? "none" : $"{lb:N0} bytes")}");
                continue;
            }
            var sa = Show(p, a);
            var sb = Show(p, b);
            if (sa == sb) continue;
            if (before == null && sb.Length == 0) continue;     // don't list empty fields on create
            if (after == null && sa.Length == 0) continue;
            lines.Add(before == null ? $"{p.Name}: {Clip(sb)}"
                : after == null ? $"{p.Name}: {Clip(sa)}"
                : $"{p.Name}: {Clip(sa)} → {Clip(sb)}");
        }
        return string.Join("\n", lines);
    }

    private static string Show(PropertyInfo p, object v) => v switch
    {
        null => "",
        DateTime d => IsTimestamp(p) ? d.ToString("dd MMM yyyy HH:mm") : d.ToString("dd MMM yyyy"),
        bool b => b ? "yes" : "no",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
    };

    private static string Clip(string s)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ⏎ ");
        return s.Length > 400 ? s[..400] + "…" : s;
    }

    // ================================================================== audit trail
    private static readonly object AuditGate = new();

    /// <summary>
    /// Who made a change, as far as a shared library needs to know: the user's own label for this
    /// PC (set in Settings), or a short code derived from the computer so nothing personal is stored.
    /// </summary>
    public static string Station { get; set; } = DefaultStation();

    public static string DefaultStation()
    {
        try
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("casekeeper-station:" + Environment.MachineName));
            return "PC-" + Convert.ToHexString(hash)[..6];
        }
        catch { return "PC"; }
    }

    private void EnsureAuditTable(SqliteConnection c) => Exec(c, @"
CREATE TABLE IF NOT EXISTS AuditLog(Id INTEGER PRIMARY KEY AUTOINCREMENT, At TEXT NOT NULL, Station TEXT, Action TEXT NOT NULL,
  EntityType TEXT, EntityId INTEGER, Summary TEXT, Details TEXT, PrevHash TEXT, Hash TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS IX_AuditLog_Action ON AuditLog(Action);
CREATE INDEX IF NOT EXISTS IX_AuditLog_Entity ON AuditLog(EntityType, EntityId);");

    /// <summary>Records something that happened. Never throws: a failed audit write is logged instead.</summary>
    public void Audit(string action, string entityType, long? entityId, string summary, string details = "")
    {
        try
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            AuditLocked(c, action, entityType, entityId, summary, details);
            tx.Commit();
        }
        catch (Exception ex)
        {
            Services.AppLog.Error("Audit", $"Couldn't write audit entry '{action}'", ex);
        }
    }

    /// <summary>
    /// Appends to the hash chain on a connection the caller owns. Each entry's fingerprint covers
    /// the previous one, so editing or deleting any past entry breaks every fingerprint after it.
    /// </summary>
    private void AuditLocked(SqliteConnection c, string action, string entityType, long? entityId, string summary, string details)
    {
        lock (AuditGate)
        {
            string prev;
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT Hash FROM AuditLog ORDER BY Id DESC LIMIT 1";
                prev = cmd.ExecuteScalar() as string ?? "";
            }
            var at = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var hash = ChainHash(prev, at, Station, action, entityType, entityId, summary, details);
            Exec(c, @"INSERT INTO AuditLog(At,Station,Action,EntityType,EntityId,Summary,Details,PrevHash,Hash)
                      VALUES($at,$st,$a,$et,$eid,$s,$d,$p,$h)",
                ("$at", at), ("$st", Station), ("$a", action), ("$et", entityType ?? ""), ("$eid", entityId),
                ("$s", summary ?? ""), ("$d", details ?? ""), ("$p", prev), ("$h", hash));
        }
    }

    private static string ChainHash(string prev, string at, string station, string action, string entityType,
        long? entityId, string summary, string details)
    {
        var text = string.Join("\u001f", prev, at, station ?? "", action, entityType ?? "",
            entityId?.ToString(CultureInfo.InvariantCulture) ?? "", summary ?? "", details ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>True if the same thing was audited recently, so typing a note doesn't log every pause.</summary>
    private static bool AuditedRecently(SqliteConnection c, string action, string entityType, long entityId, TimeSpan window)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT At FROM AuditLog WHERE Action=$a AND EntityType=$t AND EntityId=$id ORDER BY Id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$a", action);
        cmd.Parameters.AddWithValue("$t", entityType);
        cmd.Parameters.AddWithValue("$id", entityId);
        return cmd.ExecuteScalar() is string s
               && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
               && DateTime.UtcNow - at.ToUniversalTime() < window;
    }

    public List<AuditEntry> GetAudit(int limit = 5000, string entityType = null, long? entityId = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = entityType != null ? "WHERE EntityType=$t" + (entityId.HasValue ? " AND EntityId=$id" : "") : "";
        cmd.CommandText = $"SELECT * FROM AuditLog {where} ORDER BY Id DESC LIMIT $lim";
        cmd.Parameters.AddWithValue("$lim", limit);
        if (entityType != null) cmd.Parameters.AddWithValue("$t", entityType);
        if (entityId.HasValue) cmd.Parameters.AddWithValue("$id", entityId.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<AuditEntry>();
        while (r.Read()) list.Add(ReadAudit(r));
        return list;
    }

    private static AuditEntry ReadAudit(SqliteDataReader r) => new()
    {
        Id = (long)r["Id"],
        At = DateTime.TryParse(r["At"] as string, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : DateTime.MinValue,
        Station = r["Station"] as string ?? "", Action = r["Action"] as string ?? "", EntityType = r["EntityType"] as string ?? "",
        EntityId = r["EntityId"] is long l ? l : null, Summary = r["Summary"] as string ?? "", Details = r["Details"] as string ?? "",
        PrevHash = r["PrevHash"] as string ?? "", Hash = r["Hash"] as string ?? ""
    };

    public int AuditCount()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM AuditLog";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Recomputes the whole chain from the first entry, stopping at the first one that doesn't match.</summary>
    public AuditVerification VerifyAudit()
    {
        var result = new AuditVerification { Ok = true };
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM AuditLog ORDER BY Id";
        using var r = cmd.ExecuteReader();
        string prev = "";
        while (r.Read())
        {
            var e = ReadAudit(r);
            var at = r["At"] as string ?? "";
            var expected = ChainHash(prev, at, e.Station, e.Action, e.EntityType, e.EntityId, e.Summary, e.Details);
            result.Checked++;
            if (e.PrevHash != prev || e.Hash != expected)
            {
                result.Ok = false;
                result.FirstBrokenId = e.Id;
                return result;
            }
            prev = e.Hash;
        }
        result.LastHash = prev;
        return result;
    }

    // ------------------------------------------------------------------ helpers used by the audit of older tables
    private static string Diff(params (string Field, object Before, object After)[] fields)
    {
        static string S(object o) => o switch
        {
            null => "",
            DateTime d => d.ToString("dd MMM yyyy"),
            bool b => b ? "yes" : "no",
            _ => Convert.ToString(o, CultureInfo.InvariantCulture) ?? ""
        };
        return string.Join("\n", fields.Where(f => S(f.Before) != S(f.After))
            .Select(f => $"{f.Field}: {Clip(S(f.Before))} → {Clip(S(f.After))}"));
    }

    private string CategoryLabel(SqliteConnection c, long id)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT CASE WHEN Code<>'' THEN Code || ' – ' || Name ELSE Name END FROM Categories WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string ?? $"#{id}";
    }

    private string PartyLabel(SqliteConnection c, long? id)
    {
        if (id == null) return "";
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Role || CASE WHEN Name<>'' THEN ' – ' || Name ELSE '' END FROM Parties WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id.Value);
        return cmd.ExecuteScalar() as string ?? $"#{id}";
    }

    // ================================================================== merging records from a backup
    private static readonly HashSet<string> LinkColumns = new(StringComparer.OrdinalIgnoreCase)
        { "DocumentId", "EventId", "OrderId", "HearingId", "TemplateId", "LetterheadId" };

    /// <summary>
    /// Copies hearings, orders, directions and the other records across from another library,
    /// skipping any that are already here with identical content. Links to documents follow the
    /// documents that were brought in; other links are dropped rather than pointing at the wrong thing.
    /// </summary>
    private void MergeRecords(SqliteConnection c, SqliteConnection src, Dictionary<long, long> docMap, MergeResult result)
    {
        foreach (var type in RecordTypes)
        {
            var map = MapOf(type);
            using (var exists = src.CreateCommand())
            {
                exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n";
                exists.Parameters.AddWithValue("$n", map.Table);
                if (Convert.ToInt32(exists.ExecuteScalar()) == 0) continue;
            }
            var contentCols = map.Columns.Where(p => !LinkColumns.Contains(p.Name) && p.PropertyType != typeof(byte[])).ToList();
            string Key(object e) => string.Join("\u001f", contentCols.Select(p => Show(p, p.GetValue(e))));

            var have = new HashSet<string>();
            foreach (var e in ReadAll(c, type, map)) have.Add(Key(e));
            foreach (var e in ReadAll(src, type, map))
            {
                if (!have.Add(Key(e))) continue;
                var cols = map.Columns;
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"INSERT INTO [{map.Table}]({string.Join(",", cols.Select(p => $"[{p.Name}]"))}) " +
                                  $"VALUES({string.Join(",", cols.Select((_, i) => "$p" + i))})";
                for (int i = 0; i < cols.Count; i++)
                {
                    var p = cols[i];
                    object v = p.GetValue(e);
                    if (LinkColumns.Contains(p.Name))
                        v = p.Name == "DocumentId" && v is long old && docMap.TryGetValue(old, out var mapped) ? mapped : null;
                    cmd.Parameters.AddWithValue("$p" + i, ToDb(p, v));
                }
                cmd.ExecuteNonQuery();
                result.RecordsAdded++;
            }
        }

        // Settings this library doesn't have yet (case details, prompts, preferences) come across too.
        // Anything already set here is kept, so a merge never overwrites your own choices.
        using (var exists = src.CreateCommand())
        {
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Settings'";
            if (Convert.ToInt32(exists.ExecuteScalar()) == 0) return;
        }
        using var read = src.CreateCommand();
        read.CommandText = "SELECT Key, Value FROM Settings";
        using var r = read.ExecuteReader();
        while (r.Read())
        {
            var key = r.GetString(0);
            if (key is "onboarded" or "lastBackup" or "station.label" or "ai.apiKey") continue;
            // Never take another library's AI service, keys or consent: where case material goes is chosen here, by hand
            if (key is "ai.provider" or "bulk.provider" || key.StartsWith("ai.cloud.", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("bulk.cloudReview.", StringComparison.OrdinalIgnoreCase)
                || (key.StartsWith("ai.", StringComparison.OrdinalIgnoreCase) && key.EndsWith(".key", StringComparison.OrdinalIgnoreCase))
                || key.StartsWith("ai.anthropic", StringComparison.OrdinalIgnoreCase) || key.StartsWith("ai.openai", StringComparison.OrdinalIgnoreCase)) continue;
            Exec(c, "INSERT OR IGNORE INTO Settings(Key,Value) VALUES($k,$v)", ("$k", key), ("$v", r[1] as string ?? ""));
        }
    }

    private static List<object> ReadAll(SqliteConnection conn, Type type, EntityMap map)
    {
        var list = new List<object>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM [{map.Table}]";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var e = (IEntity)Activator.CreateInstance(type)!;
            e.Id = r.GetInt64(r.GetOrdinal("Id"));
            for (int i = 0; i < r.FieldCount; i++)
            {
                var p = map.Columns.FirstOrDefault(x => x.Name.Equals(r.GetName(i), StringComparison.OrdinalIgnoreCase));
                if (p != null) p.SetValue(e, FromDb(p, r.GetValue(i)));
            }
            list.Add(e);
        }
        return list;
    }

    // ================================================================== health
    /// <summary>SQLite's own consistency check. Returns "ok" when the database is healthy.</summary>
    public string QuickCheck()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check";
        return cmd.ExecuteScalar() as string ?? "unknown";
    }

    /// <summary>
    /// Writes a consistent copy of the live database to <paramref name="destPath"/> using SQLite's
    /// online backup. The copy is encrypted with the same key as the library when it is protected.
    /// </summary>
    public void SnapshotTo(string destPath, string password)
    {
        using var src = Open();
        var builder = new SqliteConnectionStringBuilder { DataSource = destPath, Pooling = false };
        if (!string.IsNullOrEmpty(password)) builder.Password = password;
        using var dst = new SqliteConnection(builder.ToString());
        dst.Open();
        src.BackupDatabase(dst);
    }
}
