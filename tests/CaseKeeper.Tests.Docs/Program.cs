using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CaseKeeper.Models;
using CaseKeeper.Services;
using CaseKeeper.Services.Documents;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;

namespace CaseKeeper.Services { public static class AppLog { public static void Warn(string a, string m, Exception e = null) { } } }

static class Program
{
    static int failures;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static int Validate(byte[] docx, string label)
    {
        using var ms = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(ms, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(doc).ToList();
        foreach (var e in errors.Take(8)) Console.WriteLine($"   [{label}] {e.Description} @ {e.Path?.XPath}");
        return errors.Count;
    }

    static byte[] Png()
    {
        var bmp = new WriteableBitmap(120, 48, 96, 96, PixelFormats.Bgra32, null);
        var px = new byte[120 * 48 * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 208; px[i + 1] = 214; px[i + 2] = 61; px[i + 3] = 255; }
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, 120, 48), px, 120 * 4, 0);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    [STAThread]
    static void Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(outDir);
        var body = "# Heading one\n\nA paragraph with **bold**, *italic* and __underline__.\nA second line.\n\n## Sub\n\n- bullet\n  - sub bullet\n\n1. one\n2. two\n\n#. statement para\n#. next para\n\n> a quote\n\n| A | B |\n|---|---|\n| 1 | 2 & <x> |\n\n---\n\n[right] right aligned\n\nSigned: ______________\n\n[page break]\n\nEnd.";
        var blocks = Markup.Parse(body);
        Check(blocks.Any(b => b.Kind == BlockKind.Table && b.Table.Headers.Count == 2), "markup: table with header row");
        Check(blocks.Count(b => b.Kind == BlockKind.Numbered && b.Number == 2) >= 1, "markup: numbering");
        Check(blocks.Any(b => b.PlainText.Contains("______________")), "markup: signature underscores kept");
        Check(blocks.Any(b => b.Inlines.Any(i => i.Underline && i.Text == "underline")), "markup: underline");

        var doc = new DocModel { Title = "Test document", Subtitle = "Sub", Footer = "Confidential" };
        doc.AddRange(blocks);

        var logo = Png();
        foreach (var layout in new[] { "Classic", "Band", "Minimal" })
        {
            var lh = new Letterhead { Name = "T", SenderName = "Alex Test", AddressLines = "1 Street\nTown", Phone = "0100", Email = "a@example.test",
                Layout = layout, LogoPng = logo, LogoAlign = layout == "Classic" ? "Right" : "Left", FooterText = "Private" };
            var docx = DocxDocWriter.Render(doc, lh);
            File.WriteAllBytes(Path.Combine(outDir, $"render-{layout}.docx"), docx);
            Check(Validate(docx, layout) == 0, $"docx {layout} letterhead validates");
            var pdf = new PdfDocWriter(doc, lh).Render();
            File.WriteAllBytes(Path.Combine(outDir, $"render-{layout}.pdf"), pdf);
            Check(pdf.Length > 1000 && !System.Text.Encoding.Latin1.GetString(pdf).Contains("/Author (" + Environment.UserName), $"pdf {layout} rendered, no user name");
            var html = HtmlDocWriter.Render(doc, lh);
            File.WriteAllText(Path.Combine(outDir, $"render-{layout}.html"), html);
            Check(!html.Contains("<script") && html.Contains("2 &amp; &lt;x&gt;"), $"html {layout} escaped, no script");
        }
        Check(Validate(DocxDocWriter.Render(doc, null), "plain") == 0, "docx without letterhead validates");

