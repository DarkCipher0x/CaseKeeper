namespace CaseKeeper.Services;

/// <summary>
/// Date arithmetic for court deadlines in England &amp; Wales, following the approach of Family
/// Procedure Rules r.2.8: "clear days" exclude both the starting day and the day of the event;
/// periods of five days or less skip weekends and bank holidays; and a deadline that falls on a
/// day the court office is closed moves to the next working day. Always check the actual rule
/// or order, which can say otherwise.
/// </summary>
public static class Deadlines
{
    /// <summary>One-off changes to the usual pattern (moved or extra holidays).</summary>
    private static readonly Dictionary<int, (DateTime[] Add, DateTime[] Remove)> Specials = new()
    {
        [2012] = (new[] { new DateTime(2012, 6, 4), new DateTime(2012, 6, 5) }, new[] { new DateTime(2012, 5, 28) }),
        [2020] = (new[] { new DateTime(2020, 5, 8) }, new[] { new DateTime(2020, 5, 4) }),
        [2022] = (new[] { new DateTime(2022, 6, 2), new DateTime(2022, 6, 3), new DateTime(2022, 9, 19) }, new[] { new DateTime(2022, 5, 30) }),
        [2023] = (new[] { new DateTime(2023, 5, 8) }, Array.Empty<DateTime>()),
    };

    private static readonly Dictionary<int, HashSet<DateTime>> Cache = new();

