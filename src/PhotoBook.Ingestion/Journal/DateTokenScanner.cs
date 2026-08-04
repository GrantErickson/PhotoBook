using System.Globalization;
using System.Text.RegularExpressions;

namespace PhotoBook.Ingestion.Journal;

/// <summary>Which shape of date token the scanner recognized (doc 11's matcher table).</summary>
public enum DateTokenKind
{
    /// <summary>ISO order, e.g. <c>2024-01-05</c>.</summary>
    Iso,

    /// <summary>Month name then day, e.g. <c>January 5</c>, <c>Jan 5th 2024</c>.</summary>
    MonthDay,

    /// <summary>Day then month name, e.g. <c>5 January 2024</c>.</summary>
    DayMonth,

    /// <summary>Numeric, read month/day per the en-US decision, e.g. <c>1/5/2024</c>, <c>1-5</c>.</summary>
    Numeric,

    /// <summary>Weekday plus an ordinal with no month, e.g. <c>Monday the 5th</c>.</summary>
    WeekdayOrdinal,

    /// <summary>A bare ordinal, e.g. <c>The 5th.</c></summary>
    OrdinalOnly,
}

/// <summary>What the caller knows while scanning: the book's year and the chronology cursor.</summary>
/// <param name="BookYear">The book's year (R3); dates written without a year resolve into it.</param>
/// <param name="Cursor">
/// The date of the most recent entry matched at confidence ≥ 0.70. Matchers with no month stated
/// resolve their day number against it (doc 11, "Chronology prior").
/// </param>
public readonly record struct JournalDateContext(int BookYear, DateOnly? Cursor);

/// <summary>One date token recognized at a position in a paragraph, before any confidence scoring.</summary>
/// <param name="Kind">The token shape.</param>
/// <param name="Date">The resolved date.</param>
/// <param name="Length">Characters consumed from the scan position.</param>
/// <param name="HadYear">True when the text stated a year, rather than the book year being inferred.</param>
/// <param name="StatedWeekday">The weekday the text named, when it named one.</param>
/// <param name="SwappedReading">
/// The day/month reading of an ambiguous numeric date, e.g. <c>5/1</c> also reads as 1 May. Null when
/// the reading is forced or the token is not numeric.
/// </param>
public sealed record DateToken(
    DateTokenKind Kind,
    DateOnly Date,
    int Length,
    bool HadYear,
    DayOfWeek? StatedWeekday = null,
    DateOnly? SwappedReading = null);

/// <summary>
/// Recognizes the date forms doc 11 lists, at a given position in a paragraph. It answers "is there a
/// date token here and what does it say" — the confidence policy lives in
/// <see cref="JournalDateMatchers"/>, so the two can be tested independently (doc 13).
/// <para>
/// <b>Numeric dates are read month/day (en-US)</b> per the doc 11 decision. When the day field is
/// greater than 12 the reading is forced; when both readings are valid dates the M/D reading is kept
/// and the D/M reading is reported in <see cref="DateToken.SwappedReading"/> so the caller can drop
/// confidence and offer it as an alternate.
/// </para>
/// </summary>
public static partial class DateTokenScanner
{
    private const string WeekdayPattern =
        @"(?<wd>monday|tuesday|wednesday|thursday|friday|saturday|sunday|mon|tues|tue|thurs|thur|thu|wed|fri|sat|sun)\.?";

    private const string MonthPattern =
        @"(?<mon>january|february|march|april|may|june|july|august|september|october|november|december|" +
        @"jan|feb|mar|apr|jun|jul|aug|sept|sep|oct|nov|dec)\.?";