        // ---- an imported Word template with a field split across runs, a header field, comments and an author
        byte[] template;
        using (var ms = new MemoryStream())
        {
            using (var w = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Template))
            {
                var main = w.AddMainDocumentPart();
                main.Document = new Document(new Body(
                    new Paragraph(new Run(new Text("Case: {{case.")), new Run(new RunProperties(new Bold()), new Text("num")), new Run(new Text("ber}} end"))),
                    new Paragraph(new Run(new Text("To: {{recipient.address}}"))),
                    new Paragraph(new Run(new Text("{{body}}"))),
                    new Paragraph(new CommentRangeStart { Id = "1" }, new Run(new Text("Unknown {{nope}}")), new CommentRangeEnd { Id = "1" },
                        new Run(new CommentReference { Id = "1" })),
                    new SectionProperties()));
                var hp = main.AddNewPart<HeaderPart>();
                hp.Header = new Header(new Paragraph(new Run(new Text("Header {{sender.name}}"))));
                var cp = main.AddNewPart<WordprocessingCommentsPart>();
                cp.Comments = new Comments(new Comment(new Paragraph(new Run(new Text("secret comment")))) { Id = "1", Author = "Secret Person" });
                w.PackageProperties.Creator = "Secret Person";
                w.PackageProperties.LastModifiedBy = "Secret Person";
            }
            template = ms.ToArray();
        }
        var fields = new Dictionary<string, string> { ["case.number"] = "ZZ123", ["recipient.address"] = "Line 1\nLine 2", ["sender.name"] = "Alex Test" };
        var missing = new List<string>();
        var filled = DocxDocWriter.FillTemplate(template, fields, Markup.Parse("Body **text** here.\n\n- point"), missing, "Filled");
        File.WriteAllBytes(Path.Combine(outDir, "filled.docx"), filled);
        Check(Validate(filled, "filled") == 0, "filled template validates");
        using (var ms = new MemoryStream(filled))
        using (var w = WordprocessingDocument.Open(ms, false))
        {
            var text = w.MainDocumentPart.Document.InnerText;
            Check(text.Contains("Case: ZZ123 end"), "split-run field replaced: " + text[..Math.Min(40, text.Length)]);
            Check(text.Contains("Body text here."), "{{body}} replaced with content");
            Check(!text.Contains("{{body}}"), "no {{body}} left");
            Check(w.MainDocumentPart.HeaderParts.First().Header.InnerText.Contains("Alex Test"), "header field replaced");
            Check(w.MainDocumentPart.Document.Descendants<Break>().Any(), "multi-line value became line breaks");
            Check(string.IsNullOrEmpty(w.PackageProperties.Creator) && string.IsNullOrEmpty(w.PackageProperties.LastModifiedBy), "author metadata removed");
            Check(w.MainDocumentPart.WordprocessingCommentsPart == null, "comments (with author names) removed");
            Check(w.DocumentType == WordprocessingDocumentType.Document, ".dotx became .docx");
        }
        Check(missing.Contains("nope"), "unknown field reported as missing");
        Check(DocxDocWriter.ToMarkup(template).Contains("{{case.number}}") || DocxDocWriter.ToMarkup(template).Contains("{{case.**num**ber}}"), "template converts to markup");
        Console.WriteLine("   markup: " + DocxDocWriter.ToMarkup(template).Replace("\n", " ⏎ "));

        // ---- zip-bomb / junk protection
        try { WordSafety.Check(new byte[] { 1, 2, 3 }); Check(false, "junk file rejected"); }
        catch (InvalidDataException) { Check(true, "junk file rejected"); }

        // ---- deadlines
        Check(Deadlines.EasterSunday(2026) == new DateTime(2026, 4, 5), "Easter 2026");
        var bh = Deadlines.BankHolidays(2026).OrderBy(d => d).ToList();
        Console.WriteLine("   2026 bank holidays: " + string.Join(", ", bh.Select(d => d.ToString("ddd d MMM"))));
        Check(bh.Count == 8 && bh.Contains(new DateTime(2026, 12, 28)), "2026 has 8 bank holidays incl. Boxing Day substitute (Mon 28 Dec)");
        Check(Deadlines.BankHolidays(2023).Contains(new DateTime(2023, 5, 8)), "2023 coronation holiday");
        Check(Deadlines.ClearDaysBefore(new DateTime(2026, 10, 19), 3) == new DateTime(2026, 10, 13), "3 clear days before Mon 19 Oct = Tue 13 Oct");
        Check(Deadlines.DaysAfter(new DateTime(2026, 12, 4), 21) == new DateTime(2026, 12, 29), "21 days after 4 Dec 2026 lands on 25 Dec -> next working day 29 Dec");

        // ---- help assistant: links in answers and guide lookup
        var (cleaned, hl) = HelpKnowledge.ExtractLinks(
            "Go to **Letters** – [Open Letters & templates](go:letters). Then check (settings:ai). Bogus [x](go:nowhere) and (guide:app.ai).");
        Check(hl.Count == 3 && hl[0].Target == "letters" && hl[1].Kind == "settings" && hl[2].Kind == "guide", "help: valid links extracted, made-up screen ignored");
        Check(!cleaned.Contains("(go:") && !cleaned.Contains("(settings:") && cleaned.Contains("Open Letters & templates") && cleaned.Contains("Bogus x"),
            "help: answer text reads naturally without link syntax");
        var guideText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "USER-GUIDE.md"));
        Check(System.Text.RegularExpressions.Regex.IsMatch(guideText, @"(?m)^## \d+\. Letters") && guideText.Contains("## 4. Bulk Case Import"),
            "help: user guide available to the assistant (including Bulk Case Import)");
        var rel = HelpKnowledge.Relevant("how do I use my own word letterhead");
        Check(rel.Count > 0 && rel[0].Title.Contains("Letters"), "help: letterhead question finds the letters section (" + (rel.FirstOrDefault().Title ?? "none") + ")");
        Check(HelpKnowledge.AppMap.Contains("(go:letters)") && HelpKnowledge.Screens.Keys.All(k => k == "import" || k == "assistant" || HelpKnowledge.AppMap.Contains($"(go:{k})")),
            "help: every screen link is listed in the app map");
        var (c2, l2) = HelpKnowledge.ExtractLinks("See the guide [go:guide].\n\nLinks:\n[Open Contact log](go:contact)");
        Check(l2.Count == 2 && !c2.Contains("Links:") && !c2.Contains("go:"), "help: [go:x] form accepted, empty 'Links:' heading removed");
        Check(HelpKnowledge.Recipes.SelectMany(r => r.Links).All(l => { var t = new List<HelpKnowledge.Link>(); HelpKnowledge.AddLink(t, l); return t.Count == 1; }),
            "help: every link in the ready-made directions points somewhere real");
        var known = HelpKnowledge.AppMap + guideText;
        var unknown = HelpKnowledge.Recipes.SelectMany(r => System.Text.RegularExpressions.Regex.Matches(r.Steps, @"\*\*(.+?)\*\*").Select(m => m.Groups[1].Value))
            .Distinct().Where(n => !known.Contains(n.TrimEnd('…', '.', ' '), StringComparison.OrdinalIgnoreCase)).ToList();
        Check(unknown.Count == 0, "help: every bold name in the directions exists in the app map or guide" + (unknown.Count > 0 ? ": " + string.Join(", ", unknown) : ""));
        foreach (var (q, want) in new[] { ("will I get custody of my kids?", "law"), ("how do i password protect everything", "Password"),
                     ("he didn't turn up for contact", "contact"), ("how do I find form C1A", "category"), ("make a court bundle", "bundle"),
                     ("I need legal aid forms", "category"), ("how do I move everything to my new laptop", "Back up") })
        {
            var m = HelpKnowledge.MatchRecipes(q).FirstOrDefault();
            Check(m != null && m.Title.Contains(want, StringComparison.OrdinalIgnoreCase), $"help: \"{q}\" → {m?.Title ?? "nothing"}");
        }
        // Bulk Case Import: the questions the help assistant must answer with the right directions
        foreach (var (q, want) in new[]
                 {
                     ("How do I import an existing case folder?", "whole case folder"), ("Can CaseKeeper include subfolders?", "whole case folder"),
                     ("What happens to my original files?", "original files"), ("How are duplicates handled?", "Duplicates"),
                     ("How does AI categorisation work?", "confidence"), ("Does my case data leave my computer?", "leave my computer"),
                     ("How do I review unknown documents?", "Unknown"), ("How do I approve extracted hearing dates?", "Approve"),
                     ("How do I correct an AI suggestion?", "Approve"), ("How do I pause or resume analysis?", "Pause"),
                     ("What happens if Ollama is not running?", "Ollama not running"), ("How do I rerun failed analysis?", "analysis failed"),
                     ("How do I remove or undo an approved suggestion?", "Undo"), ("Why was a document placed in Unknown?", "Unknown"),
                     ("How do confidence scores work?", "confidence"), ("how do i get back something i deleted", "Delete or get back"),
                     ("The AI analysing takes a very long time, can I speed it up?", "faster"), ("Which AI model is best for importing a folder?", "faster"),
                     ("How do I use Claude with my Anthropic API key?", "Claude or ChatGPT"), ("Can I use OpenAI on Azure Foundry?", "Microsoft Foundry"),
                     ("What gets sent to the cloud AI, is it masked?", "masking"), ("Which of these documents is the newest version?", "versions"),
                     ("How do I install Ollama?", "local AI"),
                     ("Can you check and organise my whole library?", "whole library"), ("How do I sort out everything I have already?", "whole library"),
                     ("Show me how my case is laid out", "Case map"), ("Is there a matrix view of my case?", "Case map"),
                     ("How do I put complaints in the Complaints section?", "complaints"), ("What should I organise next?", "organise next")
                 })
        {
            var m = HelpKnowledge.MatchRecipes(q).FirstOrDefault();
            Check(m != null && m.Title.Contains(want, StringComparison.OrdinalIgnoreCase), $"help: \"{q}\" → {m?.Title ?? "nothing"}");
        }
        var (bulkText, bulkLinks) = HelpKnowledge.Finish("Use the folder import. [Open Import review](go:review)", "How do I import an existing case folder?");
        Check(bulkLinks[0].Target == "bulkimport" && bulkLinks.Any(l => l.Target == "review"), "help: the Bulk Case Import button comes first for folder questions");
        Check(HelpKnowledge.AppMap.Contains("(go:bulkimport)") && HelpKnowledge.AppMap.Contains("(guide:app.bulkimport)"), "help: the app map lists the Bulk Case Import links");
        Check(new[] { "(go:librarycheck)", "(go:casemap)", "(go:overview)", "(guide:app.librarycheck)", "(guide:app.casemap)" }.All(HelpKnowledge.AppMap.Contains) &&
              new[] { "librarycheck", "casemap", "overview" }.All(HelpKnowledge.Screens.ContainsKey), "help: the library check and Case map links are listed and valid");
        Check(CaseKeeper.Services.Guide.GuideContent.All.Any(a => a.Id == "app.librarycheck") && CaseKeeper.Services.Guide.GuideContent.All.Any(a => a.Id == "app.casemap"),
            "guide: the library check and Case map articles exist");
        var (strayText, stray) = HelpKnowledge.Finish("1. No, it stays on this computer.\n\nLinks: [Open Letters & templates](go:letters)", "Does my case data leave my computer?");
        Check(!stray.Any(l => l.Target == "letters") && stray.Any(l => l.Target == "ai") && !strayText.Contains("Letters"),
            "help: a link the AI copied from its instructions that has nothing to do with the question is dropped");
        var (_, kept) = HelpKnowledge.Finish("Write it in **Letters & templates**. [Open Letters & templates](go:letters)", "how do I write to the school");
        Check(kept.Any(l => l.Target == "letters"), "help: a link the answer actually talks about is kept");

        var (_, auto) = HelpKnowledge.Finish("Open the bundles screen and tick some documents.", "make a court bundle");
        Check(auto.Any(l => l.Target == "bundles"), "help: a button is added when the AI forgets to link");
        var (t3, wrong) = HelpKnowledge.Finish("1. Right-click → **Give exhibit numbers…**\n\n[Open Letters & templates](go:letters)", "how do i number my exhibits");
        Check(wrong[0].Target == "documents" && !t3.Contains("Open Letters"), "help: the right screen's button comes first, leftover link line removed");

        Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILURE(S)");
        Environment.Exit(failures);
    }
}
