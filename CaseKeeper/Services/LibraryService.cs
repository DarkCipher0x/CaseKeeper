using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CaseKeeper.Data;
using CaseKeeper.Models;
using Microsoft.Data.Sqlite;

namespace CaseKeeper.Services;

/// <summary>
/// Manages the document library: a folder holding copies of every imported file, the database,
/// and (when the library is password protected) the vault that holds the key material.
/// Originals you import are never modified or moved.
/// </summary>
public class LibraryService
{
    public string RootFolder { get; }
    public string FilesFolder { get; }
    public string ExportsFolder { get; }
    public string DatabasePath { get; }
    public Database Db { get; }
    public Vault Vault { get; }

    public LibraryService(string rootFolder, Vault vault = null)
    {
        RootFolder = rootFolder;
        FilesFolder = Path.Combine(rootFolder, "Files");
        ExportsFolder = Path.Combine(rootFolder, "Exports");
        DatabasePath = Path.Combine(rootFolder, "casekeeper.db");
        Directory.CreateDirectory(FilesFolder);
        Directory.CreateDirectory(ExportsFolder);
        Vault = vault ?? Vault.Load(rootFolder);
        Db = new Database(DatabasePath, Vault.DatabasePassword);
    }

    public bool IsOnNetworkDrive => Database.IsNetworkPath(RootFolder);

    // ================================================================== where the library lives
    public static string PointerFile => Path.Combine(AppPaths.SettingsFolder, "library-location.json");

    private sealed class LocationPointer { public string Path { get; set; } }

    /// <summary>The default library folder (see <see cref="AppPaths"/> for why the Store copy differs).</summary>
    public static string DefaultRoot => AppPaths.DefaultLibrary;

