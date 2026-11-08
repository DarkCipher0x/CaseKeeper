# Changelog

All notable changes to CaseKeeper are recorded here. The newest release is at the top.

Version numbers follow `MAJOR.MINOR.PATCH`:

- **PATCH** (0.1.0 → 0.1.1) — fixes only, nothing new to learn.
- **MINOR** (0.1.0 → 0.2.0) — new features, existing data untouched.
- **MAJOR** (0.x → 1.0.0) — the app is considered finished and stable.

Installing a newer version over an older one upgrades it in place. **Your documents, notes,
timeline, complaints and settings are never touched by an upgrade** — they live outside the program
folder, in `%LOCALAPPDATA%\CaseKeeper` (website installer) or `%USERPROFILE%\CaseKeeper` (Microsoft Store).

---

## v0.5.0 — 8 November 2026

CaseKeeper can now update itself. Upgrade over any earlier version: nothing you already have is changed.

### Added
- **Signed in-app updates** (website version): *Settings → Updates → Check for updates* shows whether a newer
  version is available and what's new, then **Download and verify** and **Install… (backup first)**. Installing
  offers a backup, takes a safety snapshot, closes CaseKeeper and runs the normal setup wizard; when CaseKeeper
  opens again it says whether the update worked. Your library, settings and password protection are not touched.
- **Two signatures on every update.** An update is offered only when its information is signed by both CaseKeeper
  release keys built into the app: ECDSA P-256 and the post-quantum ML-DSA-65. The website and GitHub only carry the
  files, so nothing changed there can get through. Older versions are never offered as new, and the setup program
  must match the signed size and SHA-256 exactly and say it is CaseKeeper by DarkCipher Systems at that version.
  It is checked again just before it runs.
- **Where to look:** the DarkCipher Systems website (the default), GitHub releases (with an optional read-only access
  token while the repository is private, encrypted with your Windows account), or an https address of your own.
- **Optional automatic checks,** off until you switch them on: once a week or once a day, shortly after CaseKeeper
  starts, with a notice at the bottom right. It can also download and verify the update in the background.
  Installing always asks you first.
- A check is a plain download with a generic name: no cookies, no identifiers and nothing about you, this PC or your
  case. The Microsoft Store version is updated by the Store as before, and its Updates section says so.
- New Guide & FAQ article *Updating CaseKeeper*, a help-assistant answer, and user guide section 25.

### Fixes
- GitHub release notes showed garbled characters (such as "â†’" for "→"), because the release script was read in
  the wrong text encoding. The release scripts are now saved so Windows PowerShell reads them correctly.
- "Remove all CaseKeeper data" also removes a downloaded update that hasn't been installed.

### Components
- Added BouncyCastle.Cryptography 2.7.0 (MIT) to check the post-quantum signature. No known vulnerabilities.

### For developers
- `tools\sign-update.ps1` creates, escrows, restores and uses the two release keys; `build-release.ps1` writes and
  signs `update.json`, then checks it with the app's own code before anything is published. GitHub releases now
  carry `update.json`, `update.json.sig` and `update.json.pq.sig`, and `SHA256SUMS.txt` covers them. A fourth test
  program, `tests\CaseKeeper.Tests.Updates`, runs in the release build and CI. See RELEASING.md → Signed updates.

---

## v0.4.2 — 30 October 2026

Runtime and component updates, with everything from 0.4.1. Version 0.4.1 never reached the website or the Microsoft
Store, so if you're coming from 0.4.0, read the 0.4.1 notes below too. Upgrade over any earlier version: nothing you
already have is changed.

### Security and updates
- **CaseKeeper now runs on .NET 10**, the current long-term-support version of the runtime, which Microsoft supports
  until November 2028. Support for .NET 8 ends on 10 November 2026, after which it gets no more security fixes. The
  runtime is built in, as before, so there is nothing extra to install.
- Updated components: the Word export library (DocumentFormat.OpenXml 3.5.1), the database library
  (Microsoft.Data.Sqlite 10.0.14) and the PDF library (PDFsharp 6.2.4). The packaging and Windows data-protection
  components now come with the .NET runtime itself, so they're patched with it. No known vulnerabilities in any
  component.

### Fixes
- Streaming an AI answer no longer ties up a background thread while it waits for the next part of the reply.
- Dragging documents onto a category, party, *Starred* or *Deleted* in the Documents tree works with the new runtime.
- Guide & FAQ articles fit the reading pane instead of running off its right-hand edge in a smaller window.