    /// <summary>Scans for a date token at the start of <paramref name="text"/>.</summary>
    /// <param name="text">The text to scan; leading whitespace is skipped.</param>
    /// <param name="context">Book year and chronology cursor.</param>
    /// <param name="allowCursorRelative">
    /// Allow the month-less forms (<see cref="DateTokenKind.WeekdayOrdinal"/>,
    /// <see cref="DateTokenKind.OrdinalOnly"/>), which need a cursor to resolve against.
    /// </param>
    /// <returns>The token, or null when the text does not start with a date.</returns>
    public static DateToken? TryScan(string text, JournalDateContext context, bool allowCursorRelative = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var offset = 0;
        while (offset < text.Length && char.IsWhiteSpace(text[offset])) offset++;
        var candidate = text[offset..];
        if (candidate.Length == 0) return null;

        var token = ScanIso(candidate)
                    ?? ScanMonthDay(candidate, context)
                    ?? ScanDayMonth(candidate, context)
                    ?? ScanWeekdayOrdinal(candidate, context, allowCursorRelative)
                    ?? ScanNumeric(candidate, context)
                    ?? ScanOrdinalOnly(candidate, context, allowCursorRelative);

        return token is null ? null : token with { Length = token.Length + offset };
    }

    /// <summary>
    /// Scans for the <em>end</em> of a range: after a dash or "to"/"through", either a full date or a
    /// bare day resolved into the start date's month.
    /// </summary>
    /// <param name="text">The remaining text, starting immediately after the start token.</param>
    /// <param name="start">The range's start date.</param>
    /// <param name="context">Book year and chronology cursor.</param>
    /// <returns>The end date and how many characters it consumed, or null when this is not a range.</returns>
    public static (DateOnly End, int Length)? TryScanRangeEnd(string text, DateOnly start, JournalDateContext context)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var separator = RangeSeparator().Match(text);
        if (!separator.Success) return null;

        var rest = text[separator.Length..];

        // A full date after the separator: "Jan 5 – Jan 7", "1/5 - 1/7".
        var token = TryScan(rest, context with { Cursor = start }, allowCursorRelative: true);
        if (token is not null && token.Date >= start && (token.Date.DayNumber - start.DayNumber) <= 60)
            return (token.Date, separator.Length + token.Length);

        var bare = BareRangeEnd().Match(rest);
        if (!bare.Success) return null;

        var day = int.Parse(bare.Groups["d"].ValueSpan, CultureInfo.InvariantCulture);
        if (!TryMakeDate(start.Year, start.Month, day, out var end)) return null;
        if (end < start || (end.DayNumber - start.DayNumber) > 60) return null;

        // A bare number is only a range end when the text says so unambiguously: a tight dash
        // ("January 25–27"), a spelled-out separator ("January 5 through 7"), an ordinal ("–7th"), or
        // the number ending the sentence. That keeps "January 5 — 7 inches of snow" as prose.
        var tightDash = separator.Groups["pre"].Length == 0 && separator.Groups["post"].Length == 0;
        var spelledOut = char.IsLetter(separator.Groups["sep"].Value[0]);
        var hasOrdinal = bare.Groups["ord"].Success;
        var terminated = bare.Groups["term"].Length > 0 || bare.Index + bare.Length >= rest.Length;
        if (!tightDash && !spelledOut && !hasOrdinal && !terminated) return null;

