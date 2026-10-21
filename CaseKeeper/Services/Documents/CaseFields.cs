using System.Globalization;
using CaseKeeper.Data;
using CaseKeeper.Models;

namespace CaseKeeper.Services.Documents;

/// <summary>
/// The {{fields}} that templates, letters and imported Word layouts can use, filled from the
/// case details, the parties, the next hearing, the letterhead and the letter itself.
/// </summary>
public static class CaseFields
{
    public sealed record FieldInfo(string Key, string Group, string Description);

    /// <summary>Every field, grouped for the "Insert field" menu.</summary>
    public static readonly FieldInfo[] All =
    {
        new("today", "Dates", "Today's date, e.g. 17 October 2026"),
        new("today.short", "Dates", "Today's date, e.g. 17/10/2026"),
        new("letter.date", "Letter", "The date on the letter"),
        new("letter.subject", "Letter", "The letter's subject line"),
        new("letter.ourref", "Letter", "Our reference"),
        new("letter.theirref", "Letter", "Their reference"),
        new("recipient.name", "Letter", "Who the letter is to"),
        new("recipient.address", "Letter", "Their address (several lines)"),
        new("salutation", "Letter", "Dear …"),
        new("signoff", "Letter", "Yours faithfully / sincerely"),
        new("body", "Letter", "Where the main text goes in a Word layout"),
        new("case.number", "Case", "Case number"),
        new("case.court", "Case", "Court / hearing centre"),
        new("case.type", "Case", "Type of proceedings"),
        new("case.judge", "Case", "Judge / magistrates"),
        new("case.title", "Case", "Case title, e.g. Re: A and B (Children)"),
        new("case.stage", "Case", "Current stage"),
        new("case.children", "Case", "Names (and ages) of the children"),
        new("case.nexthearing", "Case", "Date, time and type of the next hearing"),
        new("case.nexthearing.date", "Case", "Date of the next hearing"),
        new("me.name", "Me", "Your name (the party marked 'This is me')"),
        new("me.role", "Me", "Your role, e.g. Applicant"),
        new("me.contact", "Me", "Your contact details"),
        new("me.representative", "Me", "Your solicitor, or Litigant in person"),
        new("applicant.name", "Parties", "Applicant's name"),
        new("applicant.representative", "Parties", "Applicant's representative"),
        new("respondent.name", "Parties", "Respondent's name"),
        new("respondent.representative", "Parties", "Respondent's representative"),
        new("otherparty.name", "Parties", "The other main party (not you)"),
        new("cafcass.name", "Parties", "Cafcass officer's name"),
        new("guardian.name", "Parties", "Children's Guardian's name"),
        new("sender.name", "Letterhead", "Name on the letterhead"),
        new("sender.address", "Letterhead", "Address on the letterhead"),
        new("sender.phone", "Letterhead", "Phone on the letterhead"),
        new("sender.email", "Letterhead", "E-mail on the letterhead"),
    };

    public static Dictionary<string, string> Build(Database db, LetterDraft letter = null, Letterhead lh = null)
    {
        var uk = CultureInfo.GetCultureInfo("en-GB");
        var f = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["today"] = DateTime.Today.ToString("d MMMM yyyy", uk),
            ["today.short"] = DateTime.Today.ToString("dd/MM/yyyy", uk)
        };

        foreach (var key in new[] { "number", "court", "type", "judge", "stage", "title" })
            f["case." + key] = db.GetSetting("case." + key, "") ?? "";

