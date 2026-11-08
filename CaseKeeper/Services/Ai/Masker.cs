using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CaseKeeper.Services;

/// <summary>
/// Reversible masking of personal details before text goes to a cloud AI.
///
/// Each detected value (a name, e-mail address, phone number, postcode, National Insurance number, case number,
/// bank detail, date of birth or street address) is replaced with a stable placeholder such as [PERSON-1]. The same
/// value always gets the same placeholder within one run, so the AI can still tell people apart. The mapping
/// lives only in memory, in this object, and is never sent, logged or saved. The AI's answer is unmasked before
/// CaseKeeper checks it, so quotes and dates are checked against the real text exactly as before.
///
/// Masking is best effort: names that aren't in the case's list of people and have no title (Mr, Ms…) may be missed.
/// <see cref="Assess"/> says how well a text was masked, so the person can decide before anything is sent.
/// </summary>
public sealed class Masker
{
    public sealed record Kind(string Tag, string Label);

    public static readonly Kind Person = new("PERSON", "names"), Email = new("EMAIL", "e-mail addresses"),
        Phone = new("PHONE", "phone numbers"), Postcode = new("POSTCODE", "postcodes"), Nino = new("NINO", "National Insurance numbers"),
        CaseNo = new("CASE", "case numbers"), Account = new("ACCOUNT", "bank and card details"), Dob = new("DOB", "dates of birth"),
        Address = new("ADDRESS", "street addresses");

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _toToken = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toValue = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counters = new();
    private readonly List<(string Value, string Tag)> _known;   // longest first

