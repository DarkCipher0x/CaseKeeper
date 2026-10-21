# Bulk Case Import – architecture

How Bulk Case Import and Import review are built: the data flow, the database tables, the AI contract and the
safety rules. For how to use it, see section 4 of the [user guide](../USER-GUIDE.md).

## Principles

- **The AI is an assistant, not an authority.** Nothing it says reaches a case record (hearings, timeline, orders,
  directions, tasks, contact, parties, children, complaints) or a document's category until the person approves it.
  The one exception is an *advanced, off-by-default* rule that may file a very confident category for a document that
  is still Uncategorised; it never touches any other record, is audited, and can be undone.
- **Reuse, don't duplicate.** Files go through `LibraryService.Import` (encryption, SHA-256 fingerprint, audit),
  text through `LibraryService.ProcessTextAsync` (Windows OCR, Word and text extraction), approvals through the same
  `Database` calls the screens use (`Save<T>`, `SaveEvent`, `SaveParty`, `SaveComplaint`, `UpdateDocument`), hearing
  timeline entries through `HearingSync`, deadlines through `Deadlines.Calculate`, and edits through the same forms
  (`CaseModules.HearingFields()` etc., `EventEditWindow`, `PartyEditWindow`, `ComplaintEditWindow`, `DocumentEditWindow`).
- **Everything checkable is checked.** Every AI finding must quote the words it relies on and the page they are on;
  the quote is looked for on that page, and every date must actually be written in the document.
- **Resumable at every step.** State is saved after each file, so a job survives pause, cancel, crash or restart.

## Data flow

```
 folder ──► FolderScanner.Scan ──► FolderScanner.FindDuplicates ──► BulkImportService.PreviewRows
              (no changes)            (SHA-256 vs library + within folder)   (what will happen, folder mapping)
                                                                                     │ person reviews, ticks, maps
                                                                                     ▼
            BulkImportService.CreateJob ──► ImportJobs + ImportFiles rows (audited: bulkimport.start)
                                                                                     │
  RunAsync ─► 1 Import:  per file: re-hash, duplicate check, LibraryService.Import (category = Uncategorised or the
                         person's folder mapping; SourcePath = relative path; SourceModifiedAt = information only)
           ─► 2 Text:    LibraryService.ProcessTextAsync, bounded by the performance profile; no text → review by hand
           ─► 3 Analyse: DocumentAnalyzer per document (bounded, one at a time by default)
                           CategoryMatcher.Candidates → short list (K1…Kn) → Ollama structured output
                           → AnalysisSchema.Parse (validation) → provenance and date checks → AiFinding rows
           ─► 4 Check:   FindingChecks.Run (deterministic cross-document rules) → notes + conflict findings
                         optional auto-apply of very confident categories (off by default)
           ─► Review:    ImportReviewView → FindingApprover.Approve / Reject / Defer / Undo / Reconsider (all audited)
```

`BulkImportService` is the only orchestrator; `MainWindow.Bulk` holds the app's single instance, so only one job runs
at a time. Progress events drive the wizard and a banner at the bottom of the main window.

## Database

Three working tables, created and extended by the same mechanism as the record tables (`[Entity]` classes, columns
added automatically on upgrade; nothing is ever removed). They live in the library database, so SQLCipher encryption,
snapshots and full backups cover them. They are **not** in `Database.RecordTypes`, so merging another library's backup
doesn't bring its working suggestions. They are written with `SaveQuiet` (no audit entry per progress update); the
meaningful events are audited explicitly. `Database.CheckStaging` reports and tidies rows pointing at documents that
no longer exist, and runs as part of the document integrity check; purging a document removes its suggestions.

