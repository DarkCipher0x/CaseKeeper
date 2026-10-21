using CaseKeeper.Data;
using CaseKeeper.Models;

namespace CaseKeeper.Services.Documents;

/// <summary>
/// Starter templates for the documents people most often need in family proceedings in England &amp;
/// Wales. Text in [square brackets] is guidance to replace. They're a starting point, not legal
/// advice: check any directions the court has given about format and length.
/// </summary>
public static class BuiltInTemplates
{
    public sealed record Def(string Key, string Name, string Kind, string Description, string Body, string Subject = "",
        string Salutation = "Dear Sir or Madam", string SignOff = "Yours faithfully", bool IsDocument = false);

    private const string CourtHeading =
        "[centre] **IN THE FAMILY COURT**\n[centre] sitting at {{case.court}}\n\n[centre] Case number: **{{case.number}}**\n\n" +
        "[centre] **IN THE MATTER OF THE CHILDREN ACT 1989**\n[centre] **AND IN THE MATTER OF {{case.children}}**\n\n" +
        "| Applicant | Respondent |\n|---|---|\n| {{applicant.name}} | {{respondent.name}} |\n\n";

    public static readonly Def[] All =
    {
        new("letter.general", "General letter", "Letter", "A plain letter on your letterhead.",
            "[Write your letter here. Keep it clear and factual.]\n\n[A new paragraph starts after a blank line.]",
            Salutation: "Dear {{recipient.name}}", SignOff: "Yours sincerely"),

        new("letter.court", "Letter to the court", "Letter",
            "For writing to the court about your case. Copy it to the other party at the same time.",
            "I am the {{me.role}} in the above case and I am representing myself.\n\n" +
            "[Explain briefly and politely what you are writing about, and what you are asking for.]\n\n" +
            "[If you are asking for something to happen, such as a change to a hearing date, you may need to make a formal " +
            "application on form C2 instead. The court office can tell you.]\n\n" +
            "I would be grateful if this letter could be placed before the judge.\n\n" +
            "A copy of this letter has been sent to {{otherparty.name}}.",
            Subject: "Case number {{case.number}}", SignOff: "Yours faithfully"),

        new("statement.witness", "Witness statement", "Statement",
            "Numbered paragraphs, court heading and statement of truth, in the usual family court layout.",
            "[right] On behalf of the: {{me.role}}\n[right] Witness: {{me.name}}\n[right] Statement: 1st\n[right] Exhibits: [e.g. AB1 – AB4]\n[right] Date: {{today}}\n\n" +
            CourtHeading +
            "# Witness statement of {{me.name}}\n\n" +
            "#. I am the {{me.role}} in these proceedings. I make this statement in [support of my application / response to the application] dated [date].\n\n" +
            "#. The facts in this statement are within my own knowledge unless I say otherwise. Where they are not, I give the source and believe them to be true.\n\n" +
            "## Background\n\n" +
            "#. [Who the children are, their ages, who they live with, and a short history of the relationship.]\n\n" +
            "## [Next topic, e.g. Current arrangements]\n\n" +
            "#. [One point per paragraph. Give dates. Refer to documents by their exhibit number, e.g. \"I exhibit a copy as AB1\".]\n\n" +
            "## What I am asking the court to do\n\n" +
            "#. [Set out the orders you are asking for and why they are in the children's best interests.]\n\n" +
            "## Statement of truth\n\n" +
            "I believe that the facts stated in this witness statement are true. I understand that proceedings for contempt of court " +
            "may be brought against anyone who makes, or causes to be made, a false statement in a document verified by a statement " +
            "of truth without an honest belief in its truth.\n\n" +
            "Signed: ______________________________\n\nName: {{me.name}}\n\nDate: ______________________________",
            Salutation: "", SignOff: "", IsDocument: true),

        new("statement.position", "Position statement", "Position statement",
            "A short summary for a hearing of where things stand and what you are asking for.",
            CourtHeading +
            "# Position statement of the {{me.role}}\n\n[centre] For the hearing on {{case.nexthearing.date}}\n\n" +
            "## 1. Introduction\n\n#. This position statement is filed on behalf of {{me.name}}, the {{me.role}}, who is [a litigant in person / represented by …].\n\n" +
            "## 2. Background\n\n#. [Very brief history: children, ages, current arrangements, how the case started.]\n\n" +
            "## 3. Since the last hearing\n\n#. [What has happened, including whether directions have been complied with.]\n\n" +
            "## 4. Issues for this hearing\n\n- [Issue 1]\n- [Issue 2]\n\n" +
            "## 5. My position\n\n#. [Your proposals and the reasons for them, focused on the children's welfare.]\n\n" +
            "## 6. Orders sought\n\n#. [The orders and directions you are asking the court to make.]\n\n" +
            "{{me.name}}\n{{today}}",
            Salutation: "", SignOff: "", IsDocument: true),

        new("statement.casesummary", "Case summary", "Statement",
            "A one-to-two page summary of the case for a bundle (Practice Direction 27A, section A).",
            CourtHeading +
            "# Case summary\n\n## The parties and the children\n\n" +
            "| Name | Role | Represented by |\n|---|---|---|\n| {{applicant.name}} | Applicant | {{applicant.representative}} |\n| {{respondent.name}} | Respondent | {{respondent.representative}} |\n\n" +
            "Children: {{case.children}}\n\n## Background\n\n[Short, neutral summary.]\n\n## The applications before the court\n\n- [e.g. C100 dated …]\n\n" +
            "## Issues\n\n- [What the court needs to decide]\n\n## Next hearing\n\n{{case.nexthearing}}",
            Salutation: "", SignOff: "", IsDocument: true),

        new("letter.cafcass", "Letter to Cafcass", "Letter", "To the Family Court Adviser about your case.",
            "I am the {{me.role}} in case {{case.number}} at {{case.court}}.\n\n" +
            "[Explain what you are writing about: information you want them to know, a concern, or a request.]\n\n" +
            "[If you are raising a safeguarding concern, set out the facts: what happened, when, who was there, and any reference numbers (e.g. police).]\n\n" +
            "I would be grateful for an acknowledgement of this letter.",
            Subject: "{{case.title}} – case {{case.number}}", Salutation: "Dear {{cafcass.name}}", SignOff: "Yours sincerely"),

        new("letter.sar", "Subject access request (UK GDPR)", "Letter",
            "Asks an organisation (Cafcass, a council, police, a school…) for a copy of the personal data it holds about you.",
            "I am writing to make a subject access request under Article 15 of the UK General Data Protection Regulation " +
            "for a copy of all personal data you hold about me.\n\n" +
            "To help you find it, my details are:\n\n- Full name: {{me.name}}\n- Address: [your address]\n- Date of birth: [your date of birth]\n- Any reference numbers: [e.g. case number {{case.number}}]\n\n" +
            "In particular I would like: [e.g. all records, notes, emails and reports about me, including those relating to case {{case.number}}].\n\n" +
            "I understand you should normally respond within one month of receiving this request. Please let me know if you need " +
            "anything further to confirm my identity.",
            Subject: "Subject access request – {{me.name}}"),

        new("letter.complaint", "Formal complaint", "Letter", "A stage 1 complaint to an organisation.",
            "I wish to make a formal complaint under your complaints procedure.\n\n## What happened\n\n[Set out the facts in date order.]\n\n" +
            "## Why I am unhappy\n\n[Explain what went wrong, referring to their own policies or standards if you can.]\n\n" +
            "## What I would like you to do\n\n- [e.g. an explanation, an apology, a correction to a record, a change in practice]\n\n" +
            "Please confirm you have received this complaint and tell me when I can expect a full response.",
            Subject: "Formal complaint"),

        new("letter.mp", "Letter to my MP", "Letter",
            "To your Member of Parliament. Family court papers are usually confidential – check Practice Direction 12G before sharing any.",
            "I am a constituent and I am writing to ask for your help.\n\n" +
            "[Explain the issue in general terms. Do not attach or quote from court documents unless the rules allow it: " +
            "Practice Direction 12G generally permits a party to share the text or a summary of a judgment with an MP, not all the case papers.]\n\n" +
            "[Say clearly what you would like your MP to do, e.g. raise a concern with a government department or agency.]\n\n" +
            "My address is [your full address, so they can confirm you are a constituent].",
            Subject: "Request for help", Salutation: "Dear {{recipient.name}}", SignOff: "Yours sincerely"),

        new("letter.transcript", "Request for a transcript of a hearing", "Letter",
            "Covering letter for form EX107 (request for a transcript).",
            "Please find enclosed form EX107 requesting a transcript of [the judgment / the whole hearing] at the hearing on [date] " +
            "in case {{case.number}} before [judge's name].\n\n" +
            "[If you are applying for the transcript at public expense, say so and enclose the supporting information.]",
            Subject: "Request for transcript – case {{case.number}}"),

        new("letter.adjourn", "Request to adjourn a hearing", "Letter",
            "Asks the court to move a hearing. Usually needs a C2 application and fee – check with the court.",
            "I am writing to ask the court to adjourn the hearing listed on {{case.nexthearing.date}}.\n\n" +
            "The reason is [explain clearly, with evidence, e.g. a medical letter].\n\n" +
            "I have asked {{otherparty.name}} whether they agree and they [agree / do not agree / have not replied].\n\n" +
            "My dates of availability over the next [number] weeks are: [dates].\n\n" +
            "I understand a formal application on form C2 may be needed. A copy of this letter has been sent to {{otherparty.name}}.",
            Subject: "Request to adjourn – case {{case.number}}"),

        new("letter.proposal", "Proposal for arrangements (without prejudice)", "Letter",
            "A proposal to the other parent. Marked without prejudice so it can't normally be shown to the court.",
            "**WITHOUT PREJUDICE SAVE AS TO COSTS**\n\n" +
            "I would like to try to agree arrangements for {{case.children}} without needing the court to decide.\n\n" +
            "## My proposal\n\n- Living arrangements: [ ]\n- Time with each parent: [ ]\n- Holidays and special occasions: [ ]\n- Handovers: [ ]\n- Communication: [ ]\n\n" +
            "I would welcome your response by [date]. I am also willing to attend mediation.",
            Subject: "Arrangements for the children", Salutation: "Dear {{otherparty.name}}", SignOff: "Yours sincerely"),

        new("letter.compliance", "Chasing a court direction", "Letter",
            "To the other party when something the court ordered hasn't been done.",
            "The order dated [date] required [what] to be [filed / served / done] by [date]. I have not yet received it.\n\n" +
            "Please confirm by [date] when this will be done. If it is not, I may need to ask the court for further directions.",
            Subject: "Order of [date] – case {{case.number}}", Salutation: "Dear {{otherparty.name}}", SignOff: "Yours sincerely"),
    };

    /// <summary>Adds any built-in template the library doesn't have yet. Never overwrites one that has been edited.</summary>
    public static void EnsureInstalled(Database db)
    {
        try
        {
            var have = db.All<DocTemplate>().Where(t => t.IsBuiltIn).Select(t => t.BuiltInKey).ToHashSet();
            foreach (var d in All.Where(d => !have.Contains(d.Key)))
                db.Save(ToTemplate(d));
        }
        catch (Exception ex) { AppLog.Error("Templates", "Couldn't install the built-in templates", ex); }
    }

    public static DocTemplate ToTemplate(Def d) => new()
    {
        Name = d.Name, Kind = d.Kind, Description = d.Description, Body = d.Body, IsBuiltIn = true, BuiltInKey = d.Key, UpdatedAt = DateTime.Now,
        DefaultSubject = d.Subject, DefaultSalutation = d.Salutation, DefaultSignOff = d.SignOff, IsDocument = d.IsDocument
    };

    public static Def Find(string key) => All.FirstOrDefault(d => d.Key == key);
}