    public static HashSet<DateTime> BankHolidays(int year)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(year, out var cached)) return cached;
            var set = new HashSet<DateTime>();

            // New Year's Day, moved to Monday if it falls at a weekend
            set.Add(NextWeekday(new DateTime(year, 1, 1)));

            var easter = EasterSunday(year);
            set.Add(easter.AddDays(-2));    // Good Friday
            set.Add(easter.AddDays(1));     // Easter Monday

            set.Add(FirstMonday(year, 5));  // Early May
            set.Add(LastMonday(year, 5));   // Spring
            set.Add(LastMonday(year, 8));   // Summer

            // Christmas and Boxing Day, with the usual substitutes
            var xmas = new DateTime(year, 12, 25);
            switch (xmas.DayOfWeek)
            {
                case DayOfWeek.Friday: set.Add(xmas); set.Add(new DateTime(year, 12, 28)); break;
                case DayOfWeek.Saturday: set.Add(new DateTime(year, 12, 27)); set.Add(new DateTime(year, 12, 28)); break;
                case DayOfWeek.Sunday: set.Add(new DateTime(year, 12, 26)); set.Add(new DateTime(year, 12, 27)); break;
                default: set.Add(xmas); set.Add(new DateTime(year, 12, 26)); break;
            }

            if (Specials.TryGetValue(year, out var s))
            {
                foreach (var d in s.Remove) set.Remove(d);
                foreach (var d in s.Add) set.Add(d);
            }
            Cache[year] = set;
            return set;
        }
    }

    public static bool IsBankHoliday(DateTime d) => BankHolidays(d.Year).Contains(d.Date);

    public static bool IsWorkingDay(DateTime d) =>
        d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !IsBankHoliday(d);

    public static DateTime NextWorkingDay(DateTime d)
    {
        d = d.Date;
        while (!IsWorkingDay(d)) d = d.AddDays(1);
        return d;
    }

    public static DateTime AddWorkingDays(DateTime from, int days)
    {
        var d = from.Date;
        int step = days >= 0 ? 1 : -1;
        for (int n = Math.Abs(days); n > 0;)
        {
            d = d.AddDays(step);
            if (IsWorkingDay(d)) n--;
        }
        return d;
    }

    public static DateTime SubtractWorkingDays(DateTime from, int days) => AddWorkingDays(from, -days);

    /// <summary>"Within N days of" a date: counted from the day after, rolled forward past a closed day.</summary>
    public static DateTime DaysAfter(DateTime from, int days) =>
        days <= 5 ? AddWorkingDays(from, days) : NextWorkingDay(from.Date.AddDays(days));

    /// <summary>
    /// The last day to do something "at least N clear days before" an event: neither the day it's
    /// done nor the day of the event counts. Short periods skip weekends and bank holidays, and the
    /// answer is moved earlier if it lands on a day the court is closed.
    /// </summary>
    public static DateTime ClearDaysBefore(DateTime eventDate, int days)
    {
        DateTime d;
        if (days <= 5)
        {
            d = eventDate.Date;
            for (int n = days + 1; n > 0;)
            {
                d = d.AddDays(-1);
                if (IsWorkingDay(d)) n--;
            }
        }
        else d = eventDate.Date.AddDays(-(days + 1));
        while (!IsWorkingDay(d)) d = d.AddDays(-1);
        return d;
    }

    public static DateTime MonthsAfter(DateTime from, int months) => NextWorkingDay(from.Date.AddMonths(months));

    public static int WorkingDaysBetween(DateTime a, DateTime b)
    {
        if (b < a) (a, b) = (b, a);
        int n = 0;
        for (var d = a.Date.AddDays(1); d <= b.Date; d = d.AddDays(1)) if (IsWorkingDay(d)) n++;
        return n;
    }

    /// <summary>Anonymous Gregorian algorithm (Meeus/Jones/Butcher).</summary>
    public static DateTime EasterSunday(int y)
    {
        int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateTime(y, month, day);
    }

    private static DateTime NextWeekday(DateTime d)
    {
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(1);
        return d;
    }

    private static DateTime FirstMonday(int y, int month)
    {
        var d = new DateTime(y, month, 1);
        while (d.DayOfWeek != DayOfWeek.Monday) d = d.AddDays(1);
        return d;
    }

    private static DateTime LastMonday(int y, int month)
    {
        var d = new DateTime(y, month, DateTime.DaysInMonth(y, month));
        while (d.DayOfWeek != DayOfWeek.Monday) d = d.AddDays(-1);
        return d;
    }

    /// <summary>The ways of counting the deadline calculator offers. Shared by the calculator and the Import review screen.</summary>
    public static readonly string[] Methods =
    {
        "Calendar days after", "Working days after", "Calendar months after", "Clear days before", "Calendar days before", "Working days before"
    };

    /// <summary>Works out a deadline by one of <see cref="Methods"/>, from a date and a number of days or months.</summary>
    public static DateTime Calculate(string method, DateTime from, int amount) => method switch
    {
        "Working days after" => AddWorkingDays(from, amount),
        "Calendar months after" => MonthsAfter(from, amount),
        "Clear days before" => ClearDaysBefore(from, amount),
        "Calendar days before" => PreviousWorkingDay(from.AddDays(-amount)),
        "Working days before" => SubtractWorkingDays(from, amount),
        "Calendar days after" => DaysAfter(from, amount),
        _ => throw new ArgumentException("Unknown way of counting: " + method)
    };

    /// <summary>A plain-English account of how a deadline was worked out, kept with the task it creates.</summary>
    public static string Explain(string method, DateTime from, int amount, DateTime result)
    {
        var how = method switch
        {
            "Clear days before" => "Clear days: neither the day it's done nor the day of the event counts; periods of five days or less skip weekends and bank holidays.",
            "Calendar days after" when amount <= 5 => "Periods of five days or less skip weekends and bank holidays (FPR r.2.8).",
            "Working days after" or "Working days before" => "Only working days are counted (weekends and England & Wales bank holidays skipped).",
            "Calendar months after" => "Counted in calendar months.",
            _ => "Counted in calendar days."
        };
        var moved = method.EndsWith("before") ? "moved earlier" : "moved to the next working day";
        return $"{amount} {method.ToLowerInvariant()} {from:d MMMM yyyy} = {result:dddd d MMMM yyyy}. {how} " +
               $"A day the court office is closed (weekend or bank holiday) is {moved}.";
    }

    public static DateTime PreviousWorkingDay(DateTime d)
    {
        d = d.Date;
        while (!IsWorkingDay(d)) d = d.AddDays(-1);
        return d;
    }

    public static string Describe(DateTime d)
    {
        var tags = new List<string>();
        if (IsBankHoliday(d)) tags.Add("bank holiday");
        if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) tags.Add("weekend");
        int days = (d.Date - DateTime.Today).Days;
        tags.Add(days switch { 0 => "today", 1 => "tomorrow", < 0 => $"{-days} days ago", _ => $"in {days} days ({WorkingDaysBetween(DateTime.Today, d)} working days)" });
        return $"{d:dddd d MMMM yyyy} — {string.Join(", ", tags)}";
    }
}