### Source code
- The source zip that comes with each release is now an exact `git archive` of the published source, with one top
  folder and forward slashes, so it unpacks correctly on any system. Internal project notes are kept out of it.

---

## v0.4.1 — 30 October 2026

Microsoft Store readiness, a way to remove all your data, and security hardening. Upgrade over any earlier
version: nothing you already have is changed.

### Microsoft Store
- CaseKeeper can be built as a Microsoft Store package (`build-store.ps1`; see `docs/STORE.md`). Restarting after a
  restore, a factory reset or an encryption change works in the Store copy too.
- The Store copy needs no special permissions beyond being a normal desktop program. It keeps its library in
  `%USERPROFILE%\CaseKeeper`, an ordinary folder you can see and back up, which uninstalling never deletes. Its
  settings and logs live in its own app folder, which Windows removes with the app.
- The first time the Store copy opens on a PC that already has a library from the website version, it offers to copy
  that library across. Every file is checked after copying, and the original is left exactly as it was.
- The Store copy won't put a library inside the hidden AppData folder, where Windows would delete it on uninstall.

### Removing your data
- **Settings → Factory reset → Remove all CaseKeeper data…** deletes every case library on this PC that CaseKeeper
  knows about, with its settings, logs and temporary copies, then closes CaseKeeper. It lists exactly what will go,
  offers a backup first, and needs DELETE typed. Only CaseKeeper's own files are removed from each folder, and
  libraries on network drives are left alone.
- **The website uninstaller now asks** whether to delete your library too. *No* is the default and keeps everything,
  as before; *Yes* shows the same confirmation. A silent uninstall never deletes anything.
- The user guide has a new section, *Where your data is kept, and removing it*.

### Security
- Encryption keys are wiped from memory as soon as they're no longer needed, not just dropped.
- A tampered `vault.json` asking for an absurd amount of work can no longer freeze the password prompt.
- Restoring or adding a backup can't write, read or delete anything outside the library, even if the backup has been
  tampered with.
- CaseKeeper won't open a document that Windows would run as a program (`.exe`, `.js`, `.lnk` and similar). It shows
  it in File Explorer instead.
