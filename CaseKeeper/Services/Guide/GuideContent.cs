namespace CaseKeeper.Services.Guide;

public sealed record Article(string Id, string Topic, string Title, string Keywords, string Body);

/// <summary>
/// The built-in guide: general information about family proceedings in England &amp; Wales, answers
/// to common questions, and how to use every part of CaseKeeper. Written in plain English using the
/// same formatting language as templates. General information only – not legal advice. Rules, forms
/// and fees change, so articles point to official sources for anything that does.
/// </summary>
public static class GuideContent
{
    public const string Disclaimer =
        "> This is general information about the family courts in England and Wales, not legal advice about your case. " +
        "Rules, forms and fees change: check GOV.UK or the Family Procedure Rules, and get advice if you can.";

    public static readonly string[] Topics =
    {
        "Start here", "Using CaseKeeper", "How cases work", "Children cases", "Domestic abuse & safety",
        "Hearings & the court", "Documents, statements & bundles", "Orders, enforcement & appeals",
        "Divorce & money", "Care proceedings", "Money, fees & legal help", "Frequently asked questions", "Glossary"
    };

    public static readonly Article[] All =
    {
        // ================================================================== start here
        new("start", "Start here", "Welcome – how to use this guide", "help intro guide search ai explain",
            "This guide explains how family court cases work in England and Wales, answers common questions, and shows how to use every part of CaseKeeper.\n\n" +
            "- **Search** with the box at the top: type any word, a form number (C100, FL401) or a question.\n" +
            "- **Browse** by topic on the left.\n" +
            "- **Explain simply** asks your local AI (if you've set it up) to put an article into plainer words.\n" +
            "- **How does this apply to me?** asks the AI to relate an article to the case details you've entered. It only uses your information on your own computer.\n" +
            "- Not sure how to do something in CaseKeeper? Ask the **Help assistant** (sidebar, under Help & system) – it gives step-by-step directions with buttons that take you there.\n" +
            "- **Ask a question** answers from the guide's own articles, so the AI isn't guessing.\n\n" +
            Disclaimer),

        new("quickstart", "Start here", "Quick start: your first ten minutes", "getting started setup first steps beginner",
            "#. **Add the people.** Go to *Parties* and add the Applicant, the Respondent and each child (with date of birth). Tick *This is me* on yourself.\n\n" +
            "#. **Fill in Case details.** Case number, court and type. They then appear automatically in letters, bundles and reports.\n\n" +
            "#. **Import your papers.** Drag files onto the window, or use *Import*. CaseKeeper reads the text in scans so you can search them.\n\n" +
            "#. **Add your next hearing** under *Case & hearings*, and the directions from your latest order under *Directions & tasks*.\n\n" +
            "#. **Keep a timeline and contact log** as things happen. Write facts, dates and who was there.\n\n" +
            "#. **Protect it.** Turn on password protection and make a backup (*Settings*).\n\n" +
            "Tip: press **Ctrl+K** anywhere to jump to any screen or action by typing its name."),

        // ================================================================== using CaseKeeper
        new("app.modes", "Using CaseKeeper", "Simple and Professional modes", "simple professional mode power user layout sidebar",
            "CaseKeeper has two layouts. Switch any time from the bottom of the sidebar or in *Settings → Layout*.\n\n" +
            "- **Simple** shows the essentials: Dashboard, Case & hearings, Documents, Parties, Timeline, Directions & tasks, Contact log, Letters, Bundles, Guide and Settings.\n" +
            "- **Professional** adds Costs, the Scott schedule, Complaints, AI analysis, Exports, the Audit trail and more detailed options.\n\n" +
            "Nothing is lost when you switch: hidden screens keep their data."),

        new("app.shortcuts", "Using CaseKeeper", "Keyboard shortcuts and the command palette", "keyboard shortcut ctrl command palette quick",
            "| Keys | What it does |\n|---|---|\n" +
            "| Ctrl+K | Command palette: type to jump to any screen, action, document or record |\n" +
            "| Ctrl+F | Search everything |\n| Ctrl+I | Import documents |\n| Ctrl+Shift+I | Bulk Case Import (a whole folder) |\n| Ctrl+N | Add a new item on the current list |\n" +
            "| Ctrl+L | Lock CaseKeeper (password-protected libraries) |\n| F1 | Open this guide |\n" +
            "| Enter | Open / edit the selected item |\n| F2 | Rename the selected document |\n| Delete | Delete (documents go to Deleted items first) |\n" +
            "| Ctrl+D | Duplicate the selected item |\n| Ctrl+C | Copy the selected rows |\n| Ctrl+B / Ctrl+I / Ctrl+U | Bold / italic / underline in the letter editor |\n\n" +
            "Right-click almost anything – documents, categories, list rows, letters – for everything you can do with it."),

        new("app.documents", "Using CaseKeeper", "Documents, categories and the explorer", "documents import categories explorer folders drag drop rename move tags exhibit deleted bin",
            "The *Documents* screen works like File Explorer.\n\n" +
            "- **Import** by dragging files or folders onto the window, or onto a category to file them there.\n" +
            "- **Categories** cover the forms and papers used in the family courts (C100, C1A, C2, FL401, Form E, Section 7 reports, police disclosure and hundreds more). Type in the box above the tree to find one by code or any word. Sort them A–Z, by form code or by how many documents they hold, and hide empty ones.\n" +
            "- **Move documents** by dragging them onto a category, a party, *Starred* or *Deleted items*, or right-click → *Move to category*.\n" +
            "- **Right-click a document** to rename, re-categorise, tag, give it an exhibit number, add it to a bundle or the timeline, analyse it with AI, copy its fingerprint, check it hasn't changed, or see its full history.\n" +
            "- **Deleted items** keeps deleted documents for 30 days (changeable in Settings) so you can restore them.\n" +
            "- Choose the columns you want by right-clicking the column headings.\n\n" +
            "Your original files are never moved or changed. CaseKeeper keeps its own copy."),

        new("app.librarycheck", "Using CaseKeeper", "Check my library: organise what you already have",
            "library check organise organize existing documents whole library categorise duplicates versions latest edit last saved revision complaints scott schedule parties contacts timeline sections overview assistant rules without ai",
            "**Check my library** runs the documents already in CaseKeeper through the same reading, organising and cross-checking as Bulk Case Import – with AI, or with CaseKeeper's own rules only. " +
            "Open it with *Check library* in Documents, *Check my library…* in Import review or on the Case map, or **Ctrl+K**.\n\n" +
            "## Choices\n\n" +
            "- **Which documents:** all of them, only those not yet categorised, or only those not organised before.\n" +
            "- **Use AI:** your local AI, or the cloud AI you chose in *Settings → AI* (with the usual *Before anything is sent* screen and masking). Without AI, CaseKeeper's own rules still suggest categories and complaint records, and the copies and versions check always runs.\n" +
            "- **Read the text** of documents that don't have it yet (slower for scans). Untick it to leave those out.\n" +
            "- **What to fill in:** categories; parties and children (with representatives, and contact details for professionals and organisations – never for a child); hearings, orders and directions; timeline and chronology; actions and deadlines; the contact log; complaints; the Scott schedule; safeguarding; missing documents and issues. Your choice is remembered. A short summary of each document is always kept.\n" +
            "- **Write a case overview** when it finishes (uses AI).\n\n" +
            "## What happens\n\n" +
            "#. CaseKeeper reads the dates saved inside each file (when a PDF or Word file was last saved, and Word's revision number) for documents imported before it did so. Dates only – never names.\n" +
            "#. It reads any missing text, then the AI (or the rules) suggest what each document tells you, for the sections you chose.\n" +
            "#. It cross-checks everything and compares the whole library for copies and versions.\n" +
            "#. **Import review** opens, with a *Library check* card at the top of *Summary*: what was added to your case, what's waiting, each line a link. With **Add what's found to the case straight away** ticked (the default), what CaseKeeper is confident about is already in your Track sections, marked *From AI · check* – see *What the AI adds by itself*. The rest waits for you.\n\n" +
            "## Complaints\n\n" +
            "Complaint letters and replies are recognised with or without AI: complaint wording plus an organisation CaseKeeper knows (Cafcass, HMCTS, the police, a council, an MP, an ombudsman, the JCIO, the SRA and others). " +
            "The suggestion gives the organisation, any reference, the date, and a reply deadline only when the letter states one – \"within 20 working days\" is worked out from the letter's date, skipping weekends, and marked for you to check because bank holidays aren't known. " +
            "Approving a complaint also files its document under the matching complaint category if it's still Uncategorised. " +
            "A **reply** to a complaint already in *Complaints* is added to it instead of making a second one: the document is linked, the status becomes *Response Received*, the reply's summary goes into *Outcome*, and only empty fields are filled. You see exactly what will change first, and *Undo* restores the previous values.\n\n" +
            "## Parties and the Scott schedule\n\n" +
            "Someone already in *Parties* isn't added twice: the suggestion fills only their empty fields. Allegations become *Scott schedule* suggestions – who alleges it, what is alleged, when, and the document and page relied on – recorded as what someone says, never as fact.\n\n" +
            "## Which is the latest edit?\n\n" +
            "In *Duplicates & versions*, the newest copy is chosen by the document's own date, then when it was last saved inside the file, then its Word revision, then when the file last changed. Each copy has an *Edit history* line, and you can compare two **side by side** or **move the older ones to Deleted items** (restorable for as long as Deleted items keeps them).\n\n" +
            "## The help assistant\n\n" +
            "The help assistant knows how many documents are uncategorised, how many suggestions are waiting, and similar counts – numbers only, never titles, names or content – so it can suggest your next step with a button. It never changes anything itself."),

        new("app.casemap", "Using CaseKeeper", "Case map and case overview",
            "case map matrix overview laid out layout big picture grid timeline periods months quarters years category groups parties people records hearings orders complaints deadlines uncategorised undated",
            "The **Case map** (sidebar, under OVERVIEW) shows how your case is laid out.\n\n" +
            "- **Case overview** at the top: an AI-written summary of the whole case – who's involved, what has happened, where things stand, what's coming up and outstanding, the key issues and the gaps. It's written from each document's short summary and your records, not the documents' full text, and saved in *AI analysis → Reports*. Click *Write case overview* or *Refresh overview*. Without AI, the record counts are shown instead.\n" +
            "- **The matrix:** one row per category group (or per party, with *Rows*), one column per month, quarter or year (chosen automatically from your dates, or by you). Darker cells hold more documents. Click any number to see those documents in *Documents*. *Uncategorised* and *Undated* show what still needs organising.\n" +
            "- **Case records** on the same time line: hearings, orders, complaints, Scott schedule items and deadlines. Hover for the list; click to open the screen.\n" +
            "- **People:** each party, with how many documents are linked to them and how many mention them.\n\n" +
            "Everything on the Case map is worked out on this PC; only the overview uses AI, with the same consent and masking as the rest of CaseKeeper."),

        new("app.trackautoadd", "Using CaseKeeper", "What the AI adds by itself (Track auto-add)",
            "automatic auto add track timeline directions tasks contact complaints hearings orders parties scott schedule fill populate added by ai to check undo run mark checked new imports auto-check",
            "When CaseKeeper reads your documents – a **Bulk Case Import**, **Check my library**, or a document you've just imported – it adds what it's confident about straight into your case, so the **Track** sections fill themselves: *Case & hearings* (hearings and orders), *Timeline*, *Directions & tasks*, *Contact log*, *Complaints*, *Parties* and the *Scott schedule*. It also files Uncategorised documents and fills in empty document details.\n\n" +
            "## What is added, and what waits\n\n" +
            "Something is added only when all of these are true:\n\n" +
            "- the AI is at least **medium confidence** that the document says it;\n" +
            "- nothing about it **needs checking first** and there's **no conflict** with another document (for example two different dates for the same hearing). A note that only says something unsafe was left out – a time or reply date the document doesn't give, a child's contact details – or that the document isn't filed under a category yet, doesn't stop it;\n" +
            "- it has what the record needs (a hearing or timeline entry needs a date; a deadline that has to be worked out always waits for you);\n" +
            "- **nothing like it is already in your case** – a second document about the same hearing doesn't add it twice. Someone already in *Parties*, or a complaint already in *Complaints*, only has their **empty** details filled in;\n" +
            "- you haven't undone or rejected the same thing before.\n\n" +
            "Everything else waits in **Import review**, as before. Notes for review (issues, conflicts) are never added.\n\n" +
            "## Checking what was added\n\n" +
            "Each record's notes say where it came from – *Added automatically from “document”, page N … From AI · check it against the document.* " +
            "**Import review → Added by AI – to check** lists them all (the Dashboard shows how many). Open each one's document, then:\n\n" +
            "- **Mark selected as checked** – it becomes an ordinary approved record;\n" +
            "- **Undo selected** – removes what was added; it goes back in the review list and is never added automatically again;\n" +
            "- **Undo everything from the last run…**, or **Undo what this check added…** on the Library check card – takes back a whole run. Entries you've already checked are kept.\n\n" +
            "Every addition and undo is in the audit trail, marked as added automatically by AI, with the document, page, model and confidence.\n\n" +
            "## New documents\n\n" +
            "With **From now on, do this by itself for documents I import** ticked (in the *Check my library* dialog), documents you import are read once their text is ready and added the same way. With a cloud AI you're always asked first, and shown what will be sent.\n\n" +
            "## Switching it off\n\n" +
            "Untick **Add what's found to the case straight away** in the *Check my library* dialog, or in *Import review → Review options… → AUTOMATIC*. Then everything waits for you to approve it. " +
            "Filling the Track sections needs an AI – your local AI is fine. Without AI, CaseKeeper's own rules only suggest categories and complaints."),

        new("app.casememory", "Using CaseKeeper", "Case memory – what CaseKeeper knows",
            "case memory brain knows learnt learned remember notes for the ai forget rebuild encrypted what the ai is told context",
            "**Case memory** (sidebar, under OVERVIEW) shows exactly what the AI is told about your case. It's how the AI knows where your case stands when it reads a new document, writes the case overview, or answers a question about your case.\n\n" +
            "## What's in it\n\n" +
            "It's built from your own records – case details, people (names, roles, representatives and children's year of birth; never anyone's contact details), hearings, orders, open directions and deadlines, complaints, the Scott schedule, the most recent timeline entries, a count of your documents by group, and the latest case overview. It's rebuilt by itself whenever your records change, and with **Rebuild now**.\n\n" +
            "When the AI reads a document it's given the most useful part (people, case details, hearings, orders, complaints and your notes) so it uses the same names, links to what's already there instead of adding it again, and still takes every finding from the document's own words.\n\n" +
            "## Things the AI should know\n\n" +
            "Write anything that helps it read your papers correctly – who “Mum” or “Dad” means in messages, a nickname, that a set of papers is from an earlier case – and click **Save notes**. They're given to the AI with every document.\n\n" +
            "## What it has learnt from you\n\n" +
            "- When you **Edit & approve** a category to a different one, similar documents (by the words in their file name and title) go to your choice next time.\n" +
            "- When you **reject** a suggestion, the same one (same kind, same date, much the same words) isn't suggested again.\n\n" +
            "Each item has **Forget**, and **Forget everything learnt…** clears them all (your notes and records are kept).\n\n" +
            "## Where it's kept\n\n" +
            "In your library's database on this PC – **encrypted** when your library is protected with a password (*Settings → Security*), in your backups, and deleted with the library. It's never sent anywhere except as part of an AI request you've allowed: to your local AI, or to a cloud AI you've chosen, with names and personal details masked when masking is on. It isn't copied when libraries are merged; it's rebuilt from the records instead."),

        new("app.documentorder", "Using CaseKeeper", "The document list: latest versions, most important first",
            "latest version older versions revised copy copies drafts duplicates confusing important first order sort headings court papers screenshots what it is mark as latest show all versions tidy",
            "When the same document has been saved many times – *ORIGINAL*, *tone-edited v5*, *REVISED COPY v6*, *(final)* – the list soon gets confusing. The **Documents** screen sorts that out for you, without changing or deleting anything.\n\n" +
            "## Latest version of each document only\n\n" +
            "Ticked by default. Copies and drafts of one document show as **one line, the latest**, with **Latest · N older versions** next to its title. Click that to see every version, latest first (older ones are shown lighter). Untick the box to see everything, with each document's versions kept together.\n\n" +
            "CaseKeeper treats files as versions of one document when:\n\n" +
            "- their titles are the same up to a version marker after a dash, underscore or bracket – *REVISED*, *ORIGINAL*, *AMENDED*, *UPDATED*, *FINAL*, *DRAFT*, *COPY*, *tone-edited*, *v6*, *(2)* – so *“Position statement – REVISED COPY v6”* and *“Position statement – ORIGINAL v2”* go together, but *“Notice of final hearing”* stays on its own; or\n" +
            "- they have the same title; or\n" +
            "- their words are mostly the same, whatever they're called (the same check as **Import review → Duplicates & versions**), unless you said there they aren't related.\n\n" +
            "**Which is the latest:** the one you marked *Latest version* if you have; otherwise a revised/amended/updated copy beats an original, which beats a draft; then the highest number (v6 before v5); then the one most recently saved. If it's wrong, right-click the right one → **Mark as the latest version** (it's tagged *Latest version* and the others *Older version*, the same as in Import review).\n\n" +
            "## Most important first\n\n" +
            "**Order: Most important first, latest on top** puts the list under headings:\n\n" +
            "1. **Starred** – anything you've starred;\n" +
            "2. **Court papers & orders** – orders, applications (C100, C2…), position statements, witness statements, bundles, judgments;\n" +
            "3. **Reports & assessments** – Cafcass, section 7 and 16A, experts, police, health, school;\n" +
            "4. **Evidence**;\n" +
            "5. **Letters & correspondence** – letters, emails, messages, complaints and replies, transcripts;\n" +
            "6. **My notes & working copies** – checklists, analysis, preparation, inventories;\n" +
            "7. **Photos, screenshots & recordings**;\n" +
            "8. **Other documents**.\n\n" +
            "A filed document goes under the heading for its category; an Uncategorised one is placed from its file type and title (**Check my library** files them properly). Within each heading the newest is first. Clicking a column heading sorts within each heading. The other orders are **Newest first** and **Most recently added first**. The optional column **What it is** (right-click the column headings) shows the heading for each document.\n\n" +
            "All of this is worked out on this PC from what's already in your library. Nothing is sent anywhere, and nothing is renamed, moved or deleted."),

        new("app.askcase", "Using CaseKeeper", "Ask about your case",
            "ask about my case question when did what did order say next hearing where does my case stand assistant my case mode legal aid sources cite",
            "Ask questions about your own case in your own words: *Where does my case stand?*, *What did the last order say about contact?*, *Which complaints are still waiting for a reply?*\n\n" +
            "Open the **Help assistant** and choose **My case** at the top, or type into the **Ask about your case** box on the Dashboard.\n\n" +
            "## How it answers\n\n" +
            "The AI is given the **case memory** and the pages of your documents that best match your question (from the search index), with each document's summary. It's told to answer only from those, say when they don't cover something, and cite each fact – every citation becomes a button that **opens the document at that page**. Allegations are reported as what someone says, never as fact.\n\n" +
            "It helps you organise and find things in your own papers. It doesn't give legal advice or predict what a court will decide – for that, see the Guide & FAQ, an advice service or a solicitor. It never changes anything in your case.\n\n" +
            "## Privacy\n\n" +
            "With your local AI everything stays on this PC. With a cloud AI, your question, the case memory and the matching extracts are sent for that answer, after you've agreed to use that service, and with names and personal details masked when masking is on (*Settings → AI*). The audit trail records each question as a count of characters and extracts – never the question itself. Without AI, the assistant lists the matching pages instead."),

        new("app.bulkimport", "Using CaseKeeper", "Bulk Case Import and Import review",
            "bulk import folder subfolders directory existing case organise ai suggestions review approve reject unknown confidence duplicates pause resume ollama speed slow fast careful model gemma graphics card gpu battery",
            "**Bulk Case Import** brings in a whole folder of case papers at once – including every subfolder – and can ask your local AI to suggest how to organise it. " +
            "Open it with *Import folder* in Documents, *Import a whole folder* on the Dashboard, or **Ctrl+Shift+I**.\n\n" +
            "## How it works\n\n" +
            "#. **Choose a folder** and your options. Subfolders are included; hidden and system files (such as Thumbs.db and Office lock files) are skipped unless you say otherwise. Shortcuts and linked folders aren't followed unless you ask, and loops are always detected.\n" +
            "#. **Check what was found.** Nothing is imported yet. Untick files, or a whole folder, filter by status or type, and see which are duplicates, unsupported or unreadable. *Copy summary* copies the counts (never anything from inside the files).\n" +
            "#. **Import and read.** Files are copied into your library through the normal import – encrypted if your library is protected – and their text is read, including scans and photos. You can pause, stop, or close CaseKeeper; it carries on from where it stopped.\n" +
            "#. **Review.** If AI is on, your local AI suggests a category, dates and records for each document. They all wait in **Import review**.\n\n" +
            "## Your original files\n\n" +
            "Originals are **never moved, renamed or changed**, and nothing is written into the folder you import from. Each document keeps the path it had inside that folder (shown as *Original folder* in its details, and under *By original folder* in the Library tree). " +
            "A file's own date is kept for information only – it is **never** used as the document's date unless you choose it.\n\n" +
            "## Duplicates\n\n" +
            "Duplicates are found by their contents (a SHA-256 fingerprint), not by name. Choose whether to skip them (and note which document they match), import them anyway, or decide one by one in the preview.\n\n" +
            "## How the AI suggestions work\n\n" +
            "- CaseKeeper first matches each document to its own categories by form code, title and wording, then gives the AI a short list. The AI can only choose one of those, or *Unknown*.\n" +
            "- The AI's answer must follow a strict format, which CaseKeeper checks. Every suggestion must quote the words it relies on and the page they're on. If the quote isn't on that page, or a date isn't actually written in the document, the suggestion is flagged and its confidence lowered – dates the document doesn't contain are removed, never guessed.\n" +
            "- **Confidence** (0 to 1) is how strongly the document supports the suggestion. *High* confidence means the document strongly matches, but you should still confirm it. Low-confidence categories go to *Unknown / needs a category*. Change the levels in *Review options*.\n" +
            "- Dates keep their meaning: the date of a document, a hearing *listed for* a date (which doesn't mean it took place), an adjourned or vacated hearing, an alleged incident, a deadline. Allegations are recorded as what someone says, never as fact.\n" +
            "- Deadlines such as \"within 14 days\" are never worked out by the AI. Use *Work out the date…*, which uses the deadline calculator with a date and rule you check.\n" +
            "- After the AI has read everything, CaseKeeper's own checks compare documents: the same event from several documents, hearings given different dates, a hearing later adjourned or vacated, different case numbers, names spelt differently, things already in your case, and gaps in the chronology. Nothing is resolved for you – you see the sources side by side and decide.\n\n" +
            "## Import review\n\n" +
            "Every suggestion shows the document, page and quoted words (*Open at page* shows it), the confidence and why it was suggested, and any warnings. " +
            "**Approve** adds it using the same form and rules as adding it by hand; **Edit & approve** lets you correct it first; **Reject** and **Defer** do what they say. " +
            "**Undo** removes what an approval added and puts the suggestion back. Every decision is in the audit trail, marked as AI-suggested and approved by you.\n\n" +
            "What the AI is confident about is added to your case by itself (see *What the AI adds by itself*), and listed under **Added by AI – to check**. Switch that off in *Review options…*; there's then a separate option to file only very confident categories automatically.\n\n" +
            "## Speed and your PC\n\n" +
            "Bulk analysis uses a fast model, **gemma3:4b**, by default – *Review options* offers to download it if it isn't installed. Choose **Fast (recommended)** or **Careful** (a larger model, only when your graphics card can hold it). " +
            "CaseKeeper looks at your graphics card, memory and power and recommends settings: a PC without a suitable graphics card and little memory starts with AI off; a basic PC or a laptop on battery reads one document at a time for categories and document details only; a PC with a graphics card reads two to four documents at once in full. *Review options* shows *This PC: …*, marks models that are too large, and has **Use recommended settings**.\n\n" +
            "If your PC struggles – the AI runs out of memory, takes too long, or the model doesn't fit on the graphics card – CaseKeeper eases off by itself (fewer documents at once, smaller parts, a lighter model, then a quick reading), tells you, and carries on. Anything you choose yourself wins, with a warning if your PC can't sensibly run it. Reading several at once needs Ollama's OLLAMA_NUM_PARALLEL setting.\n\n" +
            "## Using a cloud AI instead\n\n" +
            "In *Review options → AI SERVICE* (or *Settings → AI*) you can have the import read by **Anthropic (Claude)**, **OpenAI (GPT)**, or either on **Microsoft Foundry** in your own Azure subscription. They're much faster on large folders; the default is the quick, low-cost model at low effort. " +
            "Before anything is sent, **Before anything is sent** shows the address it goes to, what will be masked, sensitive subjects in the batch, and any documents that couldn't be masked well – for those you choose to read them with the local AI, skip them, or send them anyway. If you don't agree, nothing is sent and the documents wait. See *Cloud AI, masking and what's sent*.\n\n" +
            "## Duplicates and versions\n\n" +
            "*Import review → Duplicates & versions* groups copies and versions of the same document by comparing the words inside them, so a PDF and a Word copy of one letter, or a draft and its final version, are found. The newest is shown first with the reason (its date, then when it was last saved inside the file, its Word revision, then when the file was last changed), with an *Edit history* line for each copy. **What changed** compares any version with the newest sentence by sentence, and **Side by side** shows two in columns. " +
            "**Keep newest, tag the others as older**, **Keep all, link as versions**, **Not related**, or **Move older to Deleted items…** (restorable from Deleted items) – each choice can be undone. To check documents already in the library, use *Check my library*.\n\n" +
            "## If the AI isn't available\n\n" +
            "The import still finishes. Documents wait as *Waiting – AI not available*, and CaseKeeper's own rules still suggest categories from form codes. Start Ollama (or, for a cloud AI, check you're online), then use *Analyse N waiting* in Import review. Failed analyses can be retried the same way.\n\n" +
            "## Privacy\n\n" +
            "With the local AI everything happens on your computer and the AI only talks to your own Ollama server. With a cloud AI, the text of the documents being analysed goes to the service you chose, masked by default, and only after you agree. Suggestions and import records are stored in your (encrypted, if protected) library and included in backups. Logs never contain document text, file names or folder paths.\n\n" +
            "The AI is an assistant, not an authority. It can be wrong, and it does not give legal advice."),

        new("app.search", "Using CaseKeeper", "Searching", "search find text ocr words phrase",
            "*Search* looks through document titles and details, your notes, and the words inside every page – including scanned letters, thanks to the text recognition built into Windows.\n\n" +
            "- Put words in \"double quotes\" to find an exact phrase.\n- Filter by category or date.\n- Double-click a result to open the document at that page."),

        new("app.case", "Using CaseKeeper", "Case details, hearings and orders", "case details hearings orders court judge next hearing countdown",
            "*Case & hearings* holds the basic facts of the case, every hearing and every order.\n\n" +
            "- **Case details** fill in letters, statements and bundle covers automatically.\n" +
            "- **Hearings** show on the dashboard with a countdown and are added to your timeline. Right-click a hearing to record the order made or add a preparation checklist.\n" +
            "- **Orders** keep a register of what was ordered. Right-click an order to add its directions to *Directions & tasks*."),

        new("app.actions", "Using CaseKeeper", "Directions, tasks and the deadline calculator", "directions tasks deadlines overdue reminders calculator clear days",
            "Everything someone must do, by when: court directions, your own tasks, and the other side's. Overdue items turn red and show on the dashboard.\n\n" +
            "The **Deadline calculator** works out dates using England & Wales bank holidays and the Family Procedure Rules' way of counting (for example, 'clear days' don't count the first and last day). It can add the result as a task. Always check the wording of your order."),

        new("app.contact", "Using CaseKeeper", "The contact log", "contact log child arrangements handover missed cancelled late",
            "Record each time contact was due: whether it happened, any lateness, cancellations and concerns. The summary line shows how many sessions took place and who cancelled. Export it as a table for the court, Cafcass or your solicitor.\n\n" +
            "Keep entries short, factual and child-focused – what happened, not opinions about the other parent."),

        new("app.costs", "Using CaseKeeper", "Costs and the Scott schedule", "costs expenses fees money scott schedule allegations",
            "**Costs & expenses** records what the case is costing, with totals by category – useful for budgeting, Help with Fees or a costs application.\n\n" +
            "**Scott schedule** is a table of allegations for a fact-finding hearing. Only prepare one if the court directs it, and follow the format it asks for."),

        new("app.letters", "Using CaseKeeper", "Letters, templates and letterheads", "letters templates letterhead word docx dotx pdf html export print fields merge",
            "Write letters and court documents on your own letterhead.\n\n" +
            "- **Start from a template**: letters to the court, Cafcass or your MP, witness statements, position statements, subject access requests, complaints and more.\n" +
            "- **Fields** like {{case.number}} and {{me.name}} fill in automatically from your case details and parties.\n" +
            "- **Letterheads**: your name, address, a logo, colours and one of three styles – or attach your own Word letterhead.\n" +
            "- **Word templates**: import a .docx or .dotx. Keep its exact design (fields are filled in and the letter goes where {{body}} is), or convert it into editable text.\n" +
            "- **Export** to Word, PDF or a web page, print, or save a PDF straight into your library. Exported files have author and editing details removed."),

        new("app.bundles", "Using CaseKeeper", "Building a court bundle", "bundle pd27a index pagination sections court bundle",
            "*Bundles & reports* puts documents into one PDF with a cover, index, sections A–F and page numbers like B12, following the general shape of Practice Direction 27A. A summary report instead lists your documents, notes and chronology without the full documents.\n\nAlways follow what the court has directed about bundles (who prepares it, what goes in, page limits and deadlines)."),

        new("app.ai", "Using CaseKeeper", "AI analysis: local or cloud", "ai ollama model llm summary chronology inconsistencies local gpu prompt anthropic claude openai gpt chatgpt foundry azure cloud effort",
            "CaseKeeper can use **Ollama**, a free program that runs AI models on your own computer, so your papers never leave it – or, if you choose, a cloud AI: **Anthropic (Claude)**, **OpenAI (GPT)**, or either on **Microsoft Foundry**. AI is off until you switch it on.\n\n" +
            "## Setting up the local AI\n\n#. In *Settings → AI → Which AI to use*, choose *Local AI on this PC (Ollama)*.\n#. *Install Ollama* (or *Get Ollama* in the Store version, which opens its download page), then *Set up the local AI*: it finds Ollama, downloads the fast model and switches AI on.\n" +
            "#. Or by hand: tick *Use AI features*, check the address (usually http://localhost:11434), press *Test connection* and choose the model.\n\n" +
            "## Setting up a cloud AI\n\n#. Get an API key from Anthropic or OpenAI – or, for Microsoft Foundry, the resource name and key, with a model deployed.\n#. In *Settings → AI → Which AI to use*, choose the service, paste the key (and resource) and press *Connect*. CaseKeeper checks it, picks the recommended fast model (Claude Haiku 5.5 or GPT-6 Luna, at low effort) and switches AI on.\n" +
            "#. Change *Model* and *Reasoning effort* if you like. Higher effort thinks for longer: more careful, slower, and more expensive.\n\n" +
            "The key is stored encrypted for your Windows account and never goes into settings files, logs or backups. See *Cloud AI, masking and what's sent*.\n\n" +
            "## Using it\n\nOn *AI analysis*, choose what to look at (documents, a category, a party, a date range, the timeline or your records) and a report: summary, chronology, issues, inconsistencies, welfare checklist, safeguarding, questions to consider and more. **Granular** mode reads large sets part by part, then combines the results.\n\n" +
            "Every instruction the AI follows can be edited in *Settings → AI → Prompts*. The AI can be wrong: always check its output against the documents. It does not give legal advice.\n\n" +
            "## Performance\n\nUse a GPU if you have one (the default lets Ollama decide). On a laptop, choose a smaller model and the Power saver profile. A larger *context size* lets the AI read more at once but uses more memory."),

        new("app.cloudai", "Using CaseKeeper", "Cloud AI, masking and what's sent", "cloud ai masking mask privacy consent anthropic openai foundry what is sent personal details trust treat with care",
            "A cloud AI is optional and off unless you choose it in *Settings → AI*. With the local AI, nothing leaves your computer.\n\n" +
            "## What is sent\n\n" +
            "Only the text of the documents (or records) you ask it to analyse, plus CaseKeeper's instructions, to the one service you chose, over an encrypted connection. CaseKeeper refuses to send to any other address, never follows a redirect, and ignores settings from other programs. Your password, keys for other services and files you haven't chosen are never sent.\n\n" +
            "## Masking\n\n" +
            "With *Mask names and personal details before sending* ticked (the default), CaseKeeper replaces names from your parties and contacts, other names it recognises, e-mail addresses, phone numbers, postcodes, National Insurance numbers, case numbers, bank and card numbers, dates of birth and addresses with tags such as [PERSON-1]. The answer is put back with the real details on your PC. The list linking tags to details stays in memory only and is never sent or saved. " +
            "Masking is careful but not perfect: an unusual name, a nickname or a badly scanned page can slip through.\n\n" +
            "## Before anything is sent\n\n" +
            "The first time you use a service, and before each bulk import, a screen shows where the documents go, how many and roughly how much text, what will be masked, sensitive subjects (for example children, health or money) and documents that couldn't be masked well. For those, choose to read them with the local AI instead, skip them, or send them anyway. If you don't agree, nothing is sent. Your choice is in the audit trail (service, address and whether masking was on – never any content). *Ask again before sending* brings the screen back.\n\n" +
            "## The service's own terms\n\n" +
            "What the service does with what it receives is set by its terms: Anthropic and OpenAI may keep requests for a limited time; Microsoft Foundry keeps them in your own Azure subscription. Check the terms before sending anything sensitive.\n\n" +
            "## Treat with care\n\n" +
            "Whichever AI you use, CaseKeeper flags documents that deserve a second look – Office files that can contain macros, badly scanned text, or a date written in the document that's later than the file was last changed – under *Conflicts & checks* in Import review."),

        new("app.assistant", "Using CaseKeeper", "The help assistant", "help assistant chat ask how do i directions ai navigate",
            "The **Help assistant** (in the sidebar under Help & system) answers \"how do I…?\" questions about CaseKeeper in plain English. " +
            "Type what you want to do – *\"How do I put my case number on every letter?\"*, *\"Where do I record a missed handover?\"* – " +
            "and you get numbered steps using the exact names on screen, with buttons that take you straight there.\n\n" +
            "- It uses the AI you chose in *Settings → AI* and knows a full map of CaseKeeper's screens, buttons and menus, plus this guide, so it " +
            "gives directions for things that really exist.\n" +
            "- **Only your question is sent** to your AI – nothing about your case, documents or parties.\n" +
            "- It remembers the conversation, so you can ask follow-ups such as \"and how do I export it?\". *New conversation* starts again.\n" +
            "- Without AI it still finds the matching help pages and screen buttons.\n" +
            "- You can also type a question into the **Ctrl+K** palette and choose *Ask the help assistant*.\n" +
            "- Its instructions can be edited in *Settings → AI → Prompts → Help assistant*.\n\n" +
            "It helps with using the app, not with the law: for how cases work, see the rest of this guide."),

        new("app.security", "Using CaseKeeper", "Security, privacy and encryption", "security password encryption aes privacy lock confidential maximum",
            "- **Password protection** encrypts the database, every document, snapshots and saved exports with AES-256. There is no recovery if the password is lost.\n" +
            "- **Auto-lock** hides everything after a period of inactivity, when Windows locks, or with Ctrl+L.\n" +
            "- **Maximum security** (Settings) switches on the strictest options in one go.\n" +
            "- Decrypted copies opened in other programs are wiped when CaseKeeper closes.\n" +
            "- Nothing is uploaded. The optional AI only talks to the server you choose, local by default. *Check for updates* only downloads, and only when you ask (or switch on automatic checks).\n" +
            "- Logs never contain your documents or notes, and your Windows user name is removed from them."),

        new("app.backup", "Using CaseKeeper", "Backups, snapshots and moving PCs", "backup restore snapshot export import settings transfer",
            "- **Backups** are one .zip of everything – documents, notes, timeline, hearings, directions, contact log, costs, letters, templates, letterheads, AI reports, bulk imports and their AI suggestions and decisions, settings and the audit trail. Add a password to encrypt it.\n" +
            "- **Import a backup** either adds it to what's here (duplicates skipped) or replaces everything.\n" +
            "- **Snapshots** are automatic daily restore points of the database, kept inside the library.\n" +
            "- **Settings & templates file** moves your letterheads, templates, prompts and preferences to another PC on their own."),

        new("app.updates", "Using CaseKeeper", "Updating CaseKeeper", "update updates upgrade new version check install signed signature setup store latest",
            "The website version of CaseKeeper can update itself from **Settings → Updates**. The Microsoft Store version is updated by the Store (Microsoft Store → **Library** → **Get updates**).\n\n" +
            "#. Click **Check for updates**. CaseKeeper downloads a small file that says which version is newest, with two signatures. Nothing about you, this PC or your case is sent.\n" +
            "#. If a newer version is available you see what's new. Click **Download and verify**: the setup program must match the signed size and checksum exactly, and say it is CaseKeeper by DarkCipher Systems.\n" +
            "#. Click **Install… (backup first)**. You're offered a backup, CaseKeeper takes a safety snapshot and closes, and the normal setup wizard runs. Your library, settings and password protection are not touched.\n" +
            "#. When CaseKeeper opens again it tells you whether the update worked.\n\n" +
            "**Why you can trust it.** An update is only offered when it is signed by both CaseKeeper release keys built into your copy: a classical one (ECDSA P-256) and a post-quantum one (ML-DSA-65). " +
            "The website or GitHub only carry the files, so nothing changed there can get through, and an older version is never offered as new.\n\n" +
            "**Automatic checks** are off until you switch them on. Then CaseKeeper checks once a week (or once a day) shortly after it starts and shows a notice; it can also download and verify the update in the background. Installing always asks you first."),

        new("app.audit", "Using CaseKeeper", "The audit trail and logs", "audit trail history log tamper evidence integrity hash chain errors",
            "Every change – documents imported, details edited, notes, hearings, letters, exports, backups, AI requests – is recorded in the **audit trail** with the date and time, and what changed.\n\n" +
            "Each entry carries a fingerprint that covers the one before it, so altering or deleting a past entry breaks the chain. *Verify* checks the whole chain. You can export the trail as a PDF, Word file, web page or spreadsheet.\n\n" +
            "**Integrity check** confirms every document is byte-for-byte what you imported (SHA-256).\n\n" +
            "The separate **error log** (Settings → Logs) records technical problems with a reference such as E-7F3A2C shown on screen."),

        // ================================================================== how cases work
        new("courts", "How cases work", "The family court system in England and Wales", "family court judges magistrates district circuit high court levels",
            "Most family cases are heard in **the Family Court**, at hearing centres across England and Wales. Cases are allocated to the right level of judge:\n\n" +
            "- **Magistrates** (lay justices, with a legal adviser) – many straightforward children cases.\n- **District Judges** – most private law children cases and many financial cases.\n" +
            "- **Circuit Judges** – more complex cases, care proceedings and appeals from magistrates and district judges.\n- **High Court judges** – the most complex or unusual cases.\n\n" +
            "Proceedings are governed by the **Family Procedure Rules 2010 (FPR)** and their Practice Directions.\n\n" + Disclaimer),

        new("lip", "How cases work", "Representing yourself (litigant in person)", "litigant in person self represented lip mckenzie friend no solicitor",
            "You can represent yourself. Judges are used to people doing so and should explain the process, but you are expected to follow the rules and the court's directions.\n\n" +
            "- Keep everything organised, with dates.\n- Read every order carefully and diary each deadline.\n- Copy correspondence to the court to the other party.\n" +
            "- You can ask for a **McKenzie friend** to sit with you for support and to take notes (see the glossary).\n- Free help: Support Through Court, Advicenow, Citizens Advice, law centres (see *Getting help*).\n\n" + Disclaimer),

        // ================================================================== children
        new("children.process", "Children cases", "How a child arrangements case works (private law)", "c100 miam fhdra dra final hearing child arrangements programme process stages section 7",
            "Disputes between parents about children follow the **Child Arrangements Programme** (Practice Direction 12B). In outline:\n\n" +
            "#. **Try to agree first.** Before applying you usually need to attend a **MIAM** (a meeting about mediation and other ways to resolve things), unless an exemption applies, such as domestic abuse.\n" +
            "#. **Application.** Form **C100**, with **C1A** if there are allegations of harm or abuse, and **C8** to keep an address confidential.\n" +
            "#. **Safeguarding.** Before the first hearing Cafcass (Cafcass Cymru in Wales) carries out checks with the police and local authority and usually speaks to each parent by phone, then writes a **safeguarding letter**.\n" +
            "#. **First hearing (FHDRA).** The court looks at safeguarding, what's agreed and what isn't, may make interim arrangements, and gives directions – for example statements, a **section 7 report**, or a fact-finding hearing.\n" +
            "#. **Dispute Resolution Appointment (DRA).** After reports are in, another attempt to narrow the issues.\n" +
            "#. **Final hearing.** If still not agreed, the court hears evidence and decides.\n\n" +
            "Some areas run newer models (such as 'Pathfinder' courts) with a different early stage. Your notices and orders will say what applies.\n\n" + Disclaimer),

        new("children.welfare", "Children cases", "The child's welfare and the welfare checklist", "welfare paramount checklist section 1 children act 1989 presumption involvement",
            "When a court decides about a child's upbringing, the child's welfare is its **paramount** consideration (Children Act 1989, section 1).\n\n" +
            "In contested cases the court must consider the **welfare checklist** in section 1(3):\n\n" +
            "- the child's wishes and feelings, in light of age and understanding;\n- physical, emotional and educational needs;\n- the likely effect of any change in circumstances;\n" +
            "- age, sex, background and any relevant characteristics;\n- any harm suffered or at risk of being suffered;\n- how capable each parent, and anyone else relevant, is of meeting the child's needs;\n- the range of powers available to the court.\n\n" +
            "The court also presumes that involvement of each parent in the child's life will further the child's welfare, unless shown otherwise (section 1(2A)) – this means involvement of some kind, not a particular amount of time. The court avoids delay, and only makes an order if that is better for the child than no order.\n\n" + Disclaimer),

        new("children.orders", "Children cases", "Types of orders about children", "child arrangements order lives with spends time prohibited steps specific issue parental responsibility",
            "- **Child arrangements order** – who a child lives with, and/or spends time or otherwise has contact with.\n" +
            "- **Prohibited steps order** – stops a particular step being taken without the court's permission (for example, removing the child from the country).\n" +
            "- **Specific issue order** – decides one particular question (for example, which school, or medical treatment).\n" +
            "- **Parental responsibility order** – gives parental responsibility to someone who doesn't have it.\n" +
            "- **Section 91(14) order** – stops someone making further applications without the court's permission.\n" +
            "- **Enforcement order** – see *Enforcement*.\n\n" + Disclaimer),

        new("children.cafcass", "Children cases", "Cafcass and section 7 reports", "cafcass family court adviser section 7 report wishes feelings safeguarding letter cymru",
            "**Cafcass** (Children and Family Court Advisory and Support Service) represents children's interests in family courts in England; **Cafcass Cymru** does so in Wales.\n\n" +
            "- The **safeguarding letter** reports the initial checks before the first hearing.\n" +
            "- A **section 7 report** is ordered when the court wants a welfare report. A Family Court Adviser (or sometimes a local authority social worker) speaks to the parents and, depending on age, the child, and may contact school and other agencies. The report makes recommendations, which the court considers but doesn't have to follow.\n\n" +
            "Tips: be on time, be child-focused, give concrete examples with dates, and keep a note of what was discussed. If something in a report is factually wrong, you can raise it – usually in your statement or at the hearing.\n\n" + Disclaimer),

        new("children.voice", "Children cases", "Children's wishes and feelings", "wishes feelings child voice meeting judge letter",
            "The court must consider the child's wishes and feelings in light of their age and understanding. Usually this happens through Cafcass. Older children may sometimes write to the judge or, rarely, meet the judge.\n\n" +
            "Avoid putting pressure on a child or involving them in the dispute. Courts generally disapprove of parents recording children or questioning them about the case.\n\n" + Disclaimer),

        // ================================================================== domestic abuse
        new("da.pd12j", "Domestic abuse & safety", "Domestic abuse in children cases (PD12J)", "domestic abuse pd12j fact finding coercive control c1a",
            "Where domestic abuse is alleged in a children case, the court follows **Practice Direction 12J**. It must consider whether the allegations are relevant to the decisions it has to make, and if so whether a **fact-finding hearing** is needed to decide whether they are true.\n\n" +
            "Domestic abuse includes physical and sexual abuse, violent or threatening behaviour, controlling or coercive behaviour, economic abuse, and psychological or emotional abuse (Domestic Abuse Act 2021). Children who see, hear or experience the effects of abuse are recognised as victims too.\n\n" +
            "Allegations are usually raised on form **C1A** and set out in a statement. Courts increasingly ask for a focused narrative statement rather than a long Scott schedule; follow the directions you're given.\n\n" + Disclaimer),

        new("da.protection", "Domestic abuse & safety", "Non-molestation and occupation orders", "non-molestation occupation order fl401 injunction breach police protection",
            "Under the Family Law Act 1996 you can apply on form **FL401** for:\n\n" +
            "- a **non-molestation order** – stops someone using or threatening violence, harassing or intimidating you (or a child);\n" +
            "- an **occupation order** – decides who can live in the home, or part of it.\n\n" +
            "In urgent cases an order can be made **without notice** to the other person, followed by a return hearing. Breaching a non-molestation order is a **criminal offence**.\n\n" +
            "**If you are in immediate danger, call 999.**\n\n" + Disclaimer),

        new("da.measures", "Domestic abuse & safety", "Special measures and cross-examination", "special measures screens separate waiting room video link part 3a qualified legal representative cross examination",
            "If you are vulnerable – including as an alleged victim of domestic abuse – the court must consider **participation directions** (Family Procedure Rules Part 3A and Practice Direction 3AA), such as separate waiting areas, screens in court, giving evidence by video link, or separate entrances and exits.\n\n" +
            "Ask the court for these in advance – often there's a box on the forms, or you can write to the court.\n\n" +
            "In some circumstances a party can't cross-examine the other in person. The court may then appoint a **qualified legal representative** to ask the questions.\n\n" + Disclaimer),

        // ================================================================== hearings
        new("hearings.attend", "Hearings & the court", "Going to a hearing", "hearing attend court what to expect remote video etiquette address judge",
            "- Arrive early. Bring your papers, a pen, and a copy of anything you'll rely on.\n- Hearings are usually in private. Remote hearings use video (for example CVP or Teams) or phone – test your link beforehand.\n" +
            "- Address a district judge or magistrates as *Sir* or *Madam*; a circuit judge as *Your Honour*.\n- Speak calmly, stick to the issues, and focus on the children.\n" +
            "- Take notes of what is decided. You'll usually receive the order later; check it carefully.\n\n" + Disclaimer),

        new("hearings.transparency", "Hearings & the court", "Privacy, reporting and sharing papers", "privacy confidential reporting journalists transparency share papers pd12g 12.73 contempt social media",
            "Family proceedings about children are generally private. Sharing documents or information from the case with people who aren't involved can be **contempt of court**, unless it's allowed by **FPR rule 12.73** and **Practice Direction 12G**, or the court gives permission.\n\n" +
            "PD12G allows some sharing, for example with a legal adviser, a McKenzie friend, a professional regulator, or (generally only the text or a summary of a judgment) an MP. Don't post about the case on social media.\n\n" +
            "Accredited journalists and legal bloggers may be allowed to attend some hearings and, in some courts, report under strict anonymity rules.\n\n" + Disclaimer),

        // ================================================================== documents
        new("docs.statements", "Documents, statements & bundles", "Writing a witness statement", "witness statement pd22a statement of truth exhibits numbered paragraphs format",
            "Family court witness statements usually:\n\n" +
            "- have the court name, case number and party names at the top, with the witness's details in the top right corner;\n- use numbered paragraphs, each dealing with one point;\n" +
            "- set things out in date order, with dates and specifics rather than generalisations;\n- refer to documents as **exhibits** (for example 'AB1'), which are attached;\n" +
            "- end with a **statement of truth**, signed and dated.\n\n" +
            "Keep to what the court asked for and any page limit in the order. Making a false statement verified by a statement of truth can lead to proceedings for contempt.\n\n" +
            "CaseKeeper's *Witness statement* template sets out this layout for you.\n\n" + Disclaimer),

        new("docs.bundles", "Documents, statements & bundles", "Court bundles (PD27A)", "bundle pd27a 350 pages index sections preparation lodging",
            "For many hearings the court needs a **bundle**: the documents in a set order, indexed and paginated (Practice Direction 27A).\n\n" +
            "- Usual sections: A preliminary documents (case summary, chronology, position statements); B applications and orders; C statements; D care plans; E experts' and other reports; F other documents.\n" +
            "- Bundles are generally limited to **350 pages** unless the court allows more.\n- Usually the applicant (or the represented party) prepares it, and the order will say when it must be lodged.\n\n" +
            "Follow any specific directions in your order – they take priority.\n\n" + Disclaimer),

        // ================================================================== orders, enforcement, appeals
        new("enforce", "Orders, enforcement & appeals", "When a child arrangements order isn't followed", "enforcement breach c79 warning notice not following order contact refused",
            "If a child arrangements order is not being followed, you can apply on form **C79** to enforce it. The court will want to know whether there was a **reasonable excuse**.\n\n" +
            "Options include a referral to a course or programme, varying the order, an **enforcement order** (unpaid work), compensation for financial loss, and in serious cases committal for contempt. Child arrangements orders carry a **warning notice** about the consequences of not complying.\n\n" +
            "Keep a factual record of missed or changed arrangements – the contact log is designed for this.\n\n" + Disclaimer),

        new("vary", "Orders, enforcement & appeals", "Changing an order", "vary discharge change order c100 c2 circumstances",
            "Orders about children can be varied if circumstances change. Usually this needs a new application (often on **C100**), and the court will again focus on the child's welfare. Agreements between parents can be recorded in a **consent order**.\n\n" + Disclaimer),

        new("appeals", "Orders, enforcement & appeals", "Appealing a decision", "appeal permission fp161 21 days grounds wrong procedural irregularity",
            "An appeal is not a re-hearing. You usually need **permission to appeal**, and must show the decision was **wrong** or **unjust because of a serious procedural or other irregularity**.\n\n" +
            "- Ask the judge for permission at the end of the hearing where possible.\n- The appellant's notice (form **FP161**) must generally be filed within **21 days** of the decision, unless the court directs otherwise.\n" +
            "- Appeals go to a higher level of judge depending on who made the decision.\n\nTime limits are strict. Get advice quickly.\n\n" + Disclaimer),

        // ================================================================== divorce & money
        new("divorce", "Divorce & money", "Divorce and dissolution", "divorce no fault conditional order final order d8 20 weeks",
            "Since April 2022 divorce and civil partnership dissolution are 'no-fault'. In outline:\n\n" +
            "#. Apply (online or on form **D8**), alone or jointly, stating the marriage has broken down irretrievably.\n" +
            "#. After at least **20 weeks** you can apply for a **conditional order**.\n" +
            "#. At least **6 weeks and 1 day** after that you can apply for the **final order**, which ends the marriage.\n\n" +
            "It's often wise to sort out finances before applying for the final order. Get advice about pensions and inheritance before you do.\n\n" + Disclaimer),

        new("finance", "Divorce & money", "Financial remedy proceedings", "financial remedy form a form e fda fdr consent order d81 section 25",
            "If you can't agree finances after a divorce or dissolution, either party can apply to the court on **Form A**. The usual stages are:\n\n" +
            "#. Exchange of financial disclosure on **Form E**.\n#. **First Appointment (FDA)** – the court identifies the issues and gives directions.\n" +
            "#. **Financial Dispute Resolution (FDR)** hearing – a judge helps the parties try to settle.\n#. Final hearing if still not agreed.\n\n" +
            "The court considers the factors in section 25 of the Matrimonial Causes Act 1973, with first consideration to the welfare of any child under 18. Agreements are usually made binding through a **consent order** (with form **D81**).\n\n" + Disclaimer),

        // ================================================================== public law
        new("care", "Care proceedings", "Care proceedings (public law)", "care proceedings local authority threshold 26 weeks plo social services guardian legal aid",
            "Care proceedings are brought by a local authority when it is worried a child is suffering, or likely to suffer, significant harm (the **threshold** in section 31 of the Children Act 1989).\n\n" +
            "- Before proceedings there is usually a **Public Law Outline (PLO)** pre-proceedings process, with a letter before proceedings and a meeting.\n" +
            "- The child has a **Children's Guardian** from Cafcass and their own solicitor.\n- Courts aim to finish within **26 weeks**.\n" +
            "- Parents with parental responsibility are entitled to **legal aid without a means or merits test** in care proceedings. Get a family law solicitor immediately.\n\n" + Disclaimer),

        // ================================================================== money & help
        new("fees", "Money, fees & legal help", "Court fees and Help with Fees", "court fee help with fees ex160 hwf remission cost",
            "Most applications have a court fee. Current fees are listed in the GOV.UK court fees leaflet (EX50).\n\n" +
            "If you're on certain benefits or a low income, you may get **Help with Fees** – apply online or on form **EX160**, and put the reference on your application.\n\n" + Disclaimer),

        new("legalaid", "Money, fees & legal help", "Legal aid", "legal aid domestic abuse evidence means test eligibility solicitor",
            "Legal aid for private family cases is limited, but may be available if you have evidence of domestic abuse or child abuse (and meet the financial test), and for mediation. It's available without a means or merits test for parents in care proceedings.\n\n" +
            "Check eligibility on GOV.UK ('Check if you can get legal aid') and find a provider with 'Find a legal aid adviser'.\n\n" + Disclaimer),

        new("help", "Money, fees & legal help", "Getting help", "help advice support charities organisations helpline",
            "Organisations that may help (check their websites for current services):\n\n" +
            "- **GOV.UK** – forms, guidance, court finder and fees.\n- **Support Through Court** – free help for people without a lawyer at many courts.\n" +
            "- **Advicenow** – plain-English guides for people representing themselves.\n- **Citizens Advice** and **law centres** – general legal advice.\n" +
            "- **Family Mediation Council** – find an accredited mediator.\n- **Cafcass / Cafcass Cymru** – information for parents and children.\n" +
            "- **Rights of Women** – legal advice lines for women.\n- **National Domestic Abuse Helpline (Refuge)** and **Men's Advice Line** – support for anyone affected by domestic abuse.\n\n" +
            "**In an emergency, call 999.**"),

        // ================================================================== FAQ
        new("faq.solicitor", "Frequently asked questions", "Do I need a solicitor?", "solicitor lawyer need represent",
            "No, you can represent yourself. But advice at key points – before you apply, after a report, before a final hearing, or about an appeal – can make a real difference. Many solicitors offer fixed-fee advice sessions, and some barristers take instructions directly from the public ('direct access').\n\n" + Disclaimer),

        new("faq.mckenzie", "Frequently asked questions", "What is a McKenzie friend?", "mckenzie friend support court",
            "Someone who comes to court with a person representing themselves, to give quiet support, take notes and help with papers. They don't usually speak for you. Tell the court in advance. Some charge a fee; they aren't regulated like lawyers.\n\n" + Disclaimer),

        new("faq.holiday", "Frequently asked questions", "Can I take my child abroad on holiday?", "holiday abroad travel outside uk permission passport consent",
            "Generally you need the consent of everyone with parental responsibility, or the court's permission, to take a child out of the UK. A person named in a **lives with** child arrangements order may take the child abroad for **less than one month** without that consent, unless an order says otherwise. A prohibited steps order may stop travel altogether.\n\nIf in doubt, get written consent or advice before booking.\n\n" + Disclaimer),

        new("faq.surname", "Frequently asked questions", "Can a child's surname be changed?", "change surname name child deed poll",
            "Usually only with the consent of everyone with parental responsibility, or the court's permission. Where there's a child arrangements order, the law says no one may change the child's surname without that consent or permission. The court decides based on the child's welfare.\n\n" + Disclaimer),

        new("faq.record", "Frequently asked questions", "Can I record conversations or handovers?", "record recording covert audio video evidence handover",
            "Courts are cautious about recordings. Secretly recording children, or using recordings in a way that involves the child in the dispute, is generally strongly discouraged and can count against you. Recordings of adults are sometimes admitted, but may be challenged. Think carefully, and get advice before relying on recordings.\n\n" + Disclaimer),

        new("faq.c2", "Frequently asked questions", "What's the difference between a C100 and a C2?", "c100 c2 difference application existing proceedings",
            "A **C100** starts a new children case (child arrangements, prohibited steps or specific issue). A **C2** is for asking for something within a case that's already going – for example to change a hearing date, for permission to do something, or for extra directions.\n\n" + Disclaimer),

        new("faq.wp", "Frequently asked questions", "What does 'without prejudice' mean?", "without prejudice calderbank offer settlement negotiation",
            "Genuine attempts to settle a dispute marked 'without prejudice' generally can't be shown to the court as evidence, so people can negotiate openly. In financial cases, 'without prejudice save as to costs' (Calderbank) offers can be shown later only on the question of costs. Children cases are different – the court will want to know what's best for the child.\n\n" + Disclaimer),

        new("faq.serve", "Frequently asked questions", "How do I serve documents on the other party?", "serve service documents c9 statement of service post email",
            "'Service' means formally giving documents to another party. The court often serves the main application; otherwise the order or rules say who must serve what, how and by when. Methods can include post, personal delivery or email (if the other party has agreed). You may need to confirm service on a **statement of service** (form **C9** in children cases, **FL415** for FL401 applications).\n\n" + Disclaimer),

        new("faq.longtime", "Frequently asked questions", "How long will my case take?", "how long duration timescale weeks months delay",
            "It varies a lot by area and complexity. Simple cases can resolve at the first hearing; contested cases with reports and a fact-finding hearing can take many months. The court is required to avoid delay where it can. Check your latest order for the next steps and dates."),

        // ================================================================== glossary
        new("glossary", "Glossary", "Glossary of family court terms", "glossary terms definitions meaning words jargon",
            "| Term | Meaning |\n|---|---|\n" +
            "| Applicant | The person who made the application |\n| Respondent | The person responding to it |\n" +
            "| Bundle | The indexed, paginated set of documents for a hearing |\n| Cafcass | Children and Family Court Advisory and Support Service (England) |\n" +
            "| CAO | Child arrangements order |\n| C100 | Application for child arrangements, prohibited steps or specific issue orders |\n" +
            "| C1A | Form for allegations of harm and domestic abuse |\n| C2 | Application within existing proceedings |\n| C8 | Form to keep contact details confidential |\n" +
            "| Consent order | An order made with everyone's agreement |\n| Contempt of court | Disobeying an order or the court's rules; can be punished |\n" +
            "| Directions | The court's instructions about what happens next |\n| DRA | Dispute Resolution Appointment |\n" +
            "| Ex parte / without notice | A hearing the other party isn't told about in advance (urgent cases) |\n| Exhibit | A document attached to and referred to in a statement |\n" +
            "| FHDRA | First Hearing Dispute Resolution Appointment |\n| Fact-finding hearing | A hearing to decide whether disputed allegations are true |\n" +
            "| FPR | Family Procedure Rules 2010 |\n| Litigant in person | Someone representing themselves |\n| McKenzie friend | A supporter who helps a litigant in person in court |\n" +
            "| MIAM | Mediation Information and Assessment Meeting |\n| Parental responsibility | The legal rights and duties of a parent |\n" +
            "| PD | Practice Direction – guidance supplementing the rules |\n| Position statement | A short summary of a party's position for a hearing |\n" +
            "| PSO | Prohibited steps order |\n| Section 7 report | A welfare report ordered under s.7 Children Act 1989 |\n" +
            "| Scott schedule | A table of allegations, responses and evidence |\n| SIO | Specific issue order |\n| Statement of truth | The declaration at the end of a statement that its facts are true |\n" +
            "| Welfare checklist | The factors in s.1(3) Children Act 1989 |\n| Without prejudice | Settlement discussions that can't generally be used as evidence |"),
    };
}
