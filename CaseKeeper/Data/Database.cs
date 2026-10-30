using System.Globalization;
using System.IO;
using System.Text;
using CaseKeeper.Models;
using Microsoft.Data.Sqlite;

namespace CaseKeeper.Data;

/// <summary>
/// All persistence for the app. A single SQLite file in the library folder, with an FTS5
/// full-text index covering document details, scanned page text and your notes.
/// </summary>
/// <summary>What an import actually changed, so the app can tell the user.</summary>
public class MergeResult
{
    public int DocumentsAdded, DocumentsSkipped, FilesMissing;
    public int CategoriesAdded, PartiesAdded, EventsAdded, ComplaintsAdded, RecordsAdded;

    public string Describe()
    {
        var lines = new List<string> { $"{DocumentsAdded} document(s) imported." };
        if (DocumentsSkipped > 0) lines.Add($"{DocumentsSkipped} skipped - already in this library.");
        if (FilesMissing > 0) lines.Add($"{FilesMissing} skipped - the file was missing from the backup.");
        if (CategoriesAdded > 0) lines.Add($"{CategoriesAdded} new category/categories added.");
        if (PartiesAdded > 0) lines.Add($"{PartiesAdded} new part(y/ies) added.");
        if (EventsAdded > 0) lines.Add($"{EventsAdded} timeline event(s) added.");
        if (ComplaintsAdded > 0) lines.Add($"{ComplaintsAdded} complaint record(s) added.");
        if (RecordsAdded > 0) lines.Add($"{RecordsAdded} other record(s) added (hearings, orders, directions, contact, costs, letters…).");
        return string.Join("\n", lines);
    }
}

public partial class Database
{
    private readonly string _connectionString;

    /// <param name="password">
    /// SQLCipher passphrase. Null or empty means an ordinary unencrypted database.
    /// </param>
    private readonly bool _networkPath;

    public Database(string dbPath, string password = null)
    {
        _connectionString = BuildConnectionString(dbPath, password);
        _networkPath = IsNetworkPath(dbPath);
        Initialise();
    }

