using CaseKeeper.Models;

namespace CaseKeeper.Data;

/// <summary>
/// The starting list of categories, based on common forms and documents in the
/// family courts of England &amp; Wales. You can add your own from within the app.
///
/// Documents that each side files separately come as a pair, "(Applicant's)" and
/// "(Respondent's)". Documents produced by the court, Cafcass or another body are single,
/// because there is only ever one version of them.
///
/// Bundle sections follow Practice Direction 27A:
/// A = Preliminary documents, B = Applications &amp; orders, C = Statements &amp; affidavits,
/// D = Care plans, E = Expert &amp; other reports (inc. Cafcass), F = Other documents.
/// </summary>
public static class DefaultCategories
{
    public const string ApplicantSuffix = "(Applicant's)";
    public const string RespondentSuffix = "(Respondent's)";

    public static readonly Dictionary<string, string> BundleSections = new()
    {
        ["A"] = "Preliminary Documents",
        ["B"] = "Applications and Orders",
        ["C"] = "Statements and Affidavits",
        ["D"] = "Care Plans",
        ["E"] = "Experts' Reports and Other Reports",
        ["F"] = "Other Documents",
    };

    /// <summary>
    /// Categories from earlier versions that have been replaced by Applicant's/Respondent's pairs.
    /// They are only removed when nothing is filed under them, so existing documents are never disturbed.
    /// </summary>
    public static readonly string[] SupersededNames =
    {
        "My witness statement", "Other party's witness statement", "Case summary", "Chronology",
        "Position statement", "Skeleton argument", "Scott schedule (allegations)",
        "Photographs", "Messages / texts / emails (screenshots)", "Contact log / diary",
        "Financial records", "Other evidence", "My solicitor / barrister", "Other party / their solicitor",
    };

