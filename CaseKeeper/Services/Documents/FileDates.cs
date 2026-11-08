using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace CaseKeeper.Services.Documents;

/// <summary>
/// Reads when a file says it was last saved, from the file itself: a PDF's ModDate (or CreationDate), a Word,
/// Excel or PowerPoint file's "modified" date and revision number, or the date a photo was taken. Used to tell
/// which copy of a document holds the latest edit. Dates and the revision number only: no author or
/// "last saved by" names are ever read. Never throws; anything it can't read gives no date.
/// </summary>
public static class FileDates
{
    public readonly record struct Result(DateTime? SavedAt, int? Revision, string Source)
    {
        public static readonly Result None = new(null, null, "");
    }

    // A real date only: not before 1990 (PDF/Office didn't exist), not more than a day in the future
    private static DateTime? Plausible(DateTime? d) =>
        d is { } v && v.Year >= 1990 && v <= DateTime.Now.AddDays(1) ? v : null;

    public static Result Read(byte[] bytes, string extension)
    {
        if (bytes == null || bytes.Length == 0) return Result.None;
        try
        {
            return (extension ?? "").ToLowerInvariant() switch
            {
                ".pdf" => Pdf(bytes),
                ".docx" or ".docm" or ".xlsx" or ".xlsm" or ".pptx" or ".pptm" => Office(bytes),
                ".jpg" or ".jpeg" or ".tif" or ".tiff" => Photo(bytes),
                _ => Result.None
            };
        }
        catch (Exception ex)
        {
            AppLog.Info("FileDates", $"No saved date read from a {extension} file: {ex.GetType().Name}");
            return Result.None;
        }
    }

    // ------------------------------------------------------------------ PDF
    // The Info dictionary is plain text in almost every PDF (incremental saves append a newer one, so the
    // last ModDate wins). XMP's xmp:ModifyDate is the fallback.
    private static readonly Regex PdfDate = new(@"/(ModDate|CreationDate)\s*\(\s*(D:)?(?<d>\d{4,14})(?<tz>[Z+\-][0-9']*)?\s*\)", RegexOptions.Compiled);
    private static readonly Regex XmpDate = new(@"<xmp:(ModifyDate|CreateDate)>(?<d>[^<]{10,40})</xmp:", RegexOptions.Compiled);

    private static Result Pdf(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        DateTime? mod = null, created = null;
        foreach (Match m in PdfDate.Matches(text))
        {
            var d = ParsePdfDate(m.Groups["d"].Value, m.Groups["tz"].Value);
            if (d == null) continue;
            if (m.Groups[1].Value == "ModDate") mod = d; else created ??= d;
        }
        if (mod == null)
            foreach (Match m in XmpDate.Matches(text))
                if (DateTime.TryParse(m.Groups["d"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var x))
                {
                    if (m.Groups[1].Value == "ModifyDate") mod = Plausible(x.ToLocalTime()); else created ??= Plausible(x.ToLocalTime());
                }
        return mod != null ? new(mod, null, "PDF last saved") : created != null ? new(created, null, "PDF created") : Result.None;
    }

    public static DateTime? ParsePdfDate(string digits, string tz = "")
    {
        if (digits.Length < 4) return null;
        int Part(int at, int len, int fallback) => digits.Length >= at + len && int.TryParse(digits.AsSpan(at, len), out var v) ? v : fallback;
        try
        {
            var local = new DateTime(Part(0, 4, 1), Math.Clamp(Part(4, 2, 1), 1, 12), 1, 0, 0, 0, DateTimeKind.Unspecified);
            local = local.AddDays(Math.Clamp(Part(6, 2, 1), 1, DateTime.DaysInMonth(local.Year, local.Month)) - 1)
                .AddHours(Part(8, 2, 0)).AddMinutes(Part(10, 2, 0)).AddSeconds(Part(12, 2, 0));
            tz = (tz ?? "").Replace("'", "");
            if (tz.StartsWith('Z')) return Plausible(DateTime.SpecifyKind(local, DateTimeKind.Utc).ToLocalTime());
            if (tz.Length >= 3 && (tz[0] == '+' || tz[0] == '-') && int.TryParse(tz.AsSpan(1, 2), out var h))
            {
                int min = tz.Length >= 5 && int.TryParse(tz.AsSpan(3, 2), out var mm) ? mm : 0;
                var offset = new TimeSpan(h, min, 0) * (tz[0] == '-' ? -1 : 1);
                return Plausible(new DateTimeOffset(local, offset).LocalDateTime);
            }
            return Plausible(local);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // ------------------------------------------------------------------ Office (docProps/core.xml)
    private static Result Office(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        var entry = zip.GetEntry("docProps/core.xml");
        if (entry == null || entry.Length > 1_000_000) return Result.None;
        using var s = entry.Open();
        var xml = XDocument.Load(s);
        XNamespace dcterms = "http://purl.org/dc/terms/", cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
        DateTime? When(string name)
        {
            var v = xml.Root?.Element(dcterms + name)?.Value;
            return DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
                ? Plausible(DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime()) : null;
        }
        int? revision = int.TryParse(xml.Root?.Element(cp + "revision")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) && r > 0 ? r : null;
        var modified = When("modified");
        if (modified != null) return new(modified, revision, "last saved");
        var created = When("created");
        return created != null || revision != null ? new(created, revision, created != null ? "created" : "") : Result.None;
    }

    // ------------------------------------------------------------------ photos (EXIF date taken)
    private static Result Photo(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames.FirstOrDefault();
        if (frame?.Metadata is not BitmapMetadata meta || string.IsNullOrEmpty(meta.DateTaken)) return Result.None;
        return DateTime.TryParse(meta.DateTaken, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var d) && Plausible(d) is { } taken
            ? new(taken, null, "photo taken") : Result.None;
    }
}