    /// <summary>
    /// SQLite's WAL journal needs shared memory, which doesn't work over SMB. A library kept on a
    /// network drive has to fall back to the older rollback journal.
    /// </summary>
    public static bool IsNetworkPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\")) return true;
            var root = Path.GetPathRoot(full);
            return root != null && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch { return false; }
    }

    private static string BuildConnectionString(string dbPath, string password)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Cache = SqliteCacheMode.Shared };
        if (!string.IsNullOrEmpty(password)) builder.Password = password;
        return builder.ToString();
    }

    /// <summary>
    /// Checks whether a database file can be opened with the given password, without
    /// running any of the schema setup.
    /// </summary>
    public static bool CanOpen(string dbPath, string password)
    {
        try
        {
            // Private cache and no pooling: a shared cache could let this "succeed" on the back of
            // another connection in this process that already holds the key
            var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Cache = SqliteCacheMode.Private, Pooling = false };
            if (!string.IsNullOrEmpty(password)) builder.Password = password;
            using var c = new SqliteConnection(builder.ToString());
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master";
            cmd.ExecuteScalar();
            return true;
        }
        catch (SqliteException) { return false; }
    }

    /// <summary>
    /// Writes an unencrypted copy of the database somewhere else, leaving the original alone.
    /// Used when building a backup, so the backup stays readable by any CaseKeeper installation.
    /// </summary>
    public static void ExportPlaintextCopy(string dbPath, string password, string destPath)
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(destPath)) File.Delete(destPath);
        using var c = new SqliteConnection(BuildConnectionString(dbPath, password));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "ATTACH DATABASE $path AS plain KEY ''; SELECT sqlcipher_export('plain'); DETACH DATABASE plain;";
        cmd.Parameters.AddWithValue("$path", destPath);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Rewrites a plaintext database as an encrypted one, or the other way round.
    /// SQLCipher can't convert a file in place, so this exports into a fresh database
    /// and swaps the files over. Ordinary tables and the FTS5 search index both come across.
    /// </summary>
    public static void ConvertEncryption(string dbPath, string currentPassword, string newPassword)
    {
        SqliteConnection.ClearAllPools();
        var temp = dbPath + ".converting";
        if (File.Exists(temp)) File.Delete(temp);

        using (var c = new SqliteConnection(BuildConnectionString(dbPath, currentPassword)))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            // An empty KEY means "write it out unencrypted"
            cmd.CommandText = "ATTACH DATABASE $path AS target KEY $key; SELECT sqlcipher_export('target'); DETACH DATABASE target;";
            cmd.Parameters.AddWithValue("$path", temp);
            cmd.Parameters.AddWithValue("$key", newPassword ?? "");
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        // Keep the original until the replacement is safely in place
        var old = dbPath + ".previous";
        if (File.Exists(old)) File.Delete(old);
        File.Move(dbPath, old);
        try
        {
            File.Move(temp, dbPath);
        }
        catch
        {
            File.Move(old, dbPath);   // put it back if the swap failed
            throw;
        }
        foreach (var leftover in new[] { old, dbPath + "-wal", dbPath + "-shm" })
            if (File.Exists(leftover) && leftover == old) File.Delete(leftover);
    }

    /// <summary>
    /// Set once the library has been deleted (Remove all CaseKeeper data). From then on nothing may
    /// open the database: SQLite would quietly create a new, empty one where the old one was.
    /// </summary>
    public static bool Retired { get; set; }

    private SqliteConnection Open()
    {
        if (Retired) throw new InvalidOperationException("CaseKeeper's data has been removed from this PC.");
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void Exec(SqliteConnection c, string sql, params (string, object)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void Initialise()
    {
        using var c = Open();
        Exec(c, _networkPath ? "PRAGMA journal_mode=DELETE;" : "PRAGMA journal_mode=WAL;");
        Exec(c, @"
CREATE TABLE IF NOT EXISTS Categories(
  Id INTEGER PRIMARY KEY AUTOINCREMENT, Code TEXT, Name TEXT NOT NULL, GroupName TEXT, Description TEXT,
  BundleSection TEXT, SortOrder INTEGER, IsCustom INTEGER DEFAULT 0);
CREATE TABLE IF NOT EXISTS Documents(
  Id INTEGER PRIMARY KEY AUTOINCREMENT, Title TEXT, CategoryId INTEGER, OriginalFileName TEXT, StoredFileName TEXT,
  FileExtension TEXT, FileHash TEXT, FileSize INTEGER, DocumentDate TEXT, DateReceived TEXT, Author TEXT, Tags TEXT,
  Description TEXT, Notes TEXT, IsImportant INTEGER DEFAULT 0, PageCount INTEGER DEFAULT 0, OcrStatus INTEGER DEFAULT 0,
  ImportedAt TEXT);
CREATE TABLE IF NOT EXISTS PageNotes(DocumentId INTEGER, PageNumber INTEGER, Note TEXT, UpdatedAt TEXT,
  PRIMARY KEY(DocumentId, PageNumber));
CREATE TABLE IF NOT EXISTS PageText(DocumentId INTEGER, PageNumber INTEGER, Text TEXT, PRIMARY KEY(DocumentId, PageNumber));
CREATE TABLE IF NOT EXISTS Events(Id INTEGER PRIMARY KEY AUTOINCREMENT, EventDate TEXT, Time TEXT, Title TEXT,
  Description TEXT, EventType TEXT, DocumentId INTEGER, IncludeInChronology INTEGER DEFAULT 1);
CREATE TABLE IF NOT EXISTS Complaints(Id INTEGER PRIMARY KEY AUTOINCREMENT, Recipient TEXT, RecipientName TEXT,
  Reference TEXT, Subject TEXT, Summary TEXT, DateSent TEXT, ResponseDue TEXT, Status TEXT, Outcome TEXT);
CREATE TABLE IF NOT EXISTS ComplaintDocuments(ComplaintId INTEGER, DocumentId INTEGER, PRIMARY KEY(ComplaintId, DocumentId));
CREATE TABLE IF NOT EXISTS Settings(Key TEXT PRIMARY KEY, Value TEXT);
CREATE TABLE IF NOT EXISTS Parties(Id INTEGER PRIMARY KEY AUTOINCREMENT, Role TEXT, Name TEXT, IsMe INTEGER DEFAULT 0,
  Representative TEXT, Contact TEXT, Notes TEXT, SortOrder INTEGER DEFAULT 0);
CREATE VIRTUAL TABLE IF NOT EXISTS SearchIndex USING fts5(content, doc_id UNINDEXED, page UNINDEXED, source UNINDEXED,
  tokenize='porter unicode61');
");

        // Schema upgrades for libraries created by earlier versions. Only ever adds; never removes.
        AddColumnIfMissing(c, "Documents", "PartyId", "INTEGER");
        AddColumnIfMissing(c, "Documents", "ExhibitRef", "TEXT");
        AddColumnIfMissing(c, "Documents", "DeletedAt", "TEXT");
        AddColumnIfMissing(c, "Parties", "DateOfBirth", "TEXT");
        AddColumnIfMissing(c, "Events", "Location", "TEXT");
        AddColumnIfMissing(c, "Events", "Witnesses", "TEXT");
        AddColumnIfMissing(c, "Events", "Reference", "TEXT");
        // v0.4: where a bulk-imported file came from (relative path only) and its original timestamp
        AddColumnIfMissing(c, "Documents", "SourcePath", "TEXT");
        AddColumnIfMissing(c, "Documents", "SourceModifiedAt", "TEXT");
        Exec(c, "CREATE INDEX IF NOT EXISTS IX_Documents_Hash ON Documents(FileHash)");

        EnsureAuditTable(c);
        EnsureRecordTables(c);
        EnsureBulkImportTables(c);
        SyncDefaultCategories(c);
    }

    /// <summary>
    /// Adds any built-in categories the library doesn't have yet, so an existing library picks up
    /// new ones (such as the Applicant's/Respondent's pairs) on upgrade. Nothing is ever overwritten,
    /// and a superseded category is only removed when no documents are filed under it.
    /// </summary>
    private void SyncDefaultCategories(SqliteConnection c)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT Code, Name FROM Categories";
            using var r = cmd.ExecuteReader();
            while (r.Read()) existing.Add($"{r[0]}|{r[1]}");
        }

        using var tx = c.BeginTransaction();
        int nextOrder;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(MAX(SortOrder),0)+1 FROM Categories";
            nextOrder = Convert.ToInt32(cmd.ExecuteScalar());
        }
        foreach (var cat in DefaultCategories.Build())
        {
            if (!existing.Add($"{cat.Code}|{cat.Name}")) continue;
            cat.SortOrder = nextOrder++;
            InsertCategory(c, cat);
        }

        // Tidy away categories that an Applicant's/Respondent's pair has replaced, so a library
        // created by an earlier version doesn't end up listing "C2" three times. Only ever removes
        // built-in categories that have no documents filed under them.
        Exec(c, @"DELETE FROM Categories WHERE IsCustom=0
                  AND NOT EXISTS (SELECT 1 FROM Documents d WHERE d.CategoryId=Categories.Id)
                  AND EXISTS (SELECT 1 FROM Categories o WHERE o.Name = Categories.Name || ' ' || $suffix)",
            ("$suffix", DefaultCategories.ApplicantSuffix));

        // Ones that were renamed as well as split, so the base name no longer matches
        foreach (var name in DefaultCategories.SupersededNames)
            Exec(c, @"DELETE FROM Categories WHERE Name=$n AND IsCustom=0
                      AND NOT EXISTS (SELECT 1 FROM Documents d WHERE d.CategoryId=Categories.Id)", ("$n", name));
        tx.Commit();
    }

    // ---------- helpers ----------
    private static string D(DateTime? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateTime? PD(object o) =>
        o is string s && DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    private static string S(SqliteDataReader r, string col) => r[col] as string ?? "";

    // ---------- settings ----------
    public string GetSetting(string key, string fallback = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string ?? fallback;
    }

    public void SetSetting(string key, string value)
    {
        using var c = Open();
        Exec(c, "INSERT INTO Settings(Key,Value) VALUES($k,$v) ON CONFLICT(Key) DO UPDATE SET Value=$v", ("$k", key), ("$v", value));
    }

    public Dictionary<string, string> AllSettings()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Key, Value FROM Settings";
        using var r = cmd.ExecuteReader();
        var d = new Dictionary<string, string>();
        while (r.Read()) d[r.GetString(0)] = r[1] as string ?? "";
        return d;
    }

    // ---------- categories ----------
    private void InsertCategory(SqliteConnection c, Category cat)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO Categories(Code,Name,GroupName,Description,BundleSection,SortOrder,IsCustom)
                            VALUES($c,$n,$g,$d,$b,$s,$i); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$c", cat.Code ?? "");
        cmd.Parameters.AddWithValue("$n", cat.Name);
        cmd.Parameters.AddWithValue("$g", cat.GroupName ?? "");
        cmd.Parameters.AddWithValue("$d", cat.Description ?? "");
        cmd.Parameters.AddWithValue("$b", cat.BundleSection ?? "F");
        cmd.Parameters.AddWithValue("$s", cat.SortOrder);
        cmd.Parameters.AddWithValue("$i", cat.IsCustom ? 1 : 0);
        cat.Id = (long)cmd.ExecuteScalar();
    }

    public void AddCategory(Category cat)
    {
        using var c = Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(MAX(SortOrder),0)+1 FROM Categories";
            cat.SortOrder = Convert.ToInt32(cmd.ExecuteScalar());
        }
        InsertCategory(c, cat);
        AuditLocked(c, "category.create", "category", cat.Id, $"Added category “{cat.DisplayName}”",
            $"Group: {cat.GroupName}\nBundle section: {cat.BundleSection}");
    }

    public void UpdateCategory(Category cat)
    {
        using var c = Open();
        var before = GetCategories().FirstOrDefault(x => x.Id == cat.Id);
        Exec(c, "UPDATE Categories SET Code=$c,Name=$n,GroupName=$g,Description=$d,BundleSection=$b WHERE Id=$id",
            ("$c", cat.Code ?? ""), ("$n", cat.Name), ("$g", cat.GroupName ?? ""), ("$d", cat.Description ?? ""),
            ("$b", cat.BundleSection ?? "F"), ("$id", cat.Id));
        var diff = before == null ? "" : Diff(("Code", before.Code, cat.Code), ("Name", before.Name, cat.Name),
            ("Group", before.GroupName, cat.GroupName), ("Description", before.Description, cat.Description),
            ("Bundle section", before.BundleSection, cat.BundleSection));
        if (diff.Length > 0) AuditLocked(c, "category.update", "category", cat.Id, $"Changed category “{cat.DisplayName}”", diff);
    }

    /// <summary>Deletes a category, moving its documents to <paramref name="moveToId"/>.</summary>
    public void DeleteCategory(long id, long moveToId)
    {
        using var c = Open();
        var label = CategoryLabel(c, id);
        Exec(c, "UPDATE Documents SET CategoryId=$m WHERE CategoryId=$id", ("$m", moveToId), ("$id", id));
        Exec(c, "DELETE FROM Categories WHERE Id=$id", ("$id", id));
        AuditLocked(c, "category.delete", "category", id, $"Deleted category “{label}”",
            $"Its documents were moved to “{CategoryLabel(c, moveToId)}”");
    }

    public List<Category> GetCategories()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT c.*, (SELECT COUNT(*) FROM Documents d WHERE d.CategoryId=c.Id AND d.DeletedAt IS NULL) AS Cnt
                            FROM Categories c";
        using var r = cmd.ExecuteReader();
        var list = new List<Category>();
        while (r.Read())
            list.Add(new Category
            {
                Id = (long)r["Id"], Code = S(r, "Code"), Name = S(r, "Name"), GroupName = S(r, "GroupName"),
                Description = S(r, "Description"), BundleSection = S(r, "BundleSection"),
                SortOrder = Convert.ToInt32(r["SortOrder"]), IsCustom = (long)r["IsCustom"] == 1,
                DocumentCount = Convert.ToInt32(r["Cnt"])
            });
        // Alphabetical throughout: groups by name, categories by the label the user actually sees
        return list
            .OrderBy(x => x.GroupName, UI.NaturalComparer.Instance)
            .ThenBy(x => x.DisplayName, UI.NaturalComparer.Instance)
            .ToList();
    }

    // ---------- documents ----------
    private static CaseDocument ReadDocument(SqliteDataReader r) => new()
    {
        Id = (long)r["Id"], Title = S(r, "Title"), CategoryId = r["CategoryId"] is long l ? l : 0,
        PartyId = r["PartyId"] is long pid ? pid : null,
        OriginalFileName = S(r, "OriginalFileName"), StoredFileName = S(r, "StoredFileName"),
        FileExtension = S(r, "FileExtension"), FileHash = S(r, "FileHash"), FileSize = r["FileSize"] is long fs ? fs : 0,
        DocumentDate = PD(r["DocumentDate"]), DateReceived = PD(r["DateReceived"]), Author = S(r, "Author"),
        Tags = S(r, "Tags"), Description = S(r, "Description"), Notes = S(r, "Notes"),
        IsImportant = r["IsImportant"] is long i && i == 1, PageCount = Convert.ToInt32(r["PageCount"]),
        OcrStatus = (OcrStatus)Convert.ToInt32(r["OcrStatus"]),
        ImportedAt = DateTime.TryParse(S(r, "ImportedAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ia) ? ia : DateTime.MinValue,
        ExhibitRef = HasColumn(r, "ExhibitRef") ? S(r, "ExhibitRef") : "",
        DeletedAt = HasColumn(r, "DeletedAt") && DateTime.TryParse(r["DeletedAt"] as string, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var da) ? da : null,
        SourcePath = HasColumn(r, "SourcePath") ? S(r, "SourcePath") : "",
        SourceModifiedAt = HasColumn(r, "SourceModifiedAt") && DateTime.TryParse(r["SourceModifiedAt"] as string, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var sm) ? sm : null
    };

    private static bool HasColumn(SqliteDataReader r, string name)
    {
        for (int i = 0; i < r.FieldCount; i++)
            if (r.GetName(i).Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Every document in the library (not those in Deleted items).</summary>
    public List<CaseDocument> GetDocuments() => QueryDocuments("WHERE DeletedAt IS NULL");

    /// <summary>Documents in Deleted items, most recently deleted first.</summary>
    public List<CaseDocument> GetDeletedDocuments() => QueryDocuments("WHERE DeletedAt IS NOT NULL", "DeletedAt DESC");

    private List<CaseDocument> QueryDocuments(string where, string order = null, params (string, object)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT * FROM Documents {where} ORDER BY {order ?? "COALESCE(DocumentDate, substr(ImportedAt,1,10)) DESC, Id DESC"}";
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<CaseDocument>();
        while (r.Read()) list.Add(ReadDocument(r));
        return list;
    }

    public CaseDocument GetDocument(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM Documents WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadDocument(r) : null;
    }

    public CaseDocument FindByHash(string hash)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM Documents WHERE FileHash=$h AND DeletedAt IS NULL LIMIT 1";
        cmd.Parameters.AddWithValue("$h", hash);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadDocument(r) : null;
    }

    public void AddDocument(CaseDocument d)
    {
        using var c = Open();
        InsertDocument(c, d);
    }

    /// <summary>Inserts a document and indexes it, on a connection the caller owns.</summary>
    private void InsertDocument(SqliteConnection c, CaseDocument d)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO Documents(Title,CategoryId,PartyId,OriginalFileName,StoredFileName,FileExtension,FileHash,FileSize,
            DocumentDate,DateReceived,Author,Tags,Description,Notes,IsImportant,PageCount,OcrStatus,ImportedAt,ExhibitRef,SourcePath,SourceModifiedAt)
            VALUES($t,$c,$pid,$o,$s,$e,$h,$sz,$dd,$dr,$a,$tg,$de,$n,$i,$p,$ocr,$ia,$ex,$sp,$sm); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$ex", d.ExhibitRef ?? "");
        cmd.Parameters.AddWithValue("$sp", d.SourcePath ?? "");
        cmd.Parameters.AddWithValue("$sm", (object)d.SourceModifiedAt?.ToString("o", CultureInfo.InvariantCulture) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", d.Title);
        cmd.Parameters.AddWithValue("$c", d.CategoryId);
        cmd.Parameters.AddWithValue("$pid", (object)d.PartyId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$o", d.OriginalFileName);
        cmd.Parameters.AddWithValue("$s", d.StoredFileName);
        cmd.Parameters.AddWithValue("$e", d.FileExtension);
        cmd.Parameters.AddWithValue("$h", d.FileHash);
        cmd.Parameters.AddWithValue("$sz", d.FileSize);
        cmd.Parameters.AddWithValue("$dd", (object)D(d.DocumentDate) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dr", (object)D(d.DateReceived) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$a", d.Author ?? "");
        cmd.Parameters.AddWithValue("$tg", d.Tags ?? "");
        cmd.Parameters.AddWithValue("$de", d.Description ?? "");
        cmd.Parameters.AddWithValue("$n", d.Notes ?? "");
        cmd.Parameters.AddWithValue("$i", d.IsImportant ? 1 : 0);
        cmd.Parameters.AddWithValue("$p", d.PageCount);
        cmd.Parameters.AddWithValue("$ocr", (int)d.OcrStatus);
        cmd.Parameters.AddWithValue("$ia", d.ImportedAt.ToString("o"));
        d.Id = (long)cmd.ExecuteScalar();
        ReindexDocumentDetails(c, d);
        AuditLocked(c, "document.import", "document", d.Id, $"Imported “{d.Title}”",
            $"File: {d.OriginalFileName} ({d.FileSize:N0} bytes)\nSHA-256: {d.FileHash}\nCategory: {CategoryLabel(c, d.CategoryId)}" +
            (d.PartyId.HasValue ? $"\nParty: {PartyLabel(c, d.PartyId)}" : "") +
            (string.IsNullOrEmpty(d.SourcePath) ? "" : $"\nFrom folder: {d.SourcePath}"));
    }

    public void UpdateDocument(CaseDocument d)
    {
        using var c = Open();
        var before = GetDocument(d.Id);
        Exec(c, @"UPDATE Documents SET Title=$t,CategoryId=$c,PartyId=$pid,DocumentDate=$dd,DateReceived=$dr,Author=$a,Tags=$tg,
                  Description=$de,Notes=$n,IsImportant=$i,PageCount=$p,OcrStatus=$ocr,ExhibitRef=$ex WHERE Id=$id",
            ("$t", d.Title), ("$c", d.CategoryId), ("$pid", d.PartyId), ("$dd", D(d.DocumentDate)), ("$dr", D(d.DateReceived)),
            ("$a", d.Author ?? ""), ("$tg", d.Tags ?? ""), ("$de", d.Description ?? ""), ("$n", d.Notes ?? ""),
            ("$i", d.IsImportant ? 1 : 0), ("$p", d.PageCount), ("$ocr", (int)d.OcrStatus), ("$ex", d.ExhibitRef ?? ""), ("$id", d.Id));
        ReindexDocumentDetails(c, d);
        if (before == null) return;

        var diff = Diff(("Title", before.Title, d.Title),
            ("Category", CategoryLabel(c, before.CategoryId), CategoryLabel(c, d.CategoryId)),
            ("Party", PartyLabel(c, before.PartyId), PartyLabel(c, d.PartyId)),
            ("Document date", before.DocumentDate, d.DocumentDate), ("Date received", before.DateReceived, d.DateReceived),
            ("Author", before.Author, d.Author), ("Tags", before.Tags, d.Tags), ("Summary", before.Description, d.Description),
            ("Starred", before.IsImportant, d.IsImportant), ("Exhibit", before.ExhibitRef, d.ExhibitRef));
        bool notesChanged = (before.Notes ?? "") != (d.Notes ?? "");
        if (diff.Length > 0)
            AuditLocked(c, "document.update", "document", d.Id, $"Changed details of “{d.Title}”",
                diff + (notesChanged ? "\nNotes: edited" : ""));
        else if (notesChanged && !AuditedRecently(c, "document.notes", "document", d.Id, TimeSpan.FromMinutes(10)))
            AuditLocked(c, "document.notes", "document", d.Id, $"Edited notes on “{d.Title}”",
                $"Notes now {(d.Notes ?? "").Length:N0} characters");
    }

    public void SetOcrStatus(long docId, OcrStatus status, int? pageCount = null)
    {
        using var c = Open();
        if (pageCount.HasValue)
            Exec(c, "UPDATE Documents SET OcrStatus=$s, PageCount=$p WHERE Id=$id", ("$s", (int)status), ("$p", pageCount.Value), ("$id", docId));
        else
            Exec(c, "UPDATE Documents SET OcrStatus=$s WHERE Id=$id", ("$s", (int)status), ("$id", docId));
    }

    /// <summary>Moves a document to Deleted items. Its file, notes and links are kept until it is purged.</summary>
    public void SoftDeleteDocument(long id)
    {
        using var c = Open();
        var d = GetDocument(id);
        if (d == null) return;
        Exec(c, "UPDATE Documents SET DeletedAt=$at WHERE Id=$id", ("$at", DateTime.Now.ToString("o")), ("$id", id));
        AuditLocked(c, "document.delete", "document", id, $"Moved “{d.Title}” to Deleted items", $"SHA-256: {d.FileHash}");
    }

    public void RestoreDocument(long id)
    {
        using var c = Open();
        var d = GetDocument(id);
        if (d == null) return;
        Exec(c, "UPDATE Documents SET DeletedAt=NULL WHERE Id=$id", ("$id", id));
        AuditLocked(c, "document.restore", "document", id, $"Restored “{d.Title}” from Deleted items", "");
    }

    /// <summary>Removes a document and everything attached to it for good. The caller deletes the file.</summary>
    public void DeleteDocument(long id)
    {
        using var c = Open();
        var d = GetDocument(id);
        using var tx = c.BeginTransaction();
        foreach (var t in new[] { "PageNotes", "PageText", "ComplaintDocuments" })
            Exec(c, $"DELETE FROM {t} WHERE DocumentId=$id", ("$id", id));
        Exec(c, "UPDATE Events SET DocumentId=NULL WHERE DocumentId=$id", ("$id", id));
        foreach (var t in new[] { "Hearings", "CourtOrders", "CaseActions", "ContactSessions", "Expenses" })
            Exec(c, $"UPDATE [{t}] SET DocumentId=NULL WHERE DocumentId=$id", ("$id", id));
        Exec(c, "DELETE FROM SearchIndex WHERE doc_id=$id", ("$id", id));
        PurgeStagingFor(c, id);
        Exec(c, "DELETE FROM Documents WHERE Id=$id", ("$id", id));
        AuditLocked(c, "document.purge", "document", id, $"Permanently deleted “{d?.Title ?? "#" + id}”",
            d == null ? "" : $"File: {d.OriginalFileName}\nSHA-256: {d.FileHash}");
        tx.Commit();
    }

    private void ReindexDocumentDetails(SqliteConnection c, CaseDocument d)
    {
        Exec(c, "DELETE FROM SearchIndex WHERE doc_id=$id AND source IN ('meta','notes')", ("$id", d.Id));
        var meta = string.Join(" ", d.Title, d.OriginalFileName, d.Author, d.Tags, d.Description, d.SourcePath);
        Exec(c, "INSERT INTO SearchIndex(content,doc_id,page,source) VALUES($t,$id,0,'meta')", ("$t", meta), ("$id", d.Id));
        if (!string.IsNullOrWhiteSpace(d.Notes))
            Exec(c, "INSERT INTO SearchIndex(content,doc_id,page,source) VALUES($t,$id,0,'notes')", ("$t", d.Notes), ("$id", d.Id));
    }

    // ---------- page text (OCR) ----------
    public void SavePageText(long docId, IReadOnlyList<string> pages)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "DELETE FROM PageText WHERE DocumentId=$id", ("$id", docId));
        Exec(c, "DELETE FROM SearchIndex WHERE doc_id=$id AND source='page'", ("$id", docId));
        for (int i = 0; i < pages.Count; i++)
        {
            var text = pages[i] ?? "";
            Exec(c, "INSERT INTO PageText(DocumentId,PageNumber,Text) VALUES($id,$p,$t)", ("$id", docId), ("$p", i + 1), ("$t", text));
            if (!string.IsNullOrWhiteSpace(text))
                Exec(c, "INSERT INTO SearchIndex(content,doc_id,page,source) VALUES($t,$id,$p,'page')", ("$t", text), ("$id", docId), ("$p", i + 1));
        }
        tx.Commit();
    }

    public string GetPageText(long docId, int page)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Text FROM PageText WHERE DocumentId=$id AND PageNumber=$p";
        cmd.Parameters.AddWithValue("$id", docId);
        cmd.Parameters.AddWithValue("$p", page);
        return cmd.ExecuteScalar() as string ?? "";
    }

    public Dictionary<int, string> GetAllPageText(long docId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT PageNumber, Text FROM PageText WHERE DocumentId=$id ORDER BY PageNumber";
        cmd.Parameters.AddWithValue("$id", docId);
        using var r = cmd.ExecuteReader();
        var dict = new Dictionary<int, string>();
        while (r.Read()) dict[Convert.ToInt32(r[0])] = r[1] as string ?? "";
        return dict;
    }

    // ---------- page notes ----------
    public Dictionary<int, string> GetPageNotes(long docId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT PageNumber, Note FROM PageNotes WHERE DocumentId=$id";
        cmd.Parameters.AddWithValue("$id", docId);
        using var r = cmd.ExecuteReader();
        var dict = new Dictionary<int, string>();
        while (r.Read()) dict[Convert.ToInt32(r[0])] = r[1] as string ?? "";
        return dict;
    }

    public void SavePageNote(long docId, int page, string note)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "DELETE FROM SearchIndex WHERE doc_id=$id AND page=$p AND source='pagenote'", ("$id", docId), ("$p", page));
        if (string.IsNullOrWhiteSpace(note))
            Exec(c, "DELETE FROM PageNotes WHERE DocumentId=$id AND PageNumber=$p", ("$id", docId), ("$p", page));
        else
        {
            Exec(c, @"INSERT INTO PageNotes(DocumentId,PageNumber,Note,UpdatedAt) VALUES($id,$p,$n,$u)
                      ON CONFLICT(DocumentId,PageNumber) DO UPDATE SET Note=$n, UpdatedAt=$u",
                ("$id", docId), ("$p", page), ("$n", note), ("$u", DateTime.Now.ToString("o")));
            Exec(c, "INSERT INTO SearchIndex(content,doc_id,page,source) VALUES($t,$id,$p,'pagenote')", ("$t", note), ("$id", docId), ("$p", page));
        }
        // Notes save as you type, so only the first edit in a ten-minute stretch is recorded
        if (!AuditedRecently(c, "pagenote.edit", "document", docId, TimeSpan.FromMinutes(10)))
            AuditLocked(c, "pagenote.edit", "document", docId, $"Edited the note on page {page}",
                string.IsNullOrWhiteSpace(note) ? "Note cleared" : $"Note now {note.Length:N0} characters");
        tx.Commit();
    }

    // ---------- search ----------
    /// <summary>Turns free text into a safe FTS5 query: every word must appear (prefix match).
    /// Text in "double quotes" is searched as an exact phrase.</summary>
    public static string BuildFtsQuery(string input)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        bool inQuote = false;
        foreach (var ch in input + " ")
        {
            if (ch == '"')
            {
                if (inQuote && sb.Length > 0) parts.Add("\"" + sb.ToString().Trim() + "\"");
                else if (!inQuote && sb.Length > 0) AddWord(sb.ToString());
                sb.Clear();
                inQuote = !inQuote;
            }
            else if (!inQuote && char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0) AddWord(sb.ToString());
                sb.Clear();
            }
            else sb.Append(ch);
        }
        return string.Join(" AND ", parts);

        void AddWord(string w)
        {
            var clean = new string(w.Where(char.IsLetterOrDigit).ToArray());
            if (clean.Length > 0) parts.Add("\"" + clean + "\"*");
        }
    }

    public List<SearchHit> Search(string text, int limit = 300)
    {
        var q = BuildFtsQuery(text);
        if (string.IsNullOrEmpty(q)) return new();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT doc_id, page, source, snippet(SearchIndex, 0, '«', '»', ' … ', 18) AS snip
                            FROM SearchIndex WHERE SearchIndex MATCH $q ORDER BY rank LIMIT $lim";
        cmd.Parameters.AddWithValue("$q", q);
        cmd.Parameters.AddWithValue("$lim", limit);
        var hits = new List<SearchHit>();
        try
        {
            using var r = cmd.ExecuteReader();
            while (r.Read())
                hits.Add(new SearchHit
                {
                    DocumentId = Convert.ToInt64(r["doc_id"]), PageNumber = Convert.ToInt32(r["page"]),
                    Source = r["source"] as string ?? "", Snippet = (r["snip"] as string ?? "").Replace("\r", " ").Replace("\n", " ")
                });
        }
        catch (SqliteException) { /* malformed query - treat as no results */ }
        return hits;
    }

    // ---------- parties ----------
    public List<Party> GetParties()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT p.*, (SELECT COUNT(*) FROM Documents d WHERE d.PartyId=p.Id) AS Cnt
                            FROM Parties p";
        using var r = cmd.ExecuteReader();
        var list = new List<Party>();
        while (r.Read())
            list.Add(new Party
            {
                Id = (long)r["Id"], Role = S(r, "Role"), Name = S(r, "Name"), IsMe = r["IsMe"] is long m && m == 1,
                Representative = S(r, "Representative"), Contact = S(r, "Contact"), Notes = S(r, "Notes"),
                SortOrder = Convert.ToInt32(r["SortOrder"]), DocumentCount = Convert.ToInt32(r["Cnt"]),
                DateOfBirth = PD(r["DateOfBirth"])
            });
        return list.OrderBy(p => p.DisplayName, UI.NaturalComparer.Instance).ToList();
    }

    public void SaveParty(Party p)
    {
        using var c = Open();
        var before = p.Id == 0 ? null : GetParties().FirstOrDefault(x => x.Id == p.Id);
        // Order: applicants first, then respondents, then everyone else
        p.SortOrder = Array.IndexOf(Party.Roles, p.Role) is var i && i >= 0 ? i : 99;
        var args = new (string, object)[]
        {
            ("$r", p.Role), ("$n", p.Name ?? ""), ("$m", p.IsMe ? 1 : 0), ("$rep", p.Representative ?? ""),
            ("$c", p.Contact ?? ""), ("$no", p.Notes ?? ""), ("$s", p.SortOrder), ("$dob", D(p.DateOfBirth)), ("$id", p.Id)
        };
        if (p.IsMe) Exec(c, "UPDATE Parties SET IsMe=0 WHERE Id<>$id", ("$id", p.Id));
        if (p.Id == 0)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"INSERT INTO Parties(Role,Name,IsMe,Representative,Contact,Notes,SortOrder,DateOfBirth)
                                VALUES($r,$n,$m,$rep,$c,$no,$s,$dob); SELECT last_insert_rowid();";
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            p.Id = (long)cmd.ExecuteScalar();
            AuditLocked(c, "party.create", "party", p.Id, $"Added party: {p.DisplayName}",
                Diff(("Representative", "", p.Representative), ("Contact", "", p.Contact), ("Date of birth", null, p.DateOfBirth)));
        }
        else
        {
            Exec(c, @"UPDATE Parties SET Role=$r,Name=$n,IsMe=$m,Representative=$rep,Contact=$c,Notes=$no,SortOrder=$s,DateOfBirth=$dob
                      WHERE Id=$id", args);
            var diff = before == null ? "" : Diff(("Role", before.Role, p.Role), ("Name", before.Name, p.Name), ("This is me", before.IsMe, p.IsMe),
                ("Representative", before.Representative, p.Representative), ("Contact", before.Contact, p.Contact),
                ("Notes", before.Notes, p.Notes), ("Date of birth", before.DateOfBirth, p.DateOfBirth));
            if (diff.Length > 0) AuditLocked(c, "party.update", "party", p.Id, $"Changed party: {p.DisplayName}", diff);
        }
    }

    public void DeleteParty(long id)
    {
        using var c = Open();
        var label = PartyLabel(c, id);
        Exec(c, "UPDATE Documents SET PartyId=NULL WHERE PartyId=$id", ("$id", id));
        Exec(c, "DELETE FROM Parties WHERE Id=$id", ("$id", id));
        AuditLocked(c, "party.delete", "party", id, $"Removed party: {label}", "Their documents were unlinked, not deleted");
    }

    // ---------- events ----------
    public List<TimelineEvent> GetEvents()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM Events ORDER BY EventDate, Time";
        using var r = cmd.ExecuteReader();
        var list = new List<TimelineEvent>();
        while (r.Read())
            list.Add(new TimelineEvent
            {
                Id = (long)r["Id"], EventDate = PD(r["EventDate"]) ?? DateTime.Today, Time = S(r, "Time"),
                Title = S(r, "Title"), Description = S(r, "Description"), EventType = S(r, "EventType"),
                DocumentId = r["DocumentId"] is long l ? l : null,
                IncludeInChronology = r["IncludeInChronology"] is long ic && ic == 1,
                Location = S(r, "Location"), Witnesses = S(r, "Witnesses"), Reference = S(r, "Reference")
            });
        return list;
    }

    public void SaveEvent(TimelineEvent e)
    {
        using var c = Open();
        var before = e.Id == 0 ? null : GetEvents().FirstOrDefault(x => x.Id == e.Id);
        var args = new (string, object)[]
        {
            ("$d", D(e.EventDate)), ("$tm", e.Time ?? ""), ("$t", e.Title), ("$de", e.Description ?? ""),
            ("$ty", e.EventType), ("$doc", e.DocumentId), ("$ic", e.IncludeInChronology ? 1 : 0),
            ("$loc", e.Location ?? ""), ("$w", e.Witnesses ?? ""), ("$ref", e.Reference ?? ""), ("$id", e.Id)
        };
        if (e.Id == 0)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"INSERT INTO Events(EventDate,Time,Title,Description,EventType,DocumentId,IncludeInChronology,Location,Witnesses,Reference)
                                VALUES($d,$tm,$t,$de,$ty,$doc,$ic,$loc,$w,$ref); SELECT last_insert_rowid();";
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            e.Id = (long)cmd.ExecuteScalar();
            AuditLocked(c, "event.create", "event", e.Id, $"Added timeline entry: {e.Title} ({e.EventDate:dd MMM yyyy})",
                Diff(("Type", "", e.EventType), ("Details", "", e.Description), ("Location", "", e.Location),
                    ("Witnesses", "", e.Witnesses), ("Reference", "", e.Reference)));
        }
        else
        {
            Exec(c, @"UPDATE Events SET EventDate=$d,Time=$tm,Title=$t,Description=$de,EventType=$ty,DocumentId=$doc,
                      IncludeInChronology=$ic,Location=$loc,Witnesses=$w,Reference=$ref WHERE Id=$id", args);
            var diff = before == null ? "" : Diff(("Date", before.EventDate, e.EventDate), ("Time", before.Time, e.Time),
                ("Title", before.Title, e.Title), ("Type", before.EventType, e.EventType), ("Details", before.Description, e.Description),
                ("Location", before.Location, e.Location), ("Witnesses", before.Witnesses, e.Witnesses),
                ("Reference", before.Reference, e.Reference), ("In chronology", before.IncludeInChronology, e.IncludeInChronology),
                ("Linked document", before.DocumentId, e.DocumentId));
            if (diff.Length > 0) AuditLocked(c, "event.update", "event", e.Id, $"Changed timeline entry: {e.Title}", diff);
        }
    }

    public void DeleteEvent(long id)
    {
        using var c = Open();
        var before = GetEvents().FirstOrDefault(x => x.Id == id);
        Exec(c, "DELETE FROM Events WHERE Id=$id", ("$id", id));
        if (before != null)
            AuditLocked(c, "event.delete", "event", id, $"Deleted timeline entry: {before.Title} ({before.EventDate:dd MMM yyyy})",
                Clip(before.Description));
    }

    // ---------- complaints ----------
    public List<Complaint> GetComplaints()
    {
        using var c = Open();
        var list = new List<Complaint>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM Complaints ORDER BY COALESCE(DateSent,'9999') DESC, Id DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new Complaint
                {
                    Id = (long)r["Id"], Recipient = S(r, "Recipient"), RecipientName = S(r, "RecipientName"),
                    Reference = S(r, "Reference"), Subject = S(r, "Subject"), Summary = S(r, "Summary"),
                    DateSent = PD(r["DateSent"]), ResponseDue = PD(r["ResponseDue"]), Status = S(r, "Status"),
                    Outcome = S(r, "Outcome")
                });
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT ComplaintId, DocumentId FROM ComplaintDocuments";
            using var r = cmd.ExecuteReader();
            var byId = list.ToDictionary(x => x.Id);
            while (r.Read())
                if (byId.TryGetValue((long)r[0], out var comp)) comp.DocumentIds.Add((long)r[1]);
        }
        return list;
    }

    public void SaveComplaint(Complaint x)
    {
        var before = x.Id == 0 ? null : GetComplaints().FirstOrDefault(o => o.Id == x.Id);
        using var c = Open();
        using var tx = c.BeginTransaction();
        var args = new (string, object)[]
        {
            ("$r", x.Recipient), ("$rn", x.RecipientName ?? ""), ("$ref", x.Reference ?? ""), ("$s", x.Subject ?? ""),
            ("$su", x.Summary ?? ""), ("$ds", D(x.DateSent)), ("$rd", D(x.ResponseDue)), ("$st", x.Status),
            ("$o", x.Outcome ?? ""), ("$id", x.Id)
        };
        if (x.Id == 0)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"INSERT INTO Complaints(Recipient,RecipientName,Reference,Subject,Summary,DateSent,ResponseDue,Status,Outcome)
                                VALUES($r,$rn,$ref,$s,$su,$ds,$rd,$st,$o); SELECT last_insert_rowid();";
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            x.Id = (long)cmd.ExecuteScalar();
        }
        else
            Exec(c, @"UPDATE Complaints SET Recipient=$r,RecipientName=$rn,Reference=$ref,Subject=$s,Summary=$su,
                      DateSent=$ds,ResponseDue=$rd,Status=$st,Outcome=$o WHERE Id=$id", args);

        Exec(c, "DELETE FROM ComplaintDocuments WHERE ComplaintId=$id", ("$id", x.Id));
        foreach (var docId in x.DocumentIds.Distinct())
            Exec(c, "INSERT INTO ComplaintDocuments(ComplaintId,DocumentId) VALUES($c,$d)", ("$c", x.Id), ("$d", docId));

        var diff = Diff(("Subject", before?.Subject, x.Subject), ("Sent to", before?.Recipient, x.Recipient),
            ("Name", before?.RecipientName, x.RecipientName), ("Reference", before?.Reference, x.Reference),
            ("Date sent", before?.DateSent, x.DateSent), ("Response due", before?.ResponseDue, x.ResponseDue),
            ("Status", before?.Status, x.Status), ("Summary", before?.Summary, x.Summary), ("Outcome", before?.Outcome, x.Outcome),
            ("Linked documents", before == null ? "" : string.Join(",", before.DocumentIds.OrderBy(i => i)),
                string.Join(",", x.DocumentIds.Distinct().OrderBy(i => i))));
        if (before == null) AuditLocked(c, "complaint.create", "complaint", x.Id, $"Added complaint: {x.Subject}", diff);
        else if (diff.Length > 0) AuditLocked(c, "complaint.update", "complaint", x.Id, $"Changed complaint: {x.Subject}", diff);
        tx.Commit();
    }

    public void DeleteComplaint(long id)
    {
        var before = GetComplaints().FirstOrDefault(o => o.Id == id);
        using var c = Open();
        Exec(c, "DELETE FROM ComplaintDocuments WHERE ComplaintId=$id", ("$id", id));
        Exec(c, "DELETE FROM Complaints WHERE Id=$id", ("$id", id));
        AuditLocked(c, "complaint.delete", "complaint", id, $"Deleted complaint: {before?.Subject ?? "#" + id}",
            before == null ? "" : $"Sent to: {before.Recipient}\nStatus: {before.Status}");
    }

    // ---------- merging another library in ----------
    /// <summary>
    /// Copies everything from another CaseKeeper database (and its files) into this one, without
    /// removing anything already here. Matching is by content, not by row id: a document is
    /// recognised by its file fingerprint, a category by its code and name, a party by role and
    /// name. That makes importing the same backup twice harmless.
    /// </summary>
    /// <param name="storeFile">
    /// Given a preferred file name and the document's plain bytes, saves it into the target
    /// library (encrypting it if that library is protected) and returns the name actually used.
    /// </param>
    public MergeResult MergeFrom(string otherDbPath, string otherFilesDir, Func<string, byte[], string> storeFile)
    {
        var result = new MergeResult();
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var src = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = otherDbPath, Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        src.Open();

        static List<T> Read<T>(SqliteConnection conn, string sql, Func<SqliteDataReader, T> map)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }

        long Scalar(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        // ---- categories, keyed on code + name
        var catLookup = Read(c, "SELECT Id, Code, Name FROM Categories",
                r => (Id: (long)r[0], Key: $"{r[1] as string}|{r[2] as string}"))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        int nextOrder = (int)Scalar("SELECT COALESCE(MAX(SortOrder),0)+1 FROM Categories");
        var catMap = new Dictionary<long, long>();
        foreach (var row in Read(src, "SELECT * FROM Categories", r => (Id: (long)r["Id"], Cat: new Category
                 {
                     Code = S(r, "Code"), Name = S(r, "Name"), GroupName = S(r, "GroupName"),
                     Description = S(r, "Description"), BundleSection = S(r, "BundleSection"),
                     IsCustom = Convert.ToInt64(r["IsCustom"]) == 1
                 })))
        {
            var key = $"{row.Cat.Code}|{row.Cat.Name}";
            if (catLookup.TryGetValue(key, out var existing)) { catMap[row.Id] = existing; continue; }
            row.Cat.SortOrder = nextOrder++;
            InsertCategory(c, row.Cat);
            catLookup[key] = row.Cat.Id;
            catMap[row.Id] = row.Cat.Id;
            result.CategoriesAdded++;
        }
        long fallbackCategory = catLookup.Values.FirstOrDefault();

        // ---- parties, keyed on role + name
        var partyLookup = Read(c, "SELECT Id, Role, Name FROM Parties",
                r => (Id: (long)r[0], Key: $"{r[1] as string}|{r[2] as string}"))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var partyMap = new Dictionary<long, long>();
        foreach (var row in Read(src, "SELECT * FROM Parties", r => (Id: (long)r["Id"],
                     Role: S(r, "Role"), Name: S(r, "Name"), Rep: S(r, "Representative"),
                     Contact: S(r, "Contact"), Notes: S(r, "Notes"), Order: Convert.ToInt32(r["SortOrder"]))))
        {
            var key = $"{row.Role}|{row.Name}";
            if (partyLookup.TryGetValue(key, out var existing)) { partyMap[row.Id] = existing; continue; }
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"INSERT INTO Parties(Role,Name,IsMe,Representative,Contact,Notes,SortOrder)
                                VALUES($r,$n,0,$rep,$c,$no,$s); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$r", row.Role);
            cmd.Parameters.AddWithValue("$n", row.Name);
            cmd.Parameters.AddWithValue("$rep", row.Rep);
            cmd.Parameters.AddWithValue("$c", row.Contact);
            cmd.Parameters.AddWithValue("$no", row.Notes);
            cmd.Parameters.AddWithValue("$s", row.Order);
            var newId = (long)cmd.ExecuteScalar();
            partyLookup[key] = newId;
            partyMap[row.Id] = newId;
            result.PartiesAdded++;
        }

        // ---- documents, keyed on the file fingerprint
        var haveHashes = new HashSet<string>(
            Read(c, "SELECT FileHash FROM Documents", r => r[0] as string ?? ""), StringComparer.OrdinalIgnoreCase);

        var docMap = new Dictionary<long, long>();
        foreach (var d in Read(src, "SELECT * FROM Documents", ReadDocument))
        {
            if (d.DeletedAt != null) continue;      // it was in the other library's Deleted items
            if (!string.IsNullOrEmpty(d.FileHash) && !haveHashes.Add(d.FileHash)) { result.DocumentsSkipped++; continue; }

            // The bare file name only: a tampered backup must not be able to pull in files from elsewhere on the PC
            var sourceFile = Path.Combine(otherFilesDir, Path.GetFileName(d.StoredFileName ?? ""));
            if (!File.Exists(sourceFile)) { result.FilesMissing++; continue; }

            var storedName = storeFile(d.StoredFileName, File.ReadAllBytes(sourceFile));

            long oldId = d.Id;
            d.StoredFileName = storedName;
            d.CategoryId = catMap.TryGetValue(d.CategoryId, out var cm) ? cm : fallbackCategory;
            d.PartyId = d.PartyId.HasValue && partyMap.TryGetValue(d.PartyId.Value, out var pm) ? pm : null;
            InsertDocument(c, d);
            docMap[oldId] = d.Id;
            result.DocumentsAdded++;

            foreach (var pt in Read(src, $"SELECT PageNumber, Text FROM PageText WHERE DocumentId={oldId}",
                         r => (Page: Convert.ToInt32(r[0]), Text: r[1] as string ?? "")))
            {
                Exec(c, "INSERT INTO PageText(DocumentId,PageNumber,Text) VALUES($id,$p,$t)",
                    ("$id", d.Id), ("$p", pt.Page), ("$t", pt.Text));
                if (!string.IsNullOrWhiteSpace(pt.Text))
                    Exec(c, "INSERT INTO SearchIndex(content,doc_id,page,source) VALUES($t,$id,$p,'page')",
                        ("$t", pt.Text), ("$id", d.Id), ("$p", pt.Page));
            }
            foreach (var pn in Read(src, $"SELECT PageNumber, Note, UpdatedAt FROM PageNotes WHERE DocumentId={oldId}",
                         r => (Page: Convert.ToInt32(r[0]), Note: r[1] as string ?? "", At: r[2] as string ?? "")))
            {
                if (string.IsNullOrWhiteSpace(pn.Note)) continue;
                Exec(c, "INSERT INTO PageNotes(DocumentId,PageNumber,Note,UpdatedAt) VALUES($id,$p,$n,$u)",
                    ("$id", d.Id), ("$p", pn.Page), ("$n", pn.Note), ("$u", pn.At));
                Exec(c, "INSERT INTO SearchIndex(content,doc_id,page,source) VALUES($t,$id,$p,'pagenote')",
                    ("$t", pn.Note), ("$id", d.Id), ("$p", pn.Page));
            }
            ReindexDocumentDetails(c, d);
        }

        // ---- timeline, keyed on date + title + type
        var haveEvents = new HashSet<string>(
            Read(c, "SELECT EventDate, Title, EventType FROM Events", r => $"{r[0]}|{r[1]}|{r[2]}"),
            StringComparer.OrdinalIgnoreCase);
        foreach (var ev in Read(src, "SELECT * FROM Events", r => (Date: S(r, "EventDate"), Time: S(r, "Time"),
                     Title: S(r, "Title"), Desc: S(r, "Description"), Type: S(r, "EventType"),
                     DocId: r["DocumentId"] is long l ? (long?)l : null,
                     Include: Convert.ToInt64(r["IncludeInChronology"]) == 1)))
        {
            if (!haveEvents.Add($"{ev.Date}|{ev.Title}|{ev.Type}")) continue;
            Exec(c, @"INSERT INTO Events(EventDate,Time,Title,Description,EventType,DocumentId,IncludeInChronology)
                      VALUES($d,$tm,$t,$de,$ty,$doc,$ic)",
                ("$d", ev.Date), ("$tm", ev.Time), ("$t", ev.Title), ("$de", ev.Desc), ("$ty", ev.Type),
                ("$doc", ev.DocId.HasValue && docMap.TryGetValue(ev.DocId.Value, out var dm) ? dm : null),
                ("$ic", ev.Include ? 1 : 0));
            result.EventsAdded++;
        }

        // ---- complaints, keyed on subject + recipient + date sent
        var haveComplaints = new HashSet<string>(
            Read(c, "SELECT Subject, Recipient, DateSent FROM Complaints", r => $"{r[0]}|{r[1]}|{r[2]}"),
            StringComparer.OrdinalIgnoreCase);
        foreach (var x in Read(src, "SELECT * FROM Complaints", r => (Id: (long)r["Id"], Recipient: S(r, "Recipient"),
                     RecipientName: S(r, "RecipientName"), Reference: S(r, "Reference"), Subject: S(r, "Subject"),
                     Summary: S(r, "Summary"), Sent: S(r, "DateSent"), Due: S(r, "ResponseDue"),
                     Status: S(r, "Status"), Outcome: S(r, "Outcome"))))
        {
            if (!haveComplaints.Add($"{x.Subject}|{x.Recipient}|{x.Sent}")) continue;
            long newId;
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = @"INSERT INTO Complaints(Recipient,RecipientName,Reference,Subject,Summary,DateSent,ResponseDue,Status,Outcome)
                                    VALUES($r,$rn,$ref,$s,$su,$ds,$rd,$st,$o); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$r", x.Recipient);
                cmd.Parameters.AddWithValue("$rn", x.RecipientName);
                cmd.Parameters.AddWithValue("$ref", x.Reference);
                cmd.Parameters.AddWithValue("$s", x.Subject);
                cmd.Parameters.AddWithValue("$su", x.Summary);
                cmd.Parameters.AddWithValue("$ds", (object)NullIfEmpty(x.Sent) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$rd", (object)NullIfEmpty(x.Due) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$st", x.Status);
                cmd.Parameters.AddWithValue("$o", x.Outcome);
                newId = (long)cmd.ExecuteScalar();
            }
            foreach (var link in Read(src, $"SELECT DocumentId FROM ComplaintDocuments WHERE ComplaintId={x.Id}",
                         r => (long)r[0]))
                if (docMap.TryGetValue(link, out var mapped))
                    Exec(c, "INSERT OR IGNORE INTO ComplaintDocuments(ComplaintId,DocumentId) VALUES($c,$d)",
                        ("$c", newId), ("$d", mapped));
            result.ComplaintsAdded++;
        }

        MergeRecords(c, src, docMap, result);
        AuditLocked(c, "backup.merge", "library", null, "Merged a backup into this library", result.Describe());
        tx.Commit();
        return result;

        static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>Flushes the WAL so the .db file can be safely copied for backup.</summary>
    public void Checkpoint()
    {
        using var c = Open();
        Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
    }
}