| Table | Purpose | Key columns |
|---|---|---|
| `ImportJobs` | One bulk import | `RootPath` (resume only, never exported), `RootName`, `OptionsJson`, `Phase`, `State`, `DuplicateMode`, `Model`, `PromptVersion`, `SchemaVersion`, `AnalysisVersion`, `LastErrorCode` |
| `ImportFiles` | Every file the scan found | `JobId`, `RelativePath`, `Extension`, `SizeBytes`, `SourceModifiedAt`, `SourceCreatedAt`, `ContentHash`, `Included`, `Status`, `DuplicateOfDocumentId`, `DocumentId`, `MappedCategoryId`, `MappedTags`, `ErrorCode`, `ErrorMessage` (plain, non-sensitive), `TextStatus`, `AnalysisStatus`, `AnalysisFingerprint`, `AnalysisAttempts`, `AnalysedAt`, `ManuallyReviewed` |
| `AiFindings` | One suggestion | `JobId`, `DocumentId`, `Kind`, `Title`, `Detail`, `DateValue`, `DateText`, `DatePrecision`, `DateRole`, `TimeText`, `PageFrom`, `PageTo`, `Excerpt` (≤240 chars; cleared on rejection), `Confidence`, `Reason`, `AssertedBy`, `PayloadJson`, `Fingerprint`, `ConflictGroup`, `Warnings` (from validation), `CheckNotes` (from cross-checks, recalculated), `MatchedRecordType/Id`, `Status`, `CreatedRecordType/Id`, `PreviousValue` (for undo), `Model`, `PromptVersion`, `SchemaVersion`, `AnalysisVersion`, `CreatedAt`, `DecidedAt` |

Two columns are added to `Documents`: `SourcePath` (the path *inside* the chosen folder, or the file name if the
person chose not to keep paths – never the full Windows path) and `SourceModifiedAt` (information only, never used as
the document date). Both are carried by backup merges.

Statuses: files `Pending → Imported | ImportedDuplicate | DuplicateSkipped | Excluded | Missing | Inaccessible |
Unsupported | SystemFile | Failed`; text `NotStarted → Waiting → Ready | NoText | Failed | NotNeeded`; analysis
`NotStarted → Queued → Running → Done | Failed | NoText | AwaitingAi | Skipped`; findings `Proposed ⇄ Deferred →
Approved | AutoApplied | Rejected`, `Approved → (undo) Proposed`, any open finding `→ Superseded` when the document is
analysed again.

## The AI contract

Requests go through `AiService.ChatJsonAsync` to the configured Ollama endpoint only (the existing endpoint safety
rules apply: local by default, remote only if allowed and over https). `format` is a JSON Schema built by
`AnalysisSchema.Build`; servers too old for schemas get JSON mode, and CaseKeeper validates the answer itself either
way. Temperature is 0. The prompt is the editable `prompt.bulk.analyse`.

```
{ documentSummary: { documentType, documentTitle, documentDate, documentDateText, caseNumber, courtName,
                     authorOrIssuer, recipient, summary, confidence },
  categorySuggestion: { categoryId: "K1".."Kn" | "UNKNOWN", confidence, reason, alternativeCategoryIds },
  findings: [ { recordType: hearing | timelineEvent | chronologyEntry | order | direction | task | deadline |
                            contact | party | child | complaint | safeguarding | missingDocument | issueForReview,
                description, assertedBy, page, pageTo, excerpt, confidence, reason,
                date (YYYY-MM-DD only when day, month and year are written), dateText (as written),
                datePrecision: exact | month | year | approximate | unknown,
                dateRole: documentIssued | filed | served | received | applicationMade | orderMade | hearingListed |
                          hearingAdjournedFrom | hearingAdjournedTo | hearingVacated | hearingHeld | deadline | review |
                          contact | appointment | report | incidentAlleged | eventStated | complaint | correspondence |
                          birth | conditional | recurring | mentioned,
                time, details: { court, venue, attendanceMode, hearingType, judge, caseNumber, hearingStatus,
                                 timeEstimate, previousDate, responsibleParty, actionRequired, dueDate, dueDateText,
                                 dueDateExplicit, sourceDate, calculationRule, wording, name, role, representative,
                                 dateOfBirth, dateOfBirthText, orderType, madeBy, contactOutcome, recipient,
                                 reference, organisation } } ] }
```

Temporal position (past/future), conflicts, review status and fingerprints are worked out by CaseKeeper, not the model.

### Category candidates

A small model can't weigh 470 categories every time, so `CategoryMatcher.Candidates` builds a short list (10) from
form codes in the file name or text (`Form C100`), the file-name rules used by ordinary imports, recognisable headings,
and words shared with category names and descriptions. Each candidate gets a short key (K1…); the schema's `enum` only
allows those keys or `UNKNOWN`, so an invented id is impossible when the server honours the schema and is rejected by
the validator when it doesn't (retried once, then treated as Unknown with a warning). Below the *Medium* threshold a
category becomes "Uncategorised (needs review)" with the guess kept as `bestGuessId`. Without AI, CaseKeeper's own
rules still propose a category (capped below *High*).