    public static List<Category> Build()
    {
        var list = new List<Category>();
        int order = 0;

        // A document only one body ever produces: the court, Cafcass, the police, and so on.
        void One(string group, string code, string name, string section, string desc = "") =>
            list.Add(new Category { GroupName = group, Code = code, Name = name, BundleSection = section, Description = desc, SortOrder = order++ });

        // A document each side files its own version of.
        void Pair(string group, string code, string name, string section, string desc = "")
        {
            One(group, code, $"{name} {ApplicantSuffix}", section, desc);
            One(group, code, $"{name} {RespondentSuffix}", section, desc);
        }

        const string apps = "Court Applications & Forms";
        Pair(apps, "C100", "Child arrangements / prohibited steps / specific issue application", "B", "Main application under the Children Act 1989");
        Pair(apps, "C1", "Application for an order", "B", "General Children Act application not covered by C100");
        Pair(apps, "C1A", "Allegations of harm and domestic abuse", "B", "Supplemental information form about harm/abuse");
        Pair(apps, "C2", "Application in existing proceedings", "B", "Permission, directions, variation or other request within existing case");
        Pair(apps, "C3", "Application to search for / take charge of a child", "B");
        Pair(apps, "C4", "Application for disclosure of a child's whereabouts", "B");
        Pair(apps, "C7", "Acknowledgement", "B", "Acknowledgement of an application by the person responding to it");
        Pair(apps, "C8", "Confidential contact details", "B", "Keep details confidential from other parties");
        Pair(apps, "C9", "Statement of service", "B");
        Pair(apps, "C78", "Application for attachment of a warning notice", "B");
        Pair(apps, "C79", "Application related to enforcement of a child arrangements order", "B");
        One(apps, "C110A", "Application for care / supervision order (public law)", "B", "Made by a local authority");
        Pair(apps, "FM1", "MIAM / family mediation information", "B", "Mediation Information & Assessment Meeting form");
        Pair(apps, "FP2", "Application notice (Part 18)", "B");
        Pair(apps, "FL401", "Non-molestation / occupation order application", "B");
        Pair(apps, "FL403", "Application to vary, extend or discharge an order", "B");
        Pair(apps, "FL407", "Application for a warrant of arrest", "B");
        Pair(apps, "FP161", "Appellant's notice (appeal)", "B");
        Pair(apps, "EX160", "Help with fees application", "F");
        Pair(apps, "", "Other court form", "B");

        const string orders = "Court Orders & Notices";
        One(orders, "", "Notice of hearing", "B");
        One(orders, "", "Directions / case management order", "B");
        One(orders, "", "Interim order", "B");
        One(orders, "", "Child arrangements order", "B");
        One(orders, "", "Prohibited steps order", "B");
        One(orders, "", "Specific issue order", "B");
        One(orders, "", "Non-molestation / occupation order", "B");
        One(orders, "", "Enforcement order", "B");
        One(orders, "", "Section 91(14) order", "B");
        One(orders, "", "Final order", "B");
        One(orders, "", "Judgment / approved judgment", "B");

        const string statements = "Statements & Submissions";
        Pair(statements, "", "Case summary", "A");
        Pair(statements, "", "Chronology", "A");
        Pair(statements, "", "Position statement", "A");
        Pair(statements, "", "Skeleton argument", "A");
        Pair(statements, "", "Scott schedule (allegations)", "A");
        Pair(statements, "", "Witness statement", "C");
        One(statements, "", "Third-party witness statement", "C", "A statement by someone who is not a party");
        One(statements, "", "Hearing transcript", "F");

        const string cafcass = "CAFCASS";
        One(cafcass, "", "Safeguarding letter", "E", "Initial safeguarding checks before the first hearing (FHDRA)");
        One(cafcass, "S7", "Section 7 report", "E", "Welfare report ordered under s.7 Children Act 1989");
        One(cafcass, "S16A", "Section 16A risk assessment", "E");
        One(cafcass, "", "Child impact analysis / case analysis", "E");
        One(cafcass, "R16.4", "Children's guardian report (Rule 16.4)", "E");
        One(cafcass, "", "Cafcass interview / meeting notes", "F");
        One(cafcass, "", "Cafcass correspondence", "F");
        One(cafcass, "", "Cafcass complaint", "F");
        One(cafcass, "", "Cafcass complaint response", "F");
        One(cafcass, "", "Other Cafcass document", "E");

        const string agencies = "Local Authority & Agencies";
        One(agencies, "S37", "Section 37 report", "E");
        One(agencies, "S47", "Section 47 / child protection documents", "E");
        One(agencies, "", "Children's services correspondence", "F");
        One(agencies, "", "Police disclosure / records", "E");
        One(agencies, "", "School records", "E");
        One(agencies, "", "Medical / health records", "E");
        One(agencies, "", "Expert report (psychological, testing, etc.)", "E");
        One(agencies, "", "Contact centre report", "E");
        One(agencies, "", "Care plan", "D");

        const string evidence = "Evidence";
        Pair(evidence, "", "Photographs", "F");
        Pair(evidence, "", "Messages / texts / emails (screenshots)", "F");
        Pair(evidence, "", "Contact log / diary", "F");
        Pair(evidence, "", "Financial records", "F");
        Pair(evidence, "", "Other evidence", "F");

        const string exhibits = "Evidence Exhibits";
        Pair(exhibits, "", "Exhibit bundle", "F", "A set of exhibits filed together");
        Pair(exhibits, "", "Exhibit index / schedule", "F", "The list of what each exhibit is");
        Pair(exhibits, "", "Individual exhibit", "F", "A single exhibit referred to in a statement");
        Pair(exhibits, "", "Exhibit to a witness statement", "C", "Exhibited to and filed with a statement");
        Pair(exhibits, "", "Audio / video recording", "F");
        Pair(exhibits, "", "Transcript of a recording", "F");
        Pair(exhibits, "", "Expert or technical exhibit", "E");

        const string corr = "Correspondence";
        One(corr, "", "Court correspondence", "F");
        Pair(corr, "", "Solicitor / barrister correspondence", "F");
        One(corr, "", "Correspondence between the parties", "F");
        One(corr, "", "Mediation correspondence", "F");

        const string complaints = "Complaints & Representations";
        One(complaints, "", "Letters to my MP", "F");
        One(complaints, "", "MP responses", "F");
        One(complaints, "", "Ombudsman (PHSO / LGSCO)", "F");
        One(complaints, "", "Judicial Conduct (JCIO)", "F");
        One(complaints, "", "Other complaint letters", "F");
        One(complaints, "", "Complaint responses", "F");

        const string finance = "Divorce & Finances";
        Pair(finance, "D8", "Divorce / dissolution application", "B");
        Pair(finance, "Form A", "Financial remedy application", "B");
        Pair(finance, "Form E", "Financial statement", "C");
        One(finance, "", "Consent order", "B");

        One("Other", "", "Uncategorised", "F");

        AddExtendedCatalogue((g, c, n, s, d) => One(g, c, n, s, d), (g, c, n, s, d) => Pair(g, c, n, s, d));
        return list;
    }

