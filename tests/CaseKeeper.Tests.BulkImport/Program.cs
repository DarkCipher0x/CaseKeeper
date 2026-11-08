using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CaseKeeper.Data;
using CaseKeeper.Models;
using CaseKeeper.Services;
using CaseKeeper.Services.BulkImport;
using Microsoft.Data.Sqlite;

/// <summary>
/// Bulk Case Import tests. Everything here is synthetic: invented names, invented case numbers, files made on
/// the fly in a temporary folder. The AI is a scripted stand-in, so results are exact and repeatable.
/// </summary>
static class Program
{
    static int failures, skipped;
    static readonly string Root = Path.Combine(Path.GetTempPath(), "ckt", "bulk-run");
    const string Marker = "ZEBRA-CONFIDENTIAL-7731";

    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }
    static void Skip(string what) { Console.WriteLine("SKIP " + what); skipped++; }

    static string NewDir(string name)
    {
        var d = Path.Combine(Root, name);
        if (Directory.Exists(d)) { ClearAcls(d); Directory.Delete(d, true); }
        Directory.CreateDirectory(d);
        return d;
    }

    static LibraryService NewLib(string name) => new(NewDir(name));

    static string Put(string dir, string rel, string text)
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    static AnalysisSettings Settings(string prompt = "test prompt", int partChars = 9000) => new()
    {
        Prompt = prompt, PartChars = partChars, RetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero }
    };

    static BulkImportService Svc(LibraryService lib, FakeAi ai, bool aiOn = true, Func<AnalysisSettings> settings = null) =>
        new(lib, () => ai, settings ?? (() => Settings()), () => aiOn) { TextWorkers = 2 };

    static (ImportJob Job, List<ImportFile> Files) Run(LibraryService lib, BulkImportService svc, string folder, BulkOptions options = null,
        Action<List<ImportFile>> adjust = null)
    {
        options ??= new BulkOptions();
        var scan = FolderScanner.Scan(folder, options.Scan);
        FolderScanner.FindDuplicates(scan, lib.Db.GetDocumentHashes());
        var rows = BulkImportService.PreviewRows(scan, options, lib.Db.GetCategories());
        adjust?.Invoke(rows);
        var job = svc.CreateJob(folder, options, rows);
        svc.RunAsync(job.Id).GetAwaiter().GetResult();
        return (svc.GetJob(job.Id), lib.Db.Query<ImportFile>("JobId=$j", ("$j", job.Id)));
    }

    static List<AiFinding> Findings(LibraryService lib, string kind = null) =>
        lib.Db.Query<AiFinding>(kind == null ? "Status IN (0,3)" : "Status IN (0,3) AND Kind=$k", ("$k", kind));

    // ================================================================== the stand-in for Ollama
    sealed class FakeAi : IAiJsonClient
    {
        public string ModelName { get; set; } = "fake-model:1b";
        public bool Available = true;
        public int Calls;
        public string LastUser = "";
        public Func<string, string> Respond;
        public readonly Queue<object> Script = new();
        public Action<int> OnCall;
        public bool CategoriesOnly { get; set; }
        public double? GpuShare;
        public string LighterModel;
        public Task<double?> GpuShareAsync(CancellationToken ct) => Task.FromResult(GpuShare);
        public bool Lighten()
        {
            if (LighterModel == null || ModelName == LighterModel) return false;
            ModelName = LighterModel;
            return true;
        }

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Available);

        public Task<string> CompleteJsonAsync(string system, string user, JsonObject schema, CancellationToken ct)
        {
            Calls++;
            LastUser = user;
            OnCall?.Invoke(Calls);
            ct.ThrowIfCancellationRequested();
            if (!Available) throw new AiUnavailableException("down");
            if (Script.Count > 0)
            {
                var next = Script.Dequeue();
                if (next is Exception e) throw e;
                return Task.FromResult((string)next);
            }
            return Task.FromResult(Respond?.Invoke(user) ?? Answer(user));
        }
    }

    /// <summary>A stand-in for a cloud AI: the scripted AI, plus what the pre-send review needs. Records what it was sent.</summary>
    sealed class FakeCloud : ICloudAiClient
    {
        public readonly FakeAi Inner = new();
        public Masker Masker { get; init; }
        public readonly List<string> Sent = new();
        public string ProviderId => "anthropic";
        public string ProviderName => "Test Cloud";
        public string Host => "api.anthropic.com";
        public string ModelName => "anthropic/test-model";
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);
        public async Task<string> CompleteJsonAsync(string system, string user, JsonObject schema, CancellationToken ct)
        {
            var masked = Masker != null ? Masker.Mask(user) : user;
            lock (Sent) Sent.Add(masked);
            var json = await Inner.CompleteJsonAsync(system, masked, schema, ct);
            return Masker != null ? Masker.UnmaskJson(json) : json;
        }
    }

    /// <summary>0.6.0: each encrypted file is bound to its own name, and pre-0.6.0 files are converted then refused.</summary>
    static void EncryptedFileBinding(LibraryService enc, Vault vault, string firstStored)
    {
        Check(Encoding.ASCII.GetString(File.ReadAllBytes(firstStored), 0, 4) == "CKE2", "encryption: new files use the bound format (CKE2)");
        var src = NewDir("imp-enc-bind-src");
        Put(src, "second.txt", "A second, different document for the swap test.");
        var second = enc.Import(Path.Combine(src, "second.txt"), new CaseDocument { Title = "Second", CategoryId = enc.Db.GetDocuments().First().CategoryId });
        var first = enc.Db.GetDocuments().First(d => d.Id != second.Id);
        var a = enc.GetFilePath(first); var b = enc.GetFilePath(second);
        var aBytes = File.ReadAllBytes(a); var bBytes = File.ReadAllBytes(b);

        // Swap the two encrypted files: neither may decrypt as the other
        File.WriteAllBytes(a, bBytes); File.WriteAllBytes(b, aBytes);
        bool refusedA = false, refusedB = false;
        try { enc.ReadDocumentBytes(first); } catch (System.Security.Cryptography.CryptographicException) { refusedA = true; }
        try { enc.ReadDocumentBytes(second); } catch (System.Security.Cryptography.CryptographicException) { refusedB = true; }
        Check(refusedA && refusedB, "encryption: swapped document files are refused, not shown as each other");
        File.WriteAllBytes(a, aBytes); File.WriteAllBytes(b, bBytes);
        Check(Encoding.UTF8.GetString(enc.ReadDocumentBytes(second)).Contains("second, different"), "encryption: put back, they read correctly again");

        // A file in the pre-0.6.0 format (no binding) is still read, then converted on the next start
        byte[] Legacy(byte[] plain)
        {
            var key = Convert.FromBase64String(vault.DatabasePassword);
            var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plain.Length]; var tag = new byte[16];
            using (var g = new System.Security.Cryptography.AesGcm(key, 16)) g.Encrypt(nonce, plain, cipher, tag);
            return Encoding.ASCII.GetBytes("CKE1").Concat(nonce).Concat(tag).Concat(cipher).ToArray();
        }
        var oldText = Encoding.UTF8.GetBytes("Words from an older version of CaseKeeper.");
        File.WriteAllBytes(b, Legacy(oldText));
        enc.Db.SetSetting(LibraryService.BoundFilesSetting, "0");
        vault.AllowLegacyFiles = true;
        Check(Encoding.UTF8.GetString(enc.ReadDocumentBytes(second)).Contains("older version"), "encryption: a pre-0.6.0 file still opens before it's converted");
        var converted = enc.UpgradeFileBinding();
        Check(converted == 1 && Encoding.ASCII.GetString(File.ReadAllBytes(b), 0, 4) == "CKE2", $"encryption: old files are converted to the bound format ({converted})");
        Check(Encoding.UTF8.GetString(enc.ReadDocumentBytes(second)).Contains("older version") && enc.Db.GetSetting(LibraryService.BoundFilesSetting, "") == "1"
              && !vault.AllowLegacyFiles, "encryption: and read the same afterwards; the library then stops accepting the old format");
        Check(enc.Db.GetAudit(50).Any(x => x.Action == "protection.upgrade"), "encryption: the conversion is in the audit trail");
        Check(enc.UpgradeFileBinding() == 0, "encryption: converting again does nothing");
        File.WriteAllBytes(b, Legacy(oldText));
        bool refusedOld = false;
        try { enc.ReadDocumentBytes(second); } catch (System.Security.Cryptography.CryptographicException) { refusedOld = true; }
        Check(refusedOld, "encryption: an old-format file slipped in afterwards is refused");
        File.WriteAllBytes(b, bBytes);
        Check(!File.Exists(b + ".tmp") && Directory.GetFiles(enc.FilesFolder, "*.tmp").Length == 0, "encryption: no temporary files left behind");
    }

    static string KeyFor(string user, string code)
    {
        var m = Regex.Match(user, $@"(K\d+): {Regex.Escape(code)}\b");
        return m.Success ? m.Groups[1].Value : "UNKNOWN";
    }

    static JsonObject F(string type, string description, int? page, string excerpt, double confidence = 0.9, string date = null, string dateText = null,
        string precision = "exact", string role = "mentioned", string time = null, string assertedBy = null, JsonObject details = null) => new()
    {
        ["recordType"] = type, ["description"] = description, ["assertedBy"] = assertedBy, ["page"] = page, ["pageTo"] = null,
        ["excerpt"] = excerpt, ["confidence"] = confidence, ["reason"] = "Stated in the text", ["date"] = date, ["dateText"] = dateText,
        ["datePrecision"] = precision, ["dateRole"] = role, ["time"] = time, ["details"] = details ?? new JsonObject()
    };

    static string Answer(string user, string catKey = "UNKNOWN", double catConfidence = 0.5, string summary = "A document.", string docDate = null,
        string docDateText = null, string caseNumber = null, params JsonObject[] findings) => new JsonObject
    {
        ["documentSummary"] = new JsonObject
        {
            ["documentType"] = "letter", ["documentTitle"] = null, ["documentDate"] = docDate, ["documentDateText"] = docDateText,
            ["caseNumber"] = caseNumber, ["courtName"] = null, ["authorOrIssuer"] = null, ["recipient"] = null, ["summary"] = summary, ["confidence"] = 0.8
        },
        ["categorySuggestion"] = new JsonObject { ["categoryId"] = catKey, ["confidence"] = catConfidence, ["reason"] = "Looks like it", ["alternativeCategoryIds"] = new JsonArray() },
        ["findings"] = new JsonArray(findings.Select(f => (JsonNode)f).ToArray())
    }.ToJsonString();

    // ================================================================== 0.7.0: library check, complaints, latest edit
    static void LibraryCheckAndComplaints()
    {
        // ---- a library with a complaint, its reply and an ordinary letter, imported with nothing suggested
        var lib = NewLib("libcheck");
        var src = NewDir("libcheck-src");
        Put(src, "complaint.txt", "Dear Sir or Madam,\nI wish to make a formal complaint about the way the Cafcass officer handled the safeguarding letter. " +
                                  "Complaint reference CAF-20931. Please reply within 20 working days.\nYours faithfully, Winifred Quillfeather");
        Put(src, "reply.txt", "Dear Ms Quillfeather,\nThank you for your complaint. Our ref: CAF-20931. We have now investigated the points you raised " +
                              "about the Cafcass officer and your complaint is partially upheld.\nCafcass Complaints Team");
        Put(src, "school.txt", "Letter about the school run and the handover times on Tuesday afternoons, and who collects the children from the gate.");
        var svc = Svc(lib, new FakeAi(), aiOn: false);
        Run(lib, svc, src, new BulkOptions { RunAi = false, Sections = new() { "people" } });
        Check(Findings(lib, FindingKinds.Complaint).Count == 0 && Findings(lib, FindingKinds.Category).Count == 0, "library check: sections not chosen get no rule suggestions");

        // ---- library check without AI: CaseKeeper's own rules file the complaints and fill them in
        var docs = lib.Db.GetDocuments();
        var job = svc.CreateLibraryJob(docs, new BulkOptions { LibraryCheck = true, RunAi = false }, "Library check");
        svc.RunAsync(job.Id).GetAwaiter().GetResult();
        long Doc(string file) => docs.Single(d => d.OriginalFileName == file).Id;
        var complaints = Findings(lib, FindingKinds.Complaint);
        var complaint = complaints.SingleOrDefault(f => f.DocumentId == Doc("complaint.txt"));
        var reply = complaints.SingleOrDefault(f => f.DocumentId == Doc("reply.txt"));
        Check(complaints.Count == 2 && complaints.All(f => f.Model == ComplaintRules.Model), "library check (no AI): the complaint and the reply are recognised by CaseKeeper's rules");
        Check(complaints.All(f => f.DocumentId != Doc("school.txt")), "library check (no AI): an ordinary letter isn't taken for a complaint");
        var cp = complaint == null ? new JsonObject() : JsonNode.Parse(complaint.PayloadJson)!.AsObject();
        Check((string)cp["recipient"] == "Cafcass" && (string)cp["reference"] == "CAF-20931" && (string)cp["isResponse"] == "false" &&
              ((string)cp["responseDueText"] ?? "").Contains("20 working days"), "complaint rules: organisation, reference and the reply wording are filled in");
        var rp = reply == null ? new JsonObject() : JsonNode.Parse(reply.PayloadJson)!.AsObject();
        Check((string)rp["isResponse"] == "true" && (string)rp["reference"] == "CAF-20931", "complaint rules: the organisation's reply is recognised as a reply");
        var categories = lib.Db.GetCategories();
        string CatOf(long docId) => Findings(lib, FindingKinds.Category).Where(f => f.DocumentId == docId).Select(f => f.Title).FirstOrDefault() ?? "";
        Check(CatOf(Doc("complaint.txt")).Contains("Cafcass complaint") && CatOf(Doc("reply.txt")).Contains("Cafcass complaint response"),
            "library check (no AI): complaint letters are given their complaint categories");
        Check(lib.Db.GetSetting(LibraryCheck.LastRunSetting, "") != "" && lib.Db.GetAudit(50).Any(a => a.Summary.StartsWith("Library check: 3 document(s)")),
            "library check: recorded as run, and audited");

        // ---- approving the complaint, then its reply (which updates it instead of adding a second complaint)
        if (complaint != null && reply != null)
        {
            var made = FindingApprover.Approve(lib.Db, complaint, FindingApprover.Draft(lib.Db, complaint), edited: false);
            var unknownId = CategoryMatcher.Unknown(categories)?.Id;
            var filed = lib.Db.GetDocument(Doc("complaint.txt"));
            Check(made.Ok && lib.Db.GetComplaints().Count == 1, "complaint approval: one complaint record");
            Check(filed.CategoryId != unknownId && categories.First(c => c.Id == filed.CategoryId).Name == "Cafcass complaint",
                "complaint approval: also files the Uncategorised document under its complaint category");
            var replyDraft = FindingApprover.Draft(lib.Db, reply);
            Check(FindingApprover.DescribeUpdate(lib.Db, replyDraft) is { Length: > 0 }, "complaint reply: shown as an update to the existing complaint");
            var before = lib.Db.GetComplaints().Single();
            var merged = FindingApprover.Approve(lib.Db, reply, replyDraft, edited: false);
            var after = lib.Db.GetComplaints();
            Check(merged.Ok && after.Count == 1 && after[0].Status == "Response Received" && after[0].DocumentIds.Contains(Doc("reply.txt")),
                "complaint reply: links the reply and marks the response received, without a second complaint");
            Check(after[0].Reference == before.Reference && after[0].Recipient == before.Recipient && after[0].Subject == before.Subject,
                "complaint reply: fields already filled in are not overwritten");
            var undone = FindingApprover.Undo(lib.Db, lib.Db.Query<AiFinding>("Id=$i", ("$i", reply.Id)).Single());
            var restored = lib.Db.GetComplaints().Single();
            Check(undone.Ok && restored.Status == before.Status && !restored.DocumentIds.Contains(Doc("reply.txt")), "complaint reply: undo restores the complaint as it was");
        }

        // ---- the assistant's view of the library: counts only
        var stats = LibraryCheck.Count(lib.Db);
        var state = LibraryCheck.DescribeForAssistant(stats);
        Check(stats.Documents == 3 && state.Contains("Documents: 3") && !state.Contains("Quillfeather") && !state.Contains("complaint.txt") && !state.Contains("Cafcass"),
            "assistant: the library state is counts only – no names, titles or content");

        // ---- library check with AI, filling only the chosen sections
        var lib2 = NewLib("libcheck-ai");
        var src2 = NewDir("libcheck-ai-src");
        Put(src2, "notice.txt", "Notice of hearing. The hearing is listed for 12 March 2027 at the Family Court. Contact will take place every Saturday at the contact centre.");
        var ai = new FakeAi
        {
            Respond = u => Answer(u, findings: new[]
            {
                F("hearing", "Hearing listed", 1, "The hearing is listed for 12 March 2027", date: "2027-03-12", dateText: "12 March 2027", role: "hearingListed"),
                F("contact", "Contact every Saturday", 1, "Contact will take place every Saturday at the contact centre")
            })
        };
        var svc2 = Svc(lib2, ai, aiOn: true);
        Run(lib2, svc2, src2, new BulkOptions { RunAi = false, Sections = new() { "people" } });
        int callsBefore = ai.Calls;
        var job2 = svc2.CreateLibraryJob(lib2.Db.GetDocuments(), new BulkOptions { LibraryCheck = true, RunAi = true, Sections = new() { "court" } }, "Library check");
        svc2.RunAsync(job2.Id).GetAwaiter().GetResult();
        Check(ai.Calls > callsBefore && Findings(lib2, FindingKinds.Hearing).Count == 1 && Findings(lib2, FindingKinds.Contact).Count == 0,
            "library check (AI): only the chosen sections are kept");
        var job3 = svc2.CreateLibraryJob(lib2.Db.GetDocuments(), new BulkOptions { LibraryCheck = true, RunAi = true, Sections = new() { "contact" } }, "Library check");
        svc2.RunAsync(job3.Id).GetAwaiter().GetResult();
        Check(Findings(lib2, FindingKinds.Contact).Count == 1 && Findings(lib2, FindingKinds.Hearing).Count == 1,
            "library check (AI): a different section is read again, and leaves the earlier section's suggestions alone");

        // ---- which copy is the latest edit
        string Para(int seed, int n) => string.Join(" ", Enumerable.Range(0, n).Select(i => $"Sentence {seed}-{i} sets out what was agreed about the holidays and the handover times."));
        var text = Para(3, 30);
        var saved = VersionDetector.Find(new List<VersionInput>
        {
            new(1, "Agreement (PDF)", text, null, new DateTime(2026, 10, 9), "a", SavedAt: new DateTime(2026, 9, 1, 9, 0, 0)),
            new(2, "Agreement (Word)", text, null, new DateTime(2026, 9, 2), "b", SavedAt: new DateTime(2026, 10, 3, 14, 2, 0)),
        }).Single();
        Check(saved.Members[0].DocumentId == 2 && saved.NewestReason.Contains("last saved") && saved.NewestReason.Contains("3 Oct 2026"),
            "versions: the date saved inside the file beats when the file was copied, and says so");
        Check(saved.Members[0].History.Contains("last saved 3 Oct 2026"), "versions: each copy has an edit history line");
        var revised = VersionDetector.Find(new List<VersionInput>
        {
            new(1, "Draft r3", text, null, null, "a", Revision: 3),
            new(2, "Draft r7", text, null, null, "b", Revision: 7),
        }).Single();
        Check(revised.Members[0].DocumentId == 2, "versions: with no dates, the higher Office revision is the latest edit");

        // ---- dates stored inside files (dates only)
        byte[] Docx(string modified, string revision)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                var e = zip.CreateEntry("docProps/core.xml");
                using var w = new StreamWriter(e.Open());
                w.Write("<?xml version=\"1.0\"?><cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
                        "xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
                        "<dc:creator>Someone</dc:creator><cp:lastModifiedBy>Someone</cp:lastModifiedBy><cp:revision>" + revision + "</cp:revision>" +
                        "<dcterms:modified xsi:type=\"dcterms:W3CDTF\">" + modified + "</dcterms:modified></cp:coreProperties>");
            }
            return ms.ToArray();
        }
        var dx = CaseKeeper.Services.Documents.FileDates.Read(Docx("2026-10-03T14:02:00Z", "7"), ".docx");
        Check(dx.SavedAt?.ToUniversalTime() == new DateTime(2026, 10, 3, 14, 2, 0) && dx.Revision == 7, "file dates: Word's last saved date and revision are read");
        Check(CaseKeeper.Services.Documents.FileDates.Read(Docx("2099-01-01T00:00:00Z", "2"), ".docx").SavedAt == null, "file dates: an impossible future date is ignored");
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << /Producer (x) /ModDate (D:20260915103000Z) /CreationDate (D:20260901090000Z) >> endobj\n%%EOF");
        Check(CaseKeeper.Services.Documents.FileDates.Read(pdf, ".pdf").SavedAt?.Date == new DateTime(2026, 9, 15), "file dates: a PDF's modification date is read");
        Check(CaseKeeper.Services.Documents.FileDates.Read(Encoding.ASCII.GetBytes("not really a pdf"), ".pdf").SavedAt == null &&
              CaseKeeper.Services.Documents.FileDates.Read(Array.Empty<byte>(), ".docx").SavedAt == null, "file dates: unreadable files give no date and don't fail");

        // ---- working days for a reply deadline (weekends skipped)
        Check(ComplaintRules.AddWorkingDays(new DateTime(2026, 11, 6), 1) == new DateTime(2026, 11, 9) &&
              ComplaintRules.AddWorkingDays(new DateTime(2026, 11, 2), 20) == new DateTime(2026, 11, 30), "complaint rules: working days skip weekends");
    }

    // ================================================================== cloud AI, versions and trust
    static void CloudAndVersions()
    {
        // ---- nothing is sent to a cloud AI unless the person agrees
        var lib = NewLib("cloud-none");
        var src = NewDir("cloud-none-src");
        Put(src, "letter.txt", "Letter from Ottoline Fairweather about the contact arrangements for the summer holidays and the handover times.");
        var cloud = new FakeCloud { Masker = new Masker(new[] { "Ottoline Fairweather" }) };
        var svc = new BulkImportService(lib, () => cloud, () => Settings(), () => true) { TextWorkers = 1 };
        var (job, files) = Run(lib, svc, src);
        Check(cloud.Sent.Count == 0 && files.All(f => f.AnalysisValue == AnalysisState.AwaitingAi) && job.LastErrorCode == "AI_CLOUD_DECLINED",
            "cloud AI: with no one to ask, nothing is sent and the documents wait");

        // ---- the person says no
        lib = NewLib("cloud-no");
        src = NewDir("cloud-no-src");
        Put(src, "letter.txt", "Letter from Ottoline Fairweather about the contact arrangements for the summer holidays and the handover times.");
        cloud = new FakeCloud { Masker = new Masker(new[] { "Ottoline Fairweather" }) };
        CloudSendRequest seen = null;
        svc = new BulkImportService(lib, () => cloud, () => Settings(), () => true)
        {
            TextWorkers = 1, ConfirmCloudSend = r => { seen = r; return Task.FromResult(CloudSendDecision.Cancel); }
        };
        (job, files) = Run(lib, svc, src);
        Check(seen != null && seen.Items.Count == 1 && seen.Host == "api.anthropic.com" && seen.Masking, "cloud AI: the review is shown with the host and masking");
        Check(seen.Items[0].Found.GetValueOrDefault("PERSON") >= 1,
            "cloud AI: the review counts what will be masked in each document");
        Check(cloud.Sent.Count == 0 && files.All(f => f.AnalysisValue == AnalysisState.AwaitingAi), "cloud AI: declined → nothing sent");
        Check(lib.Db.GetAudit(200).Where(a => a.Action == "ai.consent").Any(a => a.Summary.Contains("declined")), "cloud AI: the refusal is in the audit trail");

        // ---- the person agrees: masked text goes, the answer comes back unmasked
        lib = NewLib("cloud-yes");
        src = NewDir("cloud-yes-src");
        Put(src, "C100 application.txt", "Form C100 application by Ottoline Fairweather (ottoline@example.org) for a child arrangements order. Phone 07700 900456.");
        cloud = new FakeCloud { Masker = new Masker(new[] { "Ottoline Fairweather" }) };
        cloud.Inner.Respond = u => Answer(u, KeyFor(u, "C100"), 0.9, summary: "Application by " + Regex.Match(u, @"\[PERSON-\d+\]").Value);
        svc = new BulkImportService(lib, () => cloud, () => Settings(), () => true)
        {
            TextWorkers = 1, ConfirmCloudSend = _ => Task.FromResult(new CloudSendDecision { Proceed = true })
        };
        (job, files) = Run(lib, svc, src);
        Check(files.All(f => f.AnalysisValue == AnalysisState.Done), "cloud AI: agreed → the documents are analysed");
        Check(cloud.Sent.Count > 0 && cloud.Sent.All(t => !t.Contains("Ottoline") && !t.Contains("Fairweather") && !t.Contains("ottoline@example.org") && !t.Contains("07700 900456")),
            "cloud AI: no names, e-mail addresses or phone numbers in what was sent");
        var sum = Findings(lib, FindingKinds.Summary).FirstOrDefault();
        Check(sum != null && sum.Detail.Contains("Ottoline Fairweather") && !sum.Detail.Contains("[PERSON-"), "cloud AI: the answer is unmasked before it's saved");
        var consent = lib.Db.GetAudit(200).FirstOrDefault(a => a.Action == "ai.consent");
        Check(consent != null && consent.Details.Contains("api.anthropic.com") && consent.Details.Contains("Masking: on") && !consent.Details.Contains("Ottoline"),
            "cloud AI: the agreement is audited with host and masking, never the content");

        // ---- documents that can't be masked well: skipped, or read by the local AI instead
        foreach (var choice in new[] { "skip", "local" })
        {
            lib = NewLib("cloud-poor-" + choice);
            src = NewDir("cloud-poor-" + choice + "-src");
            Put(src, "names.txt", "Report by Sandra Pemberton. Kevin Oduya visited. Lena Marsh and Tobias Wren attended. Grace Holloway noted it. Felix Arden agreed.");
            Put(src, "plain.txt", "A short note about the arrangements for collecting the children from school on weekdays and at the weekend.");
            cloud = new FakeCloud { Masker = new Masker() };
            var local = new FakeAi();
            svc = new BulkImportService(lib, () => cloud, () => Settings(), () => true)
            {
                TextWorkers = 1, LocalAiFactory = () => local,
                ConfirmCloudSend = r => Task.FromResult(new CloudSendDecision { Proceed = true, PoorlyMasked = choice })
            };
            (job, files) = Run(lib, svc, src);
            var poorFile = files.Single(f => f.FileName == "names.txt");
            Check(cloud.Sent.All(t => !t.Contains("Pemberton")), $"poorly masked ({choice}): never sent to the cloud");
            Check(choice == "skip" ? poorFile.AnalysisValue == AnalysisState.Skipped && poorFile.ErrorCode == "AI_NOT_SENT"
                                   : poorFile.AnalysisValue == AnalysisState.Done && local.Calls > 0,
                choice == "skip" ? "poorly masked: skipped, with the reason" : "poorly masked: read by the local AI instead");
            Check(files.Single(f => f.FileName == "plain.txt").AnalysisValue == AnalysisState.Done, $"poorly masked ({choice}): the other documents still go to the cloud");
        }

        // ---- versions: same words in different files, drafts with edits, and unrelated documents
        string Para(int seed, int n) => string.Join(" ", Enumerable.Range(0, n).Select(i => $"Sentence {seed}-{i} sets out what happened at the handover on that day and who was present."));
        var v1 = Para(1, 30);
        var v2 = v1 + " The applicant now also asks for overnight contact every other weekend. A further hearing is requested.";
        var other = Para(7, 30).Replace("handover", "school meeting").Replace("present", "late");
        var inputs = new List<VersionInput>
        {
            new(1, "Statement draft", v1, null, new DateTime(2026, 9, 12), "h1"),
            new(2, "Statement final", v2, new DateTime(2026, 10, 3), new DateTime(2026, 10, 3), "h2"),
            new(3, "Statement final (Word copy)", v2, null, new DateTime(2026, 10, 1), "h3"),
            new(4, "Unrelated letter", other, null, null, "h4"),
            new(5, "Exact copy A", Para(9, 20), null, null, "same"),
            new(6, "Exact copy B", Para(9, 20), null, null, "same"),
            new(7, "Tiny note", "See you Tuesday.", null, null, "h7"),
        };
        var groups = VersionDetector.Find(inputs);
        var versions = groups.FirstOrDefault(g => g.Members.Any(m => m.DocumentId == 1));
        Check(versions != null && versions.Members.Select(m => m.DocumentId).OrderBy(i => i).SequenceEqual(new long[] { 1, 2, 3 }), "versions: the draft and both finals are grouped");
        Check(versions?.Kind == VersionKind.Versions, "versions: a draft with edits counts as versions");
        Check(versions?.Members[0].DocumentId == 2 && versions.NewestReason.Contains("3 Oct 2026"), "versions: the newest is the one dated latest, and says why");
        var draft = versions?.Members.Single(m => m.DocumentId == 1);
        Check(draft?.Diff is { Added: >= 2, Removed: 0 } && draft.Diff.AddedSamples.Any(a => a.Contains("overnight contact")), "versions: what the newest added is listed");
        Check(groups.All(g => g.Members.All(m => m.DocumentId != 4 && m.DocumentId != 7)), "versions: unrelated and very short documents aren't grouped");
        Check(groups.Any(g => g.Kind == VersionKind.Exact && g.Members.Count == 2), "versions: byte-identical files are exact copies");
        Check(VersionDetector.TextSimilarity(v2, v2) == 1 && VersionDetector.TextSimilarity(v1, other) < 0.2, "versions: similarity measure");
        var many = Enumerable.Range(0, 400).Select(i => new VersionInput(100 + i, "Doc " + i, Para(1000 + i, 15), null, null, "x" + i)).ToList();
        var sw = Stopwatch.StartNew();
        var none = VersionDetector.Find(many);
        Check(none.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(20), $"versions: 400 different documents compared quickly ({sw.ElapsedMilliseconds} ms), none grouped");
        var g2 = groups.First(g => g.Members.Any(m => m.DocumentId == 1));
        Check(g2.Key == VersionDetector.Find(inputs.AsEnumerable().Reverse().ToList()).First(g => g.Members.Any(m => m.DocumentId == 1)).Key, "versions: a group's key doesn't depend on order");

        // ---- trust signals
        var macro = new CaseDocument { Title = "Spreadsheet", FileExtension = ".xlsm", DocumentDate = new DateTime(2026, 10, 9) };
        var signals = FindingChecks.TrustSignals(macro, new Dictionary<int, string> { [1] = "Costs schedule." }, new DateTime(2026, 9, 1));
        Check(signals.Any(x => x.Contains("macros")) && signals.Any(x => x.Contains("last changed")), "trust: macros and a date after the file was last changed are flagged");
        Check(FindingChecks.TrustSignals(new CaseDocument { Title = "Letter", FileExtension = ".pdf" }, new Dictionary<int, string> { [1] = Para(3, 10) }, null).Count == 0,
            "trust: an ordinary document has no warnings");
    }

    // ================================================================== synthetic files
    static byte[] Docx(string text)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Entry(string name, string body)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(body);
            }
            Entry("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            Entry("_rels/.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"R1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Entry("word/document.xml", "<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                string.Concat(text.Split('\n').Select(l => $"<w:p><w:r><w:t xml:space=\"preserve\">{System.Security.SecurityElement.Escape(l)}</w:t></w:r></w:p>")) +
                "</w:body></w:document>");
        }
        return ms.ToArray();
    }

    /// <summary>A picture of typed text, like a phone photo of a letter, for the OCR tests.</summary>
    static byte[] TextImage(string text)
    {
        byte[] result = null;
        var t = new Thread(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1400, 400));
                var ft = new FormattedText(text, System.Globalization.CultureInfo.GetCultureInfo("en-GB"), FlowDirection.LeftToRight,
                    new Typeface("Arial"), 36, Brushes.Black, 1.0) { MaxTextWidth = 1300 };
                dc.DrawText(ft, new Point(40, 40));
            }
            var bmp = new RenderTargetBitmap(1400, 400, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            result = ms.ToArray();
            // Close WPF on this thread properly, or the graphics driver can fault when the process exits
            System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    static void Pdf(string path, string text, byte[] asImage = null)
    {
        var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        using (var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page))
        {
            if (asImage == null)
                gfx.DrawString(text, new PdfSharp.Drawing.XFont("Arial", 16), PdfSharp.Drawing.XBrushes.Black, new PdfSharp.Drawing.XPoint(50, 100));
            else
            {
                using var ms = new MemoryStream(asImage);
                using var img = PdfSharp.Drawing.XImage.FromStream(ms);
                gfx.DrawImage(img, 30, 60, 540, 155);
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        doc.Save(path);
    }

    // ================================================================== permissions helpers
    static void Deny(string path, FileSystemRights rights)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        if (Directory.Exists(path))
        {
            var di = new DirectoryInfo(path);
            var acl = di.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(me, rights, AccessControlType.Deny));
            di.SetAccessControl(acl);
        }
        else
        {
            var fi = new FileInfo(path);
            var acl = fi.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(me, rights, AccessControlType.Deny));
            fi.SetAccessControl(acl);
        }
    }

    static void ClearAcls(string dir)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        void Fix(FileSystemInfo i)
        {
            try
            {
                if (i is DirectoryInfo d) { var a = d.GetAccessControl(); a.PurgeAccessRules(me); a.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow)); d.SetAccessControl(a); }
                else if (i is FileInfo f) { var a = f.GetAccessControl(); a.PurgeAccessRules(me); a.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow)); f.SetAccessControl(a); }
            }
            catch { /* best effort */ }
        }
        Fix(new DirectoryInfo(dir));
        try { foreach (var i in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })) Fix(i); }
        catch { /* best effort */ }
    }

    // ================================================================== tests
    [STAThread]
    static int Main()
    {
        // Test pictures are drawn in software: no graphics-driver state to tear down at exit
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        SQLitePCL.Batteries_V2.Init();
        if (Directory.Exists(Root)) { ClearAcls(Root); try { Directory.Delete(Root, true); } catch { /* links */ } }
        Directory.CreateDirectory(Root);
        var logStart = LogLength();

        Discovery();
        Importing();
        TextAndOcr();
        Ai();
        DatesAndRecords();
        CrossChecks();
        Review();
        ReviewFixes();
        HardwareTiers();
        Security(logStart);
        Hardening041();
        CloudAndVersions();
        LibraryCheckAndComplaints();

        Console.WriteLine(failures == 0 ? $"ALL PASSED{(skipped > 0 ? $" ({skipped} skipped – see SKIP lines)" : "")}" : $"{failures} FAILURE(S)");
        SqliteConnection.ClearAllPools();
        Console.Out.Flush();
        return failures;
    }

    // ---------------------------------------------------------------- folder discovery
    static void Discovery()
    {
        var empty = NewDir("scan-empty");
        Check(FolderScanner.Scan(empty, new ScanOptions()).Entries.Count == 0, "scan: empty folder finds nothing");

        var nested = NewDir("scan-nested");
        Put(nested, "a.txt", "one");
        Put(nested, @"Orders\2025\order.pdf.txt", "two");
        Put(nested, @"Letters\Cafcass\letter.txt", "three");
        var s = FolderScanner.Scan(nested, new ScanOptions());
        Check(s.Entries.Count == 3 && s.Entries.Any(e => e.RelativePath == @"Orders\2025\order.pdf.txt"), "scan: nested folders found with relative paths");
        Check(FolderScanner.Scan(nested, new ScanOptions { IncludeSubfolders = false }).Entries.Count == 1, "scan: subfolders can be left out");

        var deep = NewDir("scan-deep");
        var p = deep;
        for (int i = 0; i < 30; i++) p = Path.Combine(p, "level" + i);
        Put(p, "deep.txt", "deep");
        Check(FolderScanner.Scan(deep, new ScanOptions()).Entries.Count == 1, "scan: a 30-level deep tree is walked");
        var shallow = FolderScanner.Scan(deep, new ScanOptions { MaxDepth = 5 });
        Check(shallow.Entries.Count == 0 && shallow.TooDeep, "scan: the depth limit stops runaway trees and says so");

        var hid = NewDir("scan-hidden");
        File.SetAttributes(Put(hid, "secret.txt", "x"), FileAttributes.Hidden);
        var hidFolder = Path.Combine(hid, "HiddenFolder");
        Put(hidFolder, "inside.txt", "y");
        File.SetAttributes(hidFolder, FileAttributes.Directory | FileAttributes.Hidden);
        Put(hid, "visible.txt", "z");
        var hs = FolderScanner.Scan(hid, new ScanOptions());
        Check(hs.Entries.Count == 1 && hs.HiddenSkipped == 2, "scan: hidden files and folders are left out by default");
        var hs2 = FolderScanner.Scan(hid, new ScanOptions { IncludeHiddenFiles = true, IncludeHiddenFolders = true });
        Check(hs2.Entries.Count == 3, "scan: hidden files and folders can be included");

        var mixed = NewDir("scan-types");
        Put(mixed, "sheet.xlsx", "x");
        Put(mixed, "~$draft.docx", "lock");
        Put(mixed, "Thumbs.db", "t");
        Put(mixed, "notes.txt", "fine");
        var ms = FolderScanner.Scan(mixed, new ScanOptions());
        Check(ms.Entries.Single(e => e.RelativePath == "sheet.xlsx").Status == ImportFileStatus.Unsupported, "scan: unsupported types are reported, not imported");
        Check(ms.Entries.Count(e => e.Status == ImportFileStatus.SystemFile) == 2, "scan: Office lock files and Thumbs.db are recognised as system files");

        // A junction pointing back at its own parent: must not loop
        var loop = NewDir("scan-loop");
        Put(loop, "real.txt", "r");
        var link = Path.Combine(loop, "loop");
        var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{loop}\"") { CreateNoWindow = true, UseShellExecute = false });
        mk!.WaitForExit();
        if (Directory.Exists(link))
        {
            var ls = FolderScanner.Scan(loop, new ScanOptions());
            Check(ls.Entries.Count == 1 && ls.LinksNotFollowed == 1, "scan: links aren't followed by default");
            var sw = Stopwatch.StartNew();
            var lf = FolderScanner.Scan(loop, new ScanOptions { FollowLinks = true });
            Check(lf.Entries.Count == 1 && lf.LoopsAvoided >= 1 && sw.ElapsedMilliseconds < 5000, "scan: following links, a loop back to the parent is detected and skipped");
            Directory.Delete(link);
        }
        else Skip("scan: junction loop (couldn't create a junction here)");

        // Longer than the old 260-character limit
        var longRoot = NewDir("scan-long");
        var lp = longRoot;
        while (lp.Length < 320) lp = Path.Combine(lp, new string('f', 40));
        var longFile = Put(lp, "long-path-letter.txt", "A letter with a very long path.");
        var lscan = FolderScanner.Scan(longRoot, new ScanOptions());
        Check(longFile.Length > 260 && lscan.Entries.Count == 1, $"scan: a {longFile.Length}-character path is found");
        FolderScanner.FindDuplicates(lscan, new Dictionary<string, long>());
        Check(lscan.Entries[0].Hash.Length == 64, "scan: a long-path file is fingerprinted");

        // Permissions: an unreadable file and an unopenable folder are reported, and the scan carries on
        var perm = NewDir("scan-perm");
        var locked = Put(perm, "locked.txt", "locked");
        Put(perm, "open.txt", "open");
        var lockedDir = Path.Combine(perm, "LockedFolder");
        Put(lockedDir, "inside.txt", "hidden from us");
        Deny(locked, FileSystemRights.ReadData);
        Deny(lockedDir, FileSystemRights.ListDirectory);
        var ps = FolderScanner.Scan(perm, new ScanOptions());
        Check(ps.InaccessibleFolders.Contains("LockedFolder"), "scan: a folder we can't open is reported");
        FolderScanner.FindDuplicates(ps, new Dictionary<string, long>());
        Check(ps.Entries.Single(e => e.RelativePath == "locked.txt").Status == ImportFileStatus.Inaccessible &&
              ps.Entries.Single(e => e.RelativePath == "open.txt").Hash.Length == 64, "scan: an unreadable file is reported and the rest carry on");
        ClearAcls(perm);

        var cancel = new CancellationTokenSource();
        cancel.Cancel();
        try { FolderScanner.Scan(nested, new ScanOptions(), cancel.Token); Check(false, "scan: cancellation"); }
        catch (OperationCanceledException) { Check(true, "scan: cancelling stops the scan"); }
    }

    // ---------------------------------------------------------------- importing
    static void Importing()
    {
        var lib = NewLib("imp-lib");
        var src = NewDir("imp-src");
        Put(src, "only.txt", "Just one letter.");
        var ai = new FakeAi();
        var (job, files) = Run(lib, Svc(lib, ai, aiOn: false), src);
        Check(files.Single().StatusValue == ImportFileStatus.Imported && lib.Db.GetDocuments().Count == 1, "import: one file");
        Check(job.StateValue == ImportJobState.Completed && job.PhaseValue == ImportPhase.Review, "import: the job finishes and waits for review");
        var doc = lib.Db.GetDocuments().Single();
        Check(doc.SourcePath == "only.txt" && doc.DocumentDate == null && doc.SourceModifiedAt != null,
            "import: source path and file time kept as information; the file time is NOT used as the document date");
        Check(doc.CategoryName == "" && lib.Db.GetCategories().First(c => c.Id == doc.CategoryId).Name == "Uncategorised",
            "import: documents wait in Uncategorised until a category is approved");

        // Many files, mixed types, duplicates by content not name, same name different content
        var lib2 = NewLib("imp-many");
        var many = NewDir("imp-many-src");
        for (int i = 0; i < 40; i++) Put(many, $@"Bulk\note{i:00}.txt", $"Note number {i} with some words.");
        Put(many, @"A\statement.txt", "Statement version one.");
        Put(many, @"B\statement.txt", "Statement version two – different content.");
        Put(many, @"A\copy-of-note.txt", "Note number 3 with some words.");      // same contents as Bulk\note03.txt
        File.WriteAllBytes(Path.Combine(many, "word.docx"), Docx("A Word document about contact."));
        Put(many, "data.xlsx", "not supported");
        Put(many, "empty.txt", "");
        var before = Directory.EnumerateFiles(many, "*", SearchOption.AllDirectories).ToDictionary(f => f, f => (LibraryService.ComputeHash(f), File.GetLastWriteTimeUtc(f)));
        var (_, f2) = Run(lib2, Svc(lib2, ai, aiOn: false), many);
        Check(f2.Count(f => f.StatusValue == ImportFileStatus.Imported) == 43, $"import: many mixed files ({f2.Count(f => f.StatusValue == ImportFileStatus.Imported)} imported)");
        Check(f2.Count(f => f.RelativePath.EndsWith("statement.txt") && f.StatusValue == ImportFileStatus.Imported) == 2, "import: same file name, different contents → both imported");
        var pair = f2.Where(f => f.RelativePath is @"A\copy-of-note.txt" or @"Bulk\note03.txt").ToList();
        Check(pair.Count(f => f.StatusValue == ImportFileStatus.Imported) == 1 &&
              pair.Single(f => f.StatusValue != ImportFileStatus.Imported) is { StatusValue: ImportFileStatus.DuplicateSkipped, DuplicateOfDocumentId: not null },
            "import: same contents, different name → recognised as a duplicate and the match recorded");
        Check(f2.Single(f => f.RelativePath == "data.xlsx").StatusValue == ImportFileStatus.Unsupported &&
              f2.Single(f => f.RelativePath == "empty.txt").StatusValue == ImportFileStatus.Failed, "import: unsupported and empty files are reported, not imported");
        var after = Directory.EnumerateFiles(many, "*", SearchOption.AllDirectories).ToDictionary(f => f, f => (LibraryService.ComputeHash(f), File.GetLastWriteTimeUtc(f)));
        Check(before.Count == after.Count && before.All(kv => after[kv.Key] == kv.Value), "import: the original files are untouched (same contents and timestamps, nothing added)");

        // Re-importing the same folder: everything is a duplicate of the library now
        var (_, again) = Run(lib2, Svc(lib2, ai, aiOn: false), many);
        Check(again.Count(f => f.StatusValue == ImportFileStatus.DuplicateSkipped) == 44 && lib2.Db.GetDocuments().Count == 43,
            "import: importing the same folder twice adds nothing");
        // ...unless the person chose to keep duplicates
        var (_, kept) = Run(lib2, Svc(lib2, ai, aiOn: false), many, new BulkOptions { Duplicates = DuplicatePolicy.ImportSeparately });
        Check(kept.Count(f => f.StatusValue == ImportFileStatus.ImportedDuplicate) >= 40, "import: 'import duplicates as separate items' is honoured");

        // Cancel part-way, then resume: nothing lost, nothing doubled
        var lib3 = NewLib("imp-cancel");
        var svc3 = Svc(lib3, ai, aiOn: false);
        var opts = new BulkOptions();
        var scan = FolderScanner.Scan(many, opts.Scan);
        FolderScanner.FindDuplicates(scan, lib3.Db.GetDocumentHashes());
        var job3 = svc3.CreateJob(many, opts, BulkImportService.PreviewRows(scan, opts, lib3.Db.GetCategories()));
        using (var cts = new CancellationTokenSource())
        {
            svc3.Progress += pr => { if (pr.Phase == ImportPhase.Importing && pr.Done >= 10) cts.Cancel(); };
            svc3.RunAsync(job3.Id, cts.Token).GetAwaiter().GetResult();
        }
        var mid = svc3.GetJob(job3.Id);
        int partial = lib3.Db.GetDocuments().Count;
        Check(mid.StateValue == ImportJobState.Cancelled && partial is >= 10 and < 43, $"import: cancelling stops part-way ({partial} imported) and the job says so");
        Check(lib3.Db.VerifyAudit().Ok && lib3.Db.QuickCheck() == "ok", "import: after cancelling, the database and audit chain are valid");
        var svc3b = Svc(lib3, ai, aiOn: false);
        svc3b.RunAsync(job3.Id).GetAwaiter().GetResult();
        Check(lib3.Db.GetDocuments().Count == 43 && svc3b.GetJob(job3.Id).StateValue == ImportJobState.Completed, "import: resuming finishes the job with no duplicates");

        // A file removed after the scan
        var lib4 = NewLib("imp-missing");
        var gone = NewDir("imp-missing-src");
        var vanishing = Put(gone, "vanishing.txt", "Here for the scan only.");
        Put(gone, "stays.txt", "Still here.");
        var (_, f4) = Run(lib4, Svc(lib4, ai, aiOn: false), gone, adjust: _ => File.Delete(vanishing));
        Check(f4.Single(f => f.RelativePath == "vanishing.txt").StatusValue == ImportFileStatus.Missing &&
              f4.Single(f => f.RelativePath == "stays.txt").StatusValue == ImportFileStatus.Imported, "import: a file removed after the scan is reported Missing; the rest continue");

        // A database refusal half-way: the file is reported, no orphan copy is left, the rest import
        var lib5 = NewLib("imp-rollback");
        using (var c = new SqliteConnection($"Data Source={lib5.DatabasePath}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER test_refuse BEFORE INSERT ON Documents WHEN NEW.Title='boom' BEGIN SELECT RAISE(ABORT,'refused'); END;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var rb = NewDir("imp-rollback-src");
        Put(rb, "boom.txt", "This one is refused.");
        Put(rb, "fine.txt", "This one is fine.");
        var (_, f5) = Run(lib5, Svc(lib5, ai, aiOn: false), rb);
        Check(f5.Single(f => f.RelativePath == "boom.txt").StatusValue == ImportFileStatus.Failed && lib5.Db.GetDocuments().Count == 1,
            "import: a refused record fails that file only");
        Check(Directory.GetFiles(lib5.FilesFolder).Length == 1, "import: the refused file's copy was removed (no orphan in the library)");
        Check(lib5.Db.VerifyAudit().Ok, "import: the audit chain is intact after a refused import");

        // Encrypted library
        var encRoot = NewDir("imp-enc");
        var enc = new LibraryService(encRoot);
        enc.EnableProtection("correct horse battery staple", null);
        SqliteConnection.ClearAllPools();
        var vault = Vault.Load(encRoot);
        vault.TryUnlock("correct horse battery staple");
        enc = new LibraryService(encRoot, vault);
        var encSrc = NewDir("imp-enc-src");
        Put(encSrc, $"letter-{Marker}.txt", $"Private words {Marker} for the encrypted test.");
        var encAi = new FakeAi { Respond = u => Answer(u, summary: "Letter " + Marker, findings: F("issueForReview", "Mentions " + Marker, 1, "Private words " + Marker)) };
        Run(enc, Svc(enc, encAi), encSrc);
        var stored = Directory.GetFiles(enc.FilesFolder).Single();
        Check(Vault.LooksEncrypted(File.ReadAllBytes(stored)), "import: in a protected library the copy is encrypted");
        Check(Encoding.UTF8.GetString(enc.ReadDocumentBytes(enc.Db.GetDocuments().Single())).Contains(Marker), "import: and decrypts to the original");
        EncryptedFileBinding(enc, vault, stored);
        enc.Db.Checkpoint();
        SqliteConnection.ClearAllPools();
        var dbBytes = File.ReadAllBytes(enc.DatabasePath);
        Check(!Encoding.UTF8.GetString(dbBytes).Contains(Marker) && !Encoding.Unicode.GetString(dbBytes).Contains(Marker) && Findings(enc).Count > 0,
            "import: AI findings and job records are stored encrypted (no readable case text in the database file)");

        // A library on a network path (\\localhost\C$), where the share is available
        var unc = @"\\localhost\" + Root[0] + "$" + Root[2..] + @"\imp-unc";
        try
        {
            if (Directory.Exists(unc)) Directory.Delete(unc, true);
            Directory.CreateDirectory(unc);
            var netLib = new LibraryService(unc);
            var (_, fn) = Run(netLib, Svc(netLib, ai, aiOn: false), src);
            Check(netLib.IsOnNetworkDrive && fn.Single().StatusValue == ImportFileStatus.Imported, "import: into a library on a network path");
            SqliteConnection.ClearAllPools();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Skip("import: network-path library (\\\\localhost\\C$ isn't available here)");
        }
    }

    // ---------------------------------------------------------------- text and OCR
    static void TextAndOcr()
    {
        var lib = NewLib("ocr-lib");
        var src = NewDir("ocr-src");
        Put(src, "plain.txt", "The final hearing will be held at the Family Court.");
        File.WriteAllBytes(Path.Combine(src, "word.docx"), Docx("Order of District Judge Example.\nThe applicant shall file a statement."));
        Put(src, "blank.txt", "   \n   \n  ");
        File.WriteAllBytes(Path.Combine(src, "broken.docx"), Encoding.ASCII.GetBytes("this is not really a word document"));
        File.WriteAllBytes(Path.Combine(src, "broken.pdf"), Encoding.ASCII.GetBytes("%PDF-1.4 broken"));
        bool ocr = OcrService.Engine != null;
        if (ocr)
        {
            Pdf(Path.Combine(src, "searchable.pdf"), "NOTICE OF HEARING final hearing listed");
            Pdf(Path.Combine(src, "scanned.pdf"), null, TextImage("Scanned letter from the school about attendance"));
            File.WriteAllBytes(Path.Combine(src, "photo.png"), TextImage("Photo of a handwritten note about contact"));
        }
        var (_, files) = Run(lib, Svc(lib, new FakeAi(), aiOn: false), src);
        string Text(string name) => string.Join(" ", lib.Db.GetAllPageText(files.Single(f => f.RelativePath == name).DocumentId!.Value).Values);
        ImportFile Row(string name) => files.Single(f => f.RelativePath == name);

        Check(Row("plain.txt").TextValue == TextState.Ready && Text("plain.txt").Contains("final hearing"), "text: a text document is read");
        Check(Row("word.docx").TextValue == TextState.Ready && Text("word.docx").Contains("District Judge"), "text: a Word document is read");
        Check(Row("blank.txt").TextValue == TextState.NoText && Row("blank.txt").AnalysisValue == AnalysisState.NoText,
            "text: an empty document is marked for review by hand, never analysed");
        Check(Row("broken.docx").TextValue == TextState.Failed && Row("broken.docx").AnalysisValue == AnalysisState.NoText,
            "text: a corrupted Word file fails its text step without stopping the job");
        Check(Row("broken.pdf").TextValue == TextState.Failed, "text: a corrupted PDF (OCR failure) is recorded, not fatal");
        if (ocr)
        {
            Check(Row("searchable.pdf").TextValue == TextState.Ready && Text("searchable.pdf").Contains("HEARING", StringComparison.OrdinalIgnoreCase), "text: a searchable PDF is read");
            Check(Row("scanned.pdf").TextValue == TextState.Ready && Text("scanned.pdf").Contains("school", StringComparison.OrdinalIgnoreCase), "text: a scanned (image-only) PDF is read by OCR");
            Check(Row("photo.png").TextValue == TextState.Ready && Text("photo.png").Contains("contact", StringComparison.OrdinalIgnoreCase), "text: an image is read by OCR");
        }
        else Skip("text: PDF and image OCR (Windows OCR isn't installed on this PC)");
        Check(lib.Db.Search("final hearing").Count > 0, "text: imported text is in the full-text search index");
    }

    // ---------------------------------------------------------------- AI
    static void Ai()
    {
        // Ollama unavailable: import completes, analysis waits, a rule-based category is offered
        var lib = NewLib("ai-down");
        var src = NewDir("ai-down-src");
        Put(src, "C100 application.txt", "Form C100 application for a child arrangements order.");
        Put(src, "letter.txt", "A short letter about nothing in particular.");
        var down = new FakeAi { Available = false };
        var (job, files) = Run(lib, Svc(lib, down), src);
        Check(lib.Db.GetDocuments().Count == 2 && files.All(f => f.AnalysisValue == AnalysisState.AwaitingAi),
            "AI unavailable: the import still completes and documents wait for analysis");
        Check(job.LastErrorCode == "AI_UNAVAILABLE" && down.Calls == 0, "AI unavailable: recorded once for the job (no per-document errors)");
        var rule = Findings(lib, FindingKinds.Category).SingleOrDefault();
        Check(rule != null && rule.Model == "CaseKeeper rules" && rule.Title.StartsWith("C100") && rule.Confidence < BulkThresholds.High,
            "AI unavailable: CaseKeeper's own rules suggest C100 from the form code (never 'high' confidence)");

        // ...and later, when it's back: resuming analyses what was waiting
        down.Available = true;
        var svc = Svc(lib, down);
        svc.QueueReanalysis(job.Id, files.Select(f => f.DocumentId!.Value));
        svc.RunAsync(job.Id).GetAwaiter().GetResult();
        files = lib.Db.Query<ImportFile>("JobId=$j", ("$j", job.Id));
        Check(files.All(f => f.AnalysisValue == AnalysisState.Done) && down.Calls == 2, "retry: once the AI is back, waiting documents are analysed");

        // Timeouts, bad JSON, schema failures, invented categories
        AiCase("timeout-eased", ai => { ai.Script.Enqueue(new TimeoutException()); ai.Script.Enqueue(new TimeoutException()); },
            (f, fs) => f.AnalysisValue == AnalysisState.Done, "AI: two timeouts → settings eased off and the document is tried again (succeeds)");
        AiCase("timeout", ai => { for (int i = 0; i < 6; i++) ai.Script.Enqueue(new TimeoutException()); },
            (f, fs) => f.AnalysisValue == AnalysisState.Failed && f.ErrorCode == "AI_TIMEOUT", "AI: still timing out after easing off → the document is marked failed (AI_TIMEOUT) and the job carries on");
        AiCase("badjson", ai => { ai.Script.Enqueue("this is not json {"); ai.Script.Enqueue("{\"also\": broken"); },
            (f, fs) => f.AnalysisValue == AnalysisState.Failed && f.ErrorCode == "AI_INVALID_JSON", "AI: malformed JSON twice → failed with AI_INVALID_JSON");
        AiCase("badjson-once", ai => ai.Script.Enqueue("not json"),
            (f, fs) => f.AnalysisValue == AnalysisState.Done, "AI: malformed JSON once, then valid → retried and succeeds");
        AiCase("schema", ai => { ai.Script.Enqueue("{\"categorySuggestion\":{},\"findings\":[]}"); ai.Script.Enqueue("{\"documentSummary\":{\"summary\":\"x\"}}"); },
            (f, fs) => f.AnalysisValue == AnalysisState.Failed && f.ErrorCode == "AI_SCHEMA", "AI: JSON that fails the schema → failed with AI_SCHEMA");
        AiCase("fenced", ai => ai.Respond = u => "```json\n" + Answer(u, KeyFor(u, "C100"), 0.9) + "\n```",
            (f, fs) => f.AnalysisValue == AnalysisState.Done, "AI: an answer wrapped in a code fence is still accepted");
        AiCase("invented", ai => ai.Respond = u => Answer(u, "K99", 0.95),
            (f, fs) => fs.Single(x => x.Kind == FindingKinds.Category) is var c && c.Title.StartsWith("Uncategorised") && c.Warnings.Contains("doesn't exist"),
            "AI: an invented category id is rejected (retried, then treated as Unknown with a warning)");
        AiCase("high", ai => ai.Respond = u => Answer(u, KeyFor(u, "C100"), 0.93),
            (f, fs) => fs.Single(x => x.Kind == FindingKinds.Category) is var c && c.Title.StartsWith("C100") && c.ConfidenceBand == "High" && c.IsOpen,
            "AI: high-confidence classification is proposed (still waiting for approval)");
        AiCase("low", ai => ai.Respond = u => Answer(u, KeyFor(u, "C100"), 0.3),
            (f, fs) => fs.Single(x => x.Kind == FindingKinds.Category) is var c && c.Title.StartsWith("Uncategorised") && FindingApprover.PLong(c, "bestGuessId") != null,
            "AI: low-confidence classification becomes Uncategorised for review, keeping the best guess");
        AiCase("unknown", ai => ai.Respond = u => Answer(u, "UNKNOWN", 0.7),
            (f, fs) => fs.Single(x => x.Kind == FindingKinds.Category).Title.StartsWith("Uncategorised"), "AI: UNKNOWN goes to Uncategorised");

        // Schema enforcement at the source: the category must be one of the keys offered
        var schema = AnalysisSchema.Build(new[] { "K1", "K2" });
        var allowed = schema["properties"]!["categorySuggestion"]!["properties"]!["categoryId"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Check(allowed.SequenceEqual(new[] { "K1", "K2", "UNKNOWN" }), "AI: the JSON Schema only allows the offered category keys or UNKNOWN");

        // A long document is analysed in parts; the same finding from two parts is merged
        var big = NewLib("ai-chunks");
        var bigSrc = NewDir("ai-chunks-src");
        var pages = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"Paragraph {i}. " + new string('x', 180) +
            (i is 5 or 55 ? " The contact centre visit on 14 March 2026 was cancelled." : "")));
        Put(bigSrc, "long-report.txt", pages);
        var chunkAi = new FakeAi
        {
            Respond = u => u.Contains("14 March 2026")
                ? Answer(u, findings: F("timelineEvent", "Contact centre visit cancelled", null, "The contact centre visit on 14 March 2026 was cancelled", 0.8, "2026-03-14", "14 March 2026", role: "contact"))
                : Answer(u)
        };
        // A text file is one "page", so make it several pages by splitting it in the test library's page store
        var (bj, bf) = Run(big, Svc(big, chunkAi, settings: () => Settings(partChars: 4000)), bigSrc);
        Check(chunkAi.Calls >= 3, $"chunking: a long document is sent in parts ({chunkAi.Calls} requests)");
        var merged = Findings(big, FindingKinds.TimelineEvent);
        Check(merged.Count == 1, "chunking: the same event found in two parts is merged into one suggestion");

        // Page numbers: a quote that isn't on the page is flagged and its confidence capped
        var pageLib = NewLib("ai-pages");
        var pageSrc = NewDir("ai-pages-src");
        Put(pageSrc, "letter.txt", "Dear parent, the school confirms attendance was 96 per cent this term.");
        var pageAi = new FakeAi { Respond = u => Answer(u, findings: F("issueForReview", "Attendance concerns raised", 7, "The child was absent for six weeks", 0.9)) };
        Run(pageLib, Svc(pageLib, pageAi), pageSrc);
        var wrongPage = Findings(pageLib, FindingKinds.Issue).Single();
        Check(wrongPage.PageFrom == null && wrongPage.Confidence <= 0.35 && wrongPage.Warnings.Contains("aren't in the document"),
            "provenance: an invented quote / missing page is flagged and its confidence capped");

        // Cancellation during analysis, then resume; and nothing is redone that doesn't need to be
        var resumeLib = NewLib("ai-resume");
        var resumeSrc = NewDir("ai-resume-src");
        for (int i = 0; i < 6; i++) Put(resumeSrc, $"doc{i}.txt", $"Document number {i} about a hearing on 2 June 2027.");
        var slow = new FakeAi();
        var rsvc = Svc(resumeLib, slow);
        var ropts = new BulkOptions();
        var rscan = FolderScanner.Scan(resumeSrc, ropts.Scan);
        FolderScanner.FindDuplicates(rscan, resumeLib.Db.GetDocumentHashes());
        var rjob = rsvc.CreateJob(resumeSrc, ropts, BulkImportService.PreviewRows(rscan, ropts, resumeLib.Db.GetCategories()));
        slow.OnCall = n => { if (n == 3) rsvc.RequestPause(); };
        rsvc.RunAsync(rjob.Id).GetAwaiter().GetResult();
        var paused = rsvc.GetJob(rjob.Id);
        var rfiles = resumeLib.Db.Query<ImportFile>("JobId=$j", ("$j", rjob.Id));
        Check(paused.StateValue == ImportJobState.Paused && rfiles.All(f => f.AnalysisValue != AnalysisState.Running),
            "pause: analysis stops cleanly; nothing is left marked as running");
        int doneBefore = rfiles.Count(f => f.AnalysisValue == AnalysisState.Done);
        slow.OnCall = null;
        int callsBefore = slow.Calls;
        var rsvc2 = Svc(resumeLib, slow);
        Check(rsvc2.UnfinishedJobs().Any(j => j.Id == rjob.Id), "resume: a paused job is offered again (survives a restart)");
        rsvc2.RunAsync(rjob.Id).GetAwaiter().GetResult();
        rfiles = resumeLib.Db.Query<ImportFile>("JobId=$j", ("$j", rjob.Id));
        Check(rfiles.All(f => f.AnalysisValue == AnalysisState.Done) && slow.Calls - callsBefore == 6 - doneBefore,
            $"resume: only the unfinished documents are analysed ({slow.Calls - callsBefore} more requests)");

        // Re-running with nothing changed does nothing; changing the prompt, model or schema re-runs
        int c0 = slow.Calls;
        rsvc2.QueueReanalysis(rjob.Id, Array.Empty<long>());
        foreach (var f in rfiles) f.AnalysisValue = AnalysisState.Queued;      // queued, but fingerprints still match
        resumeLib.Db.SaveQuietBatch(rfiles);
        var job2 = rsvc2.GetJob(rjob.Id); job2.PhaseValue = ImportPhase.Analysing; resumeLib.Db.SaveQuiet(job2);
        rsvc2.RunAsync(rjob.Id).GetAwaiter().GetResult();
        Check(slow.Calls == c0, "versions: an unchanged document isn't analysed again");

        void RerunAll(BulkImportService s)
        {
            var fs = resumeLib.Db.Query<ImportFile>("JobId=$j", ("$j", rjob.Id));
            foreach (var f in fs) f.AnalysisValue = AnalysisState.Queued;
            resumeLib.Db.SaveQuietBatch(fs);
            var j = s.GetJob(rjob.Id); j.PhaseValue = ImportPhase.Analysing; resumeLib.Db.SaveQuiet(j);
            s.RunAsync(rjob.Id).GetAwaiter().GetResult();
        }
        c0 = slow.Calls;
        RerunAll(Svc(resumeLib, slow, settings: () => Settings("an edited prompt")));
        Check(slow.Calls - c0 == 6, "versions: editing the prompt makes the analyses out of date, so they re-run");
        c0 = slow.Calls;
        slow.ModelName = "another-model:7b";
        RerunAll(Svc(resumeLib, slow, settings: () => Settings("an edited prompt")));
        Check(slow.Calls - c0 == 6, "versions: changing the model re-runs them");
        c0 = slow.Calls;
        var stale = resumeLib.Db.Query<ImportFile>("JobId=$j", ("$j", rjob.Id));
        foreach (var f in stale) f.AnalysisFingerprint = "from-an-older-schema-version";
        resumeLib.Db.SaveQuietBatch(stale);
        RerunAll(Svc(resumeLib, slow, settings: () => Settings("an edited prompt")));
        Check(slow.Calls - c0 == 6, "versions: an analysis made with an older schema/check version re-runs");
        Check(resumeLib.Db.Query<AiFinding>("Status=$s", ("$s", (int)FindingStatus.Superseded)).Count > 0,
            "versions: old suggestions are kept as 'replaced', not deleted");

        // AI lost mid-run: the rest wait, nothing fails
        var lostLib = NewLib("ai-lost");
        var lostSrc = NewDir("ai-lost-src");
        for (int i = 0; i < 5; i++) Put(lostSrc, $"doc{i}.txt", $"Document {i}, a letter about the arrangements for the children.");
        var flaky = new FakeAi();
        flaky.OnCall = n => { if (n == 3) flaky.Available = false; };
        var (lj, lf) = Run(lostLib, Svc(lostLib, flaky), lostSrc);
        Check(lf.Count(f => f.AnalysisValue == AnalysisState.Done) == 2 && lf.Count(f => f.AnalysisValue == AnalysisState.AwaitingAi) == 3 &&
              lj.LastErrorCode == "AI_UNAVAILABLE", "retry: losing the AI mid-run (after bounded retries) leaves the rest waiting, not failed");
    }

    static void AiCase(string name, Action<FakeAi> setup, Func<ImportFile, List<AiFinding>, bool> ok, string label)
    {
        var lib = NewLib("ai-" + name);
        var src = NewDir("ai-" + name + "-src");
        Put(src, "C100 form.txt", "Form C100. Application under the Children Act 1989 for a child arrangements order.");
        var ai = new FakeAi();
        setup(ai);
        var (_, files) = Run(lib, Svc(lib, ai), src);
        var all = lib.Db.Query<AiFinding>("");
        Check(ok(files.Single(), all), label);
    }

    // ---------------------------------------------------------------- dates and records
    static List<AiFinding> Analyse(string text, string json, Func<DateTime> today = null)
    {
        var lib = NewLib("dates-" + Guid.NewGuid().ToString("N")[..6]);
        var src = NewDir("dates-src-" + Guid.NewGuid().ToString("N")[..6]);
        Put(src, "doc.txt", text);
        var ai = new FakeAi { Respond = u => json.Replace("{K}", "UNKNOWN") };
        Run(lib, new BulkImportService(lib, () => ai, () => Settings(), () => true), src);
        return lib.Db.Query<AiFinding>("");
    }

    static void DatesAndRecords()
    {
        var hearingText = "NOTICE OF HEARING. The application is listed for a Final Hearing on 12 March 2027 at 10:30am at the Family Court sitting at Exampletown.";
        var h = Analyse(hearingText, Answer("", findings: F("hearing", "Final hearing", 1, "listed for a Final Hearing on 12 March 2027", 0.92, "2027-03-12", "12 March 2027",
            role: "hearingListed", time: "10:30am", details: new JsonObject { ["hearingType"] = "Final Hearing", ["court"] = "Family Court sitting at Exampletown", ["hearingStatus"] = "listed" })))
            .Single(f => f.Kind == FindingKinds.Hearing);
        Check(h.DateValue == new DateTime(2027, 3, 12) && h.TimeText == "10:30" && h.DateRole == "hearingListed" && h.PageFrom == 1,
            "dates: an explicit hearing date, time and page are kept");

        var moved = Analyse("The hearing listed on 3 February 2027 is adjourned and re-listed on 17 February 2027.",
            Answer("", findings: new[]
            {
                F("hearing", "Hearing adjourned", 1, "The hearing listed on 3 February 2027 is adjourned", 0.9, "2027-02-03", "3 February 2027", role: "hearingAdjournedFrom",
                    details: new JsonObject { ["hearingStatus"] = "adjourned" }),
                F("hearing", "Re-listed hearing", 1, "re-listed on 17 February 2027", 0.9, "2027-02-17", "17 February 2027", role: "hearingAdjournedTo",
                    details: new JsonObject { ["previousDate"] = "2027-02-03" })
            }));
        Check(moved.Count(f => f.Kind == FindingKinds.Hearing) == 2 && moved.Any(f => f.DateRole == "hearingAdjournedTo" && FindingApprover.P(f, "previousDate") == "2027-02-03"),
            "dates: a hearing moved from one date to another keeps both dates and their meaning");

        var vac = Analyse("The final hearing on 9 April 2027 is vacated.", Answer("", findings: F("hearing", "Final hearing vacated", 1, "The final hearing on 9 April 2027 is vacated",
            0.9, "2027-04-09", "9 April 2027", role: "hearingVacated", details: new JsonObject { ["hearingStatus"] = "vacated" })));
        var vacLib = NewLib("vac-draft");
        Check(FindingApprover.Draft(vacLib.Db, vac.Single(f => f.Kind == FindingKinds.Hearing)) is Hearing { Status: "Vacated" }, "dates: a vacated hearing is proposed as Vacated, not Listed");

        var docVsEvent = Analyse("Letter dated 1 March 2026. The respondent says that on 5 February 2026 the handover was missed.",
            Answer("", docDate: "2026-03-01", docDateText: "1 March 2026", findings: F("timelineEvent", "Handover missed", 1,
                "on 5 February 2026 the handover was missed", 0.8, "2026-02-05", "5 February 2026", role: "incidentAlleged", assertedBy: "The respondent")));
        var summary = docVsEvent.Single(f => f.Kind == FindingKinds.Summary);
        var ev = docVsEvent.Single(f => f.Kind == FindingKinds.TimelineEvent);
        Check(summary.DateValue == new DateTime(2026, 3, 1) && summary.DateRole == "documentIssued" && ev.DateValue == new DateTime(2026, 2, 5) && ev.DateRole == "incidentAlleged",
            "dates: the document's date and the event's date are kept apart");
        Check(ev.Title.StartsWith("The respondent states:"), "allegations: an allegation is recorded as what someone states, not as fact");

        var multi = Analyse("On 2 May 2026 the school called. On 9 May 2026 the GP saw the child. On 16 May 2026 a referral was made.",
            Answer("", findings: new[]
            {
                F("timelineEvent", "School called", 1, "On 2 May 2026 the school called", 0.8, "2026-05-02", "2 May 2026", role: "eventStated"),
                F("timelineEvent", "GP appointment", 1, "On 9 May 2026 the GP saw the child", 0.8, "2026-05-09", "9 May 2026", role: "appointment"),
                F("timelineEvent", "Referral made", 1, "On 16 May 2026 a referral was made", 0.8, "2026-05-16", "16 May 2026", role: "eventStated")
            }));
        Check(multi.Where(f => f.Kind == FindingKinds.TimelineEvent).Select(f => f.DateValue).Distinct().Count() == 3, "dates: several dates in one paragraph each become their own suggestion");

        var approx = Analyse("Problems started in March 2026 and continued.", Answer("", findings: F("timelineEvent", "Problems started", 1, "Problems started in March 2026",
            0.6, null, "March 2026", precision: "month", role: "eventStated")));
        var a = approx.Single(f => f.Kind == FindingKinds.TimelineEvent);
        var lib = NewLib("approx-draft");
        Check(a.DatePrecision == "month" && a.DateValue == new DateTime(2026, 3, 1) && a.DateDisplay == "Mar 2026", "dates: a month-only date is kept as month precision");
        Check(FindingApprover.Blocker(a, FindingApprover.Draft(lib.Db, a)) != null, "dates: a month-only date can't become an exact timeline date without the person choosing one");

        var amb = Analyse("Meeting held on 03/04/2027 at the contact centre.", Answer("", findings: F("timelineEvent", "Meeting at contact centre", 1,
            "Meeting held on 03/04/2027", 0.8, "2027-03-04", "03/04/2027", role: "eventStated")));
        var am = amb.Single(f => f.Kind == FindingKinds.TimelineEvent);
        Check(am.DateValue == new DateTime(2027, 4, 3) && am.Warnings.Contains("day/month"),
            "dates: an all-number UK date is read day-first (the document wins over the AI's reading) and flagged");

        var tense = Analyse("Hearing on 1 June 2020. Review on 1 June 2099.", Answer("", findings: new[]
        {
            F("timelineEvent", "Old hearing", 1, "Hearing on 1 June 2020", 0.8, "2020-06-01", "1 June 2020", role: "eventStated"),
            F("timelineEvent", "Future review", 1, "Review on 1 June 2099", 0.8, "2099-06-01", "1 June 2099", role: "review")
        }));
        Check(tense.Any(f => f.TemporalText == "Past") && tense.Any(f => f.TemporalText == "Future"), "dates: past and future dates are told apart");

        var deadline = Analyse("ORDER: The applicant shall file a statement by 4pm on 20 January 2027. The respondent shall reply within 14 days of this order.",
            Answer("", findings: new[]
            {
                F("direction", "Applicant to file a statement", 1, "The applicant shall file a statement by 4pm on 20 January 2027", 0.9, "2027-01-20", "20 January 2027",
                    role: "deadline", details: new JsonObject { ["responsibleParty"] = "Applicant", ["dueDate"] = "2027-01-20", ["dueDateText"] = "20 January 2027", ["dueDateExplicit"] = true }),
                F("deadline", "Respondent to reply", 1, "The respondent shall reply within 14 days of this order", 0.85, null, null, role: "deadline",
                    details: new JsonObject { ["responsibleParty"] = "Respondent", ["calculationRule"] = "within 14 days of this order", ["dueDateExplicit"] = false })
            }));
        var ruleOnly = Analyse("ORDER. 3. The respondent shall file and serve a statement in reply within 14 days of service of the applicant's statement.",
            Answer("", findings: F("direction", "Respondent to file reply", 1, "The respondent shall file and serve a statement in reply within 14 days of service of the applicant's statement", 1.0)));
        var ro = ruleOnly.Single(f => f.Kind == FindingKinds.Direction);
        Check(ro.DateValue == null && FindingApprover.NeedsCalculation(ro) && FindingApprover.P(ro, "calculationRule")!.StartsWith("within 14 days"),
            "deadlines: 'within 14 days of service' in the wording is recognised as a rule to work out, even if the AI didn't say so");
        var exp = deadline.Single(f => f.Kind == FindingKinds.Direction);
        var der = deadline.Single(f => f.Kind == FindingKinds.Deadline);
        Check(exp.DateValue == new DateTime(2027, 1, 20) && exp.DateRole == "deadline", "deadlines: an explicit due date is kept");
        Check(der.DateValue == null && FindingApprover.NeedsCalculation(der) && FindingApprover.Blocker(der, FindingApprover.Draft(lib.Db, der)) != null,
            "deadlines: a deadline needing calculation is never given a date by the AI and can't be approved until it's worked out");
        var orderDate = new DateTime(2027, 1, 6);
        var calc = Deadlines.Calculate("Calendar days after", orderDate, 14);
        Check(calc == new DateTime(2027, 1, 20), "deadlines: the existing calculator works it out from the approved date and rule (14 days after 6 Jan 2027 = 20 Jan)");
        var draft = (CaseAction)FindingApprover.Draft(lib.Db, der);
        draft.DueDate = calc;
        draft.Notes += "\n" + Deadlines.Explain("Calendar days after", orderDate, 14, calc);
        var outcome = FindingApprover.Approve(lib.Db, der, draft, edited: true);
        var saved = lib.Db.All<CaseAction>().Single();
        Check(outcome.Ok && saved.DueDate == calc && saved.Notes.Contains("14 calendar days after") && saved.Responsible == "Respondent",
            "deadlines: the approved calculation is saved with how it was worked out");

        var invented = Analyse("A letter with no dates at all, just some general words about arrangements.", Answer("", findings: F("timelineEvent", "Something happened", 1, "A letter with no dates at all", 0.9,
            "2026-07-07", "7 July 2026", role: "eventStated")));
        var inv = invented.Single(f => f.Kind == FindingKinds.TimelineEvent);
        Check(inv.DateValue == null && inv.Warnings.Contains("left blank"), "dates: a date the AI gives that isn't in the text is removed, never kept");

        var misread = Analyse("Contact took place on 4 July 2026.", Answer("", findings: F("contact", "Contact took place", 1, "Contact took place on 4 July 2026", 0.9,
            "2026-07-14", "4 July 2026", role: "contact")));
        Check(misread.Single(f => f.Kind == FindingKinds.Contact).DateValue == new DateTime(2026, 7, 4), "dates: when the AI misreads a date, the date as written is used");

        var person = Analyse("Present: the applicant mother and the Cafcass officer.", Answer("", findings: F("party", "Cafcass officer", 1, "the Cafcass officer", 0.9,
            details: new JsonObject { ["name"] = "Jane Inventedname", ["role"] = "Cafcass officer" })));
        Check(!person.Any(f => f.Kind == FindingKinds.Party), "names: a person the document doesn't name is dropped, not invented");

        // ---- small-model mistakes found in manual testing with a 4B model, corrected from the wording
        var notHearing = Analyse("Dear Dr Practice, please could you confirm Riley's appointment on 3 June 2026 for the court. Yours sincerely, Sam Sample",
            Answer("", findings: F("hearing", "Appointment confirmed", 1, "confirm Riley's appointment on 3 June 2026", 1.0, "2026-06-03", "3 June 2026", role: "hearingListed")));
        var nh = notHearing.Single(f => f.Kind == FindingKinds.TimelineEvent);
        Check(!notHearing.Any(f => f.Kind == FindingKinds.Hearing) && nh.DateRole == "eventStated" && nh.Warnings.Contains("doesn't describe a hearing") && nh.Confidence <= 0.6,
            "kinds: a 'hearing' whose wording isn't about a hearing becomes a timeline event, flagged");
        var direction = Analyse("ORDER. 2. The applicant shall file and serve a witness statement by 4pm on 6 October 2026.",
            Answer("", findings: F("order", "Applicant to file witness statement", 1, "The applicant shall file and serve a witness statement by 4pm on 6 October 2026",
                1.0, "2026-10-06", "6 October 2026", role: "hearingListed")));
        var dir = direction.Single(f => f.Kind == FindingKinds.Direction);
        Check(dir.DateValue == new DateTime(2026, 10, 6) && dir.DateRole == "deadline", "kinds: 'shall' wording proposed as an order becomes a direction with its due date");
        var notContact = Analyse("Form C100. Applicant: Sam Sample. Respondent: Alex Example. The child: Riley Sample.",
            Answer("", findings: F("contact", "Applicant's name", 1, "Applicant: Sam Sample", 1.0)));
        Check(!notContact.Any(f => f.Kind == FindingKinds.Contact), "kinds: a 'contact' entry with no contact wording and no date is dropped");

        var noOrder = Analyse("Dear Ms Sample, I will file my safeguarding letter with the court before the FHDRA. No concerns were raised by the school. Yours sincerely, Jordan Officer",
            Answer("", findings: new[]
            {
                F("order", "Safeguarding letter to be filed", 1, "I will file my safeguarding letter with the court before the FHDRA", 1.0),
                F("complaint", "No concerns raised", 1, "No concerns were raised by the school", 1.0),
                F("timelineEvent", "Hearing time estimate", 1, "before the FHDRA", 1.0)
            }));
        Check(noOrder.Any(f => f.Kind == FindingKinds.Task && f.Warnings.Contains("doesn't describe an order")) &&
              noOrder.Any(f => f.Kind == FindingKinds.Issue && f.Warnings.Contains("isn't about a complaint")) &&
              !noOrder.Any(f => f.Kind is FindingKinds.Order or FindingKinds.Complaint or FindingKinds.TimelineEvent),
            "kinds: an 'order' or 'complaint' the wording doesn't support is re-filed for checking; an undated timeline entry is dropped");

        var future = DateTime.Today.AddMonths(5);
        var futureDoc = Analyse($"NOTICE OF HEARING. The final hearing has been listed on {future:d MMMM yyyy} at the Family Court sitting at Exampletown.",
            Answer("", docDate: future.ToString("yyyy-MM-dd"), docDateText: future.ToString("d MMMM yyyy"),
                findings: F("hearing", "Final hearing listed", 1, $"The final hearing has been listed on {future:d MMMM yyyy}", 1.0, future.ToString("yyyy-MM-dd"), future.ToString("d MMMM yyyy"), role: "hearingListed")));
        var fs = futureDoc.Single(f => f.Kind == FindingKinds.Summary);
        Check(fs.DateValue == null && fs.Warnings.Contains("in the future"), "document date: a 'document date' in the future is removed and explained");
        var borrowed = Analyse("NOTICE OF HEARING. The FHDRA will take place on 15 September 2026 at 2pm by video.",
            Answer("", docDate: "2026-09-15", docDateText: "15 September 2026",
                findings: F("hearing", "FHDRA", 1, "The FHDRA will take place on 15 September 2026", 1.0, "2026-09-15", "15 September 2026", role: "hearingListed")));
        var bs = borrowed.Single(f => f.Kind == FindingKinds.Summary);
        Check(bs.Warnings.Contains("Check it's really the date the document was written") && bs.Confidence <= 0.75,
            "document date: a document date that is really the hearing date is flagged, and never 'high' confidence");

        // A specific court form needs the document to name it
        var formLib = NewLib("form-check");
        var formSrc = NewDir("form-check-src");
        Put(formSrc, "letter.txt", "Dear Dr Practice, please confirm Riley's appointment on 3 June 2026. Yours sincerely, Sam Sample");
        var formAi = new FakeAi { Respond = u => Answer(u, KeyFor(u, "C"), 1.0, findings: F("timelineEvent", "GP appointment", 1, "Riley's appointment on 3 June 2026", 1.0, "2026-06-03", "3 June 2026", role: "appointment")) };
        Run(formLib, Svc(formLib, formAi), formSrc);
        var fc = Findings(formLib, FindingKinds.Category).Single();
        var ft = Findings(formLib, FindingKinds.TimelineEvent).Single();
        Check(formAi.Calls > 0 && (KeyFor(formAi.LastUser, "C") == "UNKNOWN" || (fc.Title.StartsWith("Uncategorised") && fc.Warnings.Contains("doesn't mention form"))),
            "categories: a court-form category is only proposed when the document names that form");
        Check(fc.Title.StartsWith("Uncategorised") ? ft.Warnings.Contains("couldn't match this document") : true,
            "categories: when a document can't be matched, its other suggestions say to check they're relevant");

        // The short list offers the right everyday categories for an ordinary letter
        var cands = CategoryMatcher.Candidates("letter", "letter.txt", "Dear Dr Practice, please confirm Riley's appointment at the surgery. Yours sincerely, Sam Sample",
            NewLib("cands").Db.GetCategories(), 10);
        Check(cands.Any(c => c.Category.Name == "Correspondence with health services") && cands.Any(c => c.Category.Name == "Letters I have written"),
            "categories: a letter to a GP is offered 'Correspondence with health services' and 'Letters I have written'");
    }

    // ---------------------------------------------------------------- cross-document checks
    static void CrossChecks()
    {
        var lib = NewLib("checks");
        var src = NewDir("checks-src");
        Put(src, "notice-a.txt", "Final hearing listed for 10 March 2027. Case number EX12C00001. On 2 January 2026 the parties separated.");
        Put(src, "notice-b.txt", "Final hearing listed for 17 March 2027. Case number EX12C00001. On 2 January 2026 the parties separated.");
        Put(src, "other-case.txt", "Case number ZZ99Z99999. Letter.");
        Put(src, "p1.txt", "Contact on 1 January 2024. Notes from the contact centre.");
        Put(src, "p2.txt", "Contact on 1 February 2024. Notes from the contact centre.");
        Put(src, "p3.txt", "Contact on 1 March 2024. Notes from the contact centre.");
        Put(src, "p4.txt", "Contact on 1 April 2024. Notes from the contact centre.");
        Put(src, "p5.txt", "Contact on 1 May 2025. Notes from the contact centre.");
        var ai = new FakeAi
        {
            Respond = u =>
            {
                if (u.Contains("notice-a") || u.Contains("notice-b"))
                {
                    var date = u.Contains("notice-a") ? ("2027-03-10", "10 March 2027") : ("2027-03-17", "17 March 2027");
                    return Answer(u, caseNumber: "EX12C00001", findings: new[]
                    {
                        F("hearing", "Final hearing", 1, "Final hearing listed for " + date.Item2, 0.9, date.Item1, date.Item2, role: "hearingListed",
                            details: new JsonObject { ["hearingType"] = "Final hearing" }),
                        F("timelineEvent", "The parties separated", 1, "On 2 January 2026 the parties separated", 0.8, "2026-01-02", "2 January 2026", role: "eventStated")
                    });
                }
                if (u.Contains("other-case")) return Answer(u, caseNumber: "ZZ99Z99999");
                var m = Regex.Match(u, @"Contact on (\d+ \w+ \d{4})");
                if (m.Success && UkDates.TryParse(m.Groups[1].Value, out var d, out _, out _))
                    return Answer(u, findings: F("contact", "Contact", 1, "Contact on " + m.Groups[1].Value, 0.8, d.ToString("yyyy-MM-dd"), m.Groups[1].Value, role: "contact"));
                return Answer(u);
            }
        };
        Run(lib, Svc(lib, ai), src);
        var open = Findings(lib);
        var conflicts = open.Where(f => f.Kind == FindingKinds.Conflict).ToList();
        Check(conflicts.Any(c => c.Title.StartsWith("Different dates given for the Final hearing") && c.Detail.Contains("10 March 2027") && c.Detail.Contains("17 March 2027")),
            "checks: conflicting hearing dates across documents are shown side by side");
        Check(open.Where(f => f.Kind == FindingKinds.Hearing).All(f => f.CheckNotes.Contains("different date")), "checks: both conflicting hearings are flagged, neither resolved silently");
        Check(open.Where(f => f.Kind == FindingKinds.TimelineEvent).All(f => f.CheckNotes.Contains("Probably the same")), "checks: the same event from two documents is flagged as a probable duplicate");
        Check(conflicts.Any(c => c.Title == "Different case numbers appear in the documents") &&
              open.Single(f => f.Kind == FindingKinds.Summary && FindingApprover.P(f, "caseNumber") == "ZZ99Z99999").CheckNotes.Contains("different case"),
            "checks: a document with a different case number is flagged as possibly from another case");
        Check(conflicts.Any(c => c.Title.StartsWith("Nothing dated between")), "checks: a long gap in the chronology is pointed out");

        // Existing records are recognised; re-running the checks doesn't duplicate conflicts
        var hearing = open.First(f => f.Kind == FindingKinds.Hearing);
        var draft = FindingApprover.Draft(lib.Db, hearing);
        Check(FindingApprover.FindEquivalent(lib.Db, hearing, draft) == null, "checks: no existing-record warning before anything is recorded");
        FindingApprover.Approve(lib.Db, hearing, draft, edited: false);
        var other = Findings(lib, FindingKinds.Hearing).First();
        var otherDraft = (Hearing)FindingApprover.Draft(lib.Db, other);
        otherDraft.HearingDate = ((Hearing)draft).HearingDate;
        Check(FindingApprover.FindEquivalent(lib.Db, other, otherDraft)?.Contains("already a hearing") == true, "review: a probable duplicate of an existing record is warned about before creating it");
        int before = Findings(lib, FindingKinds.Conflict).Count;
        FindingChecks.Run(lib.Db);
        Check(Findings(lib, FindingKinds.Conflict).Count == before, "checks: running the checks again doesn't pile up duplicate conflicts");

        // Conflicting hearing dates are found even when the AI leaves the hearing type out of the details
        var bareLib = NewLib("checks-bare");
        var bareSrc = NewDir("checks-bare-src");
        Put(bareSrc, "order.txt", "ORDER. The final hearing is listed on 12 March 2027 at 10:30am.");
        Put(bareSrc, "notice.txt", "NOTICE OF HEARING. The final hearing has been listed on 19 March 2027 at 10:00am.");
        var bareAi = new FakeAi
        {
            Respond = u => u.Contains("12 March")
                ? Answer(u, findings: F("hearing", "Final hearing listed", 1, "The final hearing is listed on 12 March 2027", 1.0, "2027-03-12", "12 March 2027", role: "hearingListed"))
                : Answer(u, findings: F("hearing", "Final hearing listed", 1, "The final hearing has been listed on 19 March 2027", 1.0, "2027-03-19", "19 March 2027", role: "hearingListed"))
        };
        Run(bareLib, Svc(bareLib, bareAi), bareSrc);
        Check(Findings(bareLib, FindingKinds.Conflict).Any(c => c.Title.Contains("Final hearing") && c.Title.Contains("12 Mar 2027") && c.Title.Contains("19 Mar 2027")),
            "checks: conflicting final-hearing dates are found from the description when the hearing type detail is missing");
        Check(FindingApprover.Draft(bareLib.Db, Findings(bareLib, FindingKinds.Hearing).First()) is Hearing { HearingType: "Final hearing" },
            "review: a hearing's type is taken from its description when the AI left the detail out");

        // Coming up soon
        var soonLib = NewLib("checks-soon");
        var soonSrc = NewDir("checks-soon-src");
        var soon = DateTime.Today.AddDays(5);
        Put(soonSrc, "soon.txt", $"Hearing listed for {soon:d MMMM yyyy}.");
        var soonAi = new FakeAi { Respond = u => Answer(u, findings: F("hearing", "Hearing", 1, $"Hearing listed for {soon:d MMMM yyyy}", 0.9, soon.ToString("yyyy-MM-dd"), soon.ToString("d MMMM yyyy"), role: "hearingListed")) };
        Run(soonLib, Svc(soonLib, soonAi), soonSrc);
        Check(Findings(soonLib, FindingKinds.Hearing).Single().CheckNotes.Contains("Coming up"), "checks: a hearing within two weeks is flagged for urgent review");
    }

    // ---------------------------------------------------------------- review
    static void Review()
    {
        var lib = NewLib("review");
        var src = NewDir("review-src");
        Put(src, "C2 application.txt", "Form C2. The final hearing is listed on 8 October 2027 at 2pm. The applicant states that on 3 September 2027 the child was collected late. " +
                                      "A copy of the Cafcass safeguarding letter was not enclosed. The respondent is Alex Example.");
        Put(src, "letter2.txt", "Form C2 again, a second copy with different words.");
        var ai = new FakeAi
        {
            Respond = u => Answer(u, KeyFor(u, "C2"), 0.9, findings: u.Contains("listed on 8 October") ? new[]
            {
                F("hearing", "Final hearing", 1, "The final hearing is listed on 8 October 2027", 0.9, "2027-10-08", "8 October 2027", role: "hearingListed", time: "2pm",
                    details: new JsonObject { ["hearingType"] = "final hearing" }),
                F("timelineEvent", "Child collected late", 1, "on 3 September 2027 the child was collected late", 0.8, "2027-09-03", "3 September 2027", role: "incidentAlleged", assertedBy: "The applicant"),
                F("missingDocument", "Cafcass safeguarding letter", 1, "A copy of the Cafcass safeguarding letter was not enclosed", 0.7),
                F("party", "Respondent", 1, "The respondent is Alex Example", 0.9, details: new JsonObject { ["name"] = "Alex Example", ["role"] = "Respondent" })
            } : Array.Empty<JsonObject>())
        };
        Run(lib, Svc(lib, ai), src);
        var db = lib.Db;
        int auditBefore = db.AuditCount();

        Check(db.All<Hearing>().Count == 0 && db.GetEvents().Count == 0 && db.GetParties().Count == 0 && db.All<CaseAction>().Count == 0 &&
              db.All<CourtOrder>().Count == 0 && db.All<ContactSession>().Count == 0 && db.GetComplaints().Count == 0,
            "safety: nothing reaches hearings, timeline, parties, tasks, orders, contact or complaints without approval");
        Check(db.GetDocuments().All(d => db.GetCategories().First(c => c.Id == d.CategoryId).Name == "Uncategorised"), "safety: no document is categorised without approval");

        // Approve a hearing
        var hearing = Findings(lib, FindingKinds.Hearing).Single();
        var h = (Hearing)FindingApprover.Draft(db, hearing);
        Check(h.Time == "14:00" && h.HearingType == "Final hearing" && h.Mode == "" && h.Status == "Listed", "review: a draft fills only what the document says (no invented attendance mode)");
        var res = FindingApprover.Approve(db, hearing, h, edited: false);
        var savedHearing = db.All<Hearing>().Single();
        Check(res.Ok && savedHearing.HearingDate == new DateTime(2027, 10, 8) && savedHearing.EventId != null && db.GetEvents().Any(e => e.Id == savedHearing.EventId),
            "review: approving a hearing creates it through the normal rules (with its timeline entry)");
        var audit = db.GetAudit(20).FirstOrDefault(x => x.Action == "ai.approve");
        Check(audit != null && audit.Details.Contains("AI-suggested") && audit.Details.Contains("approved by the user") && audit.Details.Contains("fake-model"),
            "audit: the approval is recorded as AI-suggested and user-approved, with model and prompt version");
        Check(db.GetAudit(30).Any(x => x.Action == "hearing.create"), "audit: the hearing itself is audited like any hand-made record");

        // Edit then approve
        var ev = Findings(lib, FindingKinds.TimelineEvent).Single();
        var e = (TimelineEvent)FindingApprover.Draft(db, ev);
        Check(e.Title.StartsWith("The applicant states"), "review: the timeline draft keeps the allegation as an allegation");
        e.Location = "School gates";
        FindingApprover.Approve(db, ev, e, edited: true);
        Check(db.GetEvents().Any(x => x.Location == "School gates") && db.GetAudit(5).First(x => x.Action == "ai.approve").Details.Contains("after editing"),
            "review: edit then approve saves the edited values and says so in the audit trail");

        // Reject, defer
        var missing = Findings(lib, FindingKinds.MissingDocument).Single();
        FindingApprover.Defer(db, missing);
        Check(db.Query<AiFinding>("Id=$i", ("$i", missing.Id)).Single().StatusValue == FindingStatus.Deferred, "review: defer keeps it for later");
        var party = Findings(lib, FindingKinds.Party).Single();
        FindingApprover.Reject(db, party);
        var rejected = db.Query<AiFinding>("Id=$i", ("$i", party.Id)).Single();
        Check(rejected.StatusValue == FindingStatus.Rejected && rejected.Excerpt == "" && db.GetParties().Count == 0 &&
              db.GetAudit(5).Any(x => x.Action == "ai.reject"), "review: reject records the decision, keeps no evidence text, creates nothing");

        // Bulk category approval
        var cats = Findings(lib, FindingKinds.Category);
        foreach (var c in cats) FindingApprover.Approve(db, c, FindingApprover.Draft(db, c), edited: false);
        Check(cats.Count == 2 && db.GetDocuments().All(d => db.GetCategories().First(c => c.Id == d.CategoryId).Code == "C2"), "review: bulk-approving categorisations files the documents");

        // Source navigation data: every finding points at a real document page
        Check(db.Query<AiFinding>("Kind<>'conflict'").All(f => db.GetDocument(f.DocumentId) != null && (f.PageFrom == null || db.GetPageText(f.DocumentId, f.PageFrom.Value).Length > 0)),
            "review: every suggestion links to its source document and a page that exists");

        // Undo
        var approvedHearing = db.Query<AiFinding>("Id=$i", ("$i", hearing.Id)).Single();
        FindingApprover.Undo(db, approvedHearing);
        Check(db.All<Hearing>().Count == 0 && !db.GetEvents().Any(x => x.EventType == "Hearing") &&
              db.Query<AiFinding>("Id=$i", ("$i", hearing.Id)).Single().StatusValue == FindingStatus.Proposed, "review: undo removes the hearing and its timeline entry and puts the suggestion back");
        var cat = db.Query<AiFinding>("Id=$i", ("$i", cats[0].Id)).Single();
        FindingApprover.Undo(db, cat);
        Check(db.GetCategories().First(c => c.Id == db.GetDocument(cat.DocumentId).CategoryId).Name == "Uncategorised", "review: undoing a category puts the old one back");
        Check(db.GetAudit(5).Any(x => x.Action == "ai.undo"), "audit: undo is recorded");

        // Optional auto-apply: categories only, off by default, audited, reversible
        var autoLib = NewLib("auto");
        var autoSrc = NewDir("auto-src");
        Put(autoSrc, "C100 form.txt", "Form C100 application. The hearing is listed on 1 December 2027.");
        var autoAi = new FakeAi { Respond = u => Answer(u, KeyFor(u, "C100"), 0.97, findings: F("hearing", "Hearing", 1, "The hearing is listed on 1 December 2027", 0.99, "2027-12-01", "1 December 2027", role: "hearingListed")) };
        autoLib.Db.SetSetting("bulk.autoApply", "1");
        Run(autoLib, Svc(autoLib, autoAi), autoSrc);
        var applied = autoLib.Db.Query<AiFinding>("Status=$s", ("$s", (int)FindingStatus.AutoApplied));
        Check(applied.Count == 1 && applied[0].Kind == FindingKinds.Category && autoLib.Db.All<Hearing>().Count == 0,
            "auto-apply: only the category is applied; the very confident hearing still waits for approval");
        Check(autoLib.Db.GetAudit(30).Any(x => x.Action == "ai.autoapply") && FindingApprover.Undo(autoLib.Db, applied[0]).Ok, "auto-apply: audited and reversible");

        Check(db.AuditCount() > auditBefore && db.VerifyAudit().Ok, "audit: the chain verifies after approvals, rejections and undo");
    }

    // ---------------------------------------------------------------- defects found by the adversarial review
// ================================================================== hardware tiers and automatic step-down
    static void HardwareTiers()
    {
        Console.WriteLine("-- hardware tiers, presets and step-down");
        const long GB = 1024L * 1024 * 1024;
        AiHardware.Machine Pc(double gpu, double ram, bool gentle = false) => new(gpu, ram, gentle);

        Check(AiHardware.TierFor(Pc(0, 4)) == AiTier.Minimal, "no graphics card and 4 GB memory: Minimal (AI off recommended)");
        Check(AiHardware.TierFor(Pc(0, 16)) == AiTier.Basic, "no graphics card, 16 GB memory: Basic");
        Check(AiHardware.TierFor(Pc(8, 32)) == AiTier.Mid, "8 GB graphics card: Mid");
        Check(AiHardware.TierFor(Pc(16, 32)) == AiTier.High, "16 GB graphics card: High");
        Check(AiHardware.TierFor(Pc(24, 64)) == AiTier.VeryHigh, "24 GB graphics card: VeryHigh");
        Check(AiHardware.TierFor(Pc(24, 64, gentle: true)) == AiTier.Basic, "a big graphics card on battery or power saver: gentle Basic settings");
        Check(AiHardware.TierFor(Pc(0, 4, gentle: true)) == AiTier.Minimal, "a small laptop on battery: Minimal");

        Check(AiHardware.Fit(3 * GB, Pc(8, 16)) == "gpu", "a 3 GB model fits an 8 GB graphics card");
        Check(AiHardware.Fit(9 * GB, Pc(8, 32)) == "cpu", "a 9 GB model doesn't fit an 8 GB card but runs (slowly) in 32 GB memory");
        Check(AiHardware.Fit(20 * GB, Pc(8, 16)) == "too large", "a 20 GB model is too large for an 8 GB card and 16 GB memory");
        Check(AiHardware.Fit(3 * GB, Pc(0, 16)) == "cpu", "with no graphics card, a small model runs on the processor");

        var installed = new List<(string, long)> { ("gemma3:4b", (long)(3.3 * GB)), ("qwen3:14b", (long)(9.3 * GB)), ("qwen3:32b", 20 * GB) };
        var high = AiHardware.Recommend(installed, Pc(16, 32));
        Check(high.Tier == AiTier.High && high.Workers == 3 && high.Depth == "full", "High tier: three at once, full reading");
        Check(high.FastModel == "gemma3:4b", "Fast preset uses gemma3:4b");
        Check(high.CarefulModel == "qwen3:14b", "Careful preset on a 16 GB card uses qwen3:14b");
        Check(high.TooLarge.Contains("qwen3:32b"), "a 20 GB model is listed as too large for a 16 GB card with 32 GB memory");
        Check(high.Note.Contains("OLLAMA_NUM_PARALLEL"), "several at once comes with the OLLAMA_NUM_PARALLEL note");
        var huge = AiHardware.Recommend(installed, Pc(24, 64));
        Check(huge.CarefulModel == "qwen3:32b" && huge.Workers == 4 && huge.TooLarge.Count == 0, "24 GB card: the larger Careful model is allowed, four at once");
        var mid = AiHardware.Recommend(installed, Pc(8, 32));
        Check(mid.Workers == 2 && mid.FastModel == "gemma3:4b" && mid.CarefulModel == null, "8 GB card: two at once; qwen3:14b doesn't fit so there's no Careful model");
        var basic = AiHardware.Recommend(installed, Pc(0, 16));
        Check(basic.Workers == 1 && basic.Depth == "categories" && basic.FastModel == "gemma3:4b" && basic.CarefulModel == null,
            "Basic PC: gemma3:4b, one at a time, categories only");
        var minimal = AiHardware.Recommend(installed, Pc(0, 4));
        Check(!minimal.AiRecommended && minimal.Summary.Contains("AI off"), "Minimal PC: AI off recommended, and the summary says so");
        var none = AiHardware.Recommend(new List<(string, long)>(), Pc(16, 32));
        Check(none.FastModel == null && none.Note.Contains("ollama pull gemma3:4b"), "no model installed: tells you how to get the default one");
        Check(AiHardware.Recommend(new List<(string, long)> { ("gemma3:4b:latest", 3 * GB) }, Pc(16, 32)).FastModel == "gemma3:4b:latest" ||
              AiHardware.Same("gemma3:4b", "gemma3:4b:latest"), "\":latest\" tags are matched");

        Check(AiHardware.ThinkSetting("qwen3:14b") is false, "qwen3: thinking switched off");
        Check(AiHardware.ThinkSetting("gpt-oss:20b") is "low", "gpt-oss: thinking turned down to low");
        Check(AiHardware.ThinkSetting("gemma3:4b") == null, "gemma3: no thinking switch sent");
        Check(AiHardware.LighterThan("qwen3:14b", installed, Pc(16, 32)) == "gemma3:4b", "the lighter fallback for qwen3:14b is gemma3:4b");
        Check(AiHardware.LighterThan("gemma3:4b", installed, Pc(16, 32)) == null, "nothing lighter than gemma3:4b installed: no fallback");

        // ---- quick reading (categories only): one request, findings not asked for
        {
            var lib = NewLib("tiers-quick");
            var dir = NewDir("tiers-quick-src");
            Put(dir, "long letter.txt", string.Join("\n\n", Enumerable.Range(1, 400).Select(i => $"Paragraph {i}. The respondent wrote again about the arrangements for the children.")));
            var ai = new FakeAi { CategoriesOnly = true };
            var (job, files) = Run(lib, Svc(lib, ai, settings: () => Settings(partChars: 4000)), dir, new BulkOptions { RunAi = true });
            Check(files.All(f => f.AnalysisValue == AnalysisState.Done), "quick reading finishes");
            Check(ai.Calls == 1, $"quick reading reads only the start of a long document (1 request, got {ai.Calls})");
            Check(ai.LastUser.Contains("QUICK READING"), "quick reading tells the AI not to list findings");
        }

        // ---- photos with little writing get a quick reading; certain form codes skip the AI altogether
        Check(DocumentAnalyzer.IsLightDocument(new CaseDocument { OriginalFileName = "receipt.jpg" }, new Dictionary<int, string> { [1] = "TOTAL £12.40" }),
            "a photo with a few words is read quickly (category and description only)");
        Check(!DocumentAnalyzer.IsLightDocument(new CaseDocument { OriginalFileName = "notice.txt" }, new Dictionary<int, string> { [1] = "Notice of hearing on 3 March 2027" }),
            "a short text document still gets a full reading (it may hold a hearing date)");
        Check(!DocumentAnalyzer.IsLightDocument(new CaseDocument { OriginalFileName = "scan.png" }, new Dictionary<int, string> { [1] = new string('x', 2000) }),
            "a photographed page full of writing still gets a full reading");
        {
            var lib = NewLib("tiers-sure");
            var dir = NewDir("tiers-sure-src");
            Put(dir, "S7 report.txt", "Section 7 report prepared by the Family Court Adviser. Case ZX26C00999. The report covers the wishes and feelings of the child.");
            Put(dir, "C100 application.txt", "Form C100. Application under the Children Act 1989 for a child arrangements order. Case ZX26C00999.");
            Put(dir, "letter from school.txt", "Dear parent, this letter is about attendance at school this term and the dates of the next meetings.");
            var ai = new FakeAi { CategoriesOnly = true };
            var (job, files) = Run(lib, Svc(lib, ai), dir, new BulkOptions { RunAi = true });
            var cats = Findings(lib, FindingKinds.Category);
            Check(files.All(f => f.AnalysisValue == AnalysisState.Done), "quick reading with certain rules finishes");
            Check(ai.Calls == 2, $"the AI isn't asked about the S7 report; it is asked about the C100 (applicant's or respondent's copy?) and the letter ({ai.Calls} request(s))");
            Check(cats.Any(c => c.Model == "CaseKeeper rules" && c.Title.StartsWith("S7") && c.Confidence < BulkThresholds.High),
                "the form code gives the category, below 'high' confidence so it still needs approving");
        }
        // ---- step-down: one out-of-memory, then fine
        {
            var lib = NewLib("tiers-step");
            var dir = NewDir("tiers-step-src");
            for (int i = 1; i <= 4; i++) Put(dir, $"letter {i}.txt", $"Letter number {i} about contact arrangements for the children, written by the applicant's solicitors.");
            var ai = new FakeAi();
            ai.Script.Enqueue(new AiOverloadException("out of memory"));
            var svc = Svc(lib, ai);
            svc.AiWorkers = 2;
            var (job, files) = Run(lib, svc, dir, new BulkOptions { RunAi = true });
            Check(job.LastErrorCode == "AI_STEPPED_DOWN", "running out of memory eases the AI settings off, and the job records it");
            Check(files.All(f => f.AnalysisValue == AnalysisState.Done), "the document that ran out of memory is tried again and succeeds");
        }

        // ---- step-down all the way: always out of memory, ends cleanly with the lighter model recorded
        {
            var lib = NewLib("tiers-floor");
            var dir = NewDir("tiers-floor-src");
            for (int i = 1; i <= 3; i++) Put(dir, $"note {i}.txt", $"Note number {i} about the school holidays and the arrangements for handover at the contact centre.");
            var ai = new FakeAi { ModelName = "big-model:14b", LighterModel = "small-model:4b" };
            ai.OnCall = _ => throw new AiOverloadException("out of memory");
            var svc = Svc(lib, ai, settings: () => Settings(partChars: 9000));
            svc.AiWorkers = 3;
            var run = Task.Run(() => Run(lib, svc, dir, new BulkOptions { RunAi = true }));
            bool finished = run.Wait(TimeSpan.FromMinutes(2));
            Check(finished, "repeated out-of-memory errors don't hang the import");
            if (finished)
            {
                var (job, files) = run.Result;
                Check(files.All(f => f.AnalysisValue == AnalysisState.Failed && f.ErrorCode == "AI_OVERLOAD"), "documents that still can't be read are marked failed, ready to retry");
                Check(job.Model == "small-model:4b", "the job records the lighter model it switched to");
                Check(ai.Calls <= 3 * 3, $"no document is tried more than three times ({ai.Calls} requests)");
            }
        }

        // ---- the model spilling off the graphics card
        if (AiHardware.Gpu.Gb >= 2)
        {
            var lib = NewLib("tiers-spill");
            var dir = NewDir("tiers-spill-src");
            for (int i = 1; i <= 2; i++) Put(dir, $"memo {i}.txt", $"Memo number {i} about the next steps in the proceedings and the documents still to be filed.");
            var ai = new FakeAi { GpuShare = 0.4 };
            var svc = Svc(lib, ai);
            svc.AiWorkers = 2;
            var (job, files) = Run(lib, svc, dir, new BulkOptions { RunAi = true });
            Check(job.LastErrorCode == "AI_STEPPED_DOWN" && files.All(f => f.AnalysisValue == AnalysisState.Done),
                "a model that doesn't fit on the graphics card eases the settings off, and the import carries on");
        }
        else Skip("graphics-card spill check (this PC has no graphics card the AI can use)");
    }

    static void ReviewFixes()
    {
        var subDate = Analyse("Contact took place on 21 May 2025 at the centre, as arranged by the parties.",
            Answer("", findings: F("timelineEvent", "Contact", 1, "Contact took place on 21 May 2025", 0.9, "2025-05-01", "1 May 2025", role: "contact")));
        Check(subDate.Single(f => f.Kind == FindingKinds.TimelineEvent).DateValue == null,
            "fix: '1 May 2025' is not accepted just because the text says '21 May 2025'");
        Check(SourcePages.HasWhole("seen on 01/05/2025.", "01/05/2025") && !SourcePages.HasWhole("seen on 01/05/2025.", "01/05/20") &&
              !SourcePages.HasWhole("the bottom of the page", "tom") && SourcePages.HasWhole("tom said no", "tom"),
            "fix: dates and names are matched as whole words and numbers");
        var tomb = Analyse("The letter was found at the bottom of the box with other papers from the school.",
            Answer("", findings: F("party", "Tom", 1, "found at the bottom of the box", 0.9, details: new JsonObject { ["name"] = "Tom", ["role"] = "Other" })));
        Check(!tomb.Any(f => f.Kind == FindingKinds.Party), "fix: a name found only inside another word ('bottom') is not accepted");

        var anchored = Analyse("ORDER. The applicant shall file a statement 7 days before the hearing on 5 June 2026.",
            Answer("", findings: F("direction", "Applicant to file statement", 1, "The applicant shall file a statement 7 days before the hearing on 5 June 2026",
                0.9, "2026-06-05", "5 June 2026", role: "hearingListed")));
        var an = anchored.Single(f => f.Kind == FindingKinds.Direction);
        Check(an.DateValue == null && FindingApprover.NeedsCalculation(an) && FindingApprover.P(an, "sourceDate") == "2026-06-05",
            "fix: '7 days before the hearing on 5 June' is a rule to work out from 5 June, never a deadline of 5 June");

        var adultDob = Analyse("Statement of Alex Example. I live in Exampletown and work full time.",
            Answer("", findings: F("party", "Alex Example", 1, "Statement of Alex Example", 0.9,
                details: new JsonObject { ["name"] = "Alex Example", ["role"] = "Respondent", ["dateOfBirth"] = "1985-02-03" })));
        Check(FindingApprover.P(adultDob.Single(f => f.Kind == FindingKinds.Party), "dateOfBirth") == null,
            "fix: an adult's date of birth that isn't in the document is removed");

        var timeLib = Analyse("NOTICE OF HEARING. The final hearing is listed on 12 March 2027 at the Family Court sitting at Exampletown.",
            Answer("", findings: F("hearing", "Final hearing", 1, "The final hearing is listed on 12 March 2027", 0.9, "2027-03-12", "12 March 2027",
                role: "hearingListed", time: "10:30")));
        var tl = timeLib.Single(f => f.Kind == FindingKinds.Hearing);
        Check(tl.TimeText == "" && tl.Warnings.Contains("time"), "fix: a time that isn't written in the document is removed");

        // The cross-checks never overwrite a decision made while they were running
        var raceLib = NewLib("fix-race");
        var raceSrc = NewDir("fix-race-src");
        Put(raceSrc, "a.txt", "Final hearing listed for 10 March 2027 at the Family Court sitting at Exampletown.");
        var raceAi = new FakeAi { Respond = u => Answer(u, findings: F("hearing", "Final hearing", 1, "Final hearing listed for 10 March 2027", 0.9, "2027-03-10", "10 March 2027", role: "hearingListed")) };
        Run(raceLib, Svc(raceLib, raceAi), raceSrc);
        var stale = raceLib.Db.Query<AiFinding>("Status IN (0,3)");
        var target = stale.First(f => f.Kind == FindingKinds.Hearing);
        FindingApprover.Reject(raceLib.Db, raceLib.Db.Query<AiFinding>("Id=$i", ("$i", target.Id)).Single());
        target.CheckNotes = "stale note";
        raceLib.Db.SaveCheckNotes(stale);
        Check(raceLib.Db.Query<AiFinding>("Id=$i", ("$i", target.Id)).Single().StatusValue == FindingStatus.Rejected,
            "fix: the cross-checks' copy of a finding can't overwrite a rejection made meanwhile");
        var approvedOnce = raceLib.Db.Query<AiFinding>("Status IN (0,3)").First();
        var copy1 = raceLib.Db.Query<AiFinding>("Id=$i", ("$i", approvedOnce.Id)).Single();
        FindingApprover.Approve(raceLib.Db, copy1, FindingApprover.Draft(raceLib.Db, copy1), edited: false);
        var second = FindingApprover.Approve(raceLib.Db, approvedOnce, FindingApprover.Draft(raceLib.Db, approvedOnce), edited: false);
        Check(!second.Ok, "fix: a suggestion approved in one place can't be approved again from a stale copy");

        // An interrupted approval comes back with a warning
        var stuck = raceLib.Db.Query<AiFinding>("Status IN (0,3)").FirstOrDefault() ?? raceLib.Db.Query<AiFinding>("").First();
        stuck.StatusValue = FindingStatus.Applying;
        raceLib.Db.SaveQuiet(stuck);
        Check(raceLib.Db.RecoverInterruptedApprovals() == 1 &&
              raceLib.Db.Query<AiFinding>("Id=$i", ("$i", stuck.Id)).Single() is { StatusValue: FindingStatus.Proposed } r && r.Warnings.Contains("interrupted"),
            "fix: an approval interrupted by a crash is put back for review with a warning");

        // One document the server refuses doesn't stop the rest; running out of memory is reported as such
        var errLib = NewLib("fix-errors");
        var errSrc = NewDir("fix-errors-src");
        Put(errSrc, "refused.txt", "This document is refused by the AI server for some reason.");
        Put(errSrc, "memory.txt", "This document makes the AI server run out of memory.");
        Put(errSrc, "fine.txt", "This document is analysed without any trouble at all.");
        var errAi = new FakeAi
        {
            Respond = u => u.Contains("refused by") ? throw new AnalysisValidationException("AI_REQUEST", "refused")
                : u.Contains("out of memory") ? throw new AiOverloadException("oom") : Answer(u)
        };
        var (ej, ef) = Run(errLib, Svc(errLib, errAi), errSrc);
        Check(ef.Single(f => f.RelativePath == "refused.txt").ErrorCode == "AI_REQUEST" && ef.Single(f => f.RelativePath == "fine.txt").AnalysisValue == AnalysisState.Done &&
              ej.LastErrorCode != "AI_UNAVAILABLE", "fix: a request the AI server refuses fails that document only, not the whole job");
        Check(ef.Single(f => f.RelativePath == "memory.txt").ErrorCode == "AI_OVERLOAD", "fix: running out of memory is recorded as overload, not 'can't be reached'");

        // An undone automatic categorisation isn't re-applied by a later run
        var autoLib = NewLib("fix-auto");
        var autoSrc = NewDir("fix-auto-src");
        Put(autoSrc, "C100 form.txt", "Form C100 application for a child arrangements order, made by the applicant.");
        var autoAi = new FakeAi { Respond = u => Answer(u, KeyFor(u, "C100"), 0.98) };
        autoLib.Db.SetSetting("bulk.autoApply", "1");
        var (aj, _) = Run(autoLib, Svc(autoLib, autoAi), autoSrc);
        var applied = autoLib.Db.Query<AiFinding>("Status=$s", ("$s", (int)FindingStatus.AutoApplied)).Single();
        FindingApprover.Undo(autoLib.Db, applied);
        var j2 = autoLib.Db.Query<ImportJob>("Id=$i", ("$i", aj.Id)).Single();
        j2.PhaseValue = ImportPhase.Checking;
        autoLib.Db.SaveQuiet(j2);
        Svc(autoLib, autoAi).RunAsync(aj.Id).GetAwaiter().GetResult();
        Check(autoLib.Db.Query<AiFinding>("Id=$i", ("$i", applied.Id)).Single().StatusValue == FindingStatus.Proposed &&
              autoLib.Db.GetCategories().First(c => c.Id == autoLib.Db.GetDocuments().Single().CategoryId).Name == "Uncategorised",
            "fix: an automatic categorisation that was undone isn't applied again by a later run");

        // "Read text later": the documents are listed as waiting, then analysed when asked
        var laterLib = NewLib("fix-later");
        var laterSrc = NewDir("fix-later-src");
        Put(laterSrc, "letter.txt", "A letter whose text is read later in the background, not straight away.");
        var laterAi = new FakeAi();
        var (lj, lf) = Run(laterLib, Svc(laterLib, laterAi), laterSrc, new BulkOptions { RunOcr = false });
        Check(lf.Single().AnalysisValue == AnalysisState.AwaitingAi && lf.Single().ErrorMessage.Contains("text"),
            "fix: with 'read text later', documents are listed as waiting rather than silently skipped");
        var ls = Svc(laterLib, laterAi);
        ls.QueueReanalysis(lj.Id, new[] { lf.Single().DocumentId!.Value });
        ls.RunAsync(lj.Id).GetAwaiter().GetResult();
        Check(laterLib.Db.Query<ImportFile>("JobId=$j", ("$j", lj.Id)).Single().AnalysisValue == AnalysisState.Done,
            "fix: asking to analyse them reads their text and analyses them");
        var busy = Svc(laterLib, laterAi);
        laterAi.OnCall = _ => { try { busy.QueueReanalysis(lj.Id, Array.Empty<long>()); Check(false, "fix: queueing during a run is refused"); } catch (InvalidOperationException) { Check(true, "fix: re-analysis can't be queued into a job that is running"); } };
        busy.QueueReanalysis(lj.Id, new[] { lf.Single().DocumentId!.Value });
        busy.RunAsync(lj.Id).GetAwaiter().GetResult();
        laterAi.OnCall = null;

        // Crash recovery picks the right copy when an older one with the same contents exists
        var crashLib = NewLib("fix-crash");
        var crashSrc = NewDir("fix-crash-src");
        var crashFile = Put(crashSrc, "order.txt", "An order with some words in it for the crash recovery test.");
        crashLib.Import(crashFile, new CaseDocument { Title = "older copy" });
        var opts = new BulkOptions { Duplicates = DuplicatePolicy.ImportSeparately };
        var scan = FolderScanner.Scan(crashSrc, opts.Scan);
        FolderScanner.FindDuplicates(scan, crashLib.Db.GetDocumentHashes());
        var crashSvc = Svc(crashLib, new FakeAi(), aiOn: false);
        var cj = crashSvc.CreateJob(crashSrc, opts, BulkImportService.PreviewRows(scan, opts, crashLib.Db.GetCategories()));
        // As if CaseKeeper stopped after copying the file but before recording it
        crashLib.Import(crashFile, new CaseDocument { Title = "order", SourcePath = "order.txt" });
        crashSvc.RunAsync(cj.Id).GetAwaiter().GetResult();
        var cf = crashLib.Db.Query<ImportFile>("JobId=$j", ("$j", cj.Id)).Single();
        Check(crashLib.Db.GetDocuments().Count == 2 && cf.DocumentId == crashLib.Db.GetDocuments().Single(d => d.SourcePath == "order.txt").Id,
            "fix: after a crash, the copy this job made is recovered (not the older one), with no extra import");
    }

    // ---------------------------------------------------------------- security and privacy
    static readonly DateTime TestStart = DateTime.Now;
    static string _startLogFile;

    static long LogLength()
    {
        _startLogFile = AppLog.LogFiles().FirstOrDefault();
        return _startLogFile == null ? 0 : new FileInfo(_startLogFile).Length;
    }

    /// <summary>Everything written to the log since the tests started, across a change of day (a new log file).</summary>
    static string LogSince(long startOffset)
    {
        var sb = new StringBuilder();
        foreach (var file in AppLog.LogFiles().Where(f => f == _startLogFile || File.GetLastWriteTime(f) >= TestStart.AddSeconds(-2)))
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (file == _startLogFile) fs.Seek(Math.Min(startOffset, fs.Length), SeekOrigin.Begin);
            sb.Append(new StreamReader(fs).ReadToEnd());
        }
        return sb.ToString();
    }

    static void Security(long logStart)
    {
        var lib = NewLib("sec");
        var src = NewDir("sec-src");
        Put(src, $@"Private {Marker}\letter {Marker}.txt", $"This letter mentions {Marker} and a hearing on 4 June 2027.");
        Put(src, "bad.txt", "This one makes the AI fail, which is what this test needs.");
        var tempBefore = Directory.Exists(LibraryService.TempCopiesFolder) ? Directory.GetFiles(LibraryService.TempCopiesFolder, "*", SearchOption.AllDirectories).Length : 0;
        var ai = new FakeAi
        {
            Respond = u => u.Contains("makes the AI fail") ? throw new InvalidOperationException("Ollama said: " + Marker)
                : Answer(u, summary: Marker, findings: F("hearing", "Hearing about " + Marker, 1, "a hearing on 4 June 2027", 0.9, "2027-06-04", "4 June 2027", role: "hearingListed"))
        };
        var (job, files) = Run(lib, Svc(lib, ai), src);
        Check(files.Single(f => f.RelativePath == "bad.txt").ErrorCode == "AI_ERROR", "privacy: an AI crash is recorded with a safe code");

        // Logs: nothing from inside the documents, no file names, no folder path
        var newLog = LogSince(logStart);
        var recent = string.Join("\n", AppLog.RecentEntries.Select(x => x.Message + " " + x.Detail));
        Check(newLog.Length > 0 && !newLog.Contains(Marker) && !recent.Contains(Marker), "privacy: no case content or file names in the logs (even when the AI's error echoes text)");
        Check(!newLog.Contains(Root, StringComparison.OrdinalIgnoreCase) && !newLog.Contains(src, StringComparison.OrdinalIgnoreCase), "privacy: no source folder paths in the logs");

        // Audit and summaries: the folder's name at most, never its full path
        var auditText = string.Join("\n", lib.Db.GetAudit().Select(a => a.Summary + " " + a.Details));
        Check(!auditText.Contains(src, StringComparison.OrdinalIgnoreCase), "privacy: the audit trail doesn't record the full source path");
        var scan = FolderScanner.Scan(src, new ScanOptions());
        var summary = BulkImportService.ScanSummary(scan, BulkImportService.PreviewRows(scan, new BulkOptions(), lib.Db.GetCategories()));
        Check(!summary.Contains(src, StringComparison.OrdinalIgnoreCase) && !summary.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase) &&
              summary.Contains("Files found: 2"), "privacy: the copied scan summary has counts and relative paths only (no Windows path or user name)");
        var doc = lib.Db.GetDocuments().First();
        Check(!doc.SourcePath.Contains(':') && !doc.SourcePath.StartsWith("\\"), "privacy: documents keep a relative source path, never the full Windows path");

        // Temporary files: the bulk import writes none
        var tempAfter = Directory.Exists(LibraryService.TempCopiesFolder) ? Directory.GetFiles(LibraryService.TempCopiesFolder, "*", SearchOption.AllDirectories).Length : 0;
        Check(tempAfter == tempBefore, "privacy: no decrypted temporary copies are left behind by importing or analysing");
        Check(!Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).Any(f => !f.EndsWith(".txt")), "privacy: nothing is written into the source folder");

        // Integrity check covers the bulk tables; orphans are tidied
        var (orphanFindings, _) = lib.Db.CheckStaging(tidy: false);
        Check(orphanFindings == 0, "integrity: no orphaned suggestions");
        var purge = lib.Db.GetDocuments().First(d => d.OriginalFileName == "bad.txt");
        lib.Purge(purge);
        Check(lib.Db.Query<AiFinding>("DocumentId=$d", ("$d", purge.Id)).Count == 0, "integrity: permanently deleting a document removes its suggestions");

        // Backup and restore carry jobs, files, findings and decisions
        var f0 = lib.Db.Query<AiFinding>("Kind=$k", ("$k", FindingKinds.Hearing)).FirstOrDefault();
        if (f0 != null) FindingApprover.Reject(lib.Db, f0);
        var counts = (Jobs: lib.Db.Query<ImportJob>("").Count, Files: lib.Db.Query<ImportFile>("").Count, Findings: lib.Db.Query<AiFinding>("").Count,
            Rejected: lib.Db.Query<AiFinding>("Status=2").Count);
        var zip = Path.Combine(Root, "backup-test.zip");
        BackupService.CreateBackup(lib, zip, "backup password");
        var restored = NewLib("sec-restore");
        BackupService.RestoreBackup(restored, zip, "backup password");
        SqliteConnection.ClearAllPools();
        var reopened = new LibraryService(restored.RootFolder);
        Check(reopened.Db.Query<ImportJob>("").Count == counts.Jobs && reopened.Db.Query<ImportFile>("").Count == counts.Files &&
              reopened.Db.Query<AiFinding>("").Count == counts.Findings && reopened.Db.Query<AiFinding>("Status=2").Count == counts.Rejected,
            "backup: restoring a backup brings back import jobs, files, suggestions and decisions");
        Check(reopened.Db.VerifyAudit().Ok, "backup: the restored audit chain verifies");

        // Merging a backup into another library brings documents with their source paths, not another library's suggestions
        var merged = NewLib("sec-merge");
        var mr = BackupService.MergeBackup(merged, zip, "backup password");
        Check(mr.DocumentsAdded == 1 && merged.Db.GetDocuments().Single().SourcePath.Contains(Marker) && merged.Db.Query<AiFinding>("").Count == 0,
            "backup: merging brings documents (with source paths) but not another library's working suggestions");

        // Upgrading a v0.3.0 library: the new tables appear, nothing is lost
        var old = NewDir("sec-upgrade");
        var oldDb = Path.Combine(old, "casekeeper.db");
        using (var c = new SqliteConnection($"Data Source={oldDb};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"CREATE TABLE Categories(Id INTEGER PRIMARY KEY AUTOINCREMENT, Code TEXT, Name TEXT NOT NULL, GroupName TEXT, Description TEXT, BundleSection TEXT, SortOrder INTEGER, IsCustom INTEGER DEFAULT 0);
CREATE TABLE Documents(Id INTEGER PRIMARY KEY AUTOINCREMENT, Title TEXT, CategoryId INTEGER, OriginalFileName TEXT, StoredFileName TEXT, FileExtension TEXT, FileHash TEXT, FileSize INTEGER, DocumentDate TEXT, DateReceived TEXT, Author TEXT, Tags TEXT, Description TEXT, Notes TEXT, IsImportant INTEGER DEFAULT 0, PageCount INTEGER DEFAULT 0, OcrStatus INTEGER DEFAULT 0, ImportedAt TEXT, PartyId INTEGER, ExhibitRef TEXT, DeletedAt TEXT);
INSERT INTO Documents(Title,CategoryId,FileHash,FileExtension,ImportedAt,OcrStatus,PageCount) VALUES('Kept from 0.3.0',1,'ABC','.pdf','2026-10-01T10:00:00',1,1);";
            cmd.ExecuteNonQuery();
        }
        var up = new Database(oldDb);
        var up2 = new Database(oldDb);      // opening twice: the migration is idempotent
        Check(up2.GetDocuments().Single().Title == "Kept from 0.3.0" && up2.GetDocuments().Single().SourcePath == "" && up.Query<ImportJob>("").Count == 0,
            "upgrade: a v0.3.0 library gains the bulk-import tables and columns without losing anything (safe to run twice)");
    }

    /// <summary>v0.4.1: tampered backups, runnable files, settings files, the vault, and Remove all CaseKeeper data.</summary>
    static void Hardening041()
    {
        // ---- a backup whose database points a document outside the Files folder
        var lib = NewLib("h41-src");
        var src = NewDir("h41-files");
        var outsideTarget = Path.Combine(Root, "h41-outside.txt");
        File.WriteAllText(outsideTarget, "must survive");
        var d = lib.Import(Put(src, "note.txt", "An ordinary note."), new CaseDocument { CategoryId = lib.Db.GetCategories().First().Id });
        var zip = Path.Combine(Root, "h41-backup.zip");
        lib.Db.SetSetting("ai.anthropic.key", "c2FtcGxlLWNpcGhlcnRleHQ=");
        lib.Db.SetSetting("ai.cloud.consent.anthropic", "1");
        lib.Db.SetSetting("prompt.system", "kept in the backup");
        BackupService.CreateBackup(lib, zip);
        Check(lib.Db.GetSetting("ai.anthropic.key", "") != "", "backup: the library's own key is untouched");
        var peek = NewDir("h41-peek");
        ZipFile.ExtractToDirectory(zip, peek);
        SqliteConnection.ClearAllPools();
        var peekDb = new Database(Path.Combine(peek, "casekeeper.db"));
        Check(peekDb.GetSetting("ai.anthropic.key", "") == "" && peekDb.GetSetting("ai.cloud.consent.anthropic", "") == "" && peekDb.GetSetting("prompt.system", "") == "kept in the backup",
            "backup: AI keys and cloud consent are left out; other settings are kept");
        SqliteConnection.ClearAllPools();
        lib.Db.SetSetting("ai.anthropic.key", "");

        // Rewrite the backup: the record's stored name climbs out of Files, and a stray entry tries to escape staging
        var tampered = Path.Combine(Root, "h41-tampered.zip");
        var work = NewDir("h41-unpack");
        ZipFile.ExtractToDirectory(zip, work);
        SqliteConnection.ClearAllPools();
        using (var c = new SqliteConnection($"Data Source={Path.Combine(work, "casekeeper.db")};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE Documents SET StoredFileName=$n, FileHash='TAMPERED'";
            cmd.Parameters.AddWithValue("$n", @"..\..\h41-outside.txt");
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var storedName = Directory.GetFiles(Path.Combine(work, "Files")).Single();
        File.Move(storedName, Path.Combine(work, "Files", "h41-outside.txt"));
        using (var z = ZipFile.Open(tampered, ZipArchiveMode.Create))
        {
            z.CreateEntryFromFile(Path.Combine(work, "casekeeper.db"), "casekeeper.db");
            z.CreateEntryFromFile(Path.Combine(work, "Files", "h41-outside.txt"), "Files/h41-outside.txt");
            using var w = new StreamWriter(z.CreateEntry("Files/../../h41-escape.txt").Open());
            w.Write("escaped");
        }

        var merged = NewLib("h41-merge");
        // Where the tampered name would point from the unpacked backup: it must never be read
        File.WriteAllText(Path.Combine(merged.RootFolder, "h41-outside.txt"), "SECRET FROM ELSEWHERE");
        BackupService.MergeBackup(merged, tampered);
        var md = merged.Db.GetDocuments().Single();
        Check(Encoding.UTF8.GetString(merged.ReadDocumentBytes(md)) == "An ordinary note.",
            "backup: a tampered stored name can't pull a file from elsewhere on the PC into the library");
        Check(Path.GetDirectoryName(merged.GetFilePath(md)) == merged.FilesFolder && File.Exists(merged.GetFilePath(md)),
            "backup: a tampered stored name lands inside Files when merged");
        Check(File.ReadAllText(outsideTarget) == "must survive" && !File.Exists(Path.Combine(Root, "h41-escape.txt")) &&
              !File.Exists(Path.Combine(merged.RootFolder, "..", "h41-escape.txt")), "backup: nothing outside the library is written by a tampered backup");

        var restored = NewLib("h41-restore");
        BackupService.RestoreBackup(restored, tampered);
        SqliteConnection.ClearAllPools();
        var reopened = new LibraryService(restored.RootFolder);
        var rd = reopened.Db.GetDocuments().Single();
        Check(Path.GetDirectoryName(reopened.GetFilePath(rd)) == reopened.FilesFolder, "backup: a restored record can only reach a file inside Files");
        reopened.Purge(rd);
        Check(File.Exists(outsideTarget), "backup: deleting a tampered record for good never deletes a file outside the library");

        // ---- file types Windows would run, and extensions that aren't extensions
        Check(FileKinds.IsRunnable("letter.EXE") && FileKinds.IsRunnable(".js") && FileKinds.IsRunnable(@"C:\x\a.lnk") && !FileKinds.IsRunnable("a.pdf") &&
              !FileKinds.IsRunnable(".docx"), "shell: runnable file types are recognised, documents aren't");
        Check(FileKinds.SafeExtension(".pdf") == ".pdf" && FileKinds.SafeExtension(@"\..\evil") == "" && FileKinds.SafeExtension(".p d f") == "" &&
              FileKinds.SafeExtension("") == "" && FileKinds.SafeExtension(null) == "", "shell: only plain extensions are put on file names");
        var odd = new CaseDocument { Title = "Odd", StoredFileName = Path.GetFileName(lib.GetFilePath(d)), FileExtension = @"\..\..\escape" };
        var copy = lib.MakeFriendlyCopy(odd, NewDir("h41-copies"));
        Check(Path.GetDirectoryName(copy) == Path.Combine(Root, "h41-copies"), "shell: a readable copy always stays in the folder asked for");

        // ---- the vault: keys are wiped, and an absurd vault.json can't hang the unlock
        var vlib = NewDir("h41-vault");
        var vault = Vault.Load(vlib);
        vault.Create("correct horse battery staple");
        var key = vault.DatabasePassword;
        Check(vault.TryUnlock("correct horse battery staple") && vault.DatabasePassword == key, "vault: re-checking the password keeps the key in use");
        Check(!vault.TryUnlock("wrong") && vault.DatabasePassword == key, "vault: a wrong password doesn't disturb the unlocked key");
        vault.Lock();
        Check(!vault.IsUnlocked && vault.DatabasePassword == null, "vault: locking forgets the key");
        var reloaded = Vault.Load(vlib);
        Check(reloaded.TryUnlock("correct horse battery staple") && reloaded.DatabasePassword == key, "vault: the key unwraps again after locking");
        var vjson = Path.Combine(vlib, Vault.FileName);
        File.WriteAllText(vjson, Regex.Replace(File.ReadAllText(vjson), "\"Iterations\":\\s*\\d+", "\"Iterations\": 2000000000"));
        var sw = Stopwatch.StartNew();
        Check(!Vault.Load(vlib).TryUnlock("correct horse battery staple") && sw.Elapsed < TimeSpan.FromSeconds(5),
            "vault: a vault.json asking for billions of rounds is refused at once");

        // ---- Remove all CaseKeeper data: CaseKeeper's own files only, other files stay, network libraries are skipped
        var target = NewDir("h41-remove");
        var rlib = new LibraryService(target);
        rlib.Import(Put(src, "remove-me.txt", "Delete this."), new CaseDocument { CategoryId = rlib.Db.GetCategories().First().Id });
        rlib.TakeSnapshot("test");
        File.WriteAllText(Path.Combine(target, "my own notes.txt"), "not CaseKeeper's");
        Directory.CreateDirectory(Path.Combine(target, "_before_restore_20260101_000000"));
        SqliteConnection.ClearAllPools();
        var plan = new DataRemoval.Plan();
        plan.Libraries.Add(target);
        var problems = DataRemoval.Execute(plan);
        Database.Retired = false;      // the test carries on using other databases
        AppLog.Disabled = false;
        Check(problems.Count == 0, "remove: no problems deleting a library");
        Check(!File.Exists(Path.Combine(target, "casekeeper.db")) && !Directory.Exists(Path.Combine(target, "Files")) &&
              !Directory.Exists(Path.Combine(target, "Snapshots")) && !Directory.Exists(Path.Combine(target, "_before_restore_20260101_000000")),
            "remove: the database, documents, snapshots and restore leftovers are gone");
        Check(File.ReadAllText(Path.Combine(target, "my own notes.txt")) == "not CaseKeeper's", "remove: other files in the library folder are left alone");
        File.Delete(Path.Combine(target, "my own notes.txt"));
        var emptyLib = NewDir("h41-remove-empty");
        new LibraryService(emptyLib);
        SqliteConnection.ClearAllPools();
        var plan2 = new DataRemoval.Plan();
        plan2.Libraries.Add(emptyLib);
        DataRemoval.Execute(plan2);
        Database.Retired = false;
        AppLog.Disabled = false;
        Check(!Directory.Exists(emptyLib), "remove: a library folder left empty is removed too");

        Database.Retired = true;
        bool refused;
        try { lib.Db.GetDocuments(); refused = false; } catch (InvalidOperationException) { refused = true; }
        Database.Retired = false;
        Check(refused && lib.Db.GetDocuments().Count == 1, "remove: once data is removed, nothing can quietly recreate the database");

        // ---- the website copy's paths are unchanged; AppData is recognised for the Store copy's checks
        Check(!AppPackage.IsPackaged && LibraryService.DefaultRoot == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CaseKeeper"),
            "paths: the website copy's default library is still %LOCALAPPDATA%\\CaseKeeper");
        Check(AppPaths.IsInsideAppData(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X")) &&
              AppPaths.IsInsideAppData(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "X")) &&
              !AppPaths.IsInsideAppData(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CaseKeeper")),
            "paths: AppData is recognised, the user folder isn't");
        Check(AppPaths.PackagedLocationProblem(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X")) == null,
            "paths: the website copy may still keep a library in AppData");

        // ---- copying the website library for the Store copy: exact, verified, never overwriting, logs left behind
        var webLib = NewLib("h41-web");
        webLib.Import(Put(src, "web.txt", "From the website version."), new CaseDocument { CategoryId = webLib.Db.GetCategories().First().Id });
        Directory.CreateDirectory(Path.Combine(webLib.RootFolder, "Logs"));
        File.WriteAllText(Path.Combine(webLib.RootFolder, "Logs", "casekeeper-20260101.log"), "log");
        SqliteConnection.ClearAllPools();
        var storeLib = Path.Combine(Root, "h41-store");
        int n = LibraryService.CopyLibrary(webLib.RootFolder, storeLib, null);
        Check(n > 0 && !Directory.Exists(Path.Combine(storeLib, "Logs")) && new LibraryService(storeLib).Db.GetDocuments().Single().Title == "web",
            "copy: the website library copies across complete, without its logs");
        SqliteConnection.ClearAllPools();
        bool overwrote;
        try { LibraryService.CopyLibrary(webLib.RootFolder, storeLib, null); overwrote = true; } catch (IOException) { overwrote = false; }
        Check(!overwrote && File.Exists(Path.Combine(storeLib, "casekeeper.db")), "copy: never overwrites a library already there (and keeps it)");
    }
}