### Validation and provenance (DocumentAnalyzer)

- Not JSON, wrong shape, or an invented category → retried once with a reminder, then the document is marked
  `Failed` with a safe code (`AI_INVALID_JSON`, `AI_SCHEMA`, `AI_BAD_CATEGORY`). Malformed individual findings are
  dropped and counted.
- **Excerpt** must appear on the cited page (normalised for case, quotes, dashes, ordinals and spacing), or somewhere in
  the part; otherwise the finding is flagged and confidence capped at 0.35.
- **Dates**: the date *as written* (parsed day-first by `UkDates`) beats the model's reading; the date must appear in the
  text in a common UK form, or it is removed. Month/year-only dates keep that precision and can't become an exact
  timeline date without the person choosing one. All-number dates that could swap are flagged.
- **Names** (party, child, judge, court, recipient…) must appear in the text; a person the document doesn't name is
  dropped.
- **Deadlines** are never calculated by the model: wording such as "within 14 days" sets `needsCalculation`, and the
  date is worked out in `DeadlineFromOrderWindow` with `Deadlines.Calculate` from a start date and rule the person
  confirms; the explanation (`Deadlines.Explain`) is saved with the task and in the audit entry.
- **Allegations** are phrased as "X states: …" / "The document alleges: …".
- Long documents are split into parts of whole pages (`[Page N]` markers) within the context budget
  (`AiService.BulkAnalysisSettings`); findings with the same fingerprint (kind + date + key words) are merged and their
  page range widened. Very long documents are read up to `bulk.maxParts` parts and a note says so.

### Versions and re-analysis

`DocumentAnalyzer.InputFingerprint` hashes the document's content hash, its page text, the prompt version
(a hash of the prompt), `AnalysisSchema.Version`, `AnalysisSchema.AnalysisVersion`, the model name and the part size.
A document is analysed again only if that fingerprint changes or the person asks (`QueueReanalysis`). New results
replace undecided suggestions (marked `Superseded`, not deleted); a suggestion matching one already approved or
rejected isn't added again.

### Failure handling

| Situation | Behaviour |
|---|---|
| AI switched off / not requested | Import completes; documents `AwaitingAi`; rule-based categories offered; one job-level code (`AI_OFF`) |
| Ollama not reachable at the start | Same, code `AI_UNAVAILABLE`; no per-document errors |
| Lost mid-run | Bounded retries (2 s, 5 s, 12 s), then the rest wait as `AwaitingAi` |
| Timeout | One retry, then `AI_TIMEOUT` for that document; the job carries on |
| Unexpected error | `AI_ERROR` with a log reference; only the exception *type* is logged |
| Pause / cancel | Cancellation token; the current file finishes or is put back in the queue; nothing is left `Running` |
| Source file gone after the scan | `Missing`, the rest continue |
| Database refuses a document | The stored copy is deleted (no orphan), the file is `Failed`, the rest continue |
| File changes while being imported | The fingerprint is taken from the bytes stored; a mismatch with the scan fails that file |

## Cross-document checks (FindingChecks)

Plain rules, run after analysis (and again on demand): probable duplicates (same kind, same day, similar words),
conflicting hearing dates for the same type of hearing within 120 days, hearings later adjourned/vacated/moved,
different case numbers (minority documents flagged as possibly from another case), names spelt differently (edit
distance ≤ 2, including existing parties), suggestions already recorded in the case, directions a later document may
have replaced, hearings/deadlines within 14 days, and gaps of six months or more in the chronology. Conflicts become
`conflict` findings with both sources side by side; nothing is changed automatically. Undecided conflicts are rebuilt
each run; decided ones are remembered by fingerprint.

## Security and privacy

- Documents, findings and job records are encrypted with the library (SQLCipher / AES-256 files) when it is protected.
- No temporary decrypted files are written by the import or the analysis (everything works from bytes).
- `AppLog` entries from this feature contain ids, stages, counts and safe codes only – no document text, titles, file
  names or paths. The audit trail records the folder's *name*, never its full path.
