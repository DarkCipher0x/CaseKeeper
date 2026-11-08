using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CaseKeeper.Data;
using CaseKeeper.Models;

namespace CaseKeeper.Services.BulkImport;

/// <summary>The choices made in the Bulk Case Import wizard. Saved with the job, and (optionally) for next time.</summary>
public sealed class BulkOptions
{
    public ScanOptions Scan { get; set; } = new();
    /// <summary>Keep each file's path inside the chosen folder with the document. Off keeps the file name only.</summary>
    public bool PreserveStructure { get; set; } = true;
    /// <summary>"none" (keep paths only), "tags" (folder names become tags) or "categories" (folders mapped to categories).</summary>
    public string FolderMapping { get; set; } = "none";
    public bool RunOcr { get; set; } = true;
    public bool RunAi { get; set; } = true;
    public DuplicatePolicy Duplicates { get; set; } = DuplicatePolicy.SkipAndRecord;
    public bool Remember { get; set; } = true;
    /// <summary>Library check: which sections to fill (see <see cref="LibraryCheck.Sections"/>). Null fills everything.</summary>
    public List<string> Sections { get; set; }
    /// <summary>This job is a Library check of documents already in the library.</summary>
    public bool LibraryCheck { get; set; }
    /// <summary>Write a case overview (AI → Reports) when the check finishes.</summary>
    public bool WriteOverview { get; set; }

    public string Describe() =>
        $"Subfolders: {(Scan.IncludeSubfolders ? "yes" : "no")} · hidden files: {(Scan.IncludeHiddenFiles ? "yes" : "no")} · " +
        $"links followed: {(Scan.FollowLinks ? "yes" : "no")} · folder paths kept: {(PreserveStructure ? "yes" : "no")} · " +
        $"folder mapping: {FolderMapping} · duplicates: {Duplicates} · text reading: {(RunOcr ? "yes" : "later")} · AI: {(RunAi ? "yes" : "no")}" +
        (Sections == null ? "" : $" · sections: {string.Join(", ", Sections)}") + (WriteOverview ? " · case overview: yes" : "");
}

public sealed class BulkProgress
{
    public long JobId { get; init; }
    public ImportPhase Phase { get; init; }
    public int Done { get; init; }
    public int Total { get; init; }
    /// <summary>File name of the item being worked on. Shown on screen only; never logged.</summary>
    public string Current { get; init; } = "";
    public string Message { get; init; } = "";
    /// <summary>Only given once enough items have been timed to estimate honestly.</summary>
    public TimeSpan? Remaining { get; init; }
    public bool Finished { get; init; }

    public string RemainingText => Remaining is not { } r ? ""
        : r.TotalMinutes < 1 ? "less than a minute left"
        : r.TotalMinutes < 90 ? $"about {Math.Ceiling(r.TotalMinutes)} min left"
        : $"about {Math.Round(r.TotalHours, 1)} hours left";
}

/// <summary>
/// Runs a Bulk Case Import from start to finish, one resumable phase at a time:
/// import the files (through the normal import, so encryption, fingerprints and the audit trail all
/// apply), read their text (the normal OCR), ask the local AI to suggest a category and records,
/// cross-check everything, then leave it all for review.
///
/// Progress is saved after every file, so a job survives cancelling, pausing, a crash or a restart and
/// carries on where it stopped. No phase holds a database transaction open for long; each file is its
/// own unit of work. Nothing is added to hearings, the timeline or any other case record here: that only
/// happens when the person approves a suggestion.
/// </summary>
public sealed class BulkImportService
{
    private readonly LibraryService _lib;
    private readonly Func<IAiJsonClient> _aiFactory;
    private readonly Func<AnalysisSettings> _settings;
    private readonly Func<bool> _aiEnabled;
    private Database Db => _lib.Db;
    private volatile bool _pauseRequested;
    private DateTime _runStarted;
    private CancellationTokenSource _runCts;

    public event Action<BulkProgress> Progress;
    /// <summary>Hands documents to the app's normal background text-reading queue (used when the person chose "read text later").</summary>
    public Action<IReadOnlyList<CaseDocument>> QueueTextLater { get; set; }
    /// <summary>How many documents the AI reads at once. One by default: local models are heavy.</summary>
    public int AiWorkers { get; set; } = 1;
    public int TextWorkers { get; set; } = Math.Max(1, Performance.OcrWorkers);
    public bool IsRunning { get; private set; }
    /// <summary>
    /// Shown before a cloud AI reads anything: what will be sent, where, and what's sensitive in it. Without it
    /// (or when the person says no) nothing is sent, and the documents wait for the AI.
    /// </summary>
    public Func<CloudSendRequest, Task<CloudSendDecision>> ConfirmCloudSend { get; set; }
    /// <summary>The local AI, for documents the person chose not to send to the cloud because they couldn't be masked well.</summary>
    public Func<IAiJsonClient> LocalAiFactory { get; set; }
    public long? RunningJobId { get; private set; }

    public BulkImportService(LibraryService lib, Func<IAiJsonClient> aiFactory, Func<AnalysisSettings> settings, Func<bool> aiEnabled)
    {
        _lib = lib;
        _aiFactory = aiFactory;
        _settings = settings;
        _aiEnabled = aiEnabled;
    }

