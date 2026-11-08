using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace CaseKeeper.Services;

/// <summary>
/// What the help assistant knows about CaseKeeper itself: a map of every screen, button, menu and
/// shortcut, plus the user guide (embedded in the program) split into sections that can be looked
/// up by question. It contains nothing about anyone's case.
///
/// Answers can include links the app turns into buttons:
///   (go:screen)          open a screen, e.g. (go:letters)
///   (settings:section)   open a Settings section, e.g. (settings:ai)
///   (guide:article)      open a guide article, e.g. (guide:app.ai)
/// </summary>
public static class HelpKnowledge
{
    /// <summary>Screens the assistant may send people to, and what they're called on screen.</summary>
    public static readonly Dictionary<string, string> Screens = new()
    {
        ["dashboard"] = "Dashboard", ["case"] = "Case & hearings → Case details", ["hearings"] = "Case & hearings → Hearings",
        ["orders"] = "Case & hearings → Orders", ["documents"] = "Documents", ["search"] = "Search", ["parties"] = "Parties",
        ["timeline"] = "Timeline", ["actions"] = "Directions & tasks", ["contact"] = "Contact log", ["complaints"] = "Complaints",
        ["costs"] = "Costs", ["scott"] = "Scott schedule", ["letters"] = "Letters & templates", ["bundles"] = "Bundles & reports",
        ["ai"] = "AI analysis", ["exports"] = "Exports", ["guide"] = "Guide & FAQ", ["audit"] = "Audit trail",
        ["assistant"] = "Help assistant", ["settings"] = "Settings", ["import"] = "Import documents",
        ["bulkimport"] = "Bulk Case Import", ["review"] = "Import review"
    };

    public static readonly Dictionary<string, string> SettingsSections = new()
    {
        ["layout"] = "Layout & experience", ["security"] = "Security", ["password"] = "Password protection", ["library"] = "Your library",
        ["backup"] = "Backup & transfer", ["recovery"] = "Safety & recovery", ["transfer"] = "Settings, templates and other cases",
        ["ai"] = "AI", ["performance"] = "Performance", ["logs"] = "Logs & diagnostics", ["updates"] = "Updates", ["categories"] = "Categories",
        ["appearance"] = "Appearance", ["ocr"] = "Text recognition (OCR)", ["sharing"] = "Sharing & confidentiality", ["about"] = "About"
    };