- Rejected suggestions keep no excerpt or detail text.
- The scan summary (`ScanSummary`) contains counts and paths inside the chosen folder only.
- Settings: `bulk.options` (remembered wizard choices), `bulk.high`, `bulk.medium`, `bulk.preset` (`fast`/`careful`),
  `bulk.model` (empty = recommended), `bulk.depth` (`auto`/`full`/`categories`), `bulk.aiWorkers` (empty = this PC's
  tier, 1–4), `bulk.maxParts`, `bulk.autoApply` (off), `bulk.autoApplyThreshold` (0.95, never below 0.85), and the
  prompt `prompt.bulk.analyse`.
- Downloading a model (`/api/pull`) happens only when the person presses the button, and is audited. Hardware
  detection reads the display adapter's memory size from the registry and the power state; nothing is sent anywhere.

## Performance and hardware tiers

The scan is a single pass without recursion on the call stack; hashing streams each file. Import is one file at a
time; text reading uses `Performance.OcrWorkers` in parallel at background priority. Only one document's text is in
memory per AI worker. The preview list is virtualised. Remaining time is shown only after three items have been timed.

`AiHardware` (Services/Ai/AiHardware.cs) sorts the PC into a tier from graphics memory, system memory and power:

| Tier | When | Documents at once | Reading | Careful model |
|---|---|---|---|---|
| Minimal | no usable graphics card and < 8 GB memory | 1 | categories only; AI unticked by default | – |
| Basic | basic PC, or any PC on battery / power saver | 1 | categories only | – |
| Mid | graphics card ≈ 6–10 GB | 2 | full | ≤ 8B model if it fits |
| High | graphics card ≥ 12 GB | 3 | full | qwen3:14b class |
| VeryHigh | graphics card ≥ 24 GB | 4 | full | up to 32B class |

A model "fits" when its size is ≤ 85 % of graphics memory (`gpu`), or ≤ 45 % of system memory (`cpu`, slow);
otherwise it is "too large for this PC". The Fast preset prefers gemma3:4b; Careful prefers qwen3:14b and similar.
`OllamaJsonClient` picks: the person's own model, else the preset's model, else the general AI model. Thinking is
switched off for qwen3/deepseek-r1/magistral/qwq and set to "low" for gpt-oss (`AiHardware.ThinkSetting`). Output is
capped at 2,048 tokens (1,024 for quick reading) and 12 findings per part. Several at once only helps when Ollama
allows parallel requests (`OLLAMA_NUM_PARALLEL`).

**Step-down** (BulkImportService.AnalysisPhaseAsync): `AI_OVERLOAD` (Ollama reports running out of memory), two
timeouts on one part (`AI_TIMEOUT`), or less than 90 % of the model on the graphics card after the first document
(`/api/ps` `size_vram`) moves one level: (1) one document at a time, (2) half-size parts, (3) a lighter installed
model (`AiHardware.LighterThan`), (4) quick reading. Levels that don't apply are skipped. Simultaneous failures count
once; each document is attempted at most three times. The job gets `LastErrorCode = AI_STEPPED_DOWN`, the model it
ended with, an `ai.settings` audit entry and a progress message.

**Less work for the AI.** `DocumentAnalyzer.IsLightDocument`: an image file (jpg, png, heic, tif…) with under 600
characters of text gets the quick reading. `RulesAreSure`: the best candidate scores ≥ 0.9 (a form code in the file
name or `Form X` in the text) and nothing else is within 0.2 – which leaves out forms filed in applicant/respondent
pairs, so the AI still decides those. In a quick reading with sure rules the AI isn't called; the result is the
rule-based category (confidence ≤ 0.8, never auto-applied). Basic and Minimal tiers cap the bulk context at 4,096
tokens for part sizes.

## Known limitations

- Supported types are those of the normal import: PDF, Word .docx, images and text. Others (.doc, .xlsx, .msg, .eml,
  .zip…) are listed as unsupported.
- PDFs are read with Windows OCR (as elsewhere in CaseKeeper), not from any embedded text layer.
- Cross-document checks are rule-based; there is no AI reasoning pass across documents.
- The scanner lists up to 50,000 files per job and 64 folder levels.