- Importing a settings file no longer changes screen-lock and security settings, the confidentiality reminder, or
  where the AI sends case material (unless that's on this PC or your home network). The import tells you what it
  left alone.
- The build now locks every NuGet package to an exact, checksummed version (`packages.lock.json`).

---

## v0.4.0 — 19 October 2026

Bulk Case Import and Import review. Upgrade over any earlier version: nothing you already have is changed.

### Bulk Case Import – bring in a whole case folder
- **Import a folder and all its subfolders** in one go: *Import folder* on the Documents screen, *Import a whole
  folder* on the Dashboard, **Ctrl+Shift+I**, or the command palette.
- **See everything before anything is imported**: how many files were found, which will be imported, duplicates,
  unsupported types and anything that couldn't be read, with the total size. Untick single files or whole folders,
  filter by status or type, search the list, and copy a summary (counts and paths inside the folder only).
- **Sensible, safe defaults**: hidden files and folders, temporary and system files (Thumbs.db, Office lock files) are
  skipped; shortcuts and linked folders aren't followed unless you ask, and folder loops are always detected; very
  long Windows paths work; folders and files you can't read are reported and the rest carry on.
- **Duplicates by contents, never by name** (SHA-256): skip them and note which document they match, import them
  anyway, or decide one by one. Two different files with the same name are both imported.
- **Your original files are never moved, renamed or changed**, and nothing is written into the folder you import
  from. Files go through the normal import, so they're encrypted when your library is protected, fingerprinted and
  recorded in the audit trail.
- **The original folder structure is kept**: each document remembers where it was inside the folder (shown in its
  details, and under a new *By original folder* branch in the Library tree). Optionally turn folder names into tags or
  choose a category for each top-level folder – you see the mapping before anything happens. A file's own date is
  kept for information only and is never used as the document's date.
- **Pause, stop and carry on later** – even after closing CaseKeeper. Progress is saved after every file, and the
  Dashboard offers to carry on. Progress shows at the bottom of the main window while you keep working, with a time
  estimate only once it can be measured honestly.

### Local AI suggestions, checked before you see them
- After reading the text (including scans and photos), your **local AI** (Ollama – nothing leaves your computer or
  network) can suggest a **category** and **records** for each document: hearings, orders, directions and deadlines,
  tasks, timeline and chronology entries, contact, parties and children, complaints, safeguarding concerns and
  documents referred to but missing.
- CaseKeeper first narrows its ~470 categories to a short list using form codes, titles and wording, and the AI can
  only choose from that list or *Unknown*. Documents it can't match confidently go to **Unknown / needs a category**.
- **Structured, validated answers**: the AI must answer in a strict JSON format (Ollama structured outputs), which
  CaseKeeper checks again itself. Bad answers are retried once, then reported – never guessed at.
- **Every suggestion is traceable**: it quotes the words it relies on and the page they're on. If the quote isn't on
  that page, or a date isn't actually written in the document, it is flagged and its confidence lowered; dates and
  names the document doesn't contain are removed. Every suggestion carries a confidence score and a reason.
- **Dates keep their meaning**: the date of a document, a hearing *listed for* a date (which doesn't mean it took
  place), adjourned and vacated hearings, alleged incidents, deadlines. UK day-first dates are read correctly and
  ambiguous ones flagged. Allegations are recorded as what someone states, never as fact.
- **Deadlines are never calculated by the AI.** "Within 14 days of this order" is worked out with the existing
  deadline calculator, from a date and rule you check – and how it was worked out is saved with the task.
- Long documents are read in parts and combined, keeping page references; repeated findings are merged.
- **Cross-checks across documents** (plain rules, not AI): the same event in several documents, a hearing given
  different dates, hearings later adjourned or vacated, different case numbers (and documents that may belong to
  another case), names spelt differently, things already in your case, directions a later order may have replaced,
  hearings and deadlines coming up within two weeks, and long gaps in the chronology – shown side by side for you to
  decide.
- If the AI is off or not running, the import still completes, CaseKeeper's own rules still suggest categories from
  form codes, and the rest can be analysed later with one click. Nothing is re-analysed unless the document, prompt,
  model or format changes – or you ask.
- Already-imported documents can be sent through the same process: right-click → *Analyse with AI* → *Organise*.
- The instructions the AI follows are editable in *Settings → AI → Prompts → Bulk Case Import*.

### Import review – you approve everything
- A new **Import review** screen (sidebar, under Papers) lists every suggestion by type, with filters for confidence,
  document, dates and warnings, and a summary of each import.
- Each suggestion shows its source document, page and quoted words (**Open at page**), what the date means, who says
  so, the confidence in plain English and why it was suggested, any warnings, and other documents to compare with.
- **Approve**, **Edit & approve** (the same form as adding it by hand), **Reject**, **Defer**, **Reconsider**, and
  **Undo** for approvals. Approve several categories at once. You're warned before adding something that looks like a
  record you already have.
- Approvals go through the normal rules (a hearing also gets its timeline entry, and so on) and are recorded in the
  audit trail as **AI-suggested and approved by you**, with the model and prompt version. Rejected suggestions keep no
  quoted text.
- **Optional, advanced, off by default**: automatically file documents under the AI's category when it is very
  confident – categories only, only for documents still in Uncategorised, audited, listed separately and reversible.
- Confidence levels and the longest document read in full are adjustable in *Review options*.

### Faster AI, matched to your PC
- **A fast default model built in**: bulk analysis uses **gemma3:4b** unless you choose otherwise – quick and reliable
  at structured answers. If it isn't installed, *Review options* offers to download it (about 3 GB, only when you ask).
- **Fast (recommended)** and **Careful** presets. Careful uses a larger model (such as qwen3:14b) only when this PC's
  graphics card can hold it; otherwise it says so and uses the Fast model.
- **Recommended settings for this PC**, from the graphics card, memory and power source:
  no graphics card and under 8 GB memory → AI off by default (CaseKeeper's own rules still suggest categories);
  a basic PC, or any laptop on battery → one document at a time, categories and document details only;
  a 6–10 GB graphics card → two at once, full reading; 12 GB or more → three at once, Careful available;
  24 GB or more → four at once, larger Careful models allowed. *Review options* shows "This PC: …" with a
  **Use recommended settings** button, and models too large for this PC are marked as such.
- **Automatic step-down**: if the AI runs out of memory, keeps timing out, or the model doesn't fit on the graphics
  card, CaseKeeper eases off one step at a time – fewer documents at once, smaller parts, a lighter installed model,
  then a quick reading – tells you what it changed, records it in the audit trail, and tries the document again.
- Models that "think aloud" (qwen3, deepseek-r1, gpt-oss) have thinking switched off or turned down for bulk analysis,
  and answers are kept short (at most 12 findings per part), which makes each document much quicker.
- **Less work for the AI**: photos with little writing (receipts, screenshots) get a quick reading – category and
  description only. In a quick reading, a document whose form code settles the category for certain (such as an S7
  report) isn't sent to the AI at all; the suggestion still waits for your approval. Basic PCs read in smaller parts.
- When you first connect to Ollama in *Settings → AI*, gemma3:4b is chosen as your AI model if it's installed and you
  haven't chosen one. A model you already chose is kept. *Settings → AI* and *Performance* show what's recommended for this PC.
- Your own choices always win: pick the model, the speed, how much is read and how many documents at once yourself –
  with a warning if this PC can't sensibly run them.

### Also
- Import jobs, suggestions and decisions are stored in the library database – encrypted with it, included in
  snapshots and backups, and checked by the integrity check. Deleting a document for good removes its suggestions.
- A file that changes while it's being imported can no longer end up with a fingerprint that doesn't match the
  library's copy, and a failed import never leaves an orphaned copy behind (this applies to every import).
- The help assistant and the Guide (F1) know how to use Bulk Case Import and Import review.
- Logs from the new feature record only ids, stages and safe codes – never document text, file names or folder paths.
- **Fixed:** on some PCs (seen with AMD Radeon graphics) CaseKeeper could crash as it closed, after showing or scanning
  a PDF. PDF pages are now drawn in software on a graphics device CaseKeeper releases itself, so closing is clean on
  every graphics card and pages look the same on every PC.

---

## v0.3.0 — 17 October 2026

The biggest update yet. Everything you already have is kept exactly as it was – upgrade over any earlier version.

### A new look, and two ways to use it
- **New icon and theme** in the DarkCipher Systems style: dark teal by default, with a matching light theme.
- **Simple and Professional modes.** Simple shows the essentials in plain English; Professional adds costs, the Scott
  schedule, AI analysis, exports and the audit trail. Switch from the bottom of the sidebar at any time.
- **Ctrl+K command palette** – type to jump to any screen, action, document, category, party or guide article.
- **First-run welcome** and a **getting-started checklist** on the dashboard. **F1** opens help for the current screen.
- The sidebar is grouped into Overview, Papers, Track, Produce and Help & system.

### Documents: a proper explorer
- **Right-click menus everywhere**, with sub-menus: move to category, set party, tags, exhibit numbers, add to bundle,
  timeline or complaint, analyse with AI, copy references or fingerprints, integrity check, history.
- **Drag and drop** documents onto categories, parties, Starred or Deleted items; drop files from Windows onto a
  category to import them there.
- **Rename (F2)**, choose columns (Exhibit, Tags, From, Type, Size, Added), smart folders (Recently added,
  Needs attention) and **Deleted items** with restore (kept 30 days by default).
- **Exhibit references** (AB1, AB2…) assigned in date order.

### Categories for everything
- About **470 categories**: every C-form and supplement, Family Law Act forms (FL401–FL415), FGM001, FL701, divorce
  (D8, D84, D36, D11, D81, D50K), financial remedy forms (A, A1, B, C, E, E1, E2, F, G, H, H1, P, P1, P2, ES1, ES2),
  appeals (FP161, FP162, N161), EX107, PRA agreements, applications without a form, every kind of order, hearings and
  case management, public law, adoption and SGO, experts, police, health, education, identity documents, legal aid and
  costs, mediation, international, legal research and your own working papers.
- **Find a category** by typing any word or form code. Sort grouped A–Z, as one A–Z list, or busiest first; hide empty
  ones. Every category picker can be searched by typing and is grouped under headings.

### New case-management screens
- **Case & hearings**: case details (used everywhere automatically), hearings with countdowns and preparation
  checklists, and a register of court orders.
- **Directions & tasks** with overdue warnings, and a **deadline calculator** that understands England & Wales bank
  holidays and FPR "clear days".
- **Contact log** with attendance statistics.
- **Costs & expenses** and a **Scott schedule** (Professional mode).
- **Timeline** entries can now record where, who else was there, and police/incident references.
- **Children's dates of birth**, so their ages are always up to date.
- The **dashboard** shows the next hearing, what's overdue, contact attendance and everything due in the next two weeks.

### Letters, templates and letterheads
- Write **letters, witness statements and position statements** with a live preview, formatting toolbar and
  fields that fill themselves in from your case details.
- Built-in UK templates: letter to the court, witness statement (with statement of truth), position statement, case
  summary, letter to Cafcass, subject access request, formal complaint, letter to your MP, transcript request,
  adjournment request, without-prejudice proposal and chasing a direction.
- **Letterheads** in three styles with your logo and colours – or **import your own Word letterhead or template**
  (.docx / .dotx), filled in while keeping its design.
- **Export to Word, PDF or HTML**, print, or save into your library. Exports carry no author names or Windows details.

### Help assistant
- Ask how to do anything in CaseKeeper in your own words and get **step-by-step directions with buttons that take you
  straight there**. Uses your local AI with a full map of the app and the user guide, so its directions are for things
  that really exist. Only your question is sent – never your case. Works (more simply) without AI too.
- Built-in, checked directions for about 25 common tasks keep answers accurate even with small models on a laptop,
  and are shown on their own when AI is off. Questions about the law or what a court will decide are pointed to the
  Guide & FAQ and to proper advice.
- Screen readers and other accessibility tools can now read the content of every screen (previously only the sidebar).

### UK Family Law Guide & FAQ
- A searchable, plain-English guide to family proceedings in England & Wales, FAQs and a glossary – plus how to use
  every part of CaseKeeper.

### Optional AI analysis (Ollama)
- Summaries, chronologies, issues and allegations, inconsistencies, welfare-checklist mapping, safeguarding concerns,
  questions to consider and more – over the whole case, chosen documents, a category, a party, a date range or your
  records. **Granular** mode analyses large sets part by part, then combines them.
- "Explain simply", "How does this apply to me?" and "Ask the AI" in the guide; "Improve with AI" in the letter writer.
- Every prompt is editable. GPU/CPU, threads, context size and keep-alive are configurable.
- Off until you switch it on. Local by default; remote servers must be allowed explicitly and use HTTPS.

### Auditable, secure and stable
- **Audit trail** of every change, with old and new values, chained with SHA-256 fingerprints so any tampering is
  detectable. Verify and export it.
- **Error log** with a reference on every error message; logs never contain case content or your user name.
- Password protection now also covers **snapshots and saved exports**.
- **Auto-lock** after inactivity, when Windows locks, or on Ctrl+L. **Maximum security** switches on the strictest
  settings in one click.
- **Daily database snapshots**, a document **integrity check**, and a database health check at start-up.
- Decrypted temporary copies are now **wiped** when CaseKeeper closes (previously they stayed in the temp folder).
- The "library in use" marker no longer records your Windows user name.
- A **settings & templates file** to move your set-up between PCs; **backups and merges** now carry every new kind of
  record and your settings; **switch between several case libraries** quickly.
- **Performance profiles**: several documents are scanned at once on a desktop; a laptop on battery works gently.

### Installation
- CaseKeeper now installs to `Program Files\DarkCipher Systems\CaseKeeper`. Upgrading from an earlier version moves it
  there automatically and removes the old `DarkCipher0x Systems` program folder. Your documents and settings are not
  in either folder and are untouched. A custom install location you chose yourself is kept.

---

## v0.2.2 — 17 October 2026

### Privacy clean-up
- The installer no longer includes debug symbol files, and the program no longer contains folder
  paths from the computer it was built on. Nothing else changes; upgrade over any earlier version.

---

## v0.2.1 — 10 October 2026

### Use a library from another folder (without moving it)

- **Settings & Backup → Your library → Use a different library…** opens a library that already
  lives somewhere else, such as a NAS, without copying, moving or deleting anything. Choose an empty
  folder instead and a new library is started there.
- This lets several PCs share one library on a network share. Point each installation at the same
  folder.
- **"Library in use" warning.** While CaseKeeper has a library open, it leaves a small marker in
  that folder. If another computer tries to open the same library at the same time, it is warned
  first, because two PCs writing at once can damage the database. The marker expires on its own if
  a PC crashes or drops off the network.
- **If the library can't be reached at startup** (NAS asleep, drive disconnected), CaseKeeper now
  offers to try again or to open a different folder, instead of just closing. A library folder you
  chose is never silently recreated empty.

### Fixes

- Dropdowns that also accept typed text (party role, event type, linked document, complaint
  recipient, category group) were hard to open. Clicking them selected text instead. Clicking
  anywhere on them now opens the list, and you can still type your own value.

---

## v0.2.0 — 10 October 2026

### Password protection (optional)

- **Encrypt the whole library with a password.** Turn it on under *Settings & Backup → Password
  protection*. Every document file and the entire database — notes, OCR'd text, search index,
  timeline, complaints — is encrypted on disk with AES-256. CaseKeeper then asks for the password
  each time it opens.
- **There is no recovery.** Forgetting the password means the data is unreadable, permanently.
  Turning it on makes you acknowledge that in writing and offers to take a backup first.
- Change the password at any time (instant — it doesn't re-encrypt your documents), or turn
  protection off again to go back to an ordinary unencrypted library.
- Documents are only ever decrypted in memory. Opening, searching, OCR and bundle building all
  work without writing a readable copy to disk.
- Backups still contain readable data so they can be restored on any machine — so if the library
  is protected and the backup isn't, CaseKeeper now says so and offers to put a password on it.

### Move the library anywhere

- **Choose where the library lives**, including an external disk or a shared network drive, under
  *Settings & Backup → Your library → Move library*.
- Moving copies everything to the new folder, checks every file byte-for-byte and confirms the
  database opens there, and only then deletes the originals. If any check fails, the original
  library is left exactly as it was.
- Prompts you to back up first.
- On a network drive the database automatically switches to a journal mode that works over a
  share. Keep the drive connected, and only use the library from one computer at a time.

### Also

- The installer now puts the app in `Program Files\DarkCipher0x Systems\CaseKeeper`.
- Password protection and library location moved near the top of Settings, above Backup.

---

## v0.1.0 — 10 October 2026

First release.

### Documents
- Import PDFs, photos and scans, Word (`.docx`) and text files, by button or by dragging them
  onto the window. Whole folders can be dropped at once.
- Imported files are copied into a private library; your originals are never moved or changed.
- Category and date are guessed from the file name where possible, and the import list can be
  corrected — or rows removed — before anything is added.
- Duplicate detection: a file already in the library is spotted by its fingerprint.
- About 125 built-in categories covering the family courts of England & Wales: the C-forms
  (C1, C1A, C2, C7, C8, C9, C78, C79, C100, C110A), FL401/FL403/FL407, FM1, FP2, FP161, EX160,
  court orders and notices, statements, CAFCASS documents, local authority and agency records,
  evidence, evidence exhibits, correspondence, complaints, and divorce/finance forms.
- Documents that each side files separately come as an Applicant's/Respondent's pair.
- Add your own categories, and set which bundle section each one belongs to.

### Reading and searching
- Built-in viewer for PDFs, images and text, with zoom, rotate and page thumbnails.
- Text recognition (OCR) runs automatically on every PDF and image using the engine built into
  Windows, so scanned letters become searchable. Nothing is uploaded.
- Full-text search across document details, the text inside pages, and your own notes.
  Quote a phrase to match it exactly.
- A notes box for every individual page, plus one for the document as a whole. Both save as you type.
- Find-in-document for jumping to a word inside a long report.

### Organising
- Parties: record the Applicant(s), Respondent(s), children, Cafcass officer, guardian and
  others, then link each document to whoever filed it.
- Timeline: hearings, deadlines, incidents, contact and communications, which becomes the
  chronology in your bundles.
- Complaints tracker for letters to your MP, Cafcass, the ombudsmen and others, with reference
  numbers and response-due dates, flagging anything overdue.
- Star important documents.

### Output
- **Court bundle**: one paginated PDF with a cover page, index, sections A–F, page numbers in
  the style of Practice Direction 27A (A1, B12 …), bookmarks, and optionally your notes as an annex.
- **Summary report**: chronology, document list and your notes without the full documents —
  useful for a complaint or a letter to an MP.
- Export a chronology on its own.
- Share documents through the standard Windows Share sheet, behind a confidentiality reminder.

### Safety and settings
- Light and dark themes, or follow Windows.
- Backup to a single `.zip` holding the database and every document file, optionally encrypted
  with AES-256.
- Import a backup either by adding it to the current library (duplicates skipped, safe to repeat)
  or by replacing everything, for disaster recovery or moving to another PC.
- Factory reset, behind a typed confirmation and an offer to back up first.
- An About section showing the version, the developer and the patch notes.
