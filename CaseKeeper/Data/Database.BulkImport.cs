using CaseKeeper.Models;
using Microsoft.Data.Sqlite;

namespace CaseKeeper.Data;

/// <summary>
/// Storage for Bulk Case Import: jobs, the files found in each job, and AI findings waiting for review.
///
/// These are working records, so they are saved quietly (no audit entry for every progress update).
/// What matters is audited by the service that uses them: starting a job, importing a document,
/// and every approval, rejection and undo. The tables are created and extended the same way as the
/// other record tables, so an older library simply gains them on first start, and they live in the
/// same (optionally encrypted) database, so backups, snapshots and protection cover them too.
/// </summary>
public partial class Database
{
    /// <summary>Bulk-import working tables. Kept out of <see cref="RecordTypes"/> so they are never merged between libraries.</summary>
    public static readonly Type[] StagingTypes = { typeof(ImportJob), typeof(ImportFile), typeof(AiFinding), typeof(CaseMemoryEntry), typeof(CaseLearning) };

    private void EnsureBulkImportTables(SqliteConnection c)
    {
        foreach (var type in StagingTypes)
        {
            var map = MapOf(type);
            var cols = string.Join(", ", map.Columns.Select(p => $"[{p.Name}] {SqlType(p.PropertyType)}"));
            Exec(c, $"CREATE TABLE IF NOT EXISTS [{map.Table}](Id INTEGER PRIMARY KEY AUTOINCREMENT, {cols});");
            var existing = ColumnsOf(c, map.Table);
            foreach (var p in map.Columns.Where(p => !existing.Contains(p.Name)))
                Exec(c, $"ALTER TABLE [{map.Table}] ADD COLUMN [{p.Name}] {SqlType(p.PropertyType)}");
        }
        Exec(c, @"CREATE INDEX IF NOT EXISTS IX_ImportFiles_Job ON ImportFiles(JobId);
                  CREATE INDEX IF NOT EXISTS IX_ImportFiles_Doc ON ImportFiles(DocumentId);
                  CREATE INDEX IF NOT EXISTS IX_AiFindings_Doc ON AiFindings(DocumentId);
                  CREATE INDEX IF NOT EXISTS IX_AiFindings_Status ON AiFindings(Status);
                  CREATE INDEX IF NOT EXISTS IX_AiFindings_Fingerprint ON AiFindings(Fingerprint);");
    }

    // ------------------------------------------------------------------ quiet (unaudited) access for working records
    private static void RequireStaging(Type t)
    {
        if (!StagingTypes.Contains(t))
            throw new InvalidOperationException($"{t.Name} is a case record: save it with Save() so the change is audited.");
    }

    /// <summary>Reads working records matching a SQL condition, e.g. <c>Query&lt;ImportFile&gt;("JobId=$j", ("$j", 4))</c>.</summary>
    public List<T> Query<T>(string where, params (string, object)[] args) where T : IEntity, new()
    {
        var map = MapOf(typeof(T));
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT * FROM [{map.Table}]" + (string.IsNullOrWhiteSpace(where) ? "" : " WHERE " + where) + " ORDER BY Id";
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(ReadEntity<T>(r, map));
        return list;
    }

    /// <summary>Saves a bulk-import working record without an audit entry. Refuses case records.</summary>
    public void SaveQuiet<T>(T e) where T : IEntity, new() => SaveQuietBatch(new[] { e });

    /// <summary>Saves many working records in one transaction (e.g. thousands of scanned files).</summary>
    public void SaveQuietBatch<T>(IEnumerable<T> items) where T : IEntity, new()
    {
        RequireStaging(typeof(T));
        var map = MapOf(typeof(T));
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var e in items)
        {
            using var cmd = c.CreateCommand();
            if (e.Id == 0)
            {
                cmd.CommandText = $"INSERT INTO [{map.Table}]({string.Join(",", map.Columns.Select(p => $"[{p.Name}]"))}) " +
                                  $"VALUES({string.Join(",", map.Columns.Select((_, i) => "$p" + i))}); SELECT last_insert_rowid();";
                for (int i = 0; i < map.Columns.Count; i++)
                    cmd.Parameters.AddWithValue("$p" + i, ToDb(map.Columns[i], map.Columns[i].GetValue(e)));
                e.Id = (long)cmd.ExecuteScalar();
            }
            else
            {
                cmd.CommandText = $"UPDATE [{map.Table}] SET {string.Join(",", map.Columns.Select((p, i) => $"[{p.Name}]=$p{i}"))} WHERE Id=$id";
                for (int i = 0; i < map.Columns.Count; i++)
                    cmd.Parameters.AddWithValue("$p" + i, ToDb(map.Columns[i], map.Columns[i].GetValue(e)));
                cmd.Parameters.AddWithValue("$id", e.Id);
                cmd.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    /// <summary>Removes working records matching a condition. Never touches case records.</summary>
    public int DeleteQuiet<T>(string where, params (string, object)[] args) where T : IEntity, new()
    {
        RequireStaging(typeof(T));
        var map = MapOf(typeof(T));
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"DELETE FROM [{map.Table}] WHERE {where}";
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ lookups the import needs
    /// <summary>The fingerprint of every document still in the library, so duplicates are found by content, not name.</summary>
    public Dictionary<string, long> GetDocumentHashes()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT FileHash, Id FROM Documents WHERE DeletedAt IS NULL AND FileHash IS NOT NULL AND FileHash<>'' ORDER BY Id";
        using var r = cmd.ExecuteReader();
        var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        while (r.Read()) d.TryAdd(r.GetString(0), r.GetInt64(1));
        return d;
    }

    /// <summary>Counts of each file status in a job, for progress and the summary.</summary>
    public Dictionary<string, int> CountImportFiles(long jobId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT 's'||Status, COUNT(*) FROM ImportFiles WHERE JobId=$j GROUP BY Status
                            UNION ALL SELECT 'a'||AnalysisStatus, COUNT(*) FROM ImportFiles WHERE JobId=$j AND DocumentId IS NOT NULL GROUP BY AnalysisStatus
                            UNION ALL SELECT 't'||TextStatus, COUNT(*) FROM ImportFiles WHERE JobId=$j AND DocumentId IS NOT NULL GROUP BY TextStatus
                            UNION ALL SELECT 'included', COUNT(*) FROM ImportFiles WHERE JobId=$j AND Included=1";
        cmd.Parameters.AddWithValue("$j", jobId);
        using var r = cmd.ExecuteReader();
        var d = new Dictionary<string, int>();
        while (r.Read()) d[r.GetString(0)] = r.GetInt32(1);
        return d;
    }

    /// <summary>
    /// Health of the bulk-import records, for the library integrity check: findings or files that point at a
    /// document which no longer exists. These are harmless (they are skipped) but are reported and tidied.
    /// </summary>
    public (int OrphanFindings, int OrphanFiles) CheckStaging(bool tidy)
    {
        using var c = Open();
        int Count(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        const string orphanFindings = "FROM AiFindings WHERE NOT EXISTS (SELECT 1 FROM Documents d WHERE d.Id=AiFindings.DocumentId)";
        const string orphanFiles = "FROM ImportFiles WHERE DocumentId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM Documents d WHERE d.Id=ImportFiles.DocumentId)";
        var result = (Count("SELECT COUNT(*) " + orphanFindings), Count("SELECT COUNT(*) " + orphanFiles));
        if (tidy && (result.Item1 > 0 || result.Item2 > 0))
        {
            Exec(c, "DELETE " + orphanFindings);
            Exec(c, "UPDATE ImportFiles SET DocumentId=NULL WHERE DocumentId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM Documents d WHERE d.Id=ImportFiles.DocumentId)");
        }
        return result;
    }

    /// <summary>
    /// Stores a fresh analysis of one document in a single transaction. Undecided suggestions from an earlier
    /// analysis are marked as replaced (kept, not deleted); a new suggestion matching one the person already
    /// approved or rejected is not added again. Returns how many were added.
    /// </summary>
    public int ReplaceFindings(long documentId, IReadOnlyList<AiFinding> fresh) => ReplaceFindings(documentId, fresh, null);

    /// <summary>
    /// As above, but only for the given kinds (a Library check that fills some sections): undecided suggestions of
    /// other kinds are left exactly as they were.
    /// </summary>
    public int ReplaceFindings(long documentId, IReadOnlyList<AiFinding> fresh, IReadOnlyCollection<string> kinds)
    {
        var map = MapOf(typeof(AiFinding));
        using var c = Open();
        using var tx = c.BeginTransaction();
        var decided = new HashSet<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT Fingerprint FROM AiFindings WHERE DocumentId=$d AND Status IN (1,2,4)";
            cmd.Parameters.AddWithValue("$d", documentId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) decided.Add(r[0] as string ?? "");
        }
        if (kinds == null)
            Exec(c, "UPDATE AiFindings SET Status=$s WHERE DocumentId=$d AND Status IN (0,3) AND Kind<>$k",
                ("$s", (int)FindingStatus.Superseded), ("$d", documentId), ("$k", FindingKinds.Conflict));
        else
        {
            var scoped = kinds.Where(k => k != FindingKinds.Conflict).ToList();
            if (scoped.Count > 0)
            {
                var args = new List<(string, object)> { ("$s", (int)FindingStatus.Superseded), ("$d", documentId) };
                args.AddRange(scoped.Select((k, i) => ("$k" + i, (object)k)));
                Exec(c, $"UPDATE AiFindings SET Status=$s WHERE DocumentId=$d AND Status IN (0,3) AND Kind IN ({string.Join(",", scoped.Select((_, i) => "$k" + i))})", args.ToArray());
            }
        }
        int added = 0;
        foreach (var f in fresh)
        {
            if (decided.Contains(f.Fingerprint)) continue;
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"INSERT INTO [{map.Table}]({string.Join(",", map.Columns.Select(p => $"[{p.Name}]"))}) " +
                              $"VALUES({string.Join(",", map.Columns.Select((_, i) => "$p" + i))}); SELECT last_insert_rowid();";
            for (int i = 0; i < map.Columns.Count; i++)
                cmd.Parameters.AddWithValue("$p" + i, ToDb(map.Columns[i], map.Columns[i].GetValue(f)));
            f.Id = (long)cmd.ExecuteScalar();
            added++;
        }
        tx.Commit();
        return added;
    }

    /// <summary>
    /// Saves only the cross-check notes of findings that are still undecided. A decision the person makes while the
    /// checks run is never overwritten by the checks' older copy of the row.
    /// </summary>
    public void SaveCheckNotes(IEnumerable<AiFinding> findings)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var f in findings)
            Exec(c, @"UPDATE AiFindings SET CheckNotes=$n, ConflictGroup=$g, MatchedRecordType=$t, MatchedRecordId=$m
                      WHERE Id=$id AND Status IN (0,3)",
                ("$n", f.CheckNotes ?? ""), ("$g", f.ConflictGroup ?? ""), ("$t", f.MatchedRecordType ?? ""), ("$m", f.MatchedRecordId), ("$id", f.Id));
        tx.Commit();
    }

    /// <summary>Adds findings without replacing anything already there (used for rule-based fallbacks).</summary>
    public void AddFindings(IEnumerable<AiFinding> findings) => SaveQuietBatch(findings.Where(f => f.Id == 0));

    /// <summary>Every document still in the library with this fingerprint, oldest first.</summary>
    public List<CaseDocument> FindAllByHash(string hash) =>
        QueryDocuments("WHERE FileHash=$h AND DeletedAt IS NULL", "Id", ("$h", hash));

    /// <summary>
    /// Approvals that were interrupted part-way (CaseKeeper closed between adding the record and recording the
    /// decision) go back to the review list with a warning to check the case before approving again.
    /// </summary>
    public int RecoverInterruptedApprovals()
    {
        var stuck = Query<AiFinding>("Status=$s", ("$s", (int)FindingStatus.Applying));
        foreach (var f in stuck)
        {
            f.StatusValue = FindingStatus.Proposed;
            f.Warnings = string.Join("\n", new[] { f.Warnings, "An approval of this was interrupted. Check your case doesn't already have it before approving again." }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        if (stuck.Count > 0)
        {
            SaveQuietBatch(stuck);
            Audit("ai.recover", "AI finding", null, $"{stuck.Count} interrupted approval(s) returned to the review list", "");
        }
        return stuck.Count;
    }

    /// <summary>When a document is purged for good, its pending suggestions go with it.</summary>
    private void PurgeStagingFor(SqliteConnection c, long documentId)
    {
        Exec(c, "DELETE FROM AiFindings WHERE DocumentId=$id", ("$id", documentId));
        Exec(c, "UPDATE ImportFiles SET DocumentId=NULL WHERE DocumentId=$id", ("$id", documentId));
    }
}