    /// <summary>
    /// Where the library should be opened from. An explicit CASEKEEPER_LIBRARY environment
    /// variable wins; then a location the user has chosen; otherwise the default folder.
    /// </summary>
    public static string ResolveRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable("CASEKEEPER_LIBRARY");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        try
        {
            if (File.Exists(PointerFile))
            {
                var saved = JsonSerializer.Deserialize<LocationPointer>(File.ReadAllText(PointerFile))?.Path;
                if (!string.IsNullOrWhiteSpace(saved))
                {
                    if (AppPaths.PackagedLocationProblem(saved) == null) return saved;
                    AppLog.Warn("Library", "Ignored a saved library location inside AppData (not safe for the Store version)");
                }
            }
        }
        catch { /* unreadable pointer - fall back to the default */ }
        return DefaultRoot;
    }

    /// <summary>
    /// The library the website version left in %LOCALAPPDATA%\CaseKeeper, when this is a Store copy
    /// opening for the first time and could copy it across. Null otherwise.
    /// </summary>
    public static string FindWebsiteLibraryToCopy(string root)
    {
        if (!AppPackage.IsPackaged) return null;
        if (!string.Equals(Path.GetFullPath(root).TrimEnd('\\'), Path.GetFullPath(DefaultRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return null;
        if (File.Exists(Path.Combine(root, "casekeeper.db"))) return null;
        var website = AppPaths.WebsiteDefaultLibrary;
        return File.Exists(Path.Combine(website, "casekeeper.db")) ? website : null;
    }

    /// <summary>
    /// Copies a whole library into an empty folder and checks every byte arrived. The source is only
    /// read, never changed, so the website version keeps working with its own copy. Its logs and
    /// "in use" marker are not copied.
    /// </summary>
    public static int CopyLibrary(string source, string destination, IProgress<string> progress)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        var logs = Path.Combine(source, "Logs") + "\\";
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(logs, StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileName(f) != LibraryLock.FileName && !f.EndsWith(".tmp") && !f.EndsWith(".part"))
            .ToList();
        var copied = new List<string>();
        try
        {
            foreach (var file in files)
            {
                var destFile = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                File.Copy(file, destFile, false);       // never overwrites anything already there
                copied.Add(destFile);
                if (!HashesMatch(file, destFile))
                    throw new IOException($"'{Path.GetRelativePath(source, file)}' did not copy correctly.");
                if (copied.Count % 10 == 0 || copied.Count == files.Count) progress?.Report($"Copying… {copied.Count} of {files.Count} files");
            }
        }
        catch
        {
            // Take back exactly what was copied, so a half-finished library is never opened
            foreach (var f in copied) try { File.Delete(f); } catch { /* best effort */ }
            throw;
        }
        return files.Count;
    }

    public static bool LocationIsFixedByEnvironment =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CASEKEEPER_LIBRARY"));

    private static void SaveLocation(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PointerFile)!);
        File.WriteAllText(PointerFile, JsonSerializer.Serialize(new LocationPointer { Path = path },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>What a folder looks like before we agree to move a library into it.</summary>
    public static string DescribeTargetProblem(string target, string currentRoot)
    {
        if (string.IsNullOrWhiteSpace(target)) return "Choose a folder.";
        string full;
        try { full = Path.GetFullPath(target); }
        catch { return "That doesn't look like a valid folder path."; }

        if (AppPaths.PackagedLocationProblem(full) is { } packaged) return packaged;
        if (string.Equals(full.TrimEnd('\\'), Path.GetFullPath(currentRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return "That's where the library already is.";
        if (full.StartsWith(Path.GetFullPath(currentRoot).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            return "That folder is inside the current library, so it can't hold the copy.";
        if (File.Exists(Path.Combine(full, "casekeeper.db")))
            return "There is already a CaseKeeper library in that folder. Choose an empty folder instead.";

        try
        {
            Directory.CreateDirectory(full);
            var probe = Path.Combine(full, ".casekeeper-write-test");
            File.WriteAllText(probe, "test");
            File.Delete(probe);
        }
        catch (Exception ex) { return "That folder can't be written to: " + ex.Message; }
        return null;
    }

    public long EstimateSizeBytes()
    {
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(RootFolder, "*", SearchOption.AllDirectories))
            try { total += new FileInfo(f).Length; } catch { /* skip unreadable */ }
        return total;
    }

    /// <summary>
    /// Copies the whole library to a new folder, checks every byte arrived, records the new
    /// location, and only then removes the originals. If anything fails before the check passes,
    /// the original library is left exactly as it was.
    /// </summary>
    public static MoveOutcome MoveLibrary(LibraryService current, string target, IProgress<string> progress)
    {
        var source = Path.GetFullPath(current.RootFolder);
        var destination = Path.GetFullPath(target);
        var outcome = new MoveOutcome { From = source, To = destination };

        current.Db.Checkpoint();
        SqliteConnection.ClearAllPools();
        Directory.CreateDirectory(destination);

        // ---- copy
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
            .Where(f => !IsTransient(f)).ToList();
        int done = 0;
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(source, file);
            var destFile = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(file, destFile, true);
            done++;
            if (done % 10 == 0 || done == files.Count)
                progress?.Report($"Copying… {done} of {files.Count} files");
        }
        outcome.FilesCopied = files.Count;

        // ---- verify every file byte-for-byte before anything is deleted
        progress?.Report("Checking the copy…");
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(source, file);
            var destFile = Path.Combine(destination, relative);
            if (!File.Exists(destFile))
                throw new IOException($"'{relative}' did not copy across. The original library has been left untouched.");
            if (!HashesMatch(file, destFile))
                throw new IOException($"'{relative}' did not copy correctly. The original library has been left untouched.");
            outcome.BytesVerified += new FileInfo(destFile).Length;
        }

        // ---- and prove the copied database actually opens and holds the same number of documents
        progress?.Report("Checking the copied database…");
        int expected = current.Db.GetDocuments().Count;
        SqliteConnection.ClearAllPools();
        var movedDbPath = Path.Combine(destination, "casekeeper.db");
        var movedDb = new Database(movedDbPath, current.Vault.DatabasePassword);
        int actual = movedDb.GetDocuments().Count;
        if (actual != expected)
            throw new IOException($"The copied library reports {actual} documents but the original has {expected}. " +
                                  "The original library has been left untouched.");
        outcome.DocumentsVerified = actual;
        SqliteConnection.ClearAllPools();

        // ---- only now is it safe to point at the new copy and remove the old one
        SaveLocation(destination);
        LibraryLock.Release();      // stop refreshing the marker in a folder we're about to delete
        progress?.Report("Removing the old copy…");
        try
        {
            Directory.Delete(source, true);
            outcome.OriginalRemoved = true;
        }
        catch (Exception ex)
        {
            // The move itself succeeded; the leftovers are a tidiness problem, not a data problem
            outcome.CleanupWarning = $"The library was moved successfully, but the old folder could not be " +
                                     $"deleted:\n{source}\n\n{ex.Message}\n\nDelete it yourself once you're happy " +
                                     $"everything is working. It still contains your documents.";
        }
        return outcome;
    }

    /// <summary>Points CaseKeeper at a library that already exists somewhere else, without copying.</summary>
    public static void UseExistingLibraryAt(string path) => SaveLocation(Path.GetFullPath(path));

    // ------------------------------------------------------------------ recent libraries (for people with several cases)
    public static string RecentFile => Path.Combine(AppPaths.SettingsFolder, "recent-libraries.json");

    /// <summary>Libraries opened on this PC, most recent first, for quick switching between cases.</summary>
    public static List<string> RecentLibraries()
    {
        try
        {
            if (!File.Exists(RecentFile)) return new List<string>();
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(RecentFile)) ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    public static void RememberLibrary(string root)
    {
        try
        {
            var list = RecentLibraries();
            list.RemoveAll(p => string.Equals(p, root, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, Path.GetFullPath(root));
            Directory.CreateDirectory(Path.GetDirectoryName(RecentFile)!);
            File.WriteAllText(RecentFile, JsonSerializer.Serialize(list.Take(10).ToList()));
        }
        catch (Exception ex) { AppLog.Warn("Library", "Couldn't update the recent libraries list", ex); }
    }

    public static void ForgetLibrary(string root)
    {
        try
        {
            var list = RecentLibraries();
            list.RemoveAll(p => string.Equals(p, root, StringComparison.OrdinalIgnoreCase));
            File.WriteAllText(RecentFile, JsonSerializer.Serialize(list));
        }
        catch { /* best effort */ }
    }

    /// <summary>What we found in a folder someone wants to switch to, without moving anything.</summary>
    public sealed class FolderCheck
    {
        /// <summary>Null when the folder can be used; otherwise why not.</summary>
        public string Problem;
        public bool HasLibrary, IsProtected, IsNetwork;
        public LibraryLock.Holder OpenElsewhere;
        /// <summary>A library one level down, for when someone picks the parent folder by mistake.</summary>
        public string NestedLibrary;
    }

    /// <summary>
    /// Checks a folder before switching to it. Unlike a move, the folder has to exist already
    /// (a mistyped path shouldn't quietly create a new, empty library) and nothing in it is changed.
    /// </summary>
    public static FolderCheck InspectFolder(string target, string currentRoot)
    {
        var check = new FolderCheck();
        if (string.IsNullOrWhiteSpace(target)) { check.Problem = "Choose a folder."; return check; }
        string full;
        try { full = Path.GetFullPath(target); }
        catch { check.Problem = "That doesn't look like a valid folder path."; return check; }

        if (AppPaths.PackagedLocationProblem(full) is { } packaged) { check.Problem = packaged; return check; }
        if (string.Equals(full.TrimEnd('\\'), Path.GetFullPath(currentRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            check.Problem = "That's the library CaseKeeper is already using.";
            return check;
        }
        if (!Directory.Exists(full))
        {
            check.Problem = "That folder doesn't exist. If it's on a network drive or NAS, check it's connected.";
            return check;
        }

        check.IsNetwork = Database.IsNetworkPath(full);
        check.HasLibrary = File.Exists(Path.Combine(full, "casekeeper.db"));
        check.IsProtected = File.Exists(Path.Combine(full, Vault.FileName));
        if (!check.HasLibrary && File.Exists(Path.Combine(full, "CaseKeeper", "casekeeper.db")))
            check.NestedLibrary = Path.Combine(full, "CaseKeeper");
        if (check.HasLibrary) check.OpenElsewhere = LibraryLock.FindOtherHolder(full);

        try
        {
            var probe = Path.Combine(full, ".casekeeper-write-test");
            File.WriteAllText(probe, "test");
            File.Delete(probe);
        }
        catch (Exception ex) { check.Problem = "CaseKeeper needs to be able to write to that folder: " + ex.Message; }
        return check;
    }

    private static bool IsTransient(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith("-wal") || name.EndsWith("-shm") || name.EndsWith(".converting") || name.EndsWith(".tmp") ||
               name == LibraryLock.FileName;
    }

    private static bool HashesMatch(string a, string b)
    {
        using var sa = File.OpenRead(a);
        using var sb = File.OpenRead(b);
        return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
    }

    // ================================================================== documents
    // Only the bare file name is ever used, so a record restored from a tampered backup can't point
    // outside the Files folder (and then be read, overwritten or deleted there)
    public string GetFilePath(CaseDocument d) => Path.Combine(FilesFolder, Path.GetFileName(d.StoredFileName ?? ""));

    public static string ComputeHash(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>The document's real contents, decrypted if the library is protected.</summary>
    public byte[] ReadDocumentBytes(CaseDocument d) => Vault.UnprotectFile(File.ReadAllBytes(GetFilePath(d)));

    public Stream OpenDocument(CaseDocument d) => new MemoryStream(ReadDocumentBytes(d), writable: false);

    /// <summary>Writes a document into the library, encrypting it when the library is protected.</summary>
    private void WriteStoredFile(string storedName, byte[] plain) =>
        File.WriteAllBytes(Path.Combine(FilesFolder, storedName), Vault.ProtectFile(plain));

    /// <summary>Copies a file into the library and records it. Page counting and text scanning happen afterwards.</summary>
    public CaseDocument Import(string sourcePath, CaseDocument details, string hash = null)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        var stored = $"{Guid.NewGuid():N}{ext}";
        // Read once, and fingerprint exactly the bytes being stored, so a file that changes while it's
        // being imported can never end up with a fingerprint that doesn't match the library's copy
        var bytes = File.ReadAllBytes(sourcePath);
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (hash != null && !string.Equals(hash, actual, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The file changed while it was being imported. Import it again.");
        WriteStoredFile(stored, bytes);

        details.OriginalFileName = Path.GetFileName(sourcePath);
        details.StoredFileName = stored;
        details.FileExtension = ext;
        details.FileHash = actual;
        details.FileSize = bytes.LongLength;
        details.ImportedAt = DateTime.Now;
        details.PageCount = FileKinds.IsImage(ext) || FileKinds.IsText(ext) || ext == ".docx" ? 1 : 0;
        details.OcrStatus = OcrService.CanExtract(ext) ? OcrStatus.Pending : OcrStatus.NotApplicable;
        if (string.IsNullOrWhiteSpace(details.Title)) details.Title = Path.GetFileNameWithoutExtension(sourcePath);
        try { Db.AddDocument(details); }
        catch
        {
            // Never leave an orphaned copy behind when the database refused the record
            try { File.Delete(Path.Combine(FilesFolder, stored)); } catch { /* best effort */ }
            throw;
        }
        return details;
    }

    // ------------------------------------------------------------------ one text scan per document at a time
    private static readonly HashSet<long> TextInProgress = new();

    /// <summary>True while a document's text is being read, by the background queue or a bulk import.</summary>
    public static bool IsTextInProgress(long documentId)
    {
        lock (TextInProgress) return TextInProgress.Contains(documentId);
    }

    /// <summary>
    /// Saves a document we already hold the bytes for (importing from a backup), avoiding any
    /// name clash and encrypting it if this library is protected. Returns the name used.
    /// </summary>
    public string StoreImportedFile(string preferredName, byte[] plain)
    {
        // The name comes from the backup's database: keep only a plain file name inside Files
        var name = Path.GetFileName(preferredName ?? "");
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            File.Exists(Path.Combine(FilesFolder, name)))
            name = $"{Guid.NewGuid():N}{FileKinds.SafeExtension(name)}";
        WriteStoredFile(name, plain);
        return name;
    }

    /// <summary>Counts pages and extracts searchable text for a document. Safe to call from a background thread.</summary>
    public async Task ProcessTextAsync(CaseDocument d, IProgress<string> progress = null, Windows.Media.Ocr.OcrEngine engine = null)
    {
        // The background queue and a bulk import can both ask for the same document; only one reads it
        lock (TextInProgress)
            if (!TextInProgress.Add(d.Id)) return;
        try { await ProcessTextCoreAsync(d, progress, engine); }
        finally { lock (TextInProgress) TextInProgress.Remove(d.Id); }
    }

    private async Task ProcessTextCoreAsync(CaseDocument d, IProgress<string> progress, Windows.Media.Ocr.OcrEngine engine)
    {
        byte[] content;
        try { content = ReadDocumentBytes(d); }
        catch
        {
            Db.SetOcrStatus(d.Id, OcrStatus.Failed, null);
            d.OcrStatus = OcrStatus.Failed;
            return;
        }

        int? pages = null;
        try
        {
            if (d.IsPdf) pages = await PdfRenderer.CountPagesAsync(content);
        }
        catch { /* encrypted or damaged PDF - handled below */ }

        if (!OcrService.CanExtract(d.FileExtension))
        {
            Db.SetOcrStatus(d.Id, OcrStatus.NotApplicable, pages);
            d.OcrStatus = OcrStatus.NotApplicable;
            return;
        }

        try
        {
            Db.SetOcrStatus(d.Id, OcrStatus.Running, pages);
            var text = await OcrService.ExtractAsync(content, d.FileExtension, progress, engine);
            Db.SavePageText(d.Id, text);
            pages ??= text.Count;
            Db.SetOcrStatus(d.Id, OcrStatus.Done, pages);
            d.OcrStatus = OcrStatus.Done;
        }
        catch (Exception ex)
        {
            AppLog.Warn("OCR", $"Text scan failed for document #{d.Id} ({d.FileExtension})", ex);
            Db.SetOcrStatus(d.Id, OcrStatus.Failed, pages);
            d.OcrStatus = OcrStatus.Failed;
        }
        if (pages.HasValue) d.PageCount = pages.Value;
    }

    /// <summary>Moves a document to Deleted items. Nothing is lost until it is purged.</summary>
    public void Delete(CaseDocument d) => Db.SoftDeleteDocument(d.Id);

    public void Restore(CaseDocument d) => Db.RestoreDocument(d.Id);

    /// <summary>Removes a document and its file for good.</summary>
    public void Purge(CaseDocument d)
    {
        Db.DeleteDocument(d.Id);
        try { File.Delete(GetFilePath(d)); }
        catch (Exception ex) { AppLog.Warn("Library", $"Couldn't remove the file for document #{d.Id}", ex); }
    }

    /// <summary>Purges anything that has sat in Deleted items for longer than <paramref name="days"/>.</summary>
    public int PurgeExpired(int days)
    {
        if (days <= 0) return 0;
        int n = 0;
        foreach (var d in Db.GetDeletedDocuments().Where(d => d.DeletedAt < DateTime.Now.AddDays(-days)))
        {
            Purge(d);
            n++;
        }
        if (n > 0) AppLog.Info("Library", $"Emptied {n} document(s) from Deleted items after {days} days");
        return n;
    }

    // ================================================================== integrity
    public sealed class IntegrityReport
    {
        public int Checked, Missing, Changed, Unreadable;
        /// <summary>Bulk-import suggestions or file records left pointing at documents that no longer exist (tidied away).</summary>
        public int StagingTidied;
        public List<string> Problems { get; } = new();
        public bool Ok => Missing + Changed + Unreadable == 0;
        public string Describe() => (Ok
            ? $"All {Checked} document file(s) are present and unchanged since they were imported (SHA-256 fingerprints match)."
            : $"{Checked} checked: {Missing} missing, {Changed} changed, {Unreadable} unreadable.\n\n" + string.Join("\n", Problems.Take(30))) +
            (StagingTidied > 0 ? $"\n\n{StagingTidied} leftover import record(s) for documents that no longer exist were tidied away." : "");
    }

    /// <summary>
    /// Re-reads every document and checks its SHA-256 fingerprint against the one recorded at
    /// import. A match proves the file is byte-for-byte what was originally added.
    /// </summary>
    public IntegrityReport VerifyIntegrity(IProgress<string> progress)
    {
        var report = new IntegrityReport();
        var docs = Db.GetDocuments().Concat(Db.GetDeletedDocuments()).ToList();
        foreach (var d in docs)
        {
            report.Checked++;
            if (report.Checked % 5 == 0) progress?.Report($"Checking documents… {report.Checked} of {docs.Count}");
            var path = GetFilePath(d);
            if (!File.Exists(path)) { report.Missing++; report.Problems.Add($"Missing: {d.Title}"); continue; }
            try
            {
                var hash = Convert.ToHexString(SHA256.HashData(ReadDocumentBytes(d)));
                if (!string.Equals(hash, d.FileHash, StringComparison.OrdinalIgnoreCase))
                {
                    report.Changed++;
                    report.Problems.Add($"Changed since import: {d.Title}");
                }
            }
            catch (Exception ex)
            {
                report.Unreadable++;
                report.Problems.Add($"Unreadable: {d.Title} ({ex.Message})");
            }
        }
        try
        {
            var (findings, files) = Db.CheckStaging(tidy: true);
            report.StagingTidied = findings + files;
        }
        catch (Exception ex) { AppLog.Warn("Integrity", "Couldn't check the bulk-import records", ex); }
        Db.Audit("integrity.verify", "library", null,
            report.Ok ? $"Integrity check passed ({report.Checked} documents)" : "Integrity check found problems",
            report.Describe());
        return report;
    }

    // ================================================================== automatic snapshots
    public string SnapshotsFolder => Path.Combine(RootFolder, "Snapshots");

    /// <summary>
    /// Saves a point-in-time copy of the database (encrypted with the library's key when the library is
    /// protected). Document files never change after import, so a snapshot plus the Files folder is a
    /// complete restore point.
    /// </summary>
    public string TakeSnapshot(string reason)
    {
        Directory.CreateDirectory(SnapshotsFolder);
        var path = Path.Combine(SnapshotsFolder, $"casekeeper-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        Db.SnapshotTo(path, Vault.DatabasePassword);
        AppLog.Info("Snapshot", $"Saved database snapshot ({reason})");
        return path;
    }

    public List<FileInfo> ListSnapshots() =>
        Directory.Exists(SnapshotsFolder)
            ? new DirectoryInfo(SnapshotsFolder).GetFiles("casekeeper-*.db").OrderByDescending(f => f.Name).ToList()
            : new List<FileInfo>();

    /// <summary>Takes a daily snapshot if the last one is old enough, then keeps only the newest <paramref name="keep"/>.</summary>
    public void SnapshotIfDue(int keep)
    {
        if (keep <= 0) return;
        try
        {
            var latest = ListSnapshots().FirstOrDefault();
            if (latest == null || DateTime.Now - latest.LastWriteTime > TimeSpan.FromHours(20)) TakeSnapshot("daily");
            foreach (var old in ListSnapshots().Skip(keep)) old.Delete();
        }
        catch (Exception ex) { AppLog.Error("Snapshot", "Automatic snapshot failed", ex); }
    }

    /// <summary>Puts a snapshot back as the live database. The current one is set aside first. Restart afterwards.</summary>
    public void RestoreSnapshot(string snapshotPath)
    {
        if (!Database.CanOpen(snapshotPath, Vault.DatabasePassword))
            throw new InvalidDataException("That snapshot can't be opened with this library's key.");
        Db.Audit("snapshot.restore", "library", null, "Restored the database from a snapshot", Path.GetFileName(snapshotPath));
        TakeSnapshot("before restoring an older snapshot");
        Db.Checkpoint();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { DatabasePath + "-wal", DatabasePath + "-shm" })
            if (File.Exists(f)) File.Delete(f);
        File.Copy(snapshotPath, DatabasePath, true);
    }

    // ================================================================== exports (bundles, letters, reports)
    public bool IsInsideExports(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(ExportsFolder).TrimEnd('\\') + "\\";
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Saves an exported file. Inside a protected library's Exports folder it is encrypted like
    /// everything else; anywhere else it's written as an ordinary readable file, because that's
    /// what the person asked for.
    /// </summary>
    public void WriteExport(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".part";
        File.WriteAllBytes(temp, IsInsideExports(path) && Vault.IsEncrypted ? Vault.ProtectFile(data) : data);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    /// <summary>Encrypts a file just written into Exports by something else (the bundle builder).</summary>
    public void ProtectExportInPlace(string path)
    {
        if (!Vault.IsEncrypted || !IsInsideExports(path) || !File.Exists(path)) return;
        var bytes = File.ReadAllBytes(path);
        if (!Vault.LooksEncrypted(bytes)) WriteAtomic(path, Vault.ProtectFile(bytes));
    }

    /// <summary>The readable contents of an exported file, decrypting it if needed.</summary>
    public byte[] ReadExport(string path) => Vault.UnprotectFile(File.ReadAllBytes(path));

    /// <summary>
    /// A path another program can open: the file itself when it's readable, otherwise a decrypted
    /// temporary copy that is wiped when CaseKeeper closes.
    /// </summary>
    public string ReadableExportPath(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (!Vault.LooksEncrypted(bytes)) return path;
        var dir = Path.Combine(TempCopiesFolder, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Path.GetFileName(path));
        File.WriteAllBytes(dest, Vault.UnprotectFile(bytes));
        return dest;
    }

    private void ConvertExportsAndSnapshots(bool encrypt, string oldDbPassword, string newDbPassword, IProgress<string> progress)
    {
        if (Directory.Exists(ExportsFolder))
        {
            progress?.Report(encrypt ? "Encrypting saved exports…" : "Decrypting saved exports…");
            foreach (var f in Directory.EnumerateFiles(ExportsFolder, "*", SearchOption.AllDirectories).ToList())
            {
                try
                {
                    var bytes = File.ReadAllBytes(f);
                    bool isEnc = Vault.LooksEncrypted(bytes);
                    if (encrypt && !isEnc) WriteAtomic(f, Vault.ProtectFile(bytes));
                    else if (!encrypt && isEnc) WriteAtomic(f, Vault.UnprotectFile(bytes));
                }
                catch (Exception ex) { AppLog.Warn("Protection", "Couldn't convert an exported file", ex); }
            }
        }
        foreach (var snap in ListSnapshots())
        {
            progress?.Report(encrypt ? "Encrypting snapshots…" : "Decrypting snapshots…");
            try { Database.ConvertEncryption(snap.FullName, oldDbPassword, newDbPassword); }
            catch (Exception ex)
            {
                // A snapshot that can't be converted would otherwise sit there unreadable (or readable); remove it
                AppLog.Warn("Protection", "Couldn't convert a snapshot, so it was removed", ex);
                try { snap.Delete(); } catch { /* ignore */ }
            }
        }
    }

    // ================================================================== temporary readable copies
    /// <summary>Where decrypted copies for sharing or opening in other programs go. Emptied at start and exit.</summary>
    public static string TempCopiesFolder => Path.Combine(Path.GetTempPath(), "CaseKeeper-Share");

    public static void PurgeTempCopies()
    {
        try
        {
            if (!Directory.Exists(TempCopiesFolder)) return;
            foreach (var f in Directory.EnumerateFiles(TempCopiesFolder, "*", SearchOption.AllDirectories))
                try { File.Delete(f); } catch { /* still open in another program; removed next time */ }
            foreach (var dir in Directory.EnumerateDirectories(TempCopiesFolder))
                try { Directory.Delete(dir, true); } catch { /* as above */ }
        }
        catch (Exception ex) { AppLog.Warn("Library", "Couldn't tidy temporary copies", ex); }
    }

    /// <summary>
    /// Writes a readable, decrypted copy of a document somewhere outside the library, for sharing
    /// or opening in another program.
    /// </summary>
    public string MakeFriendlyCopy(CaseDocument d, string folder = null)
    {
        folder ??= TempCopiesFolder;
        Directory.CreateDirectory(folder);
        var name = SafeFileName(d.Title);
        if (d.DocumentDate.HasValue) name = $"{d.DocumentDate:yyyy-MM-dd} {name}";
        var dest = Path.Combine(folder, name + FileKinds.SafeExtension(d.FileExtension));
        File.WriteAllBytes(dest, ReadDocumentBytes(d));
        return dest;
    }

    public static string SafeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((s ?? "document").Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        if (clean.Length > 90) clean = clean[..90];
        return string.IsNullOrWhiteSpace(clean) ? "document" : clean;
    }

    // ================================================================== protection
    /// <summary>
    /// Turns on password protection: encrypts every document file, then the database.
    /// The app must restart afterwards so everything reopens with the new key.
    /// </summary>
    public void EnableProtection(string password, IProgress<string> progress)
    {
        if (Vault.IsEncrypted) throw new InvalidOperationException("This library is already protected.");
        Db.Audit("protection.enable", "library", null, "Turned on password protection",
            "Documents, database, snapshots and saved exports encrypted with AES-256");
        Vault.Create(password);
        EncryptAllFiles(progress);
        ConvertExportsAndSnapshots(true, null, Vault.DatabasePassword, progress);
        progress?.Report("Encrypting the database…");
        Db.Checkpoint();
        Database.ConvertEncryption(DatabasePath, null, Vault.DatabasePassword);
    }

    /// <summary>Turns protection off again, leaving an ordinary unencrypted library.</summary>
    public void DisableProtection(IProgress<string> progress)
    {
        if (!Vault.IsUnlocked) throw new InvalidOperationException("The library is locked.");
        Db.Audit("protection.disable", "library", null, "Turned off password protection", "");
        ConvertExportsAndSnapshots(false, Vault.DatabasePassword, null, progress);
        progress?.Report("Decrypting the database…");
        Db.Checkpoint();
        Database.ConvertEncryption(DatabasePath, Vault.DatabasePassword, null);

        var files = Directory.GetFiles(FilesFolder);
        for (int i = 0; i < files.Length; i++)
        {
            var bytes = File.ReadAllBytes(files[i]);
            if (Vault.LooksEncrypted(bytes)) WriteAtomic(files[i], Vault.UnprotectFile(bytes));
            if (i % 5 == 0) progress?.Report($"Decrypting documents… {i + 1} of {files.Length}");
        }
        Vault.Remove();
    }

    /// <summary>Encrypts any file in the library that isn't already encrypted. Safe to re-run.</summary>
    public void EncryptAllFiles(IProgress<string> progress)
    {
        var files = Directory.GetFiles(FilesFolder);
        for (int i = 0; i < files.Length; i++)
        {
            var bytes = File.ReadAllBytes(files[i]);
            if (!Vault.LooksEncrypted(bytes)) WriteAtomic(files[i], Vault.ProtectFile(bytes));
            if (i % 5 == 0) progress?.Report($"Encrypting documents… {i + 1} of {files.Length}");
        }
    }

    /// <summary>Write via a temporary file so a crash can't leave a half-written document.</summary>
    private static void WriteAtomic(string path, byte[] data)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Delete(path);
        File.Move(temp, path);
    }

    /// <summary>
    /// Wipes the library back to how it was on first run: every document, note, event, complaint,
    /// party, setting and the password protection itself. This is a real delete - it also removes
    /// the safety copies that a previous restore left behind.
    /// The app must restart immediately after calling this.
    /// </summary>
    public void FactoryReset()
    {
        SqliteConnection.ClearAllPools();

        foreach (var f in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm",
                     Path.Combine(RootFolder, Vault.FileName) })
            if (File.Exists(f)) File.Delete(f);

        foreach (var dir in new[] { FilesFolder, ExportsFolder, SnapshotsFolder })
            if (Directory.Exists(dir)) Directory.Delete(dir, true);

        foreach (var dir in Directory.EnumerateDirectories(RootFolder, "_before_restore_*"))
            Directory.Delete(dir, true);
        var staging = Path.Combine(RootFolder, "_restore");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);

        Directory.CreateDirectory(FilesFolder);
        Directory.CreateDirectory(ExportsFolder);
    }
}

public class MoveOutcome
{
    public string From, To, CleanupWarning;
    public int FilesCopied, DocumentsVerified;
    public long BytesVerified;
    public bool OriginalRemoved;

    public string Describe()
    {
        var mb = Math.Round(BytesVerified / 1024d / 1024d, 1);
        var lines = new List<string>
        {
            $"The library has been moved to:\n{To}",
            "",
            $"{FilesCopied} file(s) copied and checked ({mb} MB).",
            $"{DocumentsVerified} document(s) confirmed readable at the new location."
        };
        lines.Add(OriginalRemoved ? "The old copy has been deleted." : "The old copy could not be deleted.");
        return string.Join("\n", lines);
    }
}
