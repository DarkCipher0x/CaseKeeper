# CaseKeeper tests

Four self-contained test programs. They are never part of the installer.

| Project | What it checks |
|---|---|
| `CaseKeeper.Tests.Docs` | The formatting language, Word (.docx) output against Microsoft's OpenXML validator for every letterhead style, filling imported Word templates (including fields Word splits across runs), removal of author/comment metadata, PDF and HTML output (escaping, no scripts), malformed-file rejection, and England & Wales bank holidays / FPR deadline arithmetic. |
| `CaseKeeper.Tests.BulkImport` | Bulk Case Import end to end, with synthetic files only and a scripted stand-in for Ollama: folder discovery (empty, nested, deep, hidden, unsupported, junction loops, 300+ character paths, unreadable files and folders, cancelling), importing (one, many, mixed types, duplicates by content, same name/different content, encrypted library, network-path library, cancel and resume, a file removed after the scan, a refused database write with no orphan left, originals untouched), text reading (text, Word, searchable and scanned PDF, image OCR, empty and corrupted files), the AI (unavailable, lost mid-run, timeouts, bad JSON, schema failures, invented categories, high/low/unknown, long documents in parts, quotes not on the page, pause and resume, prompt/model/schema changes), dates and records (listed/adjourned/vacated hearings, document vs event dates, approximate and ambiguous UK dates, explicit and calculated deadlines, invented dates and names removed, allegations kept as allegations, nothing committed without approval), cross-document checks, review decisions and undo, the audit trail, backup and restore, merging, upgrading a v0.3.0 library, and privacy (no case content or paths in logs, encrypted staging data, no temporary files). OCR checks are skipped, and say so, on a PC without a Windows OCR language. |
| `CaseKeeper.Tests.Updates` | The signed in-app updater, with throwaway test keys and a scripted stand-in for the website and GitHub (no internet): the pinned release keys, both signatures (ECDSA P-256 r\|\|s and ML-DSA-65; changed bytes, other keys, garbage), strict parsing (product, x.y.z only, no downgrade or replay, file name tied to the version, same https host, no query or user name, size and checksum, duplicate keys, deep nesting, capped notes), update addresses, every source (website, own address, public GitHub with redirects only within GitHub and at most five hops, private GitHub through the API with the token sent only to api.github.com), what a request carries (no cookies, credentials or query, a generic User-Agent), the download checks (hash, size, not a program, other product, publisher or version), the check just before the setup program runs, the saved settings (token DPAPI-encrypted, damaged file falls back to safe defaults), the opt-in schedule, the check after an update, and the real setup program's version information when `dist\` has one. With `--verify <update.json>` it checks a signed release the way installed copies will (build-release.ps1 runs this). |
| `CaseKeeper.Tests.Data` | Upgrading a v0.2 library without losing anything, the category catalogue (no duplicates), records, the hash-chained audit trail and tamper detection, Deleted items, encryption of the database and its snapshots, and merging backups (records and settings, safe to repeat). |

Run them from the repository root:

```powershell
dotnet run --project tests\CaseKeeper.Tests.Docs -- "$env:TEMP\ck-test-output"
dotnet run --project tests\CaseKeeper.Tests.Data
dotnet run --project tests\CaseKeeper.Tests.BulkImport
dotnet run --project tests\CaseKeeper.Tests.Updates
```

The bulk-import tests create and remove their own files under `%TEMP%\ckt\bulk-run`, and the scanner tests briefly
deny the current user access to a test file and folder there (removed again afterwards).

Each prints `PASS` / `FAIL` lines and exits with the number of failures (0 = everything passed). The release
workflow runs all four before building the installer.
