using System.Globalization;

namespace CaseKeeper.UI;

/// <summary>
/// Alphabetical ordering that reads numbers as numbers, so form codes come out as
/// C1, C1A, C2, C9, C78, C100 rather than C1, C100, C110A, C1A, C2.
/// </summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string a, string b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a == null) return -1;
        if (b == null) return 1;

        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                // Compare by value; fall back to text for numbers too big to parse
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length - nb.Length;
                int digits = string.CompareOrdinal(na, nb);
                if (digits != 0) return digits;
            }
            else
            {
                int ch = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (ch != 0) return ch;
                i++;
                j++;
            }
        }
        return (a.Length - i) - (b.Length - j);
    }

    /// <summary>Sorts any sequence alphabetically by a chosen piece of text.</summary>
    public static IOrderedEnumerable<T> Sort<T>(IEnumerable<T> source, Func<T, string> key) =>
        source.OrderBy(key, Instance);

    public static List<string> Sort(IEnumerable<string> source) =>
        source.OrderBy(s => s, Instance).ToList();
}