        return (end, separator.Length + bare.Length);
    }

    private static DateToken? ScanIso(string text)
    {
        var match = Iso().Match(text);
        if (!match.Success) return null;

        var year = int.Parse(match.Groups["y"].ValueSpan, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups["m"].ValueSpan, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups["d"].ValueSpan, CultureInfo.InvariantCulture);
        return TryMakeDate(year, month, day, out var date)
            ? new DateToken(DateTokenKind.Iso, date, match.Length, HadYear: true)
            : null;
    }

    private static DateToken? ScanMonthDay(string text, JournalDateContext context)
    {
        var match = MonthDay().Match(text);
        if (!match.Success) return null;

        var month = MonthNumber(match.Groups["mon"].Value);
        if (month == 0) return null;

        var day = int.Parse(match.Groups["d"].ValueSpan, CultureInfo.InvariantCulture);
        var hadYear = match.Groups["y"].Success;
        var year = hadYear ? int.Parse(match.Groups["y"].ValueSpan, CultureInfo.InvariantCulture) : context.BookYear;
        return TryMakeDate(year, month, day, out var date)
            ? new DateToken(DateTokenKind.MonthDay, date, match.Length, hadYear, Weekday(match))
            : null;
    }

    private static DateToken? ScanDayMonth(string text, JournalDateContext context)
    {
        var match = DayMonth().Match(text);
        if (!match.Success) return null;

        var month = MonthNumber(match.Groups["mon"].Value);
        if (month == 0) return null;

        var day = int.Parse(match.Groups["d"].ValueSpan, CultureInfo.InvariantCulture);
        var hadYear = match.Groups["y"].Success;
        var year = hadYear ? int.Parse(match.Groups["y"].ValueSpan, CultureInfo.InvariantCulture) : context.BookYear;
        return TryMakeDate(year, month, day, out var date)
            ? new DateToken(DateTokenKind.DayMonth, date, match.Length, hadYear, Weekday(match))
            : null;
    }

    private static DateToken? ScanNumeric(string text, JournalDateContext context)
    {
        var match = Numeric().Match(text);
        if (!match.Success) return null;

        var a = int.Parse(match.Groups["a"].ValueSpan, CultureInfo.InvariantCulture);
        var b = int.Parse(match.Groups["b"].ValueSpan, CultureInfo.InvariantCulture);
        var hadYear = match.Groups["y"].Success;
        var year = hadYear ? NormalizeYear(match.Groups["y"].Value) : context.BookYear;

        // en-US: month/day. If that is impossible the reading is forced to day/month instead.
        DateOnly date;
        DateOnly? swapped = null;
        if (TryMakeDate(year, a, b, out date))
        {
            if (TryMakeDate(year, b, a, out var alternate) && a != b) swapped = alternate;
        }
        else if (!TryMakeDate(year, b, a, out date))
        {
            return null;
        }

        return new DateToken(DateTokenKind.Numeric, date, match.Length, hadYear, Weekday(match), swapped);
    }

    private static DateToken? ScanWeekdayOrdinal(string text, JournalDateContext context, bool allowCursorRelative)
    {
        if (!allowCursorRelative || context.Cursor is null) return null;

        var match = WeekdayOrdinal().Match(text);
        if (!match.Success) return null;

        var day = int.Parse(match.Groups["d"].ValueSpan, CultureInfo.InvariantCulture);
        return TryResolveAgainstCursor(day, context.Cursor.Value, out var date)
            ? new DateToken(DateTokenKind.WeekdayOrdinal, date, match.Length, HadYear: false, Weekday(match))
            : null;
    }

    private static DateToken? ScanOrdinalOnly(string text, JournalDateContext context, bool allowCursorRelative)
    {
        if (!allowCursorRelative || context.Cursor is null) return null;

        var match = OrdinalOnly().Match(text);
        if (!match.Success) return null;

        var day = int.Parse(match.Groups["d"].ValueSpan, CultureInfo.InvariantCulture);
        return TryResolveAgainstCursor(day, context.Cursor.Value, out var date)
            ? new DateToken(DateTokenKind.OrdinalOnly, date, match.Length, HadYear: false)
            : null;
    }

    /// <summary>
    /// Resolves a bare day number against the chronology cursor: cursor month, then cursor + 1, then
    /// cursor − 1, taking the first chronologically plausible hit (doc 11).
    /// </summary>
    private static bool TryResolveAgainstCursor(int day, DateOnly cursor, out DateOnly date)
    {
        Span<int> offsets = [0, 1, -1];
        DateOnly? firstValid = null;

        foreach (var offset in offsets)
        {
            var anchor = cursor.AddMonths(offset);
            if (!TryMakeDate(anchor.Year, anchor.Month, day, out var candidate)) continue;

            firstValid ??= candidate;
            if (candidate >= cursor && (candidate.DayNumber - cursor.DayNumber) <= 45)
            {
                date = candidate;
                return true;
            }
        }

        date = firstValid ?? default;
        return firstValid is not null;
    }

    private static DayOfWeek? Weekday(Match match)
    {
        if (!match.Groups["wd"].Success) return null;
        var value = match.Groups["wd"].Value.TrimEnd('.').ToLowerInvariant();
        return value switch
        {
            "monday" or "mon" => DayOfWeek.Monday,
            "tuesday" or "tues" or "tue" => DayOfWeek.Tuesday,
            "wednesday" or "wed" => DayOfWeek.Wednesday,
            "thursday" or "thurs" or "thur" or "thu" => DayOfWeek.Thursday,
            "friday" or "fri" => DayOfWeek.Friday,
            "saturday" or "sat" => DayOfWeek.Saturday,
            "sunday" or "sun" => DayOfWeek.Sunday,
            _ => null,
        };
    }

    /// <summary>The month number for a full or abbreviated English month name, or 0.</summary>
    public static int MonthNumber(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        return name.TrimEnd('.').ToLowerInvariant() switch
        {
            "january" or "jan" => 1,
            "february" or "feb" => 2,
            "march" or "mar" => 3,
            "april" or "apr" => 4,
            "may" => 5,
            "june" or "jun" => 6,
            "july" or "jul" => 7,
            "august" or "aug" => 8,
            "september" or "sept" or "sep" => 9,
            "october" or "oct" => 10,
            "november" or "nov" => 11,
            "december" or "dec" => 12,
            _ => 0,
        };
    }

    private static int NormalizeYear(string value)
    {
        var year = int.Parse(value, CultureInfo.InvariantCulture);
        if (value.Length == 4) return year;
        return year <= 69 ? 2000 + year : 1900 + year;           // the usual two-digit century split
    }

    private static bool TryMakeDate(int year, int month, int day, out DateOnly date)
    {
        date = default;
        if (year < 1 || year > 9999 || month is < 1 or > 12 || day < 1) return false;
        if (day > DateTime.DaysInMonth(year, month)) return false;
        date = new DateOnly(year, month, day);
        return true;
    }

    [GeneratedRegex(@"^(?<y>\d{4})-(?<m>\d{1,2})-(?<d>\d{1,2})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Iso();

    [GeneratedRegex(@"^(?:" + WeekdayPattern + @",?\s+)?" + MonthPattern +
                    @"\s+(?<d>\d{1,2})(?:st|nd|rd|th)?(?:\s*,?\s*(?<y>\d{4}))?(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MonthDay();

    [GeneratedRegex(@"^(?:" + WeekdayPattern + @",?\s+)?(?:the\s+)?(?<d>\d{1,2})(?:st|nd|rd|th)?\s+(?:of\s+)?" +
                    MonthPattern + @"(?:\s*,?\s*(?<y>\d{4}))?(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DayMonth();

    [GeneratedRegex(@"^(?:" + WeekdayPattern + @",?\s+)?(?<a>\d{1,2})[/-](?<b>\d{1,2})(?:[/-](?<y>\d{4}|\d{2}))?(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Numeric();

    [GeneratedRegex(@"^" + WeekdayPattern + @",?\s+(?:the\s+)?(?<d>\d{1,2})(?:st|nd|rd|th)(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeekdayOrdinal();

    [GeneratedRegex(@"^(?:the\s+)?(?<d>\d{1,2})(?:st|nd|rd|th)(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OrdinalOnly();

    [GeneratedRegex(@"^(?<pre>\s*)(?<sep>[-–—]{1,2}|(?:to|through|thru|until|till)\b)(?<post>\s*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RangeSeparator();

    [GeneratedRegex(@"^(?:the\s+)?(?<d>\d{1,2})(?<ord>st|nd|rd|th)?(?<term>\s*[.,;:!?]|\s*$)?(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareRangeEnd();
}