    /// <summary>
    /// The fuller catalogue added in v0.3. Everything above stays exactly as it was (existing
    /// libraries match categories on code + name); these are added alongside on upgrade.
    /// </summary>
    private delegate void Adder(string group, string code, string name, string section, string desc = "");

    private static void AddExtendedCatalogue(Adder one, Adder pair)
    {
        // ---------------------------------------------------------------- court forms (children & general)
        const string apps = "Court Applications & Forms";
        pair(apps, "C10", "Supplement: financial provision for a child (Schedule 1)", "B", "Children Act 1989 Schedule 1 financial claims");
        pair(apps, "C10A", "Statement of means", "B", "Filed with a Schedule 1 application");
        pair(apps, "C13A", "Supplement: special guardianship order", "B");
        pair(apps, "C15", "Supplement: contact with a child in care", "B");
        pair(apps, "C51", "Application for a parental order (surrogacy)", "B");
        pair(apps, "C63", "Application for a declaration of parentage", "B");
        pair(apps, "C66", "Application under the inherent jurisdiction / wardship", "B");
        one(apps, "C(PRA1)", "Parental responsibility agreement", "B", "Agreement between the mother and father");
        one(apps, "C(PRA2)", "Step-parent parental responsibility agreement", "B");
        pair(apps, "FL402", "Notice of proceedings (Family Law Act)", "B");
        pair(apps, "FL415", "Statement of service (Family Law Act)", "B");
        pair(apps, "FGM001", "Application for an FGM protection order", "B");
        pair(apps, "FL701", "Application for a forced marriage protection order", "B");
        pair(apps, "FP6", "Certificate of service", "B");
        pair(apps, "FP25", "Witness summons", "B");
        pair(apps, "EX107", "Request for a transcript of proceedings", "F");
        pair(apps, "", "Application to adjourn or vacate a hearing", "B");
        pair(apps, "", "Application for permission to instruct an expert (Part 25)", "B");
        pair(apps, "", "Application to vary or discharge an order", "B");
        pair(apps, "", "Application to set aside an order", "B");
        pair(apps, "", "Application for permission to remove a child from the UK (relocation)", "B");
        pair(apps, "", "Application to change a child's surname", "B");
        pair(apps, "", "Application for parental responsibility", "B");
        pair(apps, "", "Application for disclosure from a third party (police, medical, school…)", "B");
        pair(apps, "", "Application for permission to disclose case documents", "B", "See FPR r.12.73 and Practice Direction 12G");
        pair(apps, "", "Application for committal (contempt of court)", "B");
        pair(apps, "", "Application for a stay of proceedings", "B");
        pair(apps, "", "Application to transfer proceedings", "B");
        pair(apps, "", "Application for participation directions / special measures", "B", "FPR Part 3A and Practice Direction 3AA");
        pair(apps, "", "Application for a qualified legal representative", "B", "Where cross-examination in person is prohibited");
        pair(apps, "", "Application for a section 91(14) order (barring further applications)", "B");
        pair(apps, "", "Application for a warning notice or enforcement (other)", "B");

        // ---------------------------------------------------------------- divorce & finances
        const string finance = "Divorce & Finances";
        pair(finance, "D11", "General application in divorce / dissolution proceedings", "B");
        pair(finance, "D84", "Application for a conditional order", "B");
        pair(finance, "D36", "Application for a final order", "B");
        pair(finance, "D50K", "Application for enforcement of a financial order", "B");
        one(finance, "D81", "Statement of information for a consent order", "B");
        pair(finance, "Form A1", "Financial remedy application (other than in divorce)", "B");
        pair(finance, "Form B", "Application under section 10(2) MCA / s.48(2) CPA", "B");
        one(finance, "Form C", "Notice of first appointment", "B");
        pair(finance, "Form E1", "Financial statement (other financial remedies)", "C");
        pair(finance, "Form E2", "Financial statement (variation of an order)", "C");
        pair(finance, "Form F", "Notice of allegation in financial remedy proceedings", "B");
        pair(finance, "Form G", "Notice of response to first appointment", "B");
        pair(finance, "Form H", "Estimate of costs", "F");
        pair(finance, "Form H1", "Statement of costs (financial remedies)", "F");
        pair(finance, "Form P", "Pension inquiry form", "F");
        one(finance, "Form P1", "Pension sharing annex", "B");
        one(finance, "Form P2", "Pension attachment annex", "B");
        one(finance, "ES1", "Composite case summary (financial remedies)", "A");
        one(finance, "ES2", "Composite schedule of assets and income", "A");
        pair(finance, "", "Questionnaire (financial remedies)", "A");
        pair(finance, "", "Statement of issues (financial remedies)", "A");
        pair(finance, "", "Replies to questionnaire", "C");
        pair(finance, "", "Open offer", "F");
        pair(finance, "", "Without prejudice / Calderbank offer", "F", "Not shown to the judge before a decision on the issues");
        pair(finance, "", "Bank statements", "F");
        pair(finance, "", "Payslips / P60", "F");
        pair(finance, "", "Tax returns / SA302", "F");
        pair(finance, "", "Pension valuation (CETV)", "E");
        one(finance, "", "Property valuation", "E");
        pair(finance, "", "Mortgage statements", "F");
        pair(finance, "", "Business accounts", "F");
        pair(finance, "", "Child maintenance (CMS) documents", "F");
        pair(finance, "", "Benefits / Universal Credit statements", "F");

        // ---------------------------------------------------------------- orders
        const string orders = "Court Orders & Notices";
        one(orders, "", "Parental responsibility order", "B");
        one(orders, "", "Special guardianship order", "B");
        one(orders, "", "Care order", "B");
        one(orders, "", "Supervision order", "B");
        one(orders, "", "Emergency protection order", "B");
        one(orders, "", "Placement order", "B");
        one(orders, "", "Adoption order", "B");
        one(orders, "", "Location / collection / passport order (Tipstaff)", "B");
        one(orders, "", "Warning notice / penal notice", "B");
        one(orders, "", "Committal order", "B");
        one(orders, "", "Costs order", "B");
        one(orders, "", "Order on appeal", "B");
        one(orders, "", "Disclosure order", "B");
        one(orders, "", "Reporting restriction order", "B");
        one(orders, "", "Order directing a section 7 report", "B");
        one(orders, "", "Undertaking to the court", "B");
        one(orders, "", "Financial remedy order", "B");
        one(orders, "", "Conditional order (divorce / dissolution)", "B");
        one(orders, "", "Final order (divorce / dissolution)", "B");
        one(orders, "", "FGM / forced marriage protection order", "B");
        one(orders, "", "Wardship order", "B");
        one(orders, "", "Declaration of parentage", "B");
        one(orders, "", "Parental order (surrogacy)", "B");
        one(orders, "", "Notice of adjournment / vacated hearing", "B");
        one(orders, "", "Notice of proceedings / service by the court", "B");
        one(orders, "", "General form of order", "B");

        // ---------------------------------------------------------------- hearings & case management
        const string hearings = "Hearings & Case Management";
        pair(hearings, "", "Draft order (proposed)", "B");
        pair(hearings, "", "Case outline / summary of issues", "A");
        pair(hearings, "", "Opening note", "A");
        pair(hearings, "", "Closing submissions", "A");
        pair(hearings, "", "Witness list", "A");
        pair(hearings, "", "Hearing timetable / time estimate", "A");
        pair(hearings, "", "Attendance note / note of hearing", "F");
        one(hearings, "", "Bundle index", "A");
        one(hearings, "", "Preliminary documents (bundle section A)", "A");
        one(hearings, "", "Court listing / hearing notice email", "F");
        one(hearings, "", "Approved note of judgment", "B");

        // ---------------------------------------------------------------- statements
        const string statements = "Statements & Submissions";
        pair(statements, "", "Statement in response", "C");
        pair(statements, "", "Affidavit", "C");
        pair(statements, "", "Response to Scott schedule", "A");
        pair(statements, "", "Welfare checklist analysis", "A", "Children Act 1989 section 1(3)");
        pair(statements, "", "Statement of issues", "A");
        one(statements, "", "Composite chronology", "A");
        pair(statements, "", "Character reference / letter of support", "C");

        // ---------------------------------------------------------------- Cafcass
        const string cafcass = "CAFCASS";
        one(cafcass, "", "Cafcass addendum report", "E");
        one(cafcass, "", "Wishes and feelings work with the child", "E");
        one(cafcass, "", "Cafcass Cymru report", "E");
        one(cafcass, "", "Planning Together for Children / SPIP certificate", "F", "Course for separating parents");
        one(cafcass, "", "Domestic Abuse Perpetrator Programme (DAPP) documents", "E");
        one(cafcass, "", "Cafcass data / subject access request response", "F");

        // ---------------------------------------------------------------- public law
        const string publicLaw = "Public Law (Care Proceedings)";
        one(publicLaw, "C11", "Supplement: emergency protection order", "B");
        one(publicLaw, "C12", "Supplement: warrant to assist a person authorised by an EPO", "B");
        one(publicLaw, "C14", "Supplement: authority to refuse contact with a child in care", "B");
        one(publicLaw, "C16", "Supplement: child assessment order", "B");
        one(publicLaw, "C17", "Supplement: education supervision order", "B");
        one(publicLaw, "C18", "Supplement: recovery order", "B");
        one(publicLaw, "C20", "Supplement: secure accommodation order", "B");
        one(publicLaw, "", "Pre-proceedings / PLO letter before proceedings", "F");
        one(publicLaw, "", "Social work evidence template (SWET)", "C");
        one(publicLaw, "", "Social work statement", "C");
        one(publicLaw, "", "Threshold document", "A");
        pair(publicLaw, "", "Response to threshold", "A");
        one(publicLaw, "", "Interim care plan", "D");
        one(publicLaw, "", "Final care plan", "D");
        one(publicLaw, "", "Placement plan", "D");
        one(publicLaw, "", "Parenting assessment (incl. PAMS)", "E");
        one(publicLaw, "", "Viability / kinship assessment", "E");
        one(publicLaw, "", "Children's guardian analysis and recommendations", "E");
        one(publicLaw, "", "Child protection conference minutes", "E");
        one(publicLaw, "", "Child in need / child protection plan", "D");
        one(publicLaw, "", "Core group minutes", "E");
        one(publicLaw, "", "Strategy meeting minutes", "E");
        one(publicLaw, "", "Family group conference record", "E");
        one(publicLaw, "", "Looked after child (LAC) review", "E");
        one(publicLaw, "", "Family time / contact records (local authority)", "E");
        one(publicLaw, "", "Section 20 agreement", "F");
        one(publicLaw, "", "Local authority chronology", "A");
        one(publicLaw, "", "Child and family assessment", "E");
        one(publicLaw, "", "LADO referral / outcome", "E");

        // ---------------------------------------------------------------- adoption & SGO
        const string adoption = "Adoption & Special Guardianship";
        one(adoption, "A58", "Application for an adoption order", "B");
        one(adoption, "A50", "Application for a placement order", "B");
        one(adoption, "", "Adoption agency report (Annex B)", "E");
        one(adoption, "", "Special guardianship assessment report", "E");
        one(adoption, "", "Special guardianship support plan", "D");
        one(adoption, "", "Letterbox / post-adoption contact agreement", "F");

        // ---------------------------------------------------------------- appeals & enforcement
        const string appeals = "Appeals & Enforcement";
        pair(appeals, "FP162", "Respondent's notice (appeal)", "B");
        pair(appeals, "N161", "Appellant's notice (Court of Appeal)", "B");
        pair(appeals, "", "Grounds of appeal", "A");
        pair(appeals, "", "Skeleton argument on appeal", "A");
        one(appeals, "", "Permission to appeal decision", "B");
        one(appeals, "", "Enforcement / compliance documents", "F");

        // ---------------------------------------------------------------- experts
        const string experts = "Experts & Assessments";
        pair(experts, "", "Letter of instruction to expert", "E");
        one(experts, "", "Expert CV and fee estimate", "E");
        pair(experts, "", "Questions to the expert", "E");
        one(experts, "", "Psychological assessment", "E");
        one(experts, "", "Psychiatric assessment", "E");
        one(experts, "", "Hair strand drug / alcohol testing", "E");
        one(experts, "", "DNA / paternity testing", "E");
        one(experts, "", "Independent social worker (ISW) report", "E");
        one(experts, "", "Domestic abuse risk assessment", "E");
        one(experts, "", "Medical expert report", "E");
        one(experts, "", "Child and adolescent psychiatry report", "E");

        // ---------------------------------------------------------------- police
        const string police = "Police & Criminal";
        one(police, "", "Police incident log (CAD)", "E");
        one(police, "", "Crime report / crime reference", "E");
        one(police, "MG11", "Witness statement to police", "E");
        one(police, "", "Body-worn video / police footage", "F");
        one(police, "", "Bail conditions", "E");
        one(police, "", "Charge / conviction record", "E");
        one(police, "", "Criminal court outcome", "E");
        one(police, "", "Clare's Law (DVDS) disclosure", "E");
        one(police, "", "DASH risk assessment", "E");
        one(police, "", "MARAC referral / minutes", "E");
        one(police, "", "Restraining order (criminal court)", "B");
        one(police, "", "Domestic Violence Protection Notice / Order (DVPN / DVPO)", "B");
        one(police, "", "Victim personal statement", "C");

        // ---------------------------------------------------------------- health
        const string health = "Health & Medical";
        one(health, "", "GP records", "E");
        one(health, "", "Hospital records", "E");
        one(health, "", "Mental health records", "E");
        one(health, "", "CAMHS records", "E");
        one(health, "", "Health visitor / midwife records", "E");
        one(health, "", "Counselling / therapy records", "E");
        one(health, "", "Prescription / medication records", "E");
        one(health, "", "Drug and alcohol service records", "E");
        one(health, "", "Medical letter / fit note", "F");

        // ---------------------------------------------------------------- education
        const string education = "Education & Childcare";
        one(education, "", "School reports", "E");
        one(education, "", "School attendance records", "E");
        one(education, "", "Education, Health and Care Plan (EHCP)", "E");
        one(education, "", "School correspondence", "F");
        one(education, "", "Nursery / childcare records", "E");
        one(education, "", "School safeguarding referral", "E");

        // ---------------------------------------------------------------- personal documents
        const string personal = "Personal & Identity Documents";
        pair(personal, "", "Birth certificate", "F");
        one(personal, "", "Marriage / civil partnership certificate", "F");
        pair(personal, "", "Passport / identity document", "F");
        pair(personal, "", "Change of name deed", "F");
        pair(personal, "", "Tenancy / housing documents", "F");
        pair(personal, "", "Proof of address", "F");
        pair(personal, "", "Immigration documents", "F");
        one(personal, "", "Death certificate", "F");

        // ---------------------------------------------------------------- evidence (more types)
        const string evidence = "Evidence";
        pair(evidence, "", "Social media posts", "F");
        pair(evidence, "", "Voicemails / call logs", "F");
        pair(evidence, "", "Diary entries", "F");
        pair(evidence, "", "Receipts", "F");
        pair(evidence, "", "Third-party letters", "F");

        // ---------------------------------------------------------------- correspondence (more)
        const string corr = "Correspondence";
        one(corr, "", "Emails from the court (HMCTS)", "F");
        pair(corr, "", "Letter before application", "F");
        one(corr, "", "Correspondence with the police", "F");
        one(corr, "", "Correspondence with the school", "F");
        one(corr, "", "Correspondence with health services", "F");
        one(corr, "", "Subject access request (SAR) and response", "F", "UK GDPR Article 15");
        one(corr, "", "Freedom of information request and response", "F");

        // ---------------------------------------------------------------- complaints (more)
        const string complaints = "Complaints & Representations";
        one(complaints, "", "HMCTS complaint", "F");
        one(complaints, "", "Local authority complaint (stages 1–3)", "F");
        one(complaints, "", "Social Work England concern", "F");
        one(complaints, "", "Solicitors Regulation Authority (SRA) report", "F");
        one(complaints, "", "Legal Ombudsman complaint", "F");
        one(complaints, "", "Police complaint / IOPC", "F");
        one(complaints, "", "Information Commissioner (ICO) complaint", "F");
        one(complaints, "", "Children's Commissioner", "F");

        // ---------------------------------------------------------------- legal aid, fees & costs
        const string costs = "Legal Aid, Fees & Costs";
        one(costs, "", "Legal aid certificate", "F");
        one(costs, "", "Legal Aid Agency correspondence", "F");
        one(costs, "", "Domestic abuse evidence for legal aid", "F");
        one(costs, "", "Court fee receipt", "F");
        one(costs, "", "Help with fees decision", "F");
        one(costs, "", "Solicitor / barrister invoices", "F");
        pair(costs, "N260", "Statement of costs (summary assessment)", "F");

        // ---------------------------------------------------------------- mediation
        const string mediation = "Mediation & Out-of-Court";
        one(mediation, "", "MIAM certificate / exemption", "B");
        one(mediation, "", "Mediation summary / memorandum of understanding", "F");
        pair(mediation, "", "Open financial statement (mediation)", "F");
        pair(mediation, "", "Parenting plan", "F");
        one(mediation, "", "Child-inclusive mediation feedback", "F");
        one(mediation, "", "Arbitration / collaborative law documents", "F");

        // ---------------------------------------------------------------- international
        const string international = "International & Relocation";
        one(international, "", "Child abduction / Hague Convention documents", "F");
        one(international, "", "Relocation (leave to remove) documents", "F");
        one(international, "", "Port alert / passport order documents", "F");
        one(international, "", "Foreign court orders", "F");
        one(international, "", "Translations", "F");
        one(international, "", "Apostille / legalisation", "F");

        // ---------------------------------------------------------------- research & working papers
        const string research = "Legal Research & Authorities";
        one(research, "", "Case law / reported judgments", "F");
        one(research, "", "Legislation extracts", "F");
        one(research, "", "Family Procedure Rules & practice directions", "F");
        one(research, "", "Guidance (Cafcass, PD12J, President's guidance…)", "F");

        const string working = "My Notes & Working Papers";
        one(working, "", "My notes", "F");
        one(working, "", "Draft documents", "F");
        one(working, "", "Checklists / to-do", "F");
        one(working, "", "Questions for the hearing", "F");
        one(working, "", "Legal advice notes (privileged)", "F");
        one(working, "", "AI analysis reports", "F", "Reports produced by CaseKeeper's optional local AI");
        one(working, "", "Letters I have written", "F", "Letters exported from CaseKeeper's letter writer");
    }
}