    // ------------------------------------------------------------------ preview → job
    /// <summary>
    /// Turns a scan into preview rows: what will happen to each file, with folder-to-tag or
    /// folder-to-category mapping applied when chosen. Nothing is saved yet.
    /// </summary>
    public static List<ImportFile> PreviewRows(ScanResult scan, BulkOptions options, IReadOnlyList<Category> categories,
        IDictionary<string, long?> folderCategories = null)
    {
        var rows = new List<ImportFile>();
        for (int i = 0; i < scan.Entries.Count; i++)
        {
            var e = scan.Entries[i];
            var row = new ImportFile
            {
                RelativePath = e.RelativePath, Extension = e.Extension, SizeBytes = e.Size, SourceModifiedAt = e.Modified,
                SourceCreatedAt = e.Created, ContentHash = e.Hash, StatusValue = e.Status, ErrorMessage = e.Message,
                DuplicateOfDocumentId = e.DuplicateOfDocumentId,
                // Index for now; becomes a real row id once the job is saved
                DuplicateOfFileId = e.DuplicateOfEntry
            };
            bool duplicate = e.DuplicateOfDocumentId != null || e.DuplicateOfEntry != null;
            row.Included = row.StatusValue == ImportFileStatus.Pending && (!duplicate || options.Duplicates == DuplicatePolicy.ImportSeparately);
            ApplyMapping(row, options, categories, folderCategories);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Folder mapping for one row: tags from the folder names, or a category chosen for its top folder.</summary>
    public static void ApplyMapping(ImportFile row, BulkOptions options, IReadOnlyList<Category> categories, IDictionary<string, long?> folderCategories)
    {
        row.MappedTags = "";
        row.MappedCategoryId = null;
        var folder = row.Folder;
        if (folder.Length == 0) return;
        if (options.FolderMapping == "tags")
            row.MappedTags = string.Join(", ", FolderTags(folder));
        else if (options.FolderMapping == "categories")
        {
            var top = TopFolder(folder);
            if (folderCategories != null && folderCategories.TryGetValue(top, out var cat)) row.MappedCategoryId = cat;
        }
    }

    public static string TopFolder(string folder)
    {
        var i = folder.IndexOfAny(new[] { '\\', '/' });
        return i < 0 ? folder : folder[..i];
    }

    /// <summary>Folder names as tidy tags: no odd characters, no repeats, at most 40 characters each.</summary>
    public static IEnumerable<string> FolderTags(string folder) =>
        folder.Split('\\', '/').Select(SanitiseTag).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

    public static string SanitiseTag(string name)
    {
        var clean = new string((name ?? "").Select(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '&' or '(' or ')' or '.' ? ch : ' ').ToArray());
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim(' ', '.', '-');
        return clean.Length > 40 ? clean[..40].Trim() : clean;
    }

    /// <summary>Suggested categories for each top-level folder (from the same rules as file names), for the mapping table.</summary>
    public static Dictionary<string, long?> SuggestFolderCategories(IEnumerable<ImportFile> rows, IReadOnlyList<Category> categories) =>
        rows.Select(r => r.Folder).Where(f => f.Length > 0).Select(TopFolder).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(f => f, f => CategoryMatcher.GuessFromName(f, categories), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A plain-text summary of a scan, for copying. Counts and paths inside the chosen folder only: never the
    /// full Windows path, the user name or anything from inside the files.
    /// </summary>
    public static string ScanSummary(ScanResult scan, IReadOnlyList<ImportFile> rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("CaseKeeper – Bulk Case Import scan summary");
        sb.AppendLine($"Files found: {rows.Count:N0} ({rows.Sum(r => r.SizeBytes) / 1024d / 1024d:0.0} MB)");
        sb.AppendLine($"Will be imported: {rows.Count(r => r.Included && r.StatusValue == ImportFileStatus.Pending):N0}");
        foreach (var g in rows.GroupBy(r => r.StatusText).OrderByDescending(g => g.Count()))
            sb.AppendLine($"  {g.Key}: {g.Count():N0}");
        sb.AppendLine("By type:");
        foreach (var g in rows.GroupBy(r => r.TypeText.Length == 0 ? "(none)" : r.TypeText).OrderByDescending(g => g.Count()))
            sb.AppendLine($"  {g.Key}: {g.Count():N0}");
        if (scan != null)
        {
            if (scan.HiddenSkipped > 0) sb.AppendLine($"Hidden items not included: {scan.HiddenSkipped:N0}");
            if (scan.LinksNotFollowed > 0) sb.AppendLine($"Links / shortcuts not followed: {scan.LinksNotFollowed:N0}");
            if (scan.LoopsAvoided > 0) sb.AppendLine($"Folder loops avoided: {scan.LoopsAvoided:N0}");
            if (scan.TruncatedAtLimit) sb.AppendLine("The scan stopped at the file limit; choose a smaller folder.");
            if (scan.InaccessibleFolders.Count > 0)
            {
                sb.AppendLine("Folders that couldn't be opened:");
                foreach (var f in scan.InaccessibleFolders.Take(50)) sb.AppendLine("  " + f);
            }
        }
        var problems = rows.Where(r => r.StatusValue is ImportFileStatus.Inaccessible or ImportFileStatus.Failed or ImportFileStatus.Missing).Take(50).ToList();
        if (problems.Count > 0)
        {
            sb.AppendLine("Files with problems:");
            foreach (var p in problems) sb.AppendLine($"  {p.RelativePath} – {p.ErrorMessage}");
        }
        return sb.ToString();
    }

    /// <summary>Saves the job and every preview row in one go, ready to run. Audited.</summary>
    public ImportJob CreateJob(string root, BulkOptions options, IReadOnlyList<ImportFile> rows)
    {
        var job = new ImportJob
        {
            RootPath = Path.GetFullPath(root), RootName = new DirectoryInfo(root).Name,
            OptionsJson = JsonSerializer.Serialize(options), PhaseValue = ImportPhase.Importing, StateValue = ImportJobState.Active,
            DuplicateMode = (int)options.Duplicates
        };
        Db.SaveQuiet(job);
        foreach (var r in rows)
        {
            r.Id = 0;
            r.JobId = job.Id;
            if (!r.Included && r.StatusValue == ImportFileStatus.Pending && r.DuplicateOfDocumentId == null && r.DuplicateOfFileId == null)
                r.StatusValue = ImportFileStatus.Excluded;
        }
        // Duplicate links pointed at preview positions; keep them as a note on the row instead
        var byIndex = rows.ToList();
        foreach (var r in rows.Where(r => r.DuplicateOfFileId is { } idx && idx >= 0 && idx < byIndex.Count)) r.ErrorMessage = $"Same contents as “{byIndex[(int)r.DuplicateOfFileId!.Value].RelativePath}”";
        foreach (var r in rows) r.DuplicateOfFileId = null;
        Db.SaveQuietBatch(rows);

        int supported = rows.Count(r => r.StatusValue == ImportFileStatus.Pending);
        Db.Audit("bulkimport.start", "bulk import", job.Id, $"Started a bulk import of the folder “{job.RootName}”",
            $"{rows.Count} file(s) found, {supported} to import, {rows.Count(r => r.StatusValue == ImportFileStatus.Excluded)} left out, " +
            $"{rows.Count(r => r.StatusValue is ImportFileStatus.Unsupported or ImportFileStatus.SystemFile or ImportFileStatus.Inaccessible)} skipped.\n{options.Describe()}");
        AppLog.Info("BulkImport", $"Job #{job.Id} created: {rows.Count} files, {supported} to import");
        return job;
    }

    /// <summary>A job that analyses documents already in the library (from the Documents right-click menu).</summary>
    public ImportJob CreateLibraryJob(IReadOnlyList<CaseDocument> docs) => CreateLibraryJob(docs, new BulkOptions(), null);

    /// <summary>
    /// A job for documents already in the library, with the chosen options: a Library check (any number of documents,
    /// with or without AI, filling only the chosen sections) or "Organise with AI" on a selection.
    /// </summary>
    public ImportJob CreateLibraryJob(IReadOnlyList<CaseDocument> docs, BulkOptions options, string name)
    {
        options ??= new BulkOptions();
        var job = new ImportJob
        {
            RootName = name ?? (docs.Count == 1 ? docs[0].Title : $"{docs.Count} documents from the library"), RootPath = "",
            OptionsJson = JsonSerializer.Serialize(options), PhaseValue = ImportPhase.PreparingText, StateValue = ImportJobState.Active
        };
        Db.SaveQuiet(job);
        Db.SaveQuietBatch(docs.Select(d => new ImportFile
        {
            JobId = job.Id, RelativePath = string.IsNullOrEmpty(d.SourcePath) ? d.OriginalFileName : d.SourcePath, Extension = d.FileExtension,
            SizeBytes = d.FileSize, ContentHash = d.FileHash, StatusValue = ImportFileStatus.Imported, DocumentId = d.Id,
            TextValue = TextState.Waiting, AnalysisValue = AnalysisState.Queued, Included = true
        }).ToList());
        if (options.LibraryCheck)
            Db.Audit("bulkimport.start", "bulk import", job.Id, $"Library check: {docs.Count} document(s) ({(options.RunAi ? "with AI" : "CaseKeeper rules only")})",
                options.Describe());
        else
            Db.Audit("bulkimport.start", "bulk import", job.Id, $"Asked the AI to organise {docs.Count} document(s) already in the library", "");
        return job;
    }

    public ImportJob GetJob(long id) => Db.Query<ImportJob>("Id=$id", ("$id", id)).FirstOrDefault();

    /// <summary>Jobs that stopped before finishing (paused, cancelled, or interrupted by closing the app).</summary>
    public List<ImportJob> UnfinishedJobs() =>
        Db.Query<ImportJob>("State IN (0,1,2) AND Phase<$p", ("$p", (int)ImportPhase.Review))
            .Where(j => !IsRunning || j.Id != RunningJobId).ToList();

    public void RequestPause()
    {
        _pauseRequested = true;
        _runCts?.Cancel();
    }

    public void RequestCancel()
    {
        _pauseRequested = false;
        _runCts?.Cancel();
    }

    /// <summary>Marks documents to be analysed again, even if nothing has changed (the person asked).</summary>
    public void QueueReanalysis(long jobId, IEnumerable<long> documentIds)
    {
        // Changing a job while it runs would be overwritten when the run saves its state: wait until it's finished
        if (IsRunning) throw new InvalidOperationException("A bulk import is running. Try again when it has finished.");
        var ids = documentIds.ToHashSet();
        var files = Db.Query<ImportFile>("JobId=$j", ("$j", jobId)).Where(f => f.DocumentId is { } d && ids.Contains(d)).ToList();
        foreach (var f in files)
        {
            f.AnalysisValue = AnalysisState.Queued;
            f.AnalysisFingerprint = "";
            if (f.TextValue is TextState.Failed or TextState.NoText) f.TextValue = TextState.Waiting;
            f.ErrorCode = "";
            f.UpdatedAt = DateTime.Now;
        }
        Db.SaveQuietBatch(files);
        var job = GetJob(jobId);
        if (job == null) return;
        if (job.PhaseValue > ImportPhase.PreparingText) job.PhaseValue = ImportPhase.PreparingText;
        job.StateValue = ImportJobState.Active;
        job.UpdatedAt = DateTime.Now;
        Db.SaveQuiet(job);
    }

    // ------------------------------------------------------------------ running
    /// <summary>
    /// Runs (or resumes) a job through its remaining phases. Returns when it finishes, pauses or is cancelled.
    /// </summary>
    public async Task RunAsync(long jobId, CancellationToken outer = default)
    {
        if (IsRunning) throw new InvalidOperationException("A bulk import is already running.");
        var job = GetJob(jobId) ?? throw new InvalidOperationException("That import job no longer exists.");
        IsRunning = true;
        RunningJobId = jobId;
        _pauseRequested = false;
        _runStarted = DateTime.Now;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = _runCts.Token;
        job.StateValue = ImportJobState.Active;
        job.UpdatedAt = DateTime.Now;
        Db.SaveQuiet(job);
        var options = ReadOptions(job);
        try
        {
            if (job.PhaseValue <= ImportPhase.Importing)
            {
                await Task.Run(() => ImportPhaseRun(job, options, ct), ct);
                SetPhase(job, ImportPhase.PreparingText);
            }
            if (job.PhaseValue <= ImportPhase.PreparingText)
            {
                await TextPhaseAsync(job, options, ct);
                SetPhase(job, ImportPhase.Analysing);
            }
            if (job.PhaseValue <= ImportPhase.Analysing)
            {
                await AnalysisPhaseAsync(job, options, ct);
                SetPhase(job, ImportPhase.Checking);
            }
            if (job.PhaseValue <= ImportPhase.Checking)
            {
                Report(job, ImportPhase.Checking, 0, 1, "", "Cross-checking the documents…", null);
                var report = await Task.Run(() => FindingChecks.Run(Db), ct);
                AutoApplyCategories(job);
                Db.Audit("bulkimport.checks", "bulk import", job.Id, "Cross-checked the documents", report.Describe());
                SetPhase(job, ImportPhase.Review);
            }
            job.StateValue = ImportJobState.Completed;
            if (options.LibraryCheck) Db.SetSetting(LibraryCheck.LastRunSetting, DateTime.Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            var counts = Db.CountImportFiles(job.Id);
            Db.Audit("bulkimport.finish", "bulk import", job.Id, $"Finished the bulk import of “{job.RootName}”", Summarise(counts));
            AppLog.Info("BulkImport", $"Job #{job.Id} finished");
        }
        catch (OperationCanceledException)
        {
            job.StateValue = _pauseRequested ? ImportJobState.Paused : ImportJobState.Cancelled;
            Db.Audit(_pauseRequested ? "bulkimport.pause" : "bulkimport.cancel", "bulk import", job.Id,
                $"{(_pauseRequested ? "Paused" : "Stopped")} the bulk import of “{job.RootName}”", $"Stopped during: {job.PhaseText}");
            AppLog.Info("BulkImport", $"Job #{job.Id} {(_pauseRequested ? "paused" : "cancelled")} during {job.PhaseValue}");
        }
        finally
        {
            // Anything left mid-analysis goes back in the queue so a resume picks it up cleanly
            var stuck = Db.Query<ImportFile>("JobId=$j AND AnalysisStatus=$s", ("$j", job.Id), ("$s", (int)AnalysisState.Running));
            foreach (var f in stuck) f.AnalysisValue = AnalysisState.Queued;
            if (stuck.Count > 0) Db.SaveQuietBatch(stuck);
            job.UpdatedAt = DateTime.Now;
            Db.SaveQuiet(job);
            IsRunning = false;
            RunningJobId = null;
            _runCts.Dispose();
            _runCts = null;
            Report(job, job.PhaseValue, 0, 0, "", job.StateText, null, finished: true);
        }
    }

    public static BulkOptions ReadOptions(ImportJob job)
    {
        try { return JsonSerializer.Deserialize<BulkOptions>(job.OptionsJson) ?? new BulkOptions(); }
        catch (JsonException) { return new BulkOptions(); }
    }

    private void SetPhase(ImportJob job, ImportPhase phase)
    {
        job.PhaseValue = phase;
        job.UpdatedAt = DateTime.Now;
        Db.SaveQuiet(job);
    }

    public static string Summarise(Dictionary<string, int> c)
    {
        int S(ImportFileStatus s) => c.GetValueOrDefault("s" + (int)s);
        int A(AnalysisState s) => c.GetValueOrDefault("a" + (int)s);
        return $"Imported: {S(ImportFileStatus.Imported) + S(ImportFileStatus.ImportedDuplicate)} · duplicates skipped: {S(ImportFileStatus.DuplicateSkipped)} · " +
               $"failed: {S(ImportFileStatus.Failed)} · missing: {S(ImportFileStatus.Missing)} · unsupported: {S(ImportFileStatus.Unsupported)} · " +
               $"can't be read: {S(ImportFileStatus.Inaccessible)} · left out: {S(ImportFileStatus.Excluded)} · system files: {S(ImportFileStatus.SystemFile)}\n" +
               $"Analysed: {A(AnalysisState.Done)} · analysis failed: {A(AnalysisState.Failed)} · no readable text: {A(AnalysisState.NoText)} · " +
               $"waiting for AI: {A(AnalysisState.AwaitingAi)}";
    }

    // ---- honest progress: an estimate only once a few items have actually been timed
    private sealed class Timer
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private int _timed;
        public void Tick() => _timed++;
        public TimeSpan? Remaining(int left) => _timed < 3 || left <= 0 ? null : TimeSpan.FromTicks(_sw.Elapsed.Ticks / _timed * left);
    }

    private void Report(ImportJob job, ImportPhase phase, int done, int total, string current, string message, TimeSpan? remaining, bool finished = false)
    {
        try
        {
            Progress?.Invoke(new BulkProgress
            {
                JobId = job.Id, Phase = phase, Done = done, Total = total, Current = current, Message = message,
                Remaining = remaining, Finished = finished
            });
        }
        catch (Exception ex) { AppLog.Warn("BulkImport", "A progress listener failed", ex); }
    }

    // ------------------------------------------------------------------ phase 1: import
    private void ImportPhaseRun(ImportJob job, BulkOptions options, CancellationToken ct)
    {
        var categories = Db.GetCategories();
        long unknownId = CategoryMatcher.Unknown(categories)?.Id ?? 0;
        var validCategories = categories.Select(c => c.Id).ToHashSet();
        var files = Db.Query<ImportFile>("JobId=$j", ("$j", job.Id));
        var todo = files.Where(f => f.StatusValue == ImportFileStatus.Pending).ToList();
        var timer = new Timer();
        int done = 0;

        foreach (var f in todo)
        {
            ct.ThrowIfCancellationRequested();
            Report(job, ImportPhase.Importing, done, todo.Count, f.FileName, "Importing", timer.Remaining(todo.Count - done));
            var full = Path.Combine(job.RootPath, f.RelativePath);
            bool duplicateOfLibrary = false;
            try
            {
                if (!File.Exists(full))
                {
                    Fail(f, ImportFileStatus.Missing, "SRC_MISSING", "The file was moved or deleted after the scan.");
                    continue;
                }
                var hash = LibraryService.ComputeHash(full);
                var sameContents = Db.FindAllByHash(hash);
                var existing = sameContents.FirstOrDefault();

                // Recovering from a crash between importing a file and recording that it was imported: a library copy
                // came from this very file, during this job, and no job's records claim it yet
                var recovered = sameContents.FirstOrDefault(d => d.SourcePath == SourceFor(f, options) && d.ImportedAt >= job.CreatedAt
                                                                 && !Db.Query<ImportFile>("DocumentId=$d", ("$d", d.Id)).Any());
                if (recovered != null)
                {
                    existing = recovered;
                    f.DocumentId = existing.Id;
                    f.StatusValue = ImportFileStatus.Imported;
                    f.TextValue = TextState.Waiting;
                    f.ContentHash = hash;
                    Save(f);
                    continue;
                }

                if (existing != null)
                {
                    f.DuplicateOfDocumentId = existing.Id;
                    duplicateOfLibrary = true;
                    if (!f.Included)
                    {
                        f.StatusValue = ImportFileStatus.DuplicateSkipped;
                        f.ErrorMessage = $"Same contents as “{existing.Title}”, already in the library.";
                        f.ContentHash = hash;
                        Save(f);
                        continue;
                    }
                }
                else if (!f.Included)
                {
                    f.StatusValue = ImportFileStatus.Excluded;
                    Save(f);
                    continue;
                }

                var details = new CaseDocument
                {
                    Title = CategoryMatcher.CleanTitle(Path.GetFileNameWithoutExtension(f.FileName)),
                    // Folder mapping is the person's own choice in the preview; otherwise it waits in Uncategorised
                    CategoryId = f.MappedCategoryId is { } mc && validCategories.Contains(mc) ? mc : unknownId,
                    Tags = f.MappedTags ?? "",
                    SourcePath = SourceFor(f, options),
                    // The file's own timestamp is kept as information only, never as the document's date
                    SourceModifiedAt = f.SourceModifiedAt
                };
                var doc = _lib.Import(full, details, hash);
                f.DocumentId = doc.Id;
                f.ContentHash = doc.FileHash;
                f.StatusValue = duplicateOfLibrary ? ImportFileStatus.ImportedDuplicate : ImportFileStatus.Imported;
                f.TextValue = OcrService.CanExtract(doc.FileExtension) ? TextState.Waiting : TextState.NotNeeded;
                f.ErrorCode = "";
                Save(f);
            }
            catch (OperationCanceledException) { throw; }
            catch (FileNotFoundException) { Fail(f, ImportFileStatus.Missing, "SRC_MISSING", "The file was moved or deleted after the scan."); }
            catch (DirectoryNotFoundException) { Fail(f, ImportFileStatus.Missing, "SRC_MISSING", "The folder was moved or deleted after the scan."); }
            catch (UnauthorizedAccessException) { Fail(f, ImportFileStatus.Inaccessible, "SRC_DENIED", "Windows won't let CaseKeeper read this file."); }
            catch (IOException ex)
            {
                Fail(f, ImportFileStatus.Failed, "SRC_IO", ex.Message.Contains("changed while") ? ex.Message : "The file couldn't be read. It may be open in another program.");
                AppLog.Warn("BulkImport", $"Job #{job.Id}: file #{f.Id} couldn't be imported ({ex.GetType().Name})");
            }
            catch (Exception ex)
            {
                var id = AppLog.Error("BulkImport", $"Job #{job.Id}: file #{f.Id} failed to import ({ex.GetType().Name})");
                Fail(f, ImportFileStatus.Failed, "IMPORT_ERROR", $"Something went wrong importing this file (ref {id}).");
            }
            finally
            {
                done++;
                timer.Tick();
            }
        }
        Report(job, ImportPhase.Importing, done, todo.Count, "", "Import finished", null);
    }

    private static string SourceFor(ImportFile f, BulkOptions options) => options.PreserveStructure ? f.RelativePath : f.FileName;

    private void Fail(ImportFile f, ImportFileStatus status, string code, string message)
    {
        f.StatusValue = status;
        f.ErrorCode = code;
        f.ErrorMessage = message;
        Save(f);
    }

    private void Save(ImportFile f)
    {
        f.UpdatedAt = DateTime.Now;
        Db.SaveQuiet(f);
    }

    // ------------------------------------------------------------------ phase 2: text
    /// <summary>
    /// Library check: records the dates stored inside each file (last saved, Office revision) for documents imported
    /// before CaseKeeper read them, so Duplicates &amp; versions can tell which copy is the latest edit. Dates only.
    /// </summary>
    private void BackfillFileDates(ImportJob job, CancellationToken ct)
    {
        int filled = 0;
        foreach (var f in Db.Query<ImportFile>("JobId=$j AND DocumentId IS NOT NULL", ("$j", job.Id)))
        {
            ct.ThrowIfCancellationRequested();
            var doc = Db.GetDocument(f.DocumentId!.Value);
            if (doc == null || doc.DeletedAt != null || doc.SavedAt != null) continue;
            try
            {
                var dates = Documents.FileDates.Read(_lib.ReadDocumentBytes(doc), doc.FileExtension);
                if (dates.SavedAt == null && dates.Revision == null) continue;
                Db.SetFileDates(doc.Id, null, dates.SavedAt, dates.Revision);
                filled++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { AppLog.Warn("BulkImport", $"Job #{job.Id}: couldn't read the dates inside document #{doc.Id} ({ex.GetType().Name})"); }
        }
        if (filled > 0) AppLog.Info("BulkImport", $"Job #{job.Id}: recorded file dates for {filled} document(s)");
    }

    private async Task TextPhaseAsync(ImportJob job, BulkOptions options, CancellationToken ct)
    {
        if (options.LibraryCheck) await Task.Run(() => BackfillFileDates(job, ct), ct);
        var files = Db.Query<ImportFile>("JobId=$j AND DocumentId IS NOT NULL AND TextStatus IN ($a,$b)",
            ("$j", job.Id), ("$a", (int)TextState.NotStarted), ("$b", (int)TextState.Waiting));
        var later = new List<CaseDocument>();
        var timer = new Timer();
        int done = 0;
        using var gate = new SemaphoreSlim(Math.Max(1, TextWorkers));

        async Task One(ImportFile f)
        {
            await gate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                var doc = Db.GetDocument(f.DocumentId!.Value);
                if (doc == null || doc.DeletedAt != null)
                {
                    f.TextValue = TextState.Failed;
                    f.AnalysisValue = AnalysisState.Skipped;
                    f.ErrorMessage = "The document was deleted.";
                    Save(f);
                    return;
                }
                Report(job, ImportPhase.PreparingText, done, files.Count, doc.Title, "Reading text", timer.Remaining(files.Count - done));
                if (doc.OcrStatus is OcrStatus.Pending or OcrStatus.Running)
                {
                    // "Read text later": leave it to the background reader, and list it as waiting so it isn't forgotten.
                    // When the person later asks for it to be analysed (Queued), its text is read now.
                    if (!options.RunOcr && f.AnalysisValue != AnalysisState.Queued)
                    {
                        lock (later) later.Add(doc);
                        f.TextValue = TextState.Waiting;
                        f.AnalysisValue = AnalysisState.AwaitingAi;
                        f.ErrorMessage = "Waiting for its text to be read. Use “Analyse waiting” in Import review once it has been.";
                        Save(f);
                        return;
                    }
                    await Task.Factory.StartNew(() =>
                    {
                        Thread.CurrentThread.Priority = Performance.BackgroundPriority;
                        try { _lib.ProcessTextAsync(doc, null, OcrService.CreateEngine()).GetAwaiter().GetResult(); }
                        finally { Thread.CurrentThread.Priority = ThreadPriority.Normal; }
                    }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    // Another part of the app may have been reading this one already: wait for it
                    while (LibraryService.IsTextInProgress(doc.Id)) await Task.Delay(400, ct);
                    doc = Db.GetDocument(doc.Id) ?? doc;
                }
                EvaluateText(f, doc);
            }
            finally
            {
                Interlocked.Increment(ref done);
                timer.Tick();
                gate.Release();
            }
        }

        await Task.WhenAll(files.Select(One));
        if (later.Count > 0) QueueTextLater?.Invoke(later);
    }

    /// <summary>Decides whether a document has enough text to analyse. No text means a person has to look, never a guess.</summary>
    private void EvaluateText(ImportFile f, CaseDocument doc)
    {
        if (doc.OcrStatus == OcrStatus.Failed)
        {
            f.TextValue = TextState.Failed;
            f.AnalysisValue = AnalysisState.NoText;
            f.ErrorMessage = "The text couldn't be read. Open it and check it yourself.";
        }
        else if (doc.OcrStatus is OcrStatus.Pending or OcrStatus.Running)
        {
            f.TextValue = TextState.Waiting;
        }
        else if (DocumentAnalyzer.UsefulChars(Db.GetAllPageText(doc.Id)) < 25)
        {
            f.TextValue = TextState.NoText;
            f.AnalysisValue = AnalysisState.NoText;
            f.ErrorMessage = "No readable text was found (perhaps a photo or a blank page). Review it by hand.";
        }
        else
        {
            f.TextValue = TextState.Ready;
            f.ErrorMessage = "";
            if (f.AnalysisValue is AnalysisState.NotStarted or AnalysisState.NoText) f.AnalysisValue = AnalysisState.Queued;
        }
        Save(f);
    }

    // ------------------------------------------------------------------ phase 3: AI analysis
    private async Task AnalysisPhaseAsync(ImportJob job, BulkOptions options, CancellationToken ct)
    {
        // Text the background queue finished since the last phase
        foreach (var w in Db.Query<ImportFile>("JobId=$j AND DocumentId IS NOT NULL AND TextStatus=$t", ("$j", job.Id), ("$t", (int)TextState.Waiting)))
            if (Db.GetDocument(w.DocumentId!.Value) is { } d && d.OcrStatus is not (OcrStatus.Pending or OcrStatus.Running)) EvaluateText(w, d);

        var queue = Db.Query<ImportFile>("JobId=$j AND DocumentId IS NOT NULL AND TextStatus=$t AND AnalysisStatus IN ($a,$b,$c,$d)",
            ("$j", job.Id), ("$t", (int)TextState.Ready), ("$a", (int)AnalysisState.NotStarted), ("$b", (int)AnalysisState.Queued),
            ("$c", (int)AnalysisState.AwaitingAi), ("$d", (int)AnalysisState.Running));
        if (queue.Count == 0) return;
        var categories = Db.GetCategories();

        string unavailableCode = !options.RunAi ? "AI_NOT_REQUESTED" : !_aiEnabled() ? "AI_OFF" : null;
        IAiJsonClient client = null;
        if (unavailableCode == null)
        {
            client = _aiFactory();
            Report(job, ImportPhase.Analysing, 0, queue.Count, "", "Checking the local AI is available…", null);
            if (client == null || !await client.IsAvailableAsync(ct)) unavailableCode = "AI_UNAVAILABLE";
        }
        if (unavailableCode != null)
        {
            // Import is complete either way. Offer CaseKeeper's own category guesses meanwhile, and wait for the AI.
            SetAwaiting(job, queue, unavailableCode, categories);
            return;
        }

        // Cloud AI: the person sees what is about to leave this PC, and decides, before anything is sent
        var viaLocal = new HashSet<long>();
        IAiJsonClient localClient = null;
        var cloud = client as ICloudAiClient;
        string cloudNote = "";
        if (cloud != null)
        {
            var items = new List<CloudSendItem>();
            foreach (var f in queue)
            {
                ct.ThrowIfCancellationRequested();
                if (Db.GetDocument(f.DocumentId!.Value) is not { DeletedAt: null } d) continue;
                items.Add(CloudSendReview.Assess(f, d, Db.GetAllPageText(d.Id), cloud.Masker));
            }
            var request = new CloudSendRequest
            {
                ProviderId = cloud.ProviderId, Provider = cloud.ProviderName, Host = cloud.Host, Model = client.ModelName, Masking = cloud.Masker != null, Items = items
            };
            var decision = ConfirmCloudSend == null ? CloudSendDecision.Cancel : await ConfirmCloudSend(request) ?? CloudSendDecision.Cancel;
            if (!decision.Proceed)
            {
                Db.Audit("ai.consent", "ai", null, $"Bulk import: declined to send {items.Count} document(s) to {cloud.ProviderName}", $"Job #{job.Id}. Host: {cloud.Host}.");
                SetAwaiting(job, queue, "AI_CLOUD_DECLINED", categories);
                return;
            }
            var poor = items.Where(i => i.PoorlyMasked).Select(i => i.File.Id).ToHashSet();
            if (poor.Count > 0 && decision.PoorlyMasked == "skip")
            {
                foreach (var f in queue.Where(f => poor.Contains(f.Id)))
                {
                    f.AnalysisValue = AnalysisState.Skipped;
                    f.ErrorCode = "AI_NOT_SENT";
                    f.ErrorMessage = "Not sent to the cloud AI: it couldn't be masked reliably, and you chose to skip such documents.";
                    Save(f);
                }
                queue = queue.Where(f => !poor.Contains(f.Id)).ToList();
            }
            else if (poor.Count > 0 && decision.PoorlyMasked == "local")
            {
                localClient = LocalAiFactory?.Invoke();
                if (localClient != null && await localClient.IsAvailableAsync(ct)) viaLocal = poor;
                else
                {
                    var wait = queue.Where(f => poor.Contains(f.Id)).ToList();
                    SetAwaiting(job, wait, "AI_UNAVAILABLE", categories);
                    queue = queue.Where(f => !poor.Contains(f.Id)).ToList();
                    localClient = null;
                }
            }
            cloudNote = $" Sent to {cloud.ProviderName} ({cloud.Host}) with personal details {(cloud.Masker != null ? "masked" : "NOT masked")}" +
                        (poor.Count > 0 ? $"; {poor.Count} poorly masked document(s): {(decision.PoorlyMasked switch { "skip" => "skipped", "local" => viaLocal.Count > 0 ? "read by the local AI" : "waiting for the local AI", _ => "sent anyway" })}" : "") + ".";
            Db.Audit("ai.consent", "ai", null, $"Bulk import: agreed to send {queue.Count - viaLocal.Count} document(s) to {cloud.ProviderName}",
                $"Job #{job.Id}. Host: {cloud.Host}. Model: {client.ModelName}. Masking: {(cloud.Masker != null ? "on" : "off")}.{cloudNote}");
            if (queue.Count == 0) return;
        }

        var baseSettings = _settings();
        // Library check: only the chosen sections are kept; categories alone can use the AI's quick reading
        var kinds = LibraryCheck.KindsFor(options.Sections);
        if (LibraryCheck.CategoriesOnly(options.Sections)) baseSettings = With(baseSettings, quick: true);
        string Scoped(string fp) => kinds == null ? fp : fp + "|" + string.Join(",", kinds.Order(StringComparer.Ordinal));
        job.Model = client.ModelName;
        job.PromptVersion = baseSettings.PromptVersion;
        job.SchemaVersion = AnalysisSchema.Version;
        job.AnalysisVersion = AnalysisSchema.AnalysisVersion;
        job.LastErrorCode = "";
        Db.SaveQuiet(job);
        Db.Audit("ai.request", "ai", null, $"Bulk import: AI analysis of {queue.Count} document(s) ({client.ModelName})",
            $"Job #{job.Id}. Prompt {baseSettings.PromptVersion}, schema {AnalysisSchema.Version}" +
            $"{(client.CategoriesOnly ? ", quick reading (categories only)" : "")}, {AiWorkers} at once.{(cloud != null ? cloudNote : " Only the configured local AI server is used.")}");

        // Automatic step-down: if the AI runs out of memory, keeps timing out, or doesn't fit on the graphics card, ease off
        // one step at a time – fewer documents at once, smaller parts, a lighter model, then quick reading – and say what changed
        const int LastLevel = 4;
        var gate = new object();
        int workers = Math.Max(1, AiWorkers), active = 0, level = 0, done = 0;
        var settings = baseSettings;
        var analyzer = new DocumentAnalyzer(client, settings, categories);
        var localAnalyzer = localClient != null ? new DocumentAnalyzer(localClient, baseSettings, categories) : null;
        bool gpuChecked = cloud != null;

        // Steps down once from the level the caller saw, so several documents failing together count as one problem
        void StepDown(string why, int seen)
        {
            string what = null;
            lock (gate)
            {
                if (level != seen || level >= LastLevel) return;
                while (what == null && level < LastLevel)
                {
                    level++;
                    if (level == 1 && workers > 1) { workers = 1; what = "reading one document at a time"; }
                    else if (level == 2 && settings.PartChars > 3000) { workers = 1; settings = With(settings, partChars: Math.Max(3000, settings.PartChars / 2)); what = "reading documents in smaller parts"; }
                    else if (level == 3 && client.Lighten()) { workers = 1; what = $"using the lighter AI model {client.ModelName}"; }
                    else if (level == 4 && !analyzer.CategoriesOnly) { workers = 1; settings = With(settings, quick: true); what = "doing a quick reading (document details and category only)"; }
                }
                if (what == null) return;
                analyzer = new DocumentAnalyzer(client, settings, categories);
                job.LastErrorCode = "AI_STEPPED_DOWN";
                job.Model = client.ModelName;
                Db.SaveQuiet(job);
            }
            AppLog.Info("BulkImport", $"Job #{job.Id}: AI settings eased to level {level} ({why})");
            Db.Audit("ai.settings", "ai", null, $"Bulk import eased its AI settings: {what}", $"Job #{job.Id}. Reason: {why}.");
            Report(job, ImportPhase.Analysing, done, queue.Count, "", $"{(cloud != null ? "The AI service is struggling" : "This PC is struggling")} ({why}), so CaseKeeper is now {what}.", null);
        }

        async Task Enter(CancellationToken token)
        {
            while (true)
            {
                lock (gate) if (active < workers) { active++; return; }
                await Task.Delay(150, token);
            }
        }

        var timer = new Timer();
        bool lostAi = false;
        using var stopOnLoss = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task One(ImportFile f)
        {
            await Enter(stopOnLoss.Token);
            try
            {
                stopOnLoss.Token.ThrowIfCancellationRequested();
                var doc = Db.GetDocument(f.DocumentId!.Value);
                if (doc == null || doc.DeletedAt != null) { f.AnalysisValue = AnalysisState.Skipped; Save(f); return; }
                var pages = Db.GetAllPageText(doc.Id);
                DocumentAnalyzer current;
                bool local = localAnalyzer != null && viaLocal.Contains(f.Id);
                lock (gate) current = local ? localAnalyzer : analyzer;
                var fingerprint = Scoped(current.InputFingerprint(doc, pages));
                if (f.AnalysisFingerprint == fingerprint)
                {
                    // Already analysed with exactly this text, prompt, schema, model and reading size: don't repeat it
                    if (f.AnalysisValue != AnalysisState.Done) { f.AnalysisValue = AnalysisState.Done; Save(f); }
                    return;
                }

                Report(job, ImportPhase.Analysing, done, queue.Count, doc.Title, "AI reading", timer.Remaining(queue.Count - done));
                f.AnalysisValue = AnalysisState.Running;
                f.AnalysisAttempts++;
                Save(f);
                try
                {
                    DocumentAnalysisResult result = null;
                    // A document that overloads the AI is tried again (at most twice) after easing off
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        int seen;
                        lock (gate) { current = local ? localAnalyzer : analyzer; seen = level; }
                        fingerprint = Scoped(current.InputFingerprint(doc, pages));
                        result = await current.AnalyseAsync(doc, pages, job.Id, stopOnLoss.Token);
                        if (result.Ok || result.ErrorCode is not ("AI_OVERLOAD" or "AI_TIMEOUT") || seen >= LastLevel) break;
                        if (local) break;
                        StepDown(result.ErrorCode == "AI_OVERLOAD" ? (cloud != null ? "the AI service was busy" : "the AI ran out of memory") : "the AI was taking too long to answer", seen);
                    }
                    if (result.Ok)
                    {
                        Db.ReplaceFindings(doc.Id, kinds == null ? result.Findings : result.Findings.Where(x => kinds.Contains(x.Kind)).ToList(), kinds);
                        f.AnalysisValue = AnalysisState.Done;
                        f.AnalysisFingerprint = fingerprint;
                        f.AnalysedAt = DateTime.Now;
                        f.ErrorCode = "";
                        f.ErrorMessage = result.Dropped > 0 ? $"{result.Dropped} suggestion(s) failed CaseKeeper's checks and were left out." : "";
                        if (!gpuChecked)
                        {
                            gpuChecked = true;
                            // A model that spills off the graphics card runs many times slower: ease off straight away
                            int seen;
                            lock (gate) seen = level;
                            if (await client.GpuShareAsync(stopOnLoss.Token) is { } share && share < 0.9 && AiHardware.Gpu.Gb >= 2)
                                StepDown($"only {share:P0} of the AI model fits on the graphics card", seen);
                        }
                    }
                    else
                    {
                        f.AnalysisValue = AnalysisState.Failed;
                        f.ErrorCode = result.ErrorCode;
                        f.ErrorMessage = result.ErrorMessage;
                        AppLog.Warn("BulkImport", $"Job #{job.Id}: analysis of document #{doc.Id} failed ({result.ErrorCode})");
                    }
                    Save(f);
                }
                catch (AiUnavailableException)
                {
                    f.AnalysisValue = AnalysisState.AwaitingAi;
                    Save(f);
                    lostAi = true;
                    stopOnLoss.Cancel();
                }
                catch (OperationCanceledException)
                {
                    f.AnalysisValue = AnalysisState.Queued;
                    Save(f);
                    throw;
                }
                catch (Exception ex)
                {
                    // Type only: messages from the AI server could, in principle, echo document text
                    var id = AppLog.Error("BulkImport", $"Job #{job.Id}: analysis of document #{doc.Id} crashed ({ex.GetType().Name})");
                    f.AnalysisValue = AnalysisState.Failed;
                    f.ErrorCode = "AI_ERROR";
                    f.ErrorMessage = $"Something went wrong analysing this document (ref {id}). Retry it later.";
                    Save(f);
                }
            }
            finally
            {
                Interlocked.Increment(ref done);
                timer.Tick();
                lock (gate) active--;
            }
        }

        try { await Task.WhenAll(queue.Select(One)); }
        catch (OperationCanceledException) when (lostAi && !ct.IsCancellationRequested) { /* handled below */ }

        if (lostAi)
        {
            var left = Db.Query<ImportFile>("JobId=$j AND TextStatus=$t AND AnalysisStatus IN ($a,$b,$c)", ("$j", job.Id), ("$t", (int)TextState.Ready),
                ("$a", (int)AnalysisState.Queued), ("$b", (int)AnalysisState.NotStarted), ("$c", (int)AnalysisState.Running));
            SetAwaiting(job, left, "AI_UNAVAILABLE", categories);
        }
    }

    private static AnalysisSettings With(AnalysisSettings s, int? partChars = null, bool? quick = null) => new()
    {
        Prompt = s.Prompt, PartChars = partChars ?? s.PartChars, MaxParts = s.MaxParts, RetryDelays = s.RetryDelays,
        CategoriesOnly = quick ?? s.CategoriesOnly, MaxFindingsPerPart = s.MaxFindingsPerPart
    };

    private void SetAwaiting(ImportJob job, List<ImportFile> files, string code, IReadOnlyList<Category> categories)
    {
        var kinds = LibraryCheck.KindsFor(ReadOptions(job).Sections);
        bool wantCategory = kinds == null || kinds.Contains(FindingKinds.Category), wantComplaint = kinds == null || kinds.Contains(FindingKinds.Complaint);
        bool Has(long docId, string kind) => Db.Query<AiFinding>("DocumentId=$d AND Kind=$k AND Status<>$s", ("$d", docId), ("$k", kind),
            ("$s", (int)FindingStatus.Superseded)).Any();
        foreach (var f in files)
        {
            f.AnalysisValue = AnalysisState.AwaitingAi;
            f.UpdatedAt = DateTime.Now;
            var doc = Db.GetDocument(f.DocumentId!.Value);
            if (doc == null) continue;
            var pages = Db.GetAllPageText(doc.Id);
            // CaseKeeper's own suggestions, added only where the document has none of that kind yet – never replacing
            // anything already there. A complaint letter gets its complaint category rather than the general guess.
            var complaint = wantCategory || wantComplaint ? ComplaintRules.Suggest(doc, pages, categories, job.Id) : new List<AiFinding>();
            var add = new List<AiFinding>();
            if (wantComplaint && complaint.FirstOrDefault(x => x.Kind == FindingKinds.Complaint) is { } c && !Has(doc.Id, FindingKinds.Complaint)) add.Add(c);
            if (wantCategory && !Has(doc.Id, FindingKinds.Category) &&
                (complaint.FirstOrDefault(x => x.Kind == FindingKinds.Category) ?? DocumentAnalyzer.RuleBasedCategory(doc, pages, categories, job.Id)) is { } cat)
                add.Add(cat);
            if (add.Count > 0) Db.AddFindings(add);
        }
        Db.SaveQuietBatch(files);
        job.LastErrorCode = code;
        Db.SaveQuiet(job);
        AppLog.Info("BulkImport", $"Job #{job.Id}: {files.Count} document(s) waiting for AI ({code})");
    }

    // ------------------------------------------------------------------ optional: apply very confident categories
    /// <summary>
    /// The advanced, off-by-default rule: file a document under the AI's category when it is very confident,
    /// there are no warnings, and the document is still Uncategorised (a person's own choice is never
    /// overridden). Categories only. Every one is audited and can be undone in the review screen.
    /// </summary>
    private void AutoApplyCategories(ImportJob job)
    {
        if (Db.GetSetting("bulk.autoApply", "0") != "1") return;
        double threshold = double.TryParse(Db.GetSetting("bulk.autoApplyThreshold", "0.95"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var t) ? Math.Clamp(t, 0.85, 1.0) : 0.95;
        var unknown = CategoryMatcher.Unknown(Db.GetCategories())?.Id;
        int applied = 0;
        foreach (var f in Db.Query<AiFinding>("JobId=$j AND Kind=$k AND Status=0", ("$j", job.Id), ("$k", FindingKinds.Category)))
        {
            // Only suggestions made in this run: one the person has already undone is theirs to decide, not the rule's
            if (f.CreatedAt < _runStarted) continue;
            if (f.Confidence < threshold || f.HasWarnings || f.Model == "CaseKeeper rules") continue;
            var cat = FindingApprover.PLong(f, "categoryId");
            var doc = Db.GetDocument(f.DocumentId);
            if (cat == null || cat == unknown || doc == null || doc.CategoryId != unknown) continue;
            doc.CategoryId = cat.Value;
            FindingApprover.Approve(Db, f, doc, edited: false, automatic: true, note: $"Auto-apply threshold {threshold:0.00}");
            applied++;
        }
        if (applied > 0) AppLog.Info("BulkImport", $"Job #{job.Id}: {applied} category suggestion(s) applied by the auto-apply rule");
    }
}