    private const RegexOptions O = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);

    private static readonly Regex EmailRx = new(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b", O, Limit);
    private static readonly Regex PhoneRx = new(@"(?<![\w+])(?:\+44\s?\(?0?\)?\s?\d{2,4}|\(?0\d{2,4}\)?)[\s\-]?\d{3,4}[\s\-]?\d{3,4}(?!\d)", O, Limit);
    private static readonly Regex PostcodeRx = new(@"\b(?:[A-Z]{1,2}\d[A-Z\d]?|GIR)\s?\d[ABD-HJLNP-UW-Z]{2}\b", O | RegexOptions.IgnoreCase, Limit);
    private static readonly Regex NinoRx = new(@"\b(?!BG|GB|NK|KN|TN|NT|ZZ)[A-CEGHJ-PR-TW-Z][A-CEGHJ-NPR-TW-Z]\s?\d{2}\s?\d{2}\s?\d{2}\s?[A-D]\b", O, Limit);
    // Family court case numbers: two letters, two digits, a letter, five digits (e.g. AB12C34567); and "Case No: …" forms
    private static readonly Regex CaseRx = new(@"\b[A-Z]{2}\d{2}[A-Z]\d{5}\b|(?<=\b(?:case|claim)\s+(?:no\.?|number|ref(?:erence)?)\s*[:.]?\s*)[A-Z0-9][A-Z0-9/\-]{4,19}\b", O | RegexOptions.IgnoreCase, Limit);
    private static readonly Regex SortCodeRx = new(@"\b\d{2}-\d{2}-\d{2}\b", O, Limit);
    private static readonly Regex AccountRx = new(@"(?<=\b(?:account|acc|a/c)\s*(?:no\.?|number)?\s*[:.]?\s*)\d{8}\b", O | RegexOptions.IgnoreCase, Limit);
    private static readonly Regex CardRx = new(@"\b(?:\d[ \-]?){13,19}\b", O, Limit);
    private static readonly Regex DobRx = new(@"(?<=\b(?:d\.?o\.?b\.?|date of birth|born(?: on)?)\s*[:.]?\s*)(?:\d{1,2}[/.\-]\d{1,2}[/.\-]\d{2,4}|\d{1,2}(?:st|nd|rd|th)?\s+[A-Z][a-z]+\s+\d{4})", O | RegexOptions.IgnoreCase, Limit);
    private static readonly Regex AddressRx = new(@"\b\d{1,4}[A-Za-z]?,?\s+(?:[A-Z][a-z']+\s+){1,3}(?:Road|Rd|Street|St|Avenue|Ave|Lane|Ln|Close|Drive|Dr|Way|Court|Ct|Crescent|Place|Pl|Gardens|Grove|Terrace|Hill|Park|Row|Square|Mews|View|Rise|Walk)\b", O, Limit);
    private static readonly Regex TitledNameRx = new(@"\b(?:Mr|Mrs|Ms|Miss|Mx|Dr|Master)\.?\s+(?:[A-Z]\.?\s+)?[A-Z][a-z'\-]+(?:\s+[A-Z][a-z'\-]+)?", O, Limit);
    private static readonly Regex TokenRx = new(@"\[(?:PERSON|EMAIL|PHONE|POSTCODE|NINO|CASE|ACCOUNT|DOB|ADDRESS)-\d+\]", O, Limit);
    private static readonly Regex CapPairRx = new(@"\b[A-Z][a-z]{2,}\s+[A-Z][a-z]{2,}\b", O, Limit);

    /// <summary>Capitalised words that are common in court papers and aren't names.</summary>
    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Family", "Court", "Courts", "Tribunal", "Children", "Child", "Act", "Order", "Orders", "Judge", "District", "Circuit", "High", "Justice",
        "Magistrates", "Cafcass", "Local", "Authority", "Council", "County", "Social", "Services", "Police", "School", "Primary", "Secondary",
        "Academy", "Hospital", "Health", "Trust", "Hearing", "Directions", "Arrangements", "Contact", "Statement", "Position", "Section",
        "Report", "Letter", "Dear", "Yours", "Sincerely", "Faithfully", "Applicant", "Respondent", "Mother", "Father", "Parent", "Parents",
        "Grandmother", "Grandfather", "Kingdom", "United", "England", "Wales", "London", "Birmingham", "Manchester", "Leeds", "Bristol",
        "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December",
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday", "Safeguarding", "Welfare", "Checklist", "Procedure",
        "Rules", "Practice", "Direction", "Legal", "Aid", "Agency", "Solicitor", "Solicitors", "Barrister", "Counsel", "Chambers", "Limited",
        "Ltd", "Bank", "Plc", "Department", "Office", "Centre", "Center", "Service", "Team", "Unit", "Officer", "Worker", "Manager",
        "Assessment", "Plan", "Plans", "Child's", "Prohibited", "Steps", "Specific", "Issue", "Non", "Molestation", "Occupation", "Protection",
        "Domestic", "Abuse", "Fact", "Finding", "Schedule", "Annex", "Appendix", "Page", "Exhibit", "Bundle", "Index", "Note", "Notes",
        "Summary", "Background", "Conclusion", "Recommendation", "Recommendations", "Introduction", "Name", "Address", "Date", "Signed",
        "Signature", "Witness", "Truth", "True", "Copy", "Email", "Telephone", "Mobile", "Reference", "Ref", "Case", "Number", "The", "This",
        // Words that often start a sentence, so "When Mother…" isn't taken for a name
        "When", "After", "Before", "During", "Since", "Although", "However", "There", "These", "Those", "They", "Then", "What", "Where",
        "Which", "While", "Following", "Further", "Please", "Our", "Your", "His", "Her", "Their", "Both", "Each", "Every", "Some", "Many",
        "Most", "All", "Any", "Also", "And", "But", "For", "From", "With", "Without", "That", "Upon", "Under", "Over", "Into", "Today",
        "Yesterday", "Tomorrow", "Last", "Next", "First", "Second", "Third", "Finally", "Accordingly", "Therefore", "Subject", "Re"
    };

    /// <param name="knownNames">The case's own people (from the list of parties): the most reliable thing to mask.</param>
    /// <param name="knownAddresses">Addresses CaseKeeper already knows (parties' addresses), masked as whole strings.</param>
    public Masker(IEnumerable<string> knownNames = null, IEnumerable<string> knownAddresses = null)
    {
        _known = new();
        foreach (var n in (knownNames ?? Enumerable.Empty<string>()).Select(s => s?.Trim()).Where(s => s is { Length: >= 3 }).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _known.Add((n, "PERSON"));
            // Each part of a full name on its own (first name, surname), skipping initials and very short parts
            foreach (var part in n.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(p => p.Length >= 3 && !NotNames.Contains(p)))
                _known.Add((part, "PERSON"));
        }
        foreach (var a in (knownAddresses ?? Enumerable.Empty<string>()).Select(s => s?.Trim()).Where(s => s is { Length: >= 6 }))
        {
            _known.Add((a, "ADDRESS"));
            foreach (var line in a.Split(new[] { '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.Length >= 6 && l.Any(char.IsDigit)))
                _known.Add((line, "ADDRESS"));
        }
        _known = _known.DistinctBy(k => k.Value.ToLowerInvariant()).OrderByDescending(k => k.Value.Length).ToList();
    }

    private Masker(List<(string Value, string Tag)> known) => _known = known;

    /// <summary>A fresh masker with the same known names and addresses, but nothing masked yet (for per-document counts).</summary>
    public Masker CloneKnown() => new(_known);

    /// <summary>How many distinct values of each kind have been masked so far in this run.</summary>
    public IReadOnlyDictionary<string, int> Counts
    {
        get { lock (_gate) return new Dictionary<string, int>(_counters); }
    }

    private string TokenFor(string value, string tag)
    {
        lock (_gate)
        {
            if (_toToken.TryGetValue(value, out var t)) return t;
            var n = _counters.GetValueOrDefault(tag) + 1;
            _counters[tag] = n;
            t = $"[{tag}-{n}]";
            _toToken[value] = t;
            _toValue[t] = value;
            return t;
        }
    }

    /// <summary>Replaces personal details in <paramref name="text"/> with placeholders.</summary>
    public string Mask(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = text;
        // Structured identifiers first, so their digits aren't caught by the looser patterns
        s = Replace(s, EmailRx, "EMAIL");
        s = Replace(s, NinoRx, "NINO");
        s = Replace(s, CaseRx, "CASE");
        s = Replace(s, DobRx, "DOB");
        s = Replace(s, SortCodeRx, "ACCOUNT");
        s = Replace(s, AccountRx, "ACCOUNT");
        s = ReplaceCards(s);
        s = Replace(s, AddressRx, "ADDRESS");
        s = Replace(s, PostcodeRx, "POSTCODE");
        s = Replace(s, PhoneRx, "PHONE");
        foreach (var (value, tag) in _known)
        {
            if (s.IndexOf(value, StringComparison.OrdinalIgnoreCase) < 0) continue;
            s = Regex.Replace(s, $@"(?<![\w\[\-]){Regex.Escape(value)}(?![\w\]])", m => IsInsideToken(s, m.Index) ? m.Value : TokenFor(m.Value, tag),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);
        }
        s = Replace(s, TitledNameRx, "PERSON");
        return s;
    }

    private string Replace(string s, Regex rx, string tag) =>
        rx.Replace(s, m => IsInsideToken(s, m.Index) || m.Value.Trim().Length == 0 ? m.Value : TokenFor(m.Value, tag));

    private string ReplaceCards(string s) => CardRx.Replace(s, m =>
    {
        var digits = new string(m.Value.Where(char.IsDigit).ToArray());
        return digits.Length is >= 13 and <= 19 && Luhn(digits) && !IsInsideToken(s, m.Index) ? TokenFor(m.Value.TrimEnd(' ', '-'), "ACCOUNT") + (m.Value.EndsWith(' ') ? " " : "") : m.Value;
    });

    private static bool Luhn(string digits)
    {
        int sum = 0;
        bool dbl = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (dbl) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
            dbl = !dbl;
        }
        return sum % 10 == 0;
    }

    /// <summary>True when position <paramref name="index"/> is inside a placeholder made earlier, so it's left alone.</summary>
    private static bool IsInsideToken(string s, int index)
    {
        if (index <= 0 || index >= s.Length) return false;
        int open = s.LastIndexOf('[', index);
        if (open < 0 || index - open > 24) return false;
        int close = s.IndexOf(']', open);
        return close >= index && TokenRx.IsMatch(s.Substring(open, close - open + 1));
    }

    /// <summary>Puts the original values back. Placeholders that this run never made are left as they are.</summary>
    public string Unmask(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return TokenRx.Replace(text, m =>
        {
            lock (_gate) return _toValue.TryGetValue(m.Value, out var v) ? v : m.Value;
        });
    }

    /// <summary>Unmasks every string inside a JSON answer, keeping the JSON valid. Unreadable JSON is unmasked as text.</summary>
    public string UnmaskJson(string json)
    {
        JsonNode node;
        try { node = JsonNode.Parse(json ?? ""); }
        catch (System.Text.Json.JsonException) { return Unmask(json); }
        if (node == null) return json;
        return Walk(node)?.ToJsonString() ?? json;

        JsonNode Walk(JsonNode n)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var key in o.Select(p => p.Key).ToList()) o[key] = Walk(o[key]);
                    return o;
                case JsonArray a:
                    for (int i = 0; i < a.Count; i++) a[i] = Walk(a[i]);
                    return a;
                case JsonValue v when v.TryGetValue<string>(out var str):
                    return JsonValue.Create(Unmask(str));
                default:
                    return n;
            }
        }
    }

    /// <summary>How well a masked text hides people: what's left that still looks like a name.</summary>
    public sealed record Assessment(int Masked, int LikelyNamesLeft, bool LowQualityText, IReadOnlyList<string> Examples)
    {
        /// <summary>True when the text probably still contains names, or is too garbled (a poor scan) to mask reliably.</summary>
        public bool Poor => LikelyNamesLeft >= 4 || LowQualityText;
    }

    /// <summary>Looks at an already-masked text and says what may have been missed. Examples never leave the PC.</summary>
    public static Assessment Assess(string masked)
    {
        masked ??= "";
        int tokens = TokenRx.Matches(masked).Count;
        var left = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in CapPairRx.Matches(masked))
        {
            var words = m.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Any(w => NotNames.Contains(w))) continue;
            left.Add(m.Value);
        }
        // A poor scan: lots of characters that aren't letters, digits, spaces or ordinary punctuation, or very short "words"
        int letters = masked.Count(char.IsLetter), odd = masked.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && ".,;:'\"()-–/&£%[]?!".IndexOf(c) < 0);
        var wordList = masked.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        double shortShare = wordList.Length == 0 ? 0 : wordList.Count(w => w.Length <= 2) / (double)wordList.Length;
        bool low = masked.Length > 400 && (letters < masked.Length * 0.45 || odd > masked.Length * 0.06 || shortShare > 0.55);
        return new Assessment(tokens, left.Count, low, left.Take(5).ToList());
    }

    /// <summary>Text added to the AI's instructions when masking is on.</summary>
    public const string Instructions =
        "PRIVACY: personal details in this text have been replaced with placeholders such as [PERSON-1], [EMAIL-2], [PHONE-1], " +
        "[POSTCODE-1], [CASE-1], [DOB-1] or [ADDRESS-1]. Treat each placeholder as the detail it stands for, and copy placeholders exactly as written " +
        "(including the square brackets) wherever you would have copied the detail, including in quotes. Never try to guess what they stand for.";

    /// <summary>
    /// Unmasks a streamed answer as it arrives, holding back a possible placeholder that is split between two pieces.
    /// </summary>
    public sealed class StreamUnmasker
    {
        private readonly Masker _m;
        private readonly StringBuilder _pending = new();
        public StreamUnmasker(Masker m) => _m = m;

        public string Push(string piece)
        {
            _pending.Append(piece);
            var s = _pending.ToString();
            int open = s.LastIndexOf('[');
            int cut = open >= 0 && s.IndexOf(']', open) < 0 && s.Length - open <= 24 ? open : s.Length;
            _pending.Clear();
            _pending.Append(s, cut, s.Length - cut);
            return _m.Unmask(s[..cut]);
        }

        public string Flush()
        {
            var s = _m.Unmask(_pending.ToString());
            _pending.Clear();
            return s;
        }
    }
}