        var parties = db.GetParties();
        Party First(Func<Party, bool> pred) => parties.FirstOrDefault(pred);
        var me = First(p => p.IsMe);
        var applicant = First(p => p.RoleGroup == "Applicant");
        var respondent = First(p => p.RoleGroup == "Respondent");
        var other = me == null ? null : First(p => p != me && p.RoleGroup is "Applicant" or "Respondent" && p.RoleGroup != me.RoleGroup);
        f["me.name"] = me?.Name ?? "";
        f["me.role"] = me?.Role ?? "";
        f["me.contact"] = me?.Contact ?? "";
        f["me.representative"] = string.IsNullOrWhiteSpace(me?.Representative) ? "Litigant in person" : me.Representative;
        f["applicant.name"] = applicant?.Name ?? "";
        f["applicant.representative"] = applicant?.Representative ?? "";
        f["respondent.name"] = respondent?.Name ?? "";
        f["respondent.representative"] = respondent?.Representative ?? "";
        f["otherparty.name"] = other?.Name ?? "";
        f["cafcass.name"] = First(p => p.Role.Contains("Cafcass", StringComparison.OrdinalIgnoreCase))?.Name ?? "";
        f["guardian.name"] = First(p => p.Role.Contains("Guardian", StringComparison.OrdinalIgnoreCase))?.Name ?? "";
        f["case.children"] = string.Join(", ", parties.Where(p => p.Role.StartsWith("Child", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.DateOfBirth is { } dob ? $"{p.Name} ({Age(dob)})" : p.Name).Where(s => !string.IsNullOrWhiteSpace(s)));

        var next = db.All<Hearing>().Where(h => h.IsUpcoming).OrderBy(h => h.HearingDate).FirstOrDefault();
        f["case.nexthearing"] = next == null ? "" : $"{next.HearingDate:d MMMM yyyy}{(string.IsNullOrWhiteSpace(next.Time) ? "" : " at " + next.Time)} – {next.HearingType}";
        f["case.nexthearing.date"] = next?.HearingDate?.ToString("d MMMM yyyy", uk) ?? "";
        if (string.IsNullOrWhiteSpace(f["case.court"]) && next != null) f["case.court"] = next.Court;

        if (lh != null)
        {
            f["sender.name"] = lh.SenderName ?? "";
            f["sender.address"] = lh.AddressLines ?? "";
            f["sender.phone"] = lh.Phone ?? "";
            f["sender.email"] = lh.Email ?? "";
        }
        else
        {
            f["sender.name"] = me?.Name ?? "";
            f["sender.address"] = me?.Contact ?? "";
            f["sender.phone"] = "";
            f["sender.email"] = "";
        }

        if (letter != null)
        {
            f["letter.date"] = (letter.LetterDate ?? DateTime.Today).ToString("d MMMM yyyy", uk);
            f["letter.subject"] = letter.Subject ?? "";
            f["letter.ourref"] = letter.OurReference ?? "";
            f["letter.theirref"] = letter.TheirReference ?? "";
            f["recipient.name"] = letter.RecipientName ?? "";
            f["recipient.address"] = letter.RecipientAddress ?? "";
            f["salutation"] = letter.Salutation ?? "";
            f["signoff"] = letter.SignOff ?? "";
        }
        return f;
    }

    private static string Age(DateTime dob)
    {
        var t = DateTime.Today;
        int a = t.Year - dob.Year - (t.DayOfYear < dob.DayOfYear ? 1 : 0);
        return a <= 0 ? "under 1" : $"aged {a}";
    }
}

/// <summary>Turns a letter (or formal document) into a <see cref="DocModel"/> ready to preview or export.</summary>
public static class LetterComposer
{
    public static DocModel Compose(LetterDraft l, IReadOnlyDictionary<string, string> fields, ICollection<string> missing = null)
    {
        var doc = new DocModel { Title = l.Title, ShowTitle = false, Footer = "" };
        string M(string s) => Markup.Merge(s ?? "", fields, missing);

        if (l.IsDocument)
        {
            // A formal document (statement, position statement…): the body carries its own headings
            doc.AddRange(Markup.Parse(M(l.Body)));
            return doc;
        }

        if (!string.IsNullOrWhiteSpace(l.RecipientName) || !string.IsNullOrWhiteSpace(l.RecipientAddress))
            doc.Add(Block.Para(string.Join("\n", new[] { M(l.RecipientName), M(l.RecipientAddress) }.Where(s => !string.IsNullOrWhiteSpace(s)))));
        doc.Add(Block.Para((l.LetterDate ?? DateTime.Today).ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB")), BlockAlign.Right));

        var refs = new List<string>();
        if (!string.IsNullOrWhiteSpace(l.TheirReference)) refs.Add("Your ref: " + M(l.TheirReference));
        if (!string.IsNullOrWhiteSpace(l.OurReference)) refs.Add("Our ref: " + M(l.OurReference));
        if (refs.Count > 0) doc.Add(Block.Para(string.Join("\n", refs)));

        if (!string.IsNullOrWhiteSpace(l.Salutation)) doc.Add(Block.Para(M(l.Salutation)));
        if (!string.IsNullOrWhiteSpace(l.Subject)) doc.Add(Block.Para("Re: " + M(l.Subject), bold: true));
        doc.AddRange(Markup.Parse(M(l.Body)));
        if (!string.IsNullOrWhiteSpace(l.SignOff))
        {
            doc.Add(Block.Para(M(l.SignOff)));
            doc.Add(new Block { Kind = BlockKind.Spacer });
            doc.Add(new Block { Kind = BlockKind.Spacer });
            var name = fields.TryGetValue("sender.name", out var n) && !string.IsNullOrWhiteSpace(n) ? n
                : fields.TryGetValue("me.name", out var me) ? me : "";
            if (!string.IsNullOrWhiteSpace(name)) doc.Add(Block.Para(name));
            if (fields.TryGetValue("me.role", out var role) && !string.IsNullOrWhiteSpace(role)) doc.Add(Block.Para(role));
        }
        return doc;
    }
}
