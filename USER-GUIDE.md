# CaseKeeper user guide

CaseKeeper keeps everything for a family court case in England & Wales in one private place on your own
computer: documents, hearings and orders, deadlines, a contact log, costs, letters and statements, and more.

> CaseKeeper organises and presents your material. It does not give legal advice. The built-in guide and the
> optional AI give general information only – check anything important with a solicitor or advice service.

**Contents**

1. [Getting started](#1-getting-started)
2. [Finding your way around](#2-finding-your-way-around)
3. [Documents and categories](#3-documents-and-categories)
4. [Bulk Case Import and Import review](#4-bulk-case-import-and-import-review)
5. [Search](#5-search)
6. [Parties](#6-parties)
7. [Case details, hearings and orders](#7-case-details-hearings-and-orders)
8. [Timeline](#8-timeline)
9. [Directions, tasks and deadlines](#9-directions-tasks-and-deadlines)
10. [Contact log](#10-contact-log)
11. [Complaints](#11-complaints)
12. [Costs and the Scott schedule](#12-costs-and-the-scott-schedule)
13. [Letters, templates and letterheads](#13-letters-templates-and-letterheads)
14. [Bundles and reports](#14-bundles-and-reports)
15. [AI analysis: local or cloud](#15-ai-analysis)
16. [Guide & FAQ](#16-guide--faq)
17. [Exports](#17-exports)
18. [Audit trail and logs](#18-audit-trail-and-logs)
19. [Security and privacy](#19-security-and-privacy)
20. [Backups, snapshots and moving PCs](#20-backups-snapshots-and-moving-pcs)
21. [Performance](#21-performance)
22. [Keyboard shortcuts](#22-keyboard-shortcuts)
23. [Troubleshooting](#23-troubleshooting)
24. [Where your data is kept, and removing it](#24-where-your-data-is-kept-and-removing-it)
25. [Updating CaseKeeper](#25-updating-casekeeper)

---

## 1. Getting started

The first time you open CaseKeeper, a short **Welcome** asks:

- **How you'll use it** – *Simple* (the essentials, in plain English) or *Professional* (every tool). Switch any
  time from the bottom of the sidebar.
- **About you** – your role and name, used to fill in letters and statements. Optional.
- **The case** – case number, court and type. Optional; you can fill these in later under *Case & hearings*.
- **Appearance and security** – dark or light, and whether to protect the library with a password.

The dashboard then shows a **Getting started** checklist. Each item is a link that takes you straight to it.

If you're upgrading from an earlier version, nothing is changed: your documents and notes are exactly as they
were, and CaseKeeper starts in Simple mode with a note about what's new. From version 0.5.0 the website version can
update itself: see [Updating CaseKeeper](#25-updating-casekeeper).

## 2. Finding your way around

The sidebar groups the screens:

| Group | Screens |
|---|---|
| Overview | Dashboard · Case map · Case & hearings |
| Papers | Documents · Import review · Search · Parties |
| Track | Timeline · Directions & tasks · Contact log · Complaints · Costs\* · Scott schedule\* |
| Produce | Letters & templates · Bundles & reports · AI analysis\* · Exports\* |
| Help & system | Guide & FAQ · Help assistant · Audit trail\* · Settings |

\* Shown in Professional mode (AI analysis also appears once AI is switched on). Hidden screens keep their data and
can always be reached with **Ctrl+K**.

**Three ways to get anywhere fast**

- **Ctrl+K – the command palette.** Type part of anything – a screen, an action ("add hearing", "backup"), a
  document title, a category or form code ("C1A"), a party, a guide article or a question – and press Enter.
- **Right-click** almost anything for everything you can do with it.
- **F1** opens the guide at the help for the screen you're on.

Under the sidebar, the privacy status shows where your AI runs: **● Local only · nothing uploaded** (green) when no
cloud AI is chosen, or **● Cloud AI on · Anthropic** (or *OpenAI*, *Foundry*) in amber when the app or Bulk Case Import
uses one. Hover for what is sent and whether masking is on; click it to open *Settings → AI*.

### Case map

**Case map** (sidebar, under Overview) shows how your case is laid out:

- **Case overview** at the top – an AI-written summary of the whole case: who's involved, what has happened, where
  things stand, what's coming up and outstanding, the key issues and the gaps. It's written from each document's
  short summary and your records (never the documents' full text) and saved in *AI analysis → Reports*. Click
  **Write case overview** or **Refresh overview**. Without AI, the record counts are shown instead.
- **The matrix** – one row per category group (or per party, with *Rows*), one column per month, quarter or year
  (chosen from your dates, or by you with *Periods*), plus *Undated* and *Total*. Darker cells hold more documents.
  **Click any number** to see those documents in Documents. The *Uncategorised* row and *Undated* column show what
  still needs organising. The party box shows one party's documents only.
- **Case records** on the same time line: hearings, orders, complaints, Scott schedule items and deadlines. Hover
  for the list; click to open the screen.
- **People** – each party, with how many documents are linked to them and how many mention their name. Click to see
  their documents.

Everything except the overview is worked out on your PC.

## 3. Documents and categories

The Documents screen works like File Explorer.

**Importing.** Drag files or whole folders onto the window, or use *Import* (Ctrl+I). Drop them onto a category or
party in the tree to file them there. PDFs, photos and scans, Word (.docx) and text files are supported. Your
originals are never moved or changed – CaseKeeper keeps its own copy and records a SHA-256 fingerprint of each.

**Text recognition.** Every PDF and image is scanned with the OCR built into Windows so you can search the words
in scanned letters. Several documents are scanned at once on a powerful PC; one at a time on a laptop on battery.

**Categories.** About 470 built-in categories cover the forms and papers used in the family courts – the C-forms
and their supplements, FL401/403/407/415, FGM001, FL701, divorce (D8, D84, D36, D11, D81), finance (Form A, A1,
B, C, E, E1, E2, F, G, H, H1, P, P1, P2, ES1, ES2), appeals (FP161, FP162, N161), EX160, EX107, orders of every kind,
Cafcass and local authority documents, public law, adoption and SGO, police, health, education, experts, mediation,
legal aid and costs, international cases, identity documents, correspondence, evidence and exhibits, legal
authorities and your own working papers. Documents each side files separately come as an Applicant's/Respondent's
pair (shown as *Appl.* / *Resp.* tags).

**Finding a category.** Type in the box above the tree – any word or a form code ("C1A", "section 7", "police",
"bank"). The options button (⇅) sorts the tree **grouped A–Z**, as **one A–Z list by form code/name**, or **busiest
first**, and can hide empty categories. Everywhere you pick a category (import, edit, bulk edit) you can type to
search, and the list is grouped under headings.

**Smart folders** at the top of the tree: All documents, Starred, Recently added (14 days), Needs attention (no date,
no category or OCR failed), By party, By original folder (after a Bulk Case Import), and **Deleted items**.

**Right-click a document** to: open; open with another program; edit details; rename (F2); move to a category;
set the party; add or remove tags; star; give exhibit numbers (e.g. AB1, AB2… in date order); add to a bundle,
the timeline or a complaint; analyse with AI; share; save copies; copy the title, a reference or the fingerprint;
scan again; check it hasn't changed; see its full history; delete.

**Drag and drop** documents onto a category (move), a party (set party), *Starred*, or *Deleted items*.

**Columns.** Right-click a column heading to show Exhibit, Tags, From, Type, Size or Added. Drag headings to reorder.

**Deleted items.** Deleting moves a document here for 30 days (change it in *Settings → Safety & recovery*), then
it's removed for good. Right-click to restore or delete permanently.

## 4. Bulk Case Import and Import review

To organise documents that are **already in CaseKeeper**, use [Check my library](#check-my-library) below.

**Bulk Case Import** brings in a whole folder of case papers at once – every subfolder included – and can ask your
local AI to suggest how to organise it. Open it with **Import folder** on the Documents screen, **Import a whole
folder** on the Dashboard, **Ctrl+Shift+I**, or the command palette.

**Step 1 – Choose a folder.** Click *Choose folder…* and pick your options:

| Option | Default | What it does |
|---|---|---|
| Include all subfolders | On | Looks in every folder inside the one you chose. |
| Include hidden files / folders | Off | Hidden items are usually system clutter. |
| Follow shortcuts and linked folders | Off | Links can point outside the folder or back into it; loops are detected either way. |
| Skip temporary and system files | On | Thumbs.db, desktop.ini, Office lock files (~$…), part-downloads. |
| Keep each file's folder path | On | The path inside the chosen folder is kept with the document. |
| Folder names | Nothing | Or turn folder names into tags, or choose a category for each top-level folder (you'll see the mapping first). |
| Files already in your library | Skip and note the match | Or import them again, or decide one by one in the preview. |
| Read the text now | On | Untick to import quickly and read the text in the background. |
| Ask my local AI to suggest… | On if AI is set up | Categories, dates and records – all for you to approve. |

Click **Find files**. Nothing is imported yet.

**Step 2 – Check what was found.** A summary shows how many files were found, how many will be imported, the
duplicates (matched by contents – a SHA-256 fingerprint – never just by name), unsupported types and anything that
couldn't be read. Untick files, or a whole folder in the **FOLDERS** tree; filter with **Show** (Will be imported,
Duplicates, Left out, Unsupported, Problems, System files) and by type; search the list. **Copy summary** copies the
counts and paths inside the folder – never your full Windows path or anything from inside the files. Click **Import**.

**Step 3 – Import and read.** Files are copied into your library through the normal import (encrypted if your
library is protected, fingerprinted, and recorded in the audit trail), then their text is read, including scans and
photos. **Pause** or **Stop** at any time, or click **Keep working while it runs** – progress shows at the bottom of the
main window. If you close CaseKeeper, everything done so far is kept and the import carries on from where it
stopped: use **Carry on importing** on the Dashboard or **Carry on** in Import review.

**Your original files are never moved, renamed or changed**, and nothing is written into the folder you import from.
Imported documents wait in *Uncategorised* until you approve a category. The original file's own date is kept for
information only (shown under *ORIGINAL FOLDER* in the document's details) and is **never** used as the document's
date unless you choose it.

### How the AI suggestions work

- CaseKeeper first matches each document to its own category catalogue using form codes, titles and wording, then
  gives the AI (your local AI, or the cloud AI you chose – see [AI providers](#ai-providers-local-or-cloud)) a short
  list to choose from. The AI can only choose one of those categories, or *Unknown*.
- The AI must answer in a strict format (structured outputs, checked again by CaseKeeper). Every suggestion
  must quote the words it relies on and give the page. If the quote isn't on that page, or a date isn't actually
  written in the document, the suggestion is flagged and its confidence lowered; a date that isn't in the document is
  removed, never guessed. Names the document doesn't contain are dropped.
- Dates keep their meaning: the date of the document, a hearing *listed for* a date (which doesn't show it took
  place), a hearing adjourned or vacated, an alleged incident, a deadline. Dates written only as numbers are read the UK
  way (day first) and flagged. Allegations are recorded as what someone states, never as fact.
- Deadlines such as "within 14 days of this order" are never worked out by the AI. Use **Work out the date…**, which
  uses the deadline calculator with a start date and rule that you check.
- Long documents are read in parts (a few pages each) and the results combined, keeping every page reference.
- After the AI has read everything, CaseKeeper's own checks compare the documents: the same event in several
  documents, a hearing given different dates, a hearing later adjourned or vacated, different case numbers, names
  spelt differently, things already in your case, directions a later order may have replaced, hearings and deadlines
  coming up within two weeks, and long gaps in the chronology. Nothing is resolved for you.

**Confidence** is a number from 0 to 1 for how strongly the document supports a suggestion: **High** (0.85 and above
by default), **Medium** (0.6) or **Low**. High confidence means the document strongly matches, but you should still
confirm it. Low-confidence categories go to *Unknown / needs a category*. Change the levels in **Review options…**.

### Speed and your PC

Bulk analysis uses a fast model, **gemma3:4b**, by default. If it isn't installed, **Review options…** has a
**Download gemma3:4b** button (about 3 GB; only when you press it). Choose **Fast (recommended)** or **Careful** – a
larger model such as qwen3:14b, offered only when your graphics card can hold it.

CaseKeeper looks at your graphics card, memory and power source and recommends settings, shown as *This PC: …*:

| This PC | Recommended |
|---|---|
| No suitable graphics card and under 8 GB memory | AI unticked by default; CaseKeeper's own rules still suggest categories |
| A basic PC, or any laptop on battery or power saver | One document at a time, categories and document details only |
| Graphics card with about 6–10 GB | Two documents at once, full reading |
| Graphics card with 12 GB or more | Three at once; Careful available |
| Graphics card with 24 GB or more | Four at once; larger Careful models allowed |

**Use recommended settings** puts everything back. Models too large for your PC are marked *too large for this PC*.
Anything you choose yourself wins, with a warning first if your PC can't sensibly run it. Reading several documents
at once only helps if Ollama allows it: set the Windows environment variable `OLLAMA_NUM_PARALLEL` to 4 and restart
Ollama.

If your PC struggles – the AI runs out of memory, keeps taking too long, or the model doesn't fit on the graphics card
– CaseKeeper eases off by itself, one step at a time: fewer documents at once, smaller parts, a lighter installed
model, then a quick reading. It tells you what it changed, records it in the audit trail and carries on.

Photos with little writing on them (receipts, screenshots) get a quick reading – a category and a description. In a
quick reading, when a form code settles the category for certain (an S7 report, for example), CaseKeeper doesn't ask
the AI at all; the suggestion is marked *CaseKeeper rules* and still waits for your approval.

### Reading the import with a cloud AI

In **Review options… → AI SERVICE** you can choose which AI reads the import: the one set in *Settings → AI*, the
local AI, or a cloud service (Anthropic, OpenAI, or either on Microsoft Foundry), with its model and reasoning effort.
Cloud models read large folders much faster than most PCs; the default is the quick, low-cost model at low effort.

Before anything is sent, the **Before anything is sent** screen shows:

- **where it goes** – the service and its address (always HTTPS), and what the service's terms say about keeping it;
- how many documents, and roughly how much text;
- **what will be masked** in this batch – names, e-mail addresses, phone numbers and so on, with counts;
- **sensitive subjects** found in the batch, such as children, health, money or police;
- **documents that couldn't be masked well** (for example a badly scanned page, or many names CaseKeeper didn't
  recognise). For these, choose: **read them with the local AI on this PC instead**, **skip them for now** (they
  wait in Import review as *not sent*), or **send them anyway**.

Choose **Send to …** to go ahead, or **Cancel**: then nothing is sent and the documents wait as *Waiting – AI not
available*. Your choice is recorded in the audit trail with the service, address and whether masking was on – never
any content. **Don't show this again for …** skips the screen for that service; *Settings → AI → Ask again before
sending* brings it back. More in [What's sent to a cloud AI, and masking](#whats-sent-to-a-cloud-ai-and-masking).

### Duplicates and versions

**Import review → Duplicates & versions** finds copies and versions of the same document among *all* your
documents, by comparing the words inside them – not just the files. So it finds:

- **Exact copies** – the same file twice;
- **Same content** – for example a PDF and a Word copy of one letter;
- **Versions** – a draft and its final version, or a statement with later edits.

Each group shows the **newest first**, with the reason, and for each older one what the newest added or removed.
The latest edit is decided by, in order: the date written in the document; when the file says it was **last saved**
(stored inside PDF and Word files); the Word **revision** number; when the file was last changed; then length. Each
copy has an **Edit history** line – *dated · last saved · revision · file changed · added to CaseKeeper*. Documents
imported before 0.7.0 get their saved dates when you run [Check my library](#check-my-library).

**What changed** opens a sentence-by-sentence comparison (added text in green, removed text struck through in red);
**Side by side** shows the newest and another copy in two columns.

Then choose **Keep newest, tag the others as older** (tags *Latest version* and *Older version*), **Keep all, link as
versions** (tag *Version*), **Not related**, or **Move older to Deleted items…** – after you confirm, the older copies
go to *Deleted items*, where they can be restored for as long as Deleted items keeps them (Settings). Every choice is
in the audit trail, and **Undo** reverses it. Very short documents (a few words) aren't compared.

### Check my library

**Check my library** runs the documents already in CaseKeeper through the same reading, organising and checking as
Bulk Case Import – with AI, or with CaseKeeper's own rules only. Open it with **Check library** on the Documents
screen, **Check my library…** in Import review or on the Case map, or **Ctrl+K** → *check library*.

| Choice | What it does |
|---|---|
| Which documents | All documents, only those not yet categorised, or only those not organised before |
| Use AI | Your local AI, or the cloud AI chosen in *Settings → AI* (with the usual review before anything is sent, and masking). Off: CaseKeeper's own rules still suggest categories and complaint records |
| Read the text of documents that don't have it yet | On by default. Off leaves those documents out |
| What to fill in | Categories · Parties & children · Hearings, orders & directions · Timeline & chronology · Actions & deadlines · Contact log · Complaints · Scott schedule · Safeguarding · Missing documents & issues. Remembered for next time |
| Write a case overview when it finishes | Uses AI; the overview appears on the Case map and in *AI analysis → Reports* |

Click **Start check**. CaseKeeper first records the dates saved inside each file (dates and revision numbers only,
never names), reads any missing text, suggests what each document tells you for the sections you chose, cross-checks
everything, and compares the whole library for copies and versions. **Import review** opens with a **Library check**
card at the top of *Summary*: what's waiting, each line a link, plus *Open the Case map* and *Write case overview*.
**Nothing changes until you approve it.**

- **Parties** already in *Parties* aren't added twice: the suggestion fills only their empty fields
  (representative, date of birth, and contact details for professionals and organisations – never for a child).
- **Allegations** become *Scott schedule* suggestions: who alleges it, what is alleged, when, the finding sought and
  the document and page relied on – always recorded as what someone says, never as fact.
- **Complaints** – see [Complaints](#11-complaints).

### Treat with care

Whichever AI you use, CaseKeeper's own checks add a **Treat with care** note under *Conflicts & checks* for documents
that deserve a second look: Office files that can contain macros (`.docm`, `.xlsm`…), scanned pages whose text came
out garbled, and a date written in a document that is later than the file itself was last changed.

### Import review

**Import review** (sidebar, under Papers) holds every suggestion until you decide. Sections on the left group them:
Summary, Duplicates & versions, Categorise documents, Unknown / needs a category, Document details, Hearings, Events & timeline, Orders, Directions &
deadlines, Tasks & missing documents, Contact, Parties & children, Complaints, Conflicts & checks, Low confidence,
Failed or not analysed, and the decided ones (Approved, Applied by AI rule, Deferred, Rejected). Filter by confidence,
document, dates or warnings, or search.

Select a suggestion to see its **SOURCE** (the document, page and quoted words – **Open at page** shows it), the
date as written and what it means, who says so, the confidence and why it was suggested, any **CHECK THIS** warnings,
and other documents to **COMPARE WITH** when there's a conflict. Then:

- **Approve** (Ctrl+Enter) adds it using the same form and rules as adding it by hand. If something similar is
  already in your case, you're warned first.
- **Edit & approve** (F2) opens that form, filled in from the document, so you can correct anything first.
- **Reject** (Delete) or **Defer**. **Reconsider** brings a rejected or deferred one back.
- **Undo** removes what an approval added (for example a hearing and its timeline entry) or puts a document's old
  category back, and returns the suggestion to the list.
- **Approve selected categories** files several documents at once (medium or high confidence only).
- **Re-run AI on this document** and **Mark document reviewed** (for documents with no readable text).

Every approval, rejection and undo is in the audit trail, marked as suggested by AI and approved by you, with the
model and prompt version. A rejected suggestion keeps no quoted text.

**Automatic filing (advanced, off by default).** In *Review options…* you can let CaseKeeper file a document under
the AI's category when it is very confident (0.95 by default). It only ever changes the category of a document still
in Uncategorised; it never adds hearings, dates, deadlines or any other record. Each one is audited, listed under
*Applied by AI rule*, and can be undone.

**If the AI isn't available,** the import still finishes, documents show *Waiting – AI not available*, and
CaseKeeper's own rules still suggest categories from form codes. Start Ollama (or, for a cloud AI, check you're
online and the key in *Settings → AI*), then use **Analyse N waiting** next to
the import in *Import review → Summary*. **Retry N failed** and **Retry selected** (under *Failed or not analysed*)
retry analyses that failed. A document is only analysed again if its text, the prompt, the model or the format has
changed – or you ask.

**Already in your library?** Right-click documents → *Analyse with AI* → **Organise – suggest a category, dates and
records for review…** sends them through the same analysis, with suggestions in Import review.

**Privacy.** With the local AI everything happens on your computer; the AI only talks to your own Ollama server.
With a cloud AI, the text of the documents being analysed goes to the service you chose – masked by default, and only
after you agree on the *Before anything is sent* screen. Import records and suggestions are stored in your library (encrypted if it's protected) and included in backups. Logs never contain
document text, file names or folder paths. The AI is an assistant, not an authority: it can be wrong, and it does not
give legal advice. The instructions it follows can be edited in *Settings → AI → Prompts → Bulk Case Import*.

## 5. Search

*Search* (Ctrl+F) looks through titles and details, your notes and page notes, and the text inside every page.
Put words in "double quotes" for an exact phrase. Filter by category and date. Double-click a result to open the
document at that page. Right-click to show it in Documents, add it to a bundle, copy the snippet or export results.

## 6. Parties

Add the Applicant(s), Respondent(s), each child (with date of birth, so their age is always right), Cafcass,
the Children's Guardian, the local authority and anyone else. Tick **This is me** on yourself – it fills your name
into letters and statements. Right-click a party card to see their documents, edit, write to them, copy their
contact details or see their history.

> If an address is confidential (for example on a C8), think carefully before recording it.

## 7. Case details, hearings and orders

*Case & hearings* has three tabs.

- **Case details** – title, number, court, type, stage, judge, issue date, MIAM status, references and a case
  summary. They fill in templates, bundle covers and AI background automatically. Everything saves as you type.
- **Hearings** – date, time, type (FHDRA, DRA, fact-finding, final hearing, FDA, FDR, IRH…), court, judge,
  attendance (in person, video, phone), time estimate, status and outcome. Each hearing is added to your timeline
  and the dashboard shows a countdown. Right-click a hearing to **record the order made**, **add hearing-preparation
  tasks** (a checklist due at sensible times beforehand, skipping weekends and bank holidays) or mark its status.
- **Orders** – a register of every order: type, date, judge, interim or final, penal notice, expiry and a summary.
  Right-click an order to **add a direction from it**.

## 8. Timeline

Record hearings, deadlines, incidents, contact and communications as they happen. Each entry can have a time,
type, details, where it happened, who else was there, and a police/incident or other reference, and can link to a
document. Entries can be left out of the chronology. Right-click to edit, duplicate, include/exclude, export the
chronology as a PDF, export the whole timeline as a table, or see an entry's history.

## 9. Directions, tasks and deadlines

Everything that has to be done, by whom and by when: **court directions**, **your own tasks**, the **other
party's tasks** and **reminders**. Filter by still to do, overdue, due within a week, done, or kind. Overdue items
turn red and appear on the dashboard. Right-click to mark done, set the status or postpone.

**Deadline calculator.** Works out dates using England & Wales bank holidays (including one-off ones) and the
Family Procedure Rules' approach to counting: "clear days" exclude the first and last day; periods of five days or
less skip weekends and bank holidays; a deadline on a closed day moves to the next working day. Presets include
appeals (21 days), subject access requests (one month), complaint replies (20 working days) and "N clear days
before a hearing". Add the result as a task in one click. Always check your order's wording.

## 10. Contact log

Log each time contact was due: date, time, children, type (direct, overnight, handover, supervised, video, phone…),
place, what happened (took place, late, cut short, cancelled by whom, not attended, child not brought), minutes late,
length, supervised, concerns and factual notes. The summary line shows how many sessions took place and who
cancelled. Export it as a table – the summary is included at the top.

## 11. Complaints

Keep track of complaints and letters to your MP, Cafcass, the ombudsmen, HMCTS, regulators and others, with
references and reply-due dates. Overdue replies are flagged. Right-click to set the status, **write a letter for this
complaint** (starts the right template), build a complaint pack, or see its history.

**Filed and filled in for you.** Bulk Case Import and Check my library recognise complaint letters and replies, with
or without AI: complaint wording plus an organisation CaseKeeper knows (Cafcass, HMCTS, the police, a council, an MP,
the ombudsmen, the JCIO, the SRA, Social Work England and others). Each suggestion in *Import review → Complaints*
gives the organisation, any reference, the date, and a reply deadline **only when the letter states one**. "Within 20
working days" is worked out from the letter's date, skipping weekends, and marked for you to check (bank holidays
aren't known). Approving a complaint also files its document under the matching complaint category (for example
*Cafcass complaint*) if it is still Uncategorised.

**Replies update the complaint.** A reply to a complaint already here (matched by reference or subject) is added to it
instead of creating a second one: the reply is linked, the status becomes *Response Received*, its summary is added to
*Outcome*, and only empty fields (reference, recipient, dates) are filled – nothing already written is changed. You see
exactly what will change before you approve, and **Undo** restores the earlier values.

## 12. Costs and the Scott schedule

**Costs & expenses** (Professional mode): date, category, description, amount, who was paid, how, reference, Help
with Fees, and whether you might claim it back. Totals by category, and exports with a category breakdown.

**Scott schedule** (Professional mode): numbered allegations with date (or approximate date), who alleges it, the
allegation, the response, evidence references (bundle pages), finding sought and status. Renumber in one click and
export landscape for the court. Only prepare one if the court directs it. Check my library (with *Scott schedule*
ticked) suggests items from allegations in your documents, for you to approve in *Import review → Scott schedule*.

## 13. Letters, templates and letterheads

**Letters & documents.** Click **New…** and choose a template or a blank letter. Fill in who it's to, the date,
references, greeting and sign-off, and write the text. The **preview** on the right updates as you type. Tick
*Formal document layout* for statements and position statements (no addresses or sign-off).

Formatting toolbar and shortcuts: **bold** (Ctrl+B), *italic* (Ctrl+I), underline (Ctrl+U), headings, bullets,
numbered statement paragraphs, right-align, tables, page breaks, and **Insert field** for details that fill
themselves in. You can also insert exhibit references and document titles.

| Formatting | Write |
|---|---|
| Heading / subheading | `# Heading` / `## Subheading` |
| Bold, italic, underline | `**bold**`, `*italic*`, `__underline__` |
| Bullet / sub-bullet | `- point` / two spaces then `- point` |
| Numbered list | `1. item` |
| Statement paragraphs that keep counting | `#. paragraph` |
| Quotation | `> quoted text` |
| Align a line | `[right] text`, `[centre] text` |
| Line across the page / page break | `---` / `[page break]` |
| Table | `| A | B |` then `|---|---|` then rows |
| A line to sign on | `Signed: ________` |

**Fields** include `{{today}}`, `{{case.number}}`, `{{case.court}}`, `{{case.children}}`, `{{case.nexthearing}}`,
`{{me.name}}`, `{{me.role}}`, `{{applicant.name}}`, `{{respondent.name}}`, `{{otherparty.name}}`,
`{{cafcass.name}}`, `{{recipient.name}}`, `{{recipient.address}}`, `{{letter.subject}}`, `{{sender.name}}` and more
(see *Insert field*). A field with no value is left visible and you're told which ones.

**Exporting.** *Export…* saves as **PDF, Word (.docx) or a web page (.html)**. *Print…* prints directly.
*Save to library* adds a PDF to your documents. Exported files never contain your Windows name, author details or
editing history. *Improve with AI* (if switched on) suggests clearer wording for the selection or the whole text.

**Built-in templates:** general letter, letter to the court, witness statement (court heading, numbered paragraphs,
statement of truth), position statement, case summary, letter to Cafcass, subject access request (UK GDPR),
formal complaint, letter to your MP (with a reminder about PD12G), request for a transcript (with EX107),
request to adjourn, without-prejudice proposal and chasing a court direction. Edit them freely; *Restore original*
puts a built-in one back.

**Your own templates.** *Templates → New*, or **Import Word…** to use a .docx or .dotx:

- **Keep its Word layout** – the exact design is used for Word exports (and for PDFs when Microsoft Word or
  LibreOffice is installed). Put `{{fields}}` anywhere in the Word file, including headers and footers – even if
  Word splits them up internally – and `{{body}}` on its own line where the letter text should go.
- **Convert to editable text** – the content becomes a CaseKeeper template you can edit here.

Imported Word files have author names, comments and similar details removed before they're stored.

**Letterheads.** Name and contact details, an address (leave out anything confidential), a footer, an accent colour,
a font, a logo (re-saved as PNG so photo location and camera details are stripped) and one of three styles:
*Classic*, *Band* or *Minimal*. Or attach your own **Word letterhead** to be used as the base for Word exports.
Mark one as the default for new letters.

## 14. Bundles and reports

*Bundles & reports* combines documents into one PDF with a cover, an index, sections A–F, page numbers like B12
and bookmarks, following the general shape of Practice Direction 27A – or a **summary report** of your chronology,
documents and notes without the full documents. Right-click the bundle list to move items between sections,
reorder or remove them. Always follow the court's own directions about bundles.

## 15. AI analysis

AI is **off** until you switch it on. CaseKeeper can use:

- **Ollama**, a free program that runs AI models on your own computer (or another computer on your network), so your
  documents don't go to an online service; or
- a **cloud AI** you choose: **Anthropic (Claude)**, **OpenAI (GPT)**, **Claude on Microsoft Foundry** or **OpenAI on
  Microsoft Foundry** (models deployed in your own Azure subscription). You need your own account and API key with
  that service, and it charges you for what you use.

### AI providers: local or cloud

Choose in *Settings → AI → Which AI to use*. Bulk import can use a different one (*Import review → Review options… →
AI SERVICE*).

**Local AI (Ollama) – one click**

1. Choose **Local AI on this PC (Ollama)**.
2. Click **Install Ollama** (the website version installs it with Windows Package Manager, after asking) or **Get
   Ollama** (the Microsoft Store version opens its download page, because Store apps don't run installers).
3. Click **Set up the local AI**: CaseKeeper finds Ollama, downloads the quick model **gemma3:4b** (about 3 GB) if no
   model is installed, chooses it and switches AI on.

Or by hand: download a model (e.g. `ollama pull llama3.1`), tick **Use AI features**, check the address (usually
`http://localhost:11434`), press **Test connection** and choose the model. Larger models give better results but need
more memory and are slower.

**Cloud AI – one click**

1. Get an API key: Anthropic (platform.claude.com), OpenAI (platform.openai.com), or, for Microsoft Foundry
   (ai.azure.com), the resource's key and a deployed model. **Get a key** / **Open Microsoft Foundry** opens the page.
2. Choose the service, paste the key – for Foundry also the **Foundry resource** (its name, or the endpoint address) –
   and click **Connect**. CaseKeeper checks the key, lists the models, picks the recommended one and switches AI on.
3. Change **Model** (for Foundry, the **Deployment name**) and **Reasoning effort** if you want. **Use recommended**
   puts back the defaults.

| Service | Recommended (quick, low cost) | Also offered | Reasoning effort |
|---|---|---|---|
| Anthropic / Claude on Foundry | Claude Haiku 5.5, low | Claude Sonnet 5.5, Claude Opus 5.5 | low, medium, high, max |
| OpenAI / OpenAI on Foundry | GPT-6 Luna, low | GPT-6.1 Terra, GPT-6.1 Sol, GPT-6 Astra | none, low, medium, high, xhigh, max |

Higher effort means the model thinks for longer: more careful, slower and more expensive. Low is plenty for sorting
and reading documents.

**Other cloud settings:** **Mask names and personal details before sending (recommended)**, on by default; **Documents
read at once** (CaseKeeper slows down by itself if the service says it's busy); **Ask again before sending** (brings
back the *Before anything is sent* screen); **Remove key**.

**Your key** is stored encrypted for your Windows account (Windows DPAPI). It's never shown again, never written to
settings files, exports, backups, logs or the audit trail, and is only sent to the service it belongs to.

### What's sent to a cloud AI, and masking

- **What's sent:** only the text of the documents or records you ask it to analyse (or your help-assistant question),
  plus CaseKeeper's instructions, to the one service you chose. Nothing is sent with the local AI.
- **Only to that service:** CaseKeeper refuses any other address (only `api.anthropic.com`, `api.openai.com`, and your
  own Foundry resource on `*.services.ai.azure.com`, `*.openai.azure.com` or `*.cognitiveservices.azure.com`), always
  uses HTTPS, never follows a redirect, and ignores AI settings left by other programs (environment variables).
- **Masking (on by default):** names of your parties, representatives and contacts, other names CaseKeeper
  recognises, e-mail addresses, phone numbers, postcodes, National Insurance numbers, case numbers, bank account,
  sort-code and card numbers, dates of birth and addresses are replaced with tags such as `[PERSON-1]` or
  `[POSTCODE-2]` before sending. The AI is told to use the tags; its answer is put back with the real details on your
  PC before CaseKeeper checks and saves it. The list linking tags to real details is kept in memory only, never sent
  and never saved.
- **Masking isn't perfect.** An unusual name, a nickname, or a badly scanned page can slip through, and the AI
  still sees what the document is about. That's why bulk import lists *documents that couldn't be masked well* and
  lets you read those locally or skip them.
- **Consent:** the first time you use a cloud service, CaseKeeper asks you to agree, and bulk import shows the
  *Before anything is sent* screen each time (unless you turn it off). Each agreement is in the audit trail.
- **The service's terms apply.** Anthropic and OpenAI may keep what they receive for a limited time under their
  terms; Microsoft Foundry processes it in your own Azure subscription. Check the terms – and whether you're allowed
  to share the papers – before sending anything. Family court papers are confidential (FPR r.12.73, PD12G).
- **Maximum security** (*Settings → Security*) switches any cloud AI off and back to the local AI.

**Running a report.** On *AI analysis*:

1. **What to look at** – the whole case, documents you tick, one category, one party's documents, a date range,
   the timeline, or your records (hearings, orders, directions, contact log, Scott schedule). Choose whether to
   include document text, your notes, the timeline and records.
2. **What kind of report** – summary, chronology, key issues & allegations, inconsistencies, welfare checklist
   (s.1(3) Children Act 1989), the children's experience, safeguarding concerns, questions to consider, a position
   statement outline, compliance with orders – or your own question. Add extra instructions if you like.
3. **Granular** mode reads each part separately and then combines the findings – best for large sets, and it keeps
   the per-part detail at the end of the report.

The report streams in as it's written; press **Stop** at any time. **Save report** keeps it in the history; you can
export it (PDF, Word, web page) or add it to your library. You can also start an analysis by right-clicking
documents or a category and choosing *Analyse with AI*.

**Prompts.** Everything the AI is told is editable in *Settings → AI → Prompts*: the system prompt (applies to
everything), the instructions for each report type, granular reading and combining, wording improvements and the
guide. *Reset* puts any prompt back.

**Hardware.** Choose GPU use (let Ollama decide / GPU as much as possible / CPU only), CPU threads, how much the
model reads at once (context size), the longest answer, creativity, how long the model stays loaded, and a timeout.
The performance profile picks sensible thread counts automatically.

**Privacy.** With the local AI, only the address you set is contacted. A server outside your home network must be
allowed explicitly and must use HTTPS. Any API key is stored encrypted for your Windows account and is never included
in settings files. Each AI request is recorded in the audit trail (what kind, which service and model, how much
text) – the content itself is not logged. **AI can be wrong: always check its output against the documents.**

## 16. Guide & FAQ

A searchable, plain-English guide to family proceedings in England & Wales – how children cases work (MIAM, C100,
safeguarding, FHDRA, section 7 reports, DRA, final hearing), the welfare checklist, orders, Cafcass, domestic abuse
and PD12J, protective orders, special measures, hearings, privacy and sharing papers, statements, bundles,
enforcement, varying orders, appeals, divorce, financial remedies, care proceedings, fees, legal aid, where to get
help, frequently asked questions and a glossary – plus how to use every part of CaseKeeper.

Type a word, a form number or a question in the search box. With AI switched on:

- **Explain simply** rewrites the article in plainer words.
- **How does this apply to me?** relates the article to your case details (on your own AI server only).
- **Ask the AI** answers your question **from the guide's own articles**, so it isn't guessing.

### The help assistant

Not sure how to do something? Open **Help assistant** (sidebar, under Help & system) and ask in your own words –
for example *"How do I put my case number on every letter?"* or *"Where do I record a missed handover?"*. You get
numbered steps with the exact names of the buttons and menus, and **buttons that take you straight there**.

- It uses your local AI and is given a complete map of CaseKeeper's screens, buttons and menus plus the relevant
  parts of this guide, so its directions are for things that really exist.
- **Only your question is sent** to your AI, with a few **counts** about your library (how many documents, how
  many are uncategorised, how many suggestions are waiting, whether a library check has run) – never a title, name or
  anything from your documents. The counts let it suggest your next organising step, with a button.
- It never changes anything itself: every change happens in the normal screens and through Import review.
- Ask follow-ups ("and how do I export it?"); *New conversation* starts again. Suggested questions get you started.
- Without AI, it still shows the matching help sections and screen buttons.
- From anywhere: press **Ctrl+K**, type your question and choose *Ask the help assistant*.
- Its instructions are editable in *Settings → AI → Prompts → Help assistant*.

## 17. Exports

*Exports* (Professional mode) lists everything saved into the library's Exports folder – bundles, letters, reports.
Open, share, save a readable copy elsewhere, add to your documents, rename (F2), show in folder or delete. In a
password-protected library these files are encrypted too, and open through a temporary copy that is wiped when
CaseKeeper closes.

## 18. Audit trail and logs

**Audit trail** (Professional mode). Every change and significant action – imports, every edit with its old and new
values, notes, hearings, orders, directions, letters, exports, shares, prints, backups, restores, sign-ins, locks
and AI requests – is recorded with the date and time. Filter by kind, date or text; click an entry to see exactly
what changed; right-click to see the history of one item. Right-click *History…* on documents, parties, records and
letters elsewhere in the app to jump straight here.

Each entry carries a SHA-256 fingerprint that includes the previous entry's fingerprint, so altering or deleting any
past entry breaks the chain. **Verify integrity** recalculates the whole chain. Export the trail as PDF, Word, a web
page or a spreadsheet – the verification result is included.

**Document integrity** (*Settings → Safety & recovery*, or right-click documents) re-reads every document and
confirms it is byte-for-byte what was imported.

**Logs** (*Settings → Logs & diagnostics*) record technical events and errors for troubleshooting, kept for 30 days
by default. They never contain your documents, notes or case details, and your Windows user name is removed. Any
error shown on screen has a reference such as **E-7F3A2C** that matches the log entry. *Copy diagnostic summary*
copies your set-up (no personal or case information) for a support request.

## 19. Security and privacy

- **Nothing is uploaded unless you choose a cloud AI.** CaseKeeper only uses the network for the optional AI – your
  own Ollama server, or the cloud AI you set up, which gets masked text only after you agree (see
  [What's sent to a cloud AI](#whats-sent-to-a-cloud-ai-and-masking)) – and for *Check for updates* (website
  version), which only downloads, and only when you click it or switch on automatic checks. See [Updating CaseKeeper](#25-updating-casekeeper).
- **Password protection** (*Settings → Password protection*) encrypts the database, every document, the daily
  snapshots and anything saved into the Exports folder, with AES-256. CaseKeeper asks for the password when it opens.
  **There is no recovery if the password is lost** – keep it safe, and keep backups.
- **Auto-lock** hides every window behind the password prompt after a period without use, whenever Windows is
  locked, or on **Ctrl+L**.
- **Maximum security** (*Settings → Security*) turns on the strictest options in one click: lock after 5 minutes and
  with Windows, passwords on every backup, AI on your own computer or network only (any cloud AI is switched off), reminders
  before sharing, and 14-day logs.
- Decrypted copies opened in other programs or shared are wiped when CaseKeeper closes (and at the next start, after
  a crash).
- Exported Word, PDF and HTML files contain no author names or Windows user details. HTML exports contain no scripts
  or external links.
- The audit trail records which PC made a change as a short code (e.g. PC-3A9F1C), or a name you choose – never your
  computer's real name or your Windows account.
- Before anything is shared or exported you're reminded about the confidentiality of family proceedings (FPR r.12.73
  and PD12G). You can turn the reminder off.

## 20. Backups, snapshots and moving PCs

- **Create backup** (*Settings → Backup & transfer*) writes one .zip of **everything**: every document and the whole
  database – notes, timeline, parties, complaints, hearings, orders, directions, contact log, costs, Scott schedule,
  letters, templates, letterheads, AI reports, bulk imports with their AI suggestions and your decisions, all settings
  and prompts, and the audit trail. Add a password to
  encrypt it with AES-256 (required in Maximum security).
- **Import from backup** either adds it to what's here – documents recognised by fingerprint, records and settings
  you don't already have – safe to repeat; or **replaces everything** (your current library is set aside first).
  Adding a backup brings its documents (with their original folder paths) but not its pending AI suggestions, which
  belong to the library they were made in; replacing everything restores those too.
- **Snapshots.** A restore point of the database is saved automatically once a day (encrypted when the library is),
  and the last 7 are kept. *Settings → Safety & recovery* lets you take one now or restore one.
- **Settings & templates file** (*Settings → Settings, templates and other cases*) moves just your set-up – letterheads,
  templates, AI prompts and preferences – to another PC or case, optionally password protected. It never contains case
  details, documents or your AI key.
- **Several cases.** Keep each case in its own library and switch between them from Settings or with Ctrl+K.
- **Moving the library** to another drive or a network share is under *Settings → Your library*.

## 21. Performance

*Settings → Performance* chooses how hard CaseKeeper works your computer:

| Mode | Documents scanned at once | OCR quality | AI threads |
|---|---|---|---|
| Automatic (default) | Power saver on battery; Maximum on a big desktop; otherwise Balanced | | |
| Power saver | 1, at low priority | lighter | a quarter of the cores |
| Balanced | 1–3 | standard | half the cores |
| Maximum | up to 8 | highest | chosen by Ollama |

## 22. Keyboard shortcuts

| Keys | Does |
|---|---|
| Ctrl+K | Command palette – find any screen, action, document, record or guide article |
| Ctrl+F | Search everything |
| Ctrl+I | Import documents |
| Ctrl+Shift+I | Bulk Case Import – a whole folder |
| Ctrl+Enter / F2 / Delete | In Import review: approve / edit & approve / reject the selected suggestion |
| Ctrl+N | Add a new item to the list you're on |
| Ctrl+L | Lock (password-protected libraries) |
| F1 | Guide for the current screen |
| Enter | Open / edit the selected item |
| F2 | Rename the selected document or export |
| Delete | Delete (documents go to Deleted items) |
| Ctrl+D | Duplicate the selected record |
| Ctrl+C | Copy the selected rows |
| Ctrl+E | Edit document details |
| Ctrl+B | Add selected documents to the bundle (in Documents) / bold (in the letter editor) |
| Ctrl+Shift+S | Star or unstar documents |
| Ctrl+I / Ctrl+U | Italic / underline in the letter editor |

## 23. Troubleshooting

| Problem | What to do |
|---|---|
| "Something went wrong" with a reference (E-…) | Your data is safe. Note the reference; the details are in *Settings → Logs*. |
| Text in scanned documents isn't searchable | Install an English language pack with OCR (Windows Settings → Time & language → Language & region), then *Settings → Text recognition → Re-scan*. |
| AI: "Couldn't connect" | Make sure Ollama is running, the address is right, and a model is installed (`ollama list`). Press *Test connection*. |
| Cloud AI: "key rejected" / "isn't allowed" | Check the key was copied in full and is still active with the service (and, for Foundry, that it belongs to that resource). Paste it again and click *Connect*. |
| Cloud AI: "model / deployment not found" | Choose another model, or for Foundry type the exact *deployment name* you created in Microsoft Foundry. |
| Cloud AI: "busy" or rate-limited | CaseKeeper retries and reads fewer documents at once by itself. If it keeps happening, lower *Documents read at once* or check your account's limits. |
| Cloud AI: documents "not sent" in Import review | You chose to skip documents that couldn't be masked well. Use *Retry selected* with the local AI, or send them on the next run. |
| AI is very slow | Use a smaller model, allow GPU use, or reduce the context size. |
| Bulk import: documents "Waiting – AI not available" | Start Ollama and check *Settings → AI → Test connection* (for a cloud AI: check you're online and click *Connect*; or you chose *Cancel* on *Before anything is sent*), then *Import review → Summary → Analyse N waiting*. |
| Bulk import: "Analysis failed" (AI_TIMEOUT, AI_INVALID_JSON…) | Try a smaller context size or a different model, then *Retry*. The import itself is complete. |
| Bulk import stopped part-way | Nothing is lost. Click *Carry on importing* on the Dashboard or *Carry on* in Import review. |
| A folder "couldn't be opened" in the preview | Windows doesn't let your account read it. Copy its files somewhere you can read, then import that. |
| PDF of a Word-layout letter doesn't match the Word design | Install Microsoft Word or LibreOffice; or export as Word. |
| The library can't be reached at start-up | Connect the drive or NAS, then *Try again* – or choose another folder. |
| "The folder's location is too long" | Choose a folder closer to the top of the drive, e.g. `C:\CaseKeeper`. |
| Something looks wrong after a change | *Settings → Safety & recovery → Restore* a snapshot, or import a backup. |
| *Check for updates*: "Couldn't reach the update server" | Check you're online, then try again. Nothing was changed. If you use your own update address, check it in *Settings → Updates*. |
| *Check for updates*: "…signature doesn't match…" or "…no signature…" | The update information wasn't signed by CaseKeeper's release keys, so nothing is offered. That is the protection working: don't install anything from that source by hand. Try again later, or download from darkciphersystems.com and check the SHA-256. |
| An update "didn't complete" | The setup program was closed or Windows permission wasn't given. Your library is unchanged. Run *Check for updates* again, or download the installer from the website. |
| CaseKeeper won't open a document and shows it in File Explorer instead | It's a type of file Windows would run as a program (for example `.exe`, `.js`, `.lnk`), so CaseKeeper won't start it. Check where it came from before opening it yourself. |
| Text looks blurry, or too big or small, on one monitor | From 0.6.1 CaseKeeper redraws itself at each monitor's scaling. If it still looks wrong after you change *Windows Settings → System → Display → Scale*, close CaseKeeper and open it again. |

## 24. Where your data is kept, and removing it

Your case library is an ordinary folder on your PC:

| Installed from | Library | Settings and logs |
|---|---|---|
| The website installer | `%LOCALAPPDATA%\CaseKeeper` | `%APPDATA%\CaseKeeper` and `%LOCALAPPDATA%\CaseKeeper\Logs` (a downloaded update waits in `%TEMP%\CaseKeeper-Update` until it's installed) |
| The Microsoft Store | `%USERPROFILE%\CaseKeeper` (a CaseKeeper folder inside your own user folder) | CaseKeeper's own app folder, which Windows manages |

If you moved the library or keep other cases, *Settings → Your library* and *Settings → Settings, templates and other
cases* show where each one is. *Settings → About → Open library folder* opens it.

**Uninstalling keeps your library.** That's deliberate: reinstalling, or moving between the website and Store
versions, picks up where you left off, and an uninstall can never destroy evidence by accident.

**To remove everything:**

- *Settings → Factory reset → Remove all CaseKeeper data…* deletes every case library on this PC that CaseKeeper
  knows about, its settings, logs and temporary copies, then closes CaseKeeper. It lists exactly what will be deleted,
  offers a backup first, and asks you to type DELETE. Libraries on network drives are left alone. Only CaseKeeper's own
  files are removed from each folder, so anything else you keep there stays.
- **Website version:** the uninstaller also asks whether to delete your library. Say *Yes* and the same
  confirmation appears; say *No* (the default) to keep it.
- **Store version:** use the Settings button above first, then *Start → right-click CaseKeeper → Uninstall*. Windows
  removes the app, its settings and its logs.
- **By hand:** close CaseKeeper and delete the library folder from the table above.

Backups you saved elsewhere, and the original files you imported from, are never touched.

**Moving from the website version to the Store version:** the first time the Store version opens, it offers to copy
the website version's library across. The original is left as it was. You can also bring a library across with a
backup (*Settings → Backup & transfer*).

## 25. Updating CaseKeeper

**Microsoft Store version:** the Store keeps it up to date. To update straight away, open the Microsoft Store →
*Library* → *Get updates*. *Settings → Updates* just says so.

**Website version:** *Settings → Updates*.

1. Click **Check for updates**. CaseKeeper downloads a small file (`update.json`) that says which version is newest,
   with two signatures, and tells you whether there's something newer than the version you have.
2. If there is, you see **What's new** and the setup program's name, size and SHA-256. Click **Download and
   verify**. It downloads into a private temporary folder and must match the signed size and SHA-256 exactly, be a
   Windows program, and say it is CaseKeeper by DarkCipher Systems at exactly that version. Anything else is
   deleted and nothing is offered.
3. Click **Install… (backup first)**. CaseKeeper checks the file again, offers to make a backup (recommended; a
   release that changes how the library is stored says so), takes a safety snapshot of the database, closes, and
   starts the normal setup wizard. Windows may ask for permission. Your library, settings and password protection
   aren't touched.
4. When CaseKeeper opens again it tells you whether the update worked ("Update verified: CaseKeeper 0.5.1 is now
   running") or didn't. Both are recorded in the audit trail.

**Why you can trust it.** An update is only offered when its information is signed by **both** CaseKeeper release
keys built into your copy: a classical ECDSA P-256 key and a post-quantum ML-DSA-65 key. The private keys never
leave the release PC, so the website, GitHub or anything in between only carries the files: if any of it were
changed or hijacked, the signatures wouldn't match and CaseKeeper would offer nothing. An older version is never
offered as new, so an old release can't be passed off as an update.

**What a check sends.** A plain download request with a generic name (`DarkCipher-Updater`): no cookies, no
identifiers, nothing about you, this PC or your case. CaseKeeper only checks when you click, or on the schedule
below.

**Where it looks** (*Updates from*):

| Choice | What it uses |
|---|---|
| The DarkCipher Systems website (recommended) | `darkciphersystems.com/downloads/casekeeper/` |
| GitHub releases | The latest release of `DarkCipher0x/CaseKeeper` on GitHub. While that repository is private, add a
  fine-grained access token with read-only *Contents* access to it alone: it is encrypted with your Windows account,
  never put in a backup, and sent only to `api.github.com`. |
| An https address of my own | The full address of an `update.json` you host, for example a mirror. The setup
  program must be in the same folder. The same signatures are required. |

**Automatic checks** are off until you tick **Check for updates automatically**. Then CaseKeeper checks once a week
(or once a day, under *How often*), about 20 seconds after it starts, and shows a notice at the bottom right with
a *Review* button. Tick **Also download and verify a new version in the background** to have it ready to install;
installing always asks you first.

You can always update by hand instead: download the newest installer from the website, check its SHA-256 against
`SHA256SUMS.txt`, and run it over the old version.