    /// <summary>A precise map of the app, given to the assistant with every question.</summary>
    public const string AppMap = @"CASEKEEPER APP MAP (exact names of screens, buttons and menus)

SIDEBAR (left). Groups: OVERVIEW: Dashboard, Case & hearings. PAPERS: Documents, Import review, Search, Parties. TRACK: Timeline, Directions & tasks, Contact log, Complaints, Costs*, Scott schedule*. PRODUCE: Letters & templates, Bundles & reports, AI analysis*, Exports*. HELP & SYSTEM: Guide & FAQ, Help assistant, Audit trail*, Settings.
* = only shown in Professional mode (AI analysis also shows once AI is switched on). Switch mode with the 'Simple mode · switch' / 'Professional mode · switch' button at the bottom of the sidebar, or Settings → Layout & experience. Hidden screens can still be reached with Ctrl+K.
Bottom of sidebar: 'Find anything Ctrl+K' opens the command palette.

GLOBAL KEYS: Ctrl+K command palette (type a screen, action, document, category/form code, party or guide article; Enter runs it). Ctrl+F Search. Ctrl+I Import documents. Ctrl+Shift+I Bulk Case Import (a whole folder). Ctrl+N add new item on list screens. Ctrl+L lock (password-protected libraries only). F1 guide for the current screen. Enter open/edit selected. F2 rename. Delete delete. Ctrl+D duplicate record. Ctrl+C copy rows. Right-click almost anything for a menu of everything you can do.

DASHBOARD: tiles (Documents, Pages, Upcoming dates, Open complaints, Next hearing, Things to do/overdue, Contact (90 days), Costs so far in Professional). 'Due in the next two weeks' list. 'Getting started' checklist (click an item to go there; Hide button). Buttons: Import documents, Import a whole folder, Search everything, Add a date or event, Build a bundle or report. After a bulk import stops part-way, a card offers 'Carry on importing' and 'Open Import review'.

CASE & HEARINGS: three tabs. 'Case details' (case title, number, court, type of proceedings, stage, judge, issue date, MIAM, Cafcass ref, LA ref, case summary; saves as you type; the children list comes from Parties). 'Hearings' (button 'Add hearing'; right-click a hearing: Record the order made…, Add hearing-preparation tasks, Mark as ▸). 'Orders' (button 'Add order'; right-click: Add a direction from this order…).

DOCUMENTS: left 'Library' tree with a 'Find a category or form…' box, an options button (⇅: Grouped by type A–Z / One list A–Z / Busiest first / Hide categories with no documents / Expand/Collapse all) and a + button (Add your own category). Smart folders: All documents, Starred, Recently added, Needs attention, By party, By original folder (folders the files came from in a Bulk Case Import), Deleted items (bottom). Middle: 'Import' button, 'Import folder' button (Bulk Case Import), filter box, document list (right-click column headings to choose columns: Exhibit, Tags, From, Type, Size, Added). Right: details panel with Open, Edit, Share, Star; for bulk-imported files it also shows ORIGINAL FOLDER (where the file was, and the file's own date, for information only).
Right-click a document: Open, Open with default app, Edit details…, Rename… (F2), Move to category ▸, Filed by / party ▸, Tags ▸ (Add tags…, Remove), Star/Unstar, Give exhibit numbers…, Add to bundle (Ctrl+B), Add to timeline…, Link to complaint ▸, Analyse with AI ▸ (first item: 'Organise – suggest a category, dates and records for review…', which sends suggestions to Import review), Share…, Save a copy to…, Copy ▸ (Title / Reference / SHA-256 fingerprint), Scan text again, Check it hasn't changed (integrity), History…, Delete… (goes to Deleted items).
Right-click a category in the tree: Show these documents, Import documents here…, Add all to the bundle, Save copies of all to a folder…, Analyse with AI…, Edit category…, Add a category in this group…, Guide: look this up, Delete category…. Right-click Deleted items: Restore everything, Empty Deleted items…. Inside Deleted items, right-click a document: Restore, Open, Delete permanently…, Empty Deleted items….
Drag documents onto a category (move), party (set party), Starred, or Deleted items. Drag files from Windows onto a category to import them there. Import supports PDF, images/scans, Word .docx, text.

BULK CASE IMPORT (a window; open with 'Import folder' in Documents, 'Import a whole folder' on the Dashboard, Ctrl+Shift+I, or 'Import a folder…' in Import review). Help button / F1.
 Step 1 'Choose a folder': 'Choose folder…'; WHAT TO INCLUDE: Include all subfolders (on), Include hidden files, Include hidden folders, Follow shortcuts and linked folders (advanced), Skip temporary and system files; ORIGINAL FOLDERS: Keep each file's folder path with the document, Don't create anything from folder names / Turn folder names into tags / Choose a category for each top-level folder; FILES ALREADY IN YOUR LIBRARY (matched by contents, a SHA-256 fingerprint): Skip them, and note which document they match / Import them again as separate documents / Let me decide for each one on the next step; AFTER IMPORTING: Read the text now, Ask my local AI to suggest categories, dates and records, Remember these choices for this library. Button 'Find files' (Stop scanning while it looks).
 Step 2 'Check what was found': summary line; FOLDERS tree (untick a folder to leave the branch out); list with Import tick, File, Type, Size, File date (info only), Status, What will happen, Note; search box, Show filter (Everything, Will be imported, Duplicates, Left out, Unsupported, Problems, System files), type filter, 'Include all shown', 'Leave out all shown', 'Copy summary'. Space ticks/unticks selected rows. Button 'Import'.
 Step 3 'Import and read': progress bar and stages (copy into library, read text, local AI suggestions, compare documents); buttons Pause, Stop, 'Keep working while it runs' (progress then shows at the bottom of the main window with an Open button). Closing CaseKeeper is safe; the import carries on next time.
 Step 4 'Finished': summary, 'Open Import review', 'Show the imported documents'. Originals are never moved, renamed or changed; the file's own date is information only, never the document's date.

IMPORT REVIEW (sidebar, PAPERS): where every AI suggestion waits for approval. Left: 'Import a folder…', sections Summary, Duplicates & versions (copies and versions of the same document, newest first, with why it's the newest; buttons Open, 'What changed' (sentence-by-sentence comparison with the newest, added in green, removed struck through), 'Keep newest, tag the others as older' (tags 'Latest version' / 'Older version', nothing deleted), 'Keep all, link as versions' (tag 'Version'), 'Not related', Undo), Categorise documents, Unknown / needs a category, Document details, Hearings, Events & timeline, Orders, Directions & deadlines, Tasks & missing documents, Contact, Parties & children, Complaints, Conflicts & checks, Low confidence, Failed or not analysed, Approved, Applied by AI rule, Deferred, Rejected; 'Review options…' at the bottom (High / Medium confidence levels; AI SERVICE: which AI reads the import (the one in Settings → AI, the local AI, or a cloud service), its model and reasoning effort; AI SPEED: 'This PC: …' with what's recommended for this PC, 'Use recommended settings', 'Download gemma3:4b' when it isn't installed, Speed (Fast (recommended) or Careful), How much the AI reads for (Automatic, Everything, or Categories and document details only), AI model, Documents the AI reads at once; longest document; and the advanced, off-by-default automatic filing of very confident categories).
 Summary: counts, AI status, and each import with Carry on, Pause, 'Analyse N waiting', 'Retry N failed', History.
 Middle: search, confidence filter (High, Medium, Low), document filter, date from/to, 'Only with warnings'; buttons 'Approve selected categories', 'Reject selected', 'Defer selected'. Columns Status, Suggestion, Date, Confidence, Document, Page, Check.
 Right (selected suggestion): Approve (Ctrl+Enter) or 'Mark as resolved' for conflicts, 'Edit & approve' (F2; opens the normal form), 'Work out the date…' (deadline calculator for deadlines like 'within 14 days'), Reject (Delete), Defer, Reconsider, Undo, 'Show it'; CHECK THIS warnings; SOURCE with 'Open at page N' and the quoted words; DATE (as written, what it means, past/future); WHO SAYS SO; CONFIDENCE (High confidence means the document strongly matches, but you should still confirm it); WHY IT WAS SUGGESTED; COMPARE WITH (other documents in a conflict); SUGGESTED BY (model, prompt and schema version); 'Re-run AI on this document', 'Mark document reviewed', History.
 'Failed or not analysed': documents that failed, have no readable text, or show 'Waiting – AI not available'; buttons 'Retry selected', Open, 'Mark as reviewed by hand'.

SEARCH: search box (Ctrl+F), 'Search' button, filters Category, Dated from/to, 'Only search my notes'. Double-click a result to open at that page. Right-click: Open at page, Show in Documents, Add to bundle, Copy snippet, Export results….

PARTIES: buttons Add Applicant, Add Respondent, Add someone else (child, Cafcass, guardian…). Each card: 'N document(s) →', Edit, Delete. Edit form has Role, Full name, Date of birth (for children), 'This is me' tick, Represented by, Contact details, Notes. Right-click a card: Show their documents, Edit…, Write to them…, Copy contact details, History…, Remove….

TIMELINE: Add event, Edit, Delete, type filter, 'Show documents by date', 'Upcoming only', 'Export chronology (PDF)'. Event form: Date, Time, Type, What happened, Details, Where, Who else was there / witnesses, Reference (police/crime number), Linked document, Include in chronology. Right-click: Add event…, Edit…, Duplicate…, Open linked document, Include/Leave out of chronology, Copy, Export chronology (PDF)…, Export as table…, History…, Delete….

DIRECTIONS & TASKS: 'Add direction or task', Edit, Delete, 'Mark done', 'Deadline calculator', filter (Everything, Still to do, Overdue, Due in the next 7 days, Done, Court directions, My tasks, Other party's tasks), Search, Export. Right-click: Mark done today, Set status ▸, Postpone ▸. Deadline calculator: Common deadlines (appeal 21 days, SAR one month, complaint 20 working days, N clear days before a hearing), How to count, From date, Number; 'Add as a task'.

CONTACT LOG: 'Log contact', Edit, Delete, filters (Took place, Did not take place, Concerns noted, Last 30/90 days), Export (includes the attendance summary). Form: Date, Time, Children, Type of contact, Where / handover point, What happened (Took place as arranged, Other parent did not attend, Cancelled by the other parent, Cancelled by me, I could not attend, Child not brought / not handed over…), Minutes late, Length of contact, Supervised or supported, I have concerns about this session, Notes, Linked document.

COMPLAINTS: New complaint, Edit, Delete, 'Build a complaint pack from this…'. Right-click: Set status ▸, Write a letter for this complaint…, History….

COSTS (Professional): 'Add expense'; category filter; 'Might claim back', 'Help with Fees'; Export with totals by category.
SCOTT SCHEDULE (Professional): 'Add allegation', 'Renumber 1, 2, 3…', status filter, Export (landscape).

LETTERS & TEMPLATES: three tabs.
 'Letters & documents': 'New…' button opens a menu (Blank letter, then template groups Letter ▸, Position statement ▸, Statement ▸). Editor: Title, Letterhead, Status, 'Formal document layout' tick, To, Date, Their address, Subject, Their reference, Our reference, Greeting, Sign-off, TEXT box with toolbar (B, I, U, H1, H2, bullet, numbered paragraph, right-align, table, page break, 'Insert field' menu, formatting help ?). Buttons: Export… (PDF/Word/HTML), Print…, Save to library, Improve with AI, Duplicate, Delete. Live Preview on the right. Ctrl+B/I/U format text.
 'Templates': New, 'Import Word…' (.docx/.dotx: Yes = keep Word layout, fills {{fields}} and puts text at {{body}}; No = convert to editable text). Editor: Save, Use this template, Duplicate, Restore original, Delete; Word layout card: Attach Word file…, Convert it to editable text, Save Word file…, Remove.
 'Letterheads': New letterhead; fields name shown at top, address, phone, e-mail, website, reference line, footer, Style (Classic/Band/Minimal), Font, Accent colour, Logo (Choose logo…), Logo position, 'Use this letterhead for new letters'; 'Attach Word letterhead…'. Save.
 Fields such as {{case.number}}, {{me.name}}, {{recipient.name}} fill automatically from Case details and Parties.

BUNDLES & REPORTS: 1. Choose documents (category filter, tick documents, 'Add ticked documents', 'Add everything shown'), 2. Arrange (move up/down, Remove, 'Sort by section & date', Clear; set Section A–F per row; right-click to move section), 3. Options (Court bundle or Summary report, title, case number, court, parties, prepared by, hearing date, include cover/index/chronology/notes/dividers/page numbers/confidential footer). 'Create PDF…' then Open / Share… / Show in folder.

AI ANALYSIS (needs AI switched on – the local AI or a cloud service in Settings → AI): 1. What to look at (The whole case / Documents I choose / One category / One party's documents / A date range / The timeline only / Records only), include text/notes/timeline/records, 2. What kind of report (Summary, Chronology, Key issues & allegations, Inconsistencies, Welfare checklist, The children's experience, Safeguarding concerns, Questions to consider, Position statement outline, Compliance with orders, My own question), Extra instructions, 'Granular' tick, 'Run analysis', Stop, settings cog. Right side: Save report, Export…, Add to library, Copy; Saved reports list (right-click Open/Export/Add to library/Delete).

EXPORTS (Professional): Open, Show folder, Refresh, filter. Right-click: Open, Share…, Save a readable copy to…, Add to my documents, Show in folder, Copy file name, Rename… (F2), Delete….

GUIDE & FAQ: search box, topics list, articles; buttons Explain simply, How does this apply to me?, Save / print…, Ask the AI.

HELP ASSISTANT: this chat.

AUDIT TRAIL (Professional): 'Verify integrity', action filter, date from/to, search, Clear filters, Export. Click an entry to see exactly what changed. Right-click: Show the history of this item, Show all of this kind, Open the document, Copy, Export selected….

SETTINGS: jump buttons at the top (Layout, Security, Password, Library, Backup, Recovery, AI, Performance, Logs, Transfer, Categories, About). Sections:
 Layout & experience (Simple/Professional, name for this PC, getting-started checklist).
 Security ('Turn on maximum security', Lock after no activity for…, Lock when Windows is locked, Always require a password on backups, Lock now).
 Appearance (Match Windows / Light / Dark).
 Password protection (Turn on password protection…, Change password…, Turn off…). No recovery if the password is lost.
 Your library (Open library folder, Move library…, Use a different library…).
 Backup & transfer (password tick, 'Create backup…', 'Import from backup…' – add to library or replace everything).
 Safety & recovery (Keep daily snapshots for…, snapshot list, Take a snapshot now, Restore selected…, Check document integrity, Check database health, Keep deleted documents for…).
 Settings, templates and other cases (Export settings & templates…, Import settings & templates…, Other cases on this PC with Switch, Start or open another case library…).
 AI (WHICH AI TO USE: 'Which AI to use' – Local AI on this PC (Ollama) / Anthropic (Claude) / OpenAI (GPT) / Claude on Microsoft Foundry / OpenAI on Microsoft Foundry. For a cloud service: API key (or Foundry key), 'Get a key' / 'Open Microsoft Foundry', 'Remove key', Foundry resource (name or endpoint), Model (or Deployment name for Foundry), Reasoning effort, 'Connect' (saves the key, checks it, picks the recommended model and switches AI on), 'Use recommended', 'Mask names and personal details before sending (recommended)', Documents read at once, 'Ask again before sending'. For the local AI: 'Set up the local AI' (finds Ollama and downloads the fast model), 'Install Ollama' (website copy, uses winget) or 'Get Ollama' (Store copy, opens the download page). LOCAL AI (OLLAMA): 'How to set up Ollama', Use AI features, Ollama address, Allow a server outside my home network, API key, 'Test connection', model dropdown, GPU, context, longest answer, creativity, CPU threads, keep model loaded, give up after, case summary as background, PROMPTS editor with Save prompt / Reset this prompt / Reset all prompts).
 Performance (Automatic / Power saver / Balanced / Maximum).
 Text recognition (OCR) (Scan any documents that failed, Re-scan all documents).
 Logs & diagnostics (Detailed logging, Keep logs for, recent warnings and errors, Open log folder, Copy diagnostic summary).
 Updates (website version only; the Microsoft Store version is updated by the Store): Updates from (The DarkCipher Systems website / GitHub releases / An https address of my own), Update address, GitHub access token (Save token, Remove), 'Check for updates', WHAT'S NEW, SETUP PROGRAM, 'Download and verify', 'Install… (backup first)', AUTOMATIC CHECKS: Check for updates automatically (off by default), How often (Once a week / Once a day), Also download and verify a new version in the background. An update is offered only when it is signed by both CaseKeeper release keys; installing asks about a backup, closes CaseKeeper and runs the setup wizard; on the next start CaseKeeper says whether the update worked.
 Categories (list, Add category…, Edit…, Delete…).
 Sharing & confidentiality (Remind me about this each time I share or export).
 About (version, What's new…, User guide…, Open library folder). Factory reset (red card at the bottom), with 'Remove all CaseKeeper data…' under it: deletes every case library on this PC that CaseKeeper knows about plus its settings, logs and temporary copies, then closes (type DELETE to confirm). Uninstalling keeps the library; use this first to remove it. Website version library: %LOCALAPPDATA%\CaseKeeper. Microsoft Store version library: %USERPROFILE%\CaseKeeper.

LINKS YOU CAN USE IN ANSWERS (they become buttons): (go:dashboard) (go:case) (go:hearings) (go:orders) (go:documents) (go:import) (go:bulkimport) (go:review) (go:search) (go:parties) (go:timeline) (go:actions) (go:contact) (go:complaints) (go:costs) (go:scott) (go:letters) (go:bundles) (go:ai) (go:exports) (go:guide) (go:audit) (go:settings)
(settings:layout) (settings:security) (settings:password) (settings:library) (settings:backup) (settings:recovery) (settings:transfer) (settings:ai) (settings:performance) (settings:logs) (settings:updates) (settings:categories) (settings:appearance) (settings:ocr)
(guide:quickstart) (guide:app.documents) (guide:app.search) (guide:app.case) (guide:app.actions) (guide:app.contact) (guide:app.costs) (guide:app.letters) (guide:app.bundles) (guide:app.ai) (guide:app.security) (guide:app.backup) (guide:app.audit) (guide:app.shortcuts) (guide:app.modes) (guide:app.assistant) (guide:app.bulkimport) (guide:app.cloudai)";

    // ------------------------------------------------------------------ ready-made directions for common tasks
    /// <summary>
    /// Exact directions for the things people ask most. The best matches are put in front of the AI, which
    /// small local models follow far more reliably than a description of the whole app; without AI they are
    /// shown as they are.
    /// </summary>
    public sealed record Recipe(string Title, string[] Keys, string Steps, string[] Links);

    public static readonly Recipe[] Recipes =
    {
        new("Import documents", new[] { "import", "upload", "add document", "add my document", "add a document", "add files", "scan", "pdf", "photo", "drag" },
            "1. Press **Ctrl+I**, or open **Documents** and click **Import**.\n2. Pick your files (PDF, photos and scans, Word .docx or text). You can also drag files from Windows onto the window – or onto a category in the **Library** tree to file them straight there.\n3. Check the category and date CaseKeeper guessed for each file, change any that are wrong, then import. Your originals are never changed, and the words in scans become searchable.",
            new[] { "go:import", "go:documents" }),
        new("Import a whole case folder (Bulk Case Import)", new[] { "whole folder", "case folder", "folder", "subfolder", "sub folder", "directory", "bulk", "everything at once", "all my files", "all my documents", "existing case", "hundreds" },
            "1. Open **Documents** and click **Import folder** (or press **Ctrl+Shift+I**, or **Import a whole folder** on the **Dashboard**).\n2. Click **Choose folder…** and pick the folder. The usual choices are already set: **Include all subfolders** is ticked; hidden files, hidden folders and shortcuts are left out (leave those unticked); temporary and system files are skipped. Then click **Find files**.\n3. Check the list: untick files, or a whole folder in the **FOLDERS** tree, filter by **Show** or type, then click **Import**. You can **Pause** at any time and carry on later.\n4. If AI is on, your local AI suggests categories, dates and records. Nothing is added to your case until you approve it in **Import review**.",
            new[] { "go:bulkimport", "go:review", "guide:app.bulkimport" }),
        new("Make the AI faster when importing a folder", new[] { "which ai model", "best model", "model for importing", "slow", "faster", "speed", "speed up", "takes ages", "long time", "quicker", "model", "gemma", "graphics card", "gpu", "careful", "fast" },
            "1. Open **Import review** and click **Review options…**. Under **AI SPEED** you'll see **This PC: …** with what CaseKeeper recommends for your graphics card, memory and power.\n2. Click **Use recommended settings**, then **Save**. **Speed** should be **Fast (recommended)**, which uses **gemma3:4b**. If it isn't installed, click **Download gemma3:4b**.\n3. For quicker results still, set **How much the AI reads for** to **Categories and document details only**.\n4. To read several documents at once, set the Windows environment variable **OLLAMA_NUM_PARALLEL** to **4** and restart Ollama.\n5. If your PC struggles, CaseKeeper eases off by itself and tells you what it changed.",
            new[] { "go:review", "guide:app.bulkimport" }),
        new("What happens to my original files", new[] { "original", "originals", "my files safe", "changed my files", "move my files", "delete my files", "rename my files" },
            "1. CaseKeeper copies each file into your library and records its SHA-256 fingerprint. Your originals are never moved, renamed or changed, and nothing is written into the folder you import from.\n2. If your library is password protected, the copies are encrypted.\n3. Each document keeps the folder it came from – see **ORIGINAL FOLDER** in the details panel, or **By original folder** in the **Library** tree. The file's own date is shown for information only and is never used as the document's date.",
            new[] { "go:documents", "guide:app.bulkimport" }),
        new("Duplicates when importing a folder", new[] { "duplicate", "duplicates", "same file", "twice", "already imported", "already in" },
            "1. Duplicates are found by their contents (a SHA-256 fingerprint), never just by name, so two different files with the same name are both imported.\n2. On step 1 of **Bulk Case Import**, under **FILES ALREADY IN YOUR LIBRARY**, choose **Skip them, and note which document they match**, **Import them again as separate documents**, or **Let me decide for each one on the next step**.\n3. On step 2, set **Show** to **Duplicates** to see them, and tick any you want after all.",
            new[] { "go:bulkimport", "guide:app.bulkimport" }),
        new("How AI suggestions, confidence and Unknown work", new[] { "confidence", "unknown", "uncategorised", "uncategorized", "why was", "score", "scores", "ai categoris", "ai categoriz", "how does the ai" },
            "1. CaseKeeper first matches each document to its own categories by form code, title and wording, then gives your local AI a short list to choose from. The AI can only pick one of those, or UNKNOWN.\n2. Every suggestion has a confidence from 0 to 1: **High**, **Medium** or **Low**. High confidence means the document strongly matches, but you should still confirm it. Documents the AI can't match with enough confidence go to **Unknown / needs a category** in **Import review**.\n3. Each suggestion shows the page and exact words it came from. Quotes or dates that can't be found in the document are flagged under **CHECK THIS** and their confidence is lowered.\n4. Change the levels with **Review options…**.",
            new[] { "go:review", "guide:app.bulkimport" }),
        new("Does my case data leave my computer?", new[] { "leave my computer", "leave my pc", "leave this computer", "case data", "data", "cloud", "online", "internet", "privacy", "who can see", "sent anywhere" },
            "1. Not unless you choose a cloud AI. Importing, reading text (OCR), duplicate and version checks and the cross-checks all happen on this computer.\n2. The AI only runs if you switch it on. The local AI talks only to your own Ollama server – normally this PC (**Ollama address** in Settings → **AI**). If you choose a cloud service (Anthropic, OpenAI or Microsoft Foundry) in Settings → **AI** → **Which AI to use**, the text of the documents you analyse is sent to that service over HTTPS, with names and personal details masked first (on by default), and only after you agree on the **Before anything is sent** screen.\n3. **Check for updates** (Settings → **Updates**) only downloads a small file saying which version is newest, with its signatures. It sends nothing about you or your case, and runs only when you click it or switch on automatic checks.\n4. Logs never contain document text, file names or your folder paths. With **Password protection**, everything – including the AI's suggestions – is encrypted.",
            new[] { "settings:ai", "settings:password", "settings:updates" }),
        new("Update CaseKeeper to a new version", new[] { "update", "updates", "upgrade", "new version", "latest version", "check for updates", "install update", "out of date" },
            "1. Open **Settings** → **Updates** and click **Check for updates**. CaseKeeper says whether a newer version is available and shows what's new.\n2. Click **Download and verify**. CaseKeeper only offers an update signed by both CaseKeeper release keys, and checks the download matches exactly.\n3. Click **Install… (backup first)**, choose whether to make a backup, then **OK**. CaseKeeper closes, the setup program updates it (Windows may ask permission), and your library stays exactly where it is.\n4. When CaseKeeper opens again it says whether the update worked.\n5. To be told automatically, tick **Check for updates automatically** there (off until you switch it on). The Microsoft Store version is updated by the Store instead: Microsoft Store → **Library** → **Get updates**.",
            new[] { "settings:updates", "guide:app.updates" }),
        new("Approve, edit or reject an AI suggestion (e.g. a hearing date)", new[] { "approve", "approval", "suggestion", "suggestions", "hearing date", "hearing dates", "extracted", "correct", "wrong date", "review" },
            "1. Open **Import review** (sidebar, under PAPERS) and choose a section such as **Hearings**.\n2. Click a suggestion to see its **SOURCE**: the document, page and quoted words. **Open at page** shows it in the document.\n3. Click **Approve** to add it, or **Edit & approve** (F2) to correct the date, time or anything else first – it opens the same form as adding one by hand. **Reject** or **Defer** it if it's wrong or you're not sure.\n4. Nothing is added to your case until you approve it, and every decision is recorded in the **Audit trail**.",
            new[] { "go:review", "guide:app.bulkimport" }),
        new("Pause, stop or carry on a bulk import", new[] { "pause", "resume", "carry on", "interrupted", "stop the import", "continue the import", "half way", "halfway" },
            "1. While it runs, click **Pause** or **Stop** in the **Bulk Case Import** window – or **Keep working while it runs** to close the window; progress then shows at the bottom of the main window.\n2. To continue, click **Carry on importing** on the **Dashboard**, or **Carry on** next to the import in **Import review** → **Summary**.\n3. Closing CaseKeeper is safe: everything done so far is kept and the import carries on from where it stopped.",
            new[] { "go:review", "go:dashboard" }),
        new("When the AI isn't available (Ollama not running, cloud AI not reachable), or analysis failed", new[] { "not running", "isn't running", "isnt running", "unavailable", "waiting for ai", "failed", "retry", "rerun", "re run", "analyse again", "analyze again", "try again" },
            "1. If the AI can't be reached, the import still finishes. Documents show **Waiting – AI not available**, and CaseKeeper's own rules still suggest categories from form codes.\n2. Start Ollama (Settings → **AI** → **Test connection**) – or, for a cloud service, check this PC is online and click **Connect** in Settings → **AI** – then in **Import review** → **Summary** use **Analyse N waiting** or **Retry N failed** next to the import – or open **Failed or not analysed**, select documents and click **Retry selected**.\n3. To redo one document, select any of its suggestions and click **Re-run AI on this document**.",
            new[] { "go:review", "settings:ai" }),
        new("Undo an approved suggestion", new[] { "undo", "approved", "an approved", "by mistake", "reverse", "take back", "remove an approved" },
            "1. In **Import review**, open **Approved** (or **Applied by AI rule**).\n2. Select the suggestion and click **Undo**. It removes what was added – for example the hearing and its timeline entry – or puts the document's old category back, and returns the suggestion to the review list.\n3. Rejected or deferred suggestions can be brought back with **Reconsider**.",
            new[] { "go:review" }),
        new("Find a category or form (e.g. C1A, C100)", new[] { "category", "categories", "form", "c1a", "c100", "fl401", "code", "sort", "alphabetical" },
            "1. Open **Documents**.\n2. Type the form code or any word (e.g. C1A, contact, police) into **Find a category or form…** above the **Library** tree.\n3. Use the **⇅** button to sort: **Grouped by type A–Z**, **One list A–Z** or **Busiest first**, or **Hide categories with no documents**.\n4. Anywhere in the app, **Ctrl+K** and the form code also finds it. Add your own with the **+** button.",
            new[] { "go:documents", "settings:categories" }),
        new("Move, rename or tag a document", new[] { "move", "rename", "tag", "organis", "organiz", "wrong category", "recategor", "file it" },
            "1. Open **Documents** and right-click the document.\n2. Choose **Move to category ▸**, **Rename…** (or press **F2**), **Tags ▸** or **Filed by / party ▸**.\n3. Quicker: drag documents onto a category, a party, **Starred** or **Deleted items** in the **Library** tree. Select several with Ctrl or Shift first to do them all at once.",
            new[] { "go:documents" }),
        new("Delete or get back a document", new[] { "delete", "deleted", "restore", "recover", "undo", "bin", "trash", "get back" },
            "1. To delete: right-click the document → **Delete…**. It goes to **Deleted items** at the bottom of the **Library** tree and is kept for 30 days (change this in Settings → **Safety & recovery**).\n2. To get it back: click **Deleted items**, right-click the document → **Restore** (or right-click **Deleted items** → **Restore everything**).",
            new[] { "go:documents", "settings:recovery" }),
        new("Exhibit numbers", new[] { "exhibit" },
            "1. In **Documents**, select the documents to exhibit (Ctrl+click or Shift+click).\n2. Right-click → **Give exhibit numbers…** and enter your prefix (usually your initials). They are numbered in date order, e.g. AB1, AB2.\n3. Right-click a column heading and tick **Exhibit** to see them in the list.",
            new[] { "go:documents" }),
        new("Search inside documents", new[] { "search", "find a word", "look for", "find text", "find where" },
            "1. Press **Ctrl+F** or open **Search**.\n2. Type the words; put a phrase in \"quotes\" to match it exactly. Narrow it with **Category**, **Dated from/to** or **Only search my notes**.\n3. Double-click a result to open the document at that page.",
            new[] { "go:search" }),
        new("Record case details, a hearing or an order", new[] { "hearing", "case number", "case details", "court name", "judge", "order", "next hearing" },
            "1. Open **Case & hearings**. The **Case details** tab holds the case number, court, judge and so on – it saves as you type and fills in letters automatically.\n2. On the **Hearings** tab click **Add hearing**; right-click it for **Add hearing-preparation tasks** or **Record the order made…**.\n3. On the **Orders** tab click **Add order**; right-click an order → **Add a direction from this order…**.",
            new[] { "go:hearings", "go:case", "go:orders" }),
        new("Deadlines, directions and tasks", new[] { "deadline", "direction", "task", "due", "overdue", "clear days", "how many days", "calculate" },
            "1. Open **Directions & tasks** and click **Add direction or task** (who must do what, by when).\n2. For dates, click **Deadline calculator**: choose a **Common deadline** (e.g. appeal 21 days, N clear days before a hearing), the **From date** and **Number**, then **Add as a task**. It knows England & Wales bank holidays.\n3. Right-click a task → **Mark done today** or **Postpone ▸**. Overdue items show on the **Dashboard**.",
            new[] { "go:actions" }),
        new("Timeline and chronology", new[] { "timeline", "chronology", "incident", "event", "what happened", "diary" },
            "1. Open **Timeline** and click **Add event**.\n2. Fill in **Date**, **Type**, **What happened**, and if useful **Where**, **Who else was there / witnesses**, **Reference** (police/crime number) and a **Linked document**. Leave **Include in chronology** ticked.\n3. **Export chronology (PDF)** makes a chronology; it is also added to bundles.",
            new[] { "go:timeline" }),
        new("Record contact with the children", new[] { "contact", "missed contact", "didn't turn up", "did not turn up", "handover", "visit" },
            "1. Open **Contact log** (sidebar, under TRACK) and click **Log contact**.\n2. Fill in **Date**, **Time** and **Children**, then pick from **What happened** – e.g. Took place as arranged, Other parent did not attend, Cancelled by the other parent, Child not brought / not handed over. Add **Minutes late** if any, tick **I have concerns about this session** if so, and write the facts in **Notes**. Attach messages under **Linked document**.\n3. Use the filters (**Did not take place**, **Concerns noted**, **Last 30/90 days**) and **Export** for a summary with attendance figures.",
            new[] { "go:contact" }),
        new("People in the case (parties, children)", new[] { "party", "parties", "applicant", "respondent", "child", "children", "cafcass officer", "guardian", "date of birth" },
            "1. Open **Parties**.\n2. Click **Add Applicant**, **Add Respondent** or **Add someone else (child, Cafcass, guardian…)**.\n3. Fill in the **Role**, **Full name**, and for children the **Date of birth**. Tick **This is me** on your own entry so letters are signed correctly.",
            new[] { "go:parties" }),
        new("Write a letter or statement", new[] { "letter", "witness statement", "statement", "position statement", "write", "draft", "template", "case summary", "subject access", "sar" },
            "1. Open **Letters & templates** – the **Letters & documents** tab.\n2. Click **New…** and pick **Blank letter** or a template from **Letter ▸**, **Position statement ▸** or **Statement ▸** (e.g. Witness statement).\n3. Fill in **To**, **Subject** and the **TEXT** box. Use **Insert field** for case details; the **Preview** on the right shows the result.\n4. Click **Export…** for PDF, Word or HTML, or **Save to library**.",
            new[] { "go:letters" }),
        new("Use your own Word letterhead or template", new[] { "letterhead", "word template", "my own word", "docx", "dotx", "logo", "import word" },
            "1. Open **Letters & templates**.\n2. For a letterhead: **Letterheads** tab → **New letterhead** → **Attach Word letterhead…** and choose your .docx/.dotx (or design one with Style, Logo and Accent colour), then **Save**. Tick **Use this letterhead for new letters** to make it the default.\n3. For a whole template: **Templates** tab → **Import Word…**. Answer **Yes** to keep the Word layout (it fills {{fields}} and puts your text where {{body}} is) or **No** to turn it into editable text.",
            new[] { "go:letters" }),
        new("Export to Word, PDF or HTML", new[] { "export", "word", "pdf", "html", "print" },
            "1. In **Letters & templates**, open the letter and click **Export…**, then choose PDF, Word or HTML (or **Print…**).\n2. Lists such as Timeline, Contact log and Directions & tasks have their own **Export** button; Timeline also has **Export chronology (PDF)**.\n3. Files you export into the library appear under **Exports** (Professional mode).",
            new[] { "go:letters", "go:exports" }),
        new("Make a court bundle or summary report", new[] { "bundle", "court bundle", "summary report", "report", "paginat" },
            "1. Open **Bundles & reports** (sidebar, under PRODUCE).\n2. **1. Choose documents**: tick documents and click **Add ticked documents** (or **Add everything shown**). Tip: right-click documents in **Documents** → **Add to bundle** (Ctrl+B).\n3. **2. Arrange**: click **Sort by section & date**, set the Section (A–F) for each, move up/down or **Remove**.\n4. **3. Options**: choose **Court bundle** or **Summary report**, fill in the title, case number, court and hearing date, and tick what to include (cover, index, chronology, page numbers…).\n5. Click **Create PDF…**, then **Open**, **Share…** or **Show in folder**.",
            new[] { "go:bundles" }),
        new("Complaints", new[] { "complain", "ombudsman", "mp" },
            "1. Open **Complaints** and click **New complaint** (who, reference, response due).\n2. Right-click it → **Write a letter for this complaint…**, or use **Build a complaint pack from this…**.\n3. Link documents from **Documents** with right-click → **Link to complaint ▸**.",
            new[] { "go:complaints" }),
        new("Password-protect (encrypt) everything", new[] { "password", "encrypt", "protect", "secure", "security", "lock", "private" },
            "1. Open **Settings** → **Password protection** and click **Turn on password protection…**. This encrypts the database, documents, snapshots and saved exports with AES-256. There is no recovery if you forget it, so take a backup when offered.\n2. Optional, in Settings → **Security**: **Turn on maximum security** (strictest settings in one click), **Lock after no activity for…**, **Lock when Windows is locked**, **Always require a password on backups**.\n3. Lock at any time with **Ctrl+L**.",
            new[] { "settings:password", "settings:security" }),
        new("Back up, or move to a new PC", new[] { "backup", "back up", "new pc", "new computer", "new laptop", "another computer", "another pc", "move everything", "migrat", "transfer", "restore a backup", "copy to" },
            "1. Open **Settings** → **Backup & transfer**, tick the password option (recommended) and click **Create backup…**. Keep the file somewhere safe, such as a USB stick.\n2. On the new PC, install CaseKeeper, then **Settings** → **Backup & transfer** → **Import from backup…** and choose **replace everything** (or add to the library).\n3. To copy just your settings, templates and letterheads: **Settings** → **Settings, templates and other cases** → **Export settings & templates…**.",
            new[] { "settings:backup", "settings:transfer" }),
        new("Move the library or open another case", new[] { "library", "nas", "network", "another case", "second case", "folder", "external drive" },
            "1. Open **Settings** → **Your library**: **Move library…** (copies, checks every file, then removes the old copy), **Use a different library…** or **Open library folder**.\n2. To keep several cases: **Settings** → **Settings, templates and other cases** → **Start or open another case library…**, and **Switch** between them.",
            new[] { "settings:library", "settings:transfer" }),
        new("Set up the local AI (Ollama)", new[] { "ollama", "ai", "model", "llm", "gemma", "llama", "set up the ai", "artificial", "local ai", "install ollama" },
            "1. Open **Settings** → **AI**. Under **Which AI to use**, choose **Local AI on this PC (Ollama)**.\n2. Click **Install Ollama** (or **Get Ollama**, which opens its download page) if it isn't installed, then **Set up the local AI**: it finds Ollama, downloads the fast model (**gemma3:4b**) and switches AI on.\n3. To do it by hand: tick **Use AI features**, leave the **Ollama address** as it is for your own PC, click **Test connection** and choose a model. The prompts can be edited in the **PROMPTS** editor there.\n4. Then use **AI analysis** in the sidebar, or right-click documents → **Analyse with AI ▸**.",
            new[] { "settings:ai", "go:ai" }),
        new("Use Claude or ChatGPT models (Anthropic, OpenAI or Microsoft Foundry)", new[] { "anthropic", "claude", "openai", "chatgpt", "gpt", "foundry", "azure", "cloud ai", "api key", "online ai", "reasoning effort", "haiku", "sonnet", "opus" },
            "1. Get an API key from the service: **Anthropic** (platform.claude.com), **OpenAI** (platform.openai.com), or a resource and key from **Microsoft Foundry** (ai.azure.com) with a model deployed.\n2. Open **Settings** → **AI** → **Which AI to use** and choose the service. Paste the key (for Foundry also the **Foundry resource**), then click **Connect**. CaseKeeper checks the key, picks the recommended fast model (Claude Haiku 5.5 or GPT-6 Luna, low effort) and switches AI on.\n3. Change **Model** and **Reasoning effort** if you want: higher effort is more careful but slower and costs more.\n4. Leave **Mask names and personal details before sending** ticked. Before anything is sent the first time, you're shown where it goes and asked to agree.\n5. The key is stored encrypted for your Windows account and never appears in settings files, logs or backups.",
            new[] { "settings:ai" }),
        new("Find duplicates and older versions of documents", new[] { "duplicate", "duplicates", "versions", "version", "older version", "newest", "latest", "draft", "which is newer", "same document", "copies", "what changed", "edits" },
            "1. Open **Import review** and choose **Duplicates & versions**. CaseKeeper compares the words inside your documents (not just the files), so a PDF and a Word copy of the same letter, or a draft and its final version, are found too.\n2. Each group shows the newest first, with why (for example its date), and what each older one is missing or added.\n3. Click **What changed** to compare one with the newest, sentence by sentence: added text in green, removed text struck through.\n4. Choose **Keep newest, tag the others as older**, **Keep all, link as versions**, or **Not related**. Nothing is deleted, and **Undo** puts it back.",
            new[] { "go:review", "guide:app.bulkimport" }),
        new("What CaseKeeper sends to a cloud AI, and masking", new[] { "masking", "mask", "masked", "what is sent", "consent", "before anything is sent", "personal details", "redact", "anonymise", "anonymize", "trust", "treat with care" },
            "1. Only the text of the documents you choose to analyse is sent, to the service you picked, over HTTPS. Nothing is sent with the local AI.\n2. With masking on (the default), names from your parties and contacts, e-mail addresses, phone numbers, postcodes, National Insurance, case, bank and card numbers, dates of birth and addresses are replaced with tags such as [PERSON-1] before sending, and put back in the answer on this PC. The list linking tags to real details is never sent or saved.\n3. Before a bulk import sends anything, **Before anything is sent** shows the address, what will be masked, sensitive subjects, and documents that couldn't be masked well. For those, choose: read them with the local AI instead, skip them, or send them anyway.\n4. Your agreement is recorded in the **Audit trail** (service, address, masking) without any content. **Ask again before sending** in Settings → **AI** brings the screen back.\n5. Documents with something unusual (macros, a badly scanned page, a date later than the file was last changed) get a **Treat with care** note under **Conflicts & checks**.",
            new[] { "settings:ai", "go:review" }),
        new("Run an AI report on documents", new[] { "analys", "summar", "inconsisten", "safeguarding", "welfare checklist", "ai report" },
            "1. Open **AI analysis** (switch AI on first in Settings → **AI**).\n2. **1. What to look at**: the whole case, documents you choose, a category, a party, a date range or the timeline.\n3. **2. What kind of report**: e.g. Summary, Chronology, Inconsistencies, Welfare checklist. Tick **Granular** for large sets.\n4. Click **Run analysis**, then **Save report**, **Export…** or **Add to library**.",
            new[] { "go:ai", "settings:ai" }),
        new("Simple and Professional mode", new[] { "professional", "simple mode", "mode", "hidden", "missing screen", "where is costs", "scott", "costs", "audit" },
            "1. Click **Simple mode · switch** (or **Professional mode · switch**) at the bottom of the sidebar, or Settings → **Layout & experience**.\n2. Professional mode adds **Costs**, **Scott schedule**, **AI analysis**, **Exports** and **Audit trail**. Any hidden screen can still be reached with **Ctrl+K**.",
            new[] { "settings:layout" }),
        new("Audit trail and history", new[] { "audit", "history", "who changed", "what changed", "tamper", "log" },
            "1. Open **Audit trail** (Professional mode) to see every change with old and new values; click **Verify integrity** to check nothing has been tampered with.\n2. For one item, right-click it (a document, party, event) → **History…**.\n3. Error logs are in Settings → **Logs & diagnostics**.",
            new[] { "go:audit", "settings:logs" }),
        new("Appearance, theme and speed", new[] { "dark", "light", "theme", "colour", "color", "slow", "fast", "battery", "laptop", "performance" },
            "1. Theme: **Settings** → **Appearance** → **Match Windows**, **Light** or **Dark**.\n2. Speed: **Settings** → **Performance** → **Automatic**, **Power saver** (laptops on battery), **Balanced** or **Maximum** (powerful PCs).",
            new[] { "settings:appearance", "settings:performance" }),
        new("A question about the law or the case outcome", new[] { "custody", "will i get", "will i win", "chance", "should i", "my rights", "judge decide", "lose my", "residence", "am i allowed", "legal advice", "what will happen" },
            "I'm sorry – I can't answer questions about the law or say what a court will decide, and nothing in CaseKeeper is legal advice. What can help:\n1. The **Guide & FAQ** explains in plain English how child arrangements cases work and what the court considers.\n2. A solicitor or a free advice service can advise on your own situation.\n3. CaseKeeper can help you prepare: record events on the **Timeline**, keep a **Contact log**, and write a **Position statement** in **Letters & templates**.",
            new[] { "go:guide", "go:letters" }),
    };

    /// <summary>The ready-made directions that best match a question (most relevant first).</summary>
    public static List<Recipe> MatchRecipes(string question, int max = 2)
    {
        var q = " " + Regex.Replace((question ?? "").ToLowerInvariant(), @"[^a-z0-9']+", " ") + " ";
        var ranked = Recipes
            // keys match the start of a word ("organis" → organise/organising); short ones (ai, mp) only a whole word
            .Select((r, i) => (r, i, score: r.Keys.Sum(k => q.Contains(k.Length <= 3 ? $" {k} " : " " + k) ? (k.Contains(' ') ? 3 : 2) : 0)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.i).ToList();
        // a much weaker second match only confuses a small model
        return ranked.Where(x => x.score * 2 >= ranked[0].score).Take(max).Select(x => x.r).ToList();
    }

    /// <summary>
    /// Everything the assistant is told about the app for one question: the app map, matching directions,
    /// relevant user-guide sections, and a closing reminder of the rules (small models follow the last thing they read).
    /// </summary>
    public static string BuildContext(string question, int guideChars)
    {
        var sb = new StringBuilder();
        sb.AppendLine(AppMap);
        var recipes = MatchRecipes(question);
        var sections = Relevant(question, guideChars);
        if (sections.Count > 0)
        {
            sb.AppendLine().AppendLine("RELEVANT SECTIONS OF THE USER GUIDE:");
            foreach (var (title, text) in sections) sb.AppendLine($"### {title}\n{text}\n");
        }
        if (recipes.Count > 0)
        {
            sb.AppendLine().AppendLine("CORRECT DIRECTIONS FOR THIS QUESTION (base your answer on these; keep the bold names and links exactly as written):");
            foreach (var r in recipes) sb.AppendLine($"### {r.Title}\n{r.Steps}\nLinks: {string.Join(" ", r.Links.Select(l => $"({l})"))}\n");
        }
        sb.AppendLine().Append("REMEMBER: only mention screens, buttons and settings named above – never make one up. Short numbered steps, exact names in **bold**, ")
          .Append("and end with one or two links written like [Open Letters & templates](go:letters). If it's about the law or what a court will decide, ")
          .Append("say you can't advise and point to the Guide & FAQ (go:guide). Under about 180 words.");
        return sb.ToString();
    }

    /// <summary>
    /// Turns the AI's answer into display text and buttons. The best-matching directions' main link always
    /// comes first (small models sometimes link the wrong screen), and if the answer had no links at all,
    /// the rest of the matching directions' links are added so there is always a way to get there.
    /// </summary>
    public static (string Text, List<Link> Links) Finish(string answer, string question)
    {
        var (text, given) = ExtractLinks(answer);
        var recipes = MatchRecipes(question);
        // the label of a link the AI wrote on its own line is now a button – drop the leftover line
        foreach (var l in given) text = Regex.Replace(text, @"(?m)^[ \t]*" + Regex.Escape(l.Label) + @"[ \t]*$\n?", "");
        // Small models copy the example link from their instructions onto every answer. Keep a link only if it's in
        // the matching directions, or the answer or question actually talks about that screen.
        var recipeLinks = recipes.SelectMany(r => r.Links).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var body = given.Aggregate(text, (t, l) => t.Replace(l.Label, ""));
        bool Fits(Link l)
        {
            if (recipeLinks.Contains($"{l.Kind}:{l.Target}") || l.Kind == "guide") return true;
            var name = l.Kind == "go" ? Screens.GetValueOrDefault(l.Target) : SettingsSections.GetValueOrDefault(l.Target);
            if (string.IsNullOrEmpty(name)) return false;
            var key = name.Split('→')[0].Trim();
            return body.Contains(key, StringComparison.OrdinalIgnoreCase) || (question ?? "").Contains(key, StringComparison.OrdinalIgnoreCase);
        }
        var kept = given.Where(Fits).ToList();
        // a dropped link's label shouldn't linger in the text either
        foreach (var l in given.Except(kept)) text = text.Replace(l.Label, "");
        text = Regex.Replace(text, @"(?im)^[ \t]*\**(links?|useful links)\**:?\**[ \t]*$\n?", "");
        var links = new List<Link>();
        if (recipes.Count > 0) AddLink(links, recipes[0].Links[0]);
        foreach (var l in kept) if (!links.Any(x => x.Kind == l.Kind && x.Target == l.Target)) links.Add(l);
        if (kept.Count == 0)
            foreach (var r in recipes)
                foreach (var l in r.Links) AddLink(links, l);
        text = Regex.Replace(text, @"\n[ \t]*(\n[ \t]*)+", "\n\n");
        return (text.Trim(), links.Take(4).ToList());
    }

    /// <summary>Adds a link written as "go:letters" / "settings:ai" / "guide:id" if it points somewhere real.</summary>
    public static void AddLink(List<Link> links, string spec)
    {
        var i = spec.IndexOf(':');
        if (i < 1) return;
        string kind = spec[..i], target = spec[(i + 1)..];
        var label = DefaultLabel(kind, target);
        if (label != null && IsValid(kind, target) && !links.Any(l => l.Kind == kind && l.Target == target))
            links.Add(new Link(kind, target, label));
    }

    // ------------------------------------------------------------------ the user guide, split into sections
    private sealed record Section(string Title, string Text);
    private static List<Section> _sections;

    /// <summary>Reads the user guide embedded in the program (falls back to the copy next to the exe).</summary>
    private static List<Section> Sections()
    {
        if (_sections != null) return _sections;
        string text = null;
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("USER-GUIDE.md");
            if (s != null) text = new StreamReader(s, Encoding.UTF8).ReadToEnd();
        }
        catch { /* fall through */ }
        if (text == null)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "USER-GUIDE.md");
            if (File.Exists(path)) text = File.ReadAllText(path);
        }
        _sections = Split(text ?? "");
        return _sections;
    }

    private static List<Section> Split(string md)
    {
        var list = new List<Section>();
        string title = "Introduction";
        var sb = new StringBuilder();
        foreach (var line in md.Replace("\r", "").Split('\n'))
        {
            if (line.StartsWith("## "))
            {
                if (sb.Length > 0) list.Add(new Section(title, sb.ToString().Trim()));
                title = line[3..].Trim();
                sb.Clear();
            }
            else sb.AppendLine(line);
        }
        if (sb.Length > 0) list.Add(new Section(title, sb.ToString().Trim()));
        return list;
    }

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "can", "how", "what", "who", "does", "is", "are", "my", "me", "do", "to", "of", "in", "on", "a", "an", "it",
        "if", "or", "be", "with", "about", "when", "will", "i", "should", "there", "this", "that", "at", "by", "from", "where", "want", "need",
        "make", "get", "use", "using", "find", "way", "please", "help", "caseKeeper"
    };

    public static List<string> Words(string q) =>
        Regex.Split((q ?? "").ToLowerInvariant(), @"[^a-z0-9]+").Where(w => w.Length > 1 && !Stop.Contains(w)).Distinct().ToList();

    /// <summary>The guide sections most relevant to a question, up to about <paramref name="maxChars"/> characters.</summary>
    public static List<(string Title, string Text)> Relevant(string question, int maxChars = 6000)
    {
        var words = Words(question);
        var scored = Sections().Select(s =>
        {
            string t = s.Title.ToLowerInvariant(), b = s.Text.ToLowerInvariant();
            int score = words.Sum(w => (t.Contains(w) ? 8 : 0) + Math.Min(6, Regex.Matches(b, Regex.Escape(w)).Count));
            return (s, score);
        }).Where(x => x.score > 0).OrderByDescending(x => x.score).ToList();

        var result = new List<(string, string)>();
        int used = 0;
        foreach (var (s, _) in scored)
        {
            var text = s.Text.Length > 3500 ? s.Text[..3500] + "…" : s.Text;
            if (used + text.Length > maxChars && result.Count > 0) break;
            result.Add((s.Title, text));
            used += text.Length;
        }
        return result;
    }

    // ------------------------------------------------------------------ links in answers
    public sealed record Link(string Kind, string Target, string Label);

    // [Label](go:x), or a bare (go:x) / [go:x] – small models write all three
    private static readonly Regex LinkPattern = new(@"\[([^\]]{1,80})\]\(\s*(go|settings|guide):([a-z0-9.\-]+)\s*\)|[\(\[](go|settings|guide):([a-z0-9.\-]+)[\)\]]", RegexOptions.IgnoreCase);

    /// <summary>
    /// Pulls the navigation links out of an answer (only ones that point somewhere real) and returns the
    /// text with each link replaced by its label, so it reads naturally.
    /// </summary>
    public static (string Text, List<Link> Links) ExtractLinks(string answer)
    {
        var links = new List<Link>();
        var text = LinkPattern.Replace(answer ?? "", m =>
        {
            bool labelled = m.Groups[1].Success;
            var kind = (labelled ? m.Groups[2].Value : m.Groups[4].Value).ToLowerInvariant();
            var target = (labelled ? m.Groups[3].Value : m.Groups[5].Value).ToLowerInvariant();
            var label = labelled ? m.Groups[1].Value.Trim() : DefaultLabel(kind, target);
            if (label == null) return labelled ? m.Groups[1].Value : "";
            if (!IsValid(kind, target)) return labelled ? m.Groups[1].Value : "";
            if (!links.Any(l => l.Kind == kind && l.Target == target)) links.Add(new Link(kind, target, label));
            return labelled ? m.Groups[1].Value : "";
        });
        text = Regex.Replace(text, @"[ \t]+([.,;:])", "$1");
        // a "Links:" heading left empty once its links became buttons
        text = Regex.Replace(text, @"(?im)^[ \t]*\**(links?|useful links)\**:?\**[ \t]*$\n?", "");
        return (text.Trim(), links);
    }

    private static bool IsValid(string kind, string target) => kind switch
    {
        "go" => Screens.ContainsKey(target),
        "settings" => SettingsSections.ContainsKey(target),
        "guide" => true,
        _ => false
    };

    private static string DefaultLabel(string kind, string target) => kind switch
    {
        "go" => Screens.TryGetValue(target, out var s) ? "Open " + s : null,
        "settings" => SettingsSections.TryGetValue(target, out var s) ? "Settings → " + s : null,
        "guide" => "Guide: " + target,
        _ => null
    };

    /// <summary>Questions to get people started.</summary>
    public static readonly string[] Suggestions =
    {
        "How do I import my documents?",
        "How do I import a whole case folder?",
        "How do I approve the hearing dates the AI found?",
        "How do I write a witness statement?",
        "How do I make a court bundle?",
        "How do I add my next hearing?",
        "How do I use my own Word letterhead?",
        "How do I protect everything with a password?",
        "How do I back up and move to a new PC?",
        "How do I set up the AI with Ollama?",
        "Where do I record missed contact?",
        "How do I find a form like C1A?"
    };
}
