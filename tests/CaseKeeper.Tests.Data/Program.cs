using System.IO;
using CaseKeeper.Data;
using CaseKeeper.Models;
using Microsoft.Data.Sqlite;

static class Program
{
    static int failures;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static void Exec(string db, string pw, string sql)
    {
        var b = new SqliteConnectionStringBuilder { DataSource = db, Pooling = false };
        if (pw != null) b.Password = pw;
        using var c = new SqliteConnection(b.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static void Main(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();
        var dir = Path.Combine(Path.GetTempPath(), "ckt", "dbtest-run");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);

        // ---- 1. a library created by v0.2 (old schema, no new columns or tables) upgrades cleanly
        var oldDb = Path.Combine(dir, "old.db");
        Exec(oldDb, null, @"
CREATE TABLE Categories(Id INTEGER PRIMARY KEY AUTOINCREMENT, Code TEXT, Name TEXT NOT NULL, GroupName TEXT, Description TEXT, BundleSection TEXT, SortOrder INTEGER, IsCustom INTEGER DEFAULT 0);
CREATE TABLE Documents(Id INTEGER PRIMARY KEY AUTOINCREMENT, Title TEXT, CategoryId INTEGER, OriginalFileName TEXT, StoredFileName TEXT, FileExtension TEXT, FileHash TEXT, FileSize INTEGER, DocumentDate TEXT, DateReceived TEXT, Author TEXT, Tags TEXT, Description TEXT, Notes TEXT, IsImportant INTEGER DEFAULT 0, PageCount INTEGER DEFAULT 0, OcrStatus INTEGER DEFAULT 0, ImportedAt TEXT, PartyId INTEGER);
CREATE TABLE PageNotes(DocumentId INTEGER, PageNumber INTEGER, Note TEXT, UpdatedAt TEXT, PRIMARY KEY(DocumentId, PageNumber));
CREATE TABLE PageText(DocumentId INTEGER, PageNumber INTEGER, Text TEXT, PRIMARY KEY(DocumentId, PageNumber));
CREATE TABLE Events(Id INTEGER PRIMARY KEY AUTOINCREMENT, EventDate TEXT, Time TEXT, Title TEXT, Description TEXT, EventType TEXT, DocumentId INTEGER, IncludeInChronology INTEGER DEFAULT 1);
CREATE TABLE Complaints(Id INTEGER PRIMARY KEY AUTOINCREMENT, Recipient TEXT, RecipientName TEXT, Reference TEXT, Subject TEXT, Summary TEXT, DateSent TEXT, ResponseDue TEXT, Status TEXT, Outcome TEXT);
CREATE TABLE ComplaintDocuments(ComplaintId INTEGER, DocumentId INTEGER, PRIMARY KEY(ComplaintId, DocumentId));
CREATE TABLE Settings(Key TEXT PRIMARY KEY, Value TEXT);
CREATE TABLE Parties(Id INTEGER PRIMARY KEY AUTOINCREMENT, Role TEXT, Name TEXT, IsMe INTEGER DEFAULT 0, Representative TEXT, Contact TEXT, Notes TEXT, SortOrder INTEGER DEFAULT 0);
CREATE VIRTUAL TABLE SearchIndex USING fts5(content, doc_id UNINDEXED, page UNINDEXED, source UNINDEXED, tokenize='porter unicode61');
INSERT INTO Categories(Code,Name,GroupName,BundleSection,SortOrder) VALUES('C100','Child arrangements / prohibited steps / specific issue application (Applicant''s)','Court Applications & Forms','B',1);
INSERT INTO Documents(Title,CategoryId,FileHash,FileExtension,ImportedAt) VALUES('Old document',1,'ABC','.pdf','2026-10-01T10:00:00');
INSERT INTO Events(EventDate,Title,EventType) VALUES('2026-10-02','Old event','Other');");
        var db = new Database(oldDb);
        Check(db.GetDocuments().Single().Title == "Old document", "v0.2 document survives the upgrade");
        Check(db.GetEvents().Single().Title == "Old event", "v0.2 timeline survives the upgrade");
        var cats = db.GetCategories();
        Check(cats.Count > 400, $"category catalogue extended ({cats.Count} categories)");
        Check(cats.Count(c => c.Code == "C100") == 2, "existing C100 pair kept, not duplicated");
        Check(cats.Select(c => $"{c.Code}|{c.Name}").Distinct().Count() == cats.Count, "no duplicate categories");
        Check(cats.Any(c => c.Code == "FL701") && cats.Any(c => c.Code == "D84") && cats.Any(c => c.Code == "EX107"), "new forms present (FL701, D84, EX107)");
        var d = db.GetDocuments().Single();
        d.ExhibitRef = "AB1";
        db.UpdateDocument(d);
        Check(db.GetDocument(d.Id).ExhibitRef == "AB1", "new ExhibitRef column works on an upgraded library");
        db.SetFileDates(d.Id, new DateTime(2026, 9, 2, 8, 0, 0), new DateTime(2026, 10, 3, 14, 2, 0), 7);
        var dated = db.GetDocument(d.Id);
        Check(dated.SourceModifiedAt == new DateTime(2026, 9, 2, 8, 0, 0) && dated.SavedAt == new DateTime(2026, 10, 3, 14, 2, 0) && dated.Revision == 7,
            "0.7.0 file dates (last saved, revision) round-trip on an upgraded library");
        db.SetFileDates(d.Id, null, null, null);
        Check(db.GetDocument(d.Id).Revision == 7, "file dates: an empty value never wipes one already recorded");

        // ---- 2. records + audit trail
        var h = new Hearing { HearingDate = new DateTime(2026, 11, 3), HearingType = "Final hearing", Court = "Test" };
        db.Save(h);
        h.Outcome = "Order made";
        db.Save(h);
        Check(db.All<Hearing>().Single().Outcome == "Order made", "record saved and updated");
        var audit = db.GetAudit();
        var upd = audit.FirstOrDefault(a => a.Action == "hearing.update");
        Check(upd != null && upd.Details.Contains("Outcome:") && upd.Details.Contains("Order made"), "audit records exactly what changed");
        Check(audit.Any(a => a.Action == "document.update" && a.Details.Contains("Exhibit")), "document edits audited with old → new values");
        db.SoftDeleteDocument(d.Id);
        Check(db.GetDocuments().Count == 0 && db.GetDeletedDocuments().Count == 1, "soft delete moves to Deleted items");
        db.RestoreDocument(d.Id);
        Check(db.GetDocuments().Count == 1, "restore brings it back");
        for (int i = 0; i < 5; i++) db.SavePageNote(d.Id, 1, "note version " + i);
        Check(db.GetAudit().Count(a => a.Action == "pagenote.edit") == 1, "typing a note doesn't flood the audit trail");
        var v = db.VerifyAudit();
        Check(v.Ok && v.Checked == db.AuditCount(), $"audit chain verifies ({v.Checked} entries)");
        Exec(oldDb, null, "UPDATE AuditLog SET Summary='tampered' WHERE Id=2");
        var v2 = new Database(oldDb).VerifyAudit();
        Check(!v2.Ok && v2.FirstBrokenId == 2, "tampering with an old entry is detected");
        db.Remove<Hearing>(h.Id);
        Check(db.All<Hearing>().Count == 0, "record removed");

        // ---- 3. encryption, snapshots and the conversion done when protection is turned on/off
        var encDb = Path.Combine(dir, "enc.db");
        const string key = "test-key-0123456789";
        var edb = new Database(encDb, key);
        edb.Save(new CaseAction { Title = "Secret task" });
        edb.Checkpoint();
        Check(!Database.CanOpen(encDb, null) && Database.CanOpen(encDb, key), "encrypted database needs its key");
        var snap = Path.Combine(dir, "snap.db");
        edb.SnapshotTo(snap, key);
        SqliteConnection.ClearAllPools();
        Check(Database.CanOpen(snap, key) && !Database.CanOpen(snap, null), "snapshot of an encrypted library is encrypted too");
        Check(new Database(snap, key).All<CaseAction>().Single().Title == "Secret task", "snapshot holds the data");
        // 0.8.0: the case memory lives in the same database, so it's encrypted with everything else
        edb.SaveQuiet(new CaseMemoryEntry { Section = "people", Text = "Applicant: Zebedee Marchbanks-Quill", Sources = 1 });
        edb.SaveQuiet(new CaseLearning { Kind = CaseLearning.NoteKind, Value = "Mum means Zebedee" });
        edb.Checkpoint();
        SqliteConnection.ClearAllPools();
        var reopened = new Database(encDb, key);
        Check(reopened.Query<CaseMemoryEntry>("").Single().Text.Contains("Marchbanks") && reopened.Query<CaseLearning>("").Single().Value == "Mum means Zebedee",
            "case memory and learning round-trip in an encrypted library");
        SqliteConnection.ClearAllPools();
        var rawBytes = File.ReadAllBytes(encDb);
        Check(System.Text.Encoding.UTF8.GetString(rawBytes).IndexOf("Marchbanks", StringComparison.Ordinal) < 0 &&
              System.Text.Encoding.Unicode.GetString(rawBytes).IndexOf("Marchbanks", StringComparison.Ordinal) < 0,
            "case memory: not readable in the encrypted file");
        Check(!Database.RecordTypes.Contains(typeof(CaseMemoryEntry)) && Database.StagingTypes.Contains(typeof(CaseLearning)),
            "case memory: a working table (never merged between libraries)");
        Database.ConvertEncryption(snap, key, null);
        SqliteConnection.ClearAllPools();
        Check(Database.CanOpen(snap, null), "snapshot can be decrypted when protection is turned off");
        var bytes = File.ReadAllBytes(encDb);
        Check(!System.Text.Encoding.UTF8.GetString(bytes).Contains("Secret task"), "no readable case text inside the encrypted file");

        // ---- 4. merging a backup brings records and settings, without overwriting local settings
        var other = Path.Combine(dir, "other.db");
        var odb = new Database(other);
        odb.Save(new ContactSession { SessionDate = new DateTime(2026, 9, 1), Outcome = "Took place as arranged" });
        odb.Save(new Letterhead { Name = "From backup" });
        odb.SetSetting("case.number", "FROMBACKUP");
        odb.SetSetting("prompt.system", "custom prompt");
        // Where case material may be sent is never taken from another library
        odb.SetSetting("ai.provider", "anthropic");
        odb.SetSetting("bulk.provider", "openai");
        odb.SetSetting("ai.anthropic.key", "c2FtcGxlLWNpcGhlcnRleHQ=");
        odb.SetSetting("ai.openai-foundry.resource", "someone-elses-resource");
        odb.SetSetting("ai.cloud.consent.anthropic", "1");
        odb.SetSetting("ai.cloud.mask", "0");
        odb.SetSetting("bulk.cloudReview.skip.anthropic", "1");
        odb.Checkpoint();
        db = new Database(oldDb);
        db.SetSetting("case.number", "LOCAL");
        Directory.CreateDirectory(Path.Combine(dir, "files"));
        SqliteConnection.ClearAllPools();
        var result = db.MergeFrom(other, Path.Combine(dir, "files"), (n, b) => n);
        Check(result.RecordsAdded >= 2, $"merge brought {result.RecordsAdded} records");
        Check(db.GetSetting("case.number") == "LOCAL", "merge kept the local case number");
        Check(db.GetSetting("prompt.system") == "custom prompt", "merge brought settings this library didn't have");
        Check(new[] { "ai.provider", "bulk.provider", "ai.anthropic.key", "ai.openai-foundry.resource", "ai.cloud.consent.anthropic", "ai.cloud.mask", "bulk.cloudReview.skip.anthropic" }
                  .All(k => string.IsNullOrEmpty(db.GetSetting(k, ""))),
              "merge never takes another library's cloud AI choice, keys, resource, consent or masking setting");
        var again = db.MergeFrom(other, Path.Combine(dir, "files"), (n, b) => n);
        Check(again.RecordsAdded == 0, "merging the same backup twice adds nothing");
        Check(db.VerifyAudit().Ok == false, "(chain still shows the earlier tamper – as it should)");

        Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILURE(S)");
        SqliteConnection.ClearAllPools();
        Environment.Exit(failures);
    }
}
