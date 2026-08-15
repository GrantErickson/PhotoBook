namespace PhotoBook.Ingestion.Journal;

/// <summary>One paragraph's date match, after every confidence adjustment of doc 11 has been applied.</summary>
/// <param name="MatcherName">The matcher that fired, camelCase, as stored in <see cref="PhotoBook.Core.Model.JournalEntry.MatchedBy"/>.</param>
/// <param name="DateStart">First day covered.</param>
/// <param name="DateEnd">Last day covered; equals <paramref name="DateStart"/> unless the token was a range.</param>
/// <param name="Confidence">Final confidence, 0..1.</param>
/// <param name="TokenLength">Characters of the paragraph the date token consumed.</param>
/// <param name="Alternates">Other defensible resolutions, offered as one-click chips in the Import Report.</param>
/// <param name="OutOfYear">True when the resolved date fell outside the book year (R3).</param>
public sealed record JournalDateMatch(
    string MatcherName,
    DateOnly DateStart,
    DateOnly DateEnd,
    double Confidence,
    int TokenLength,
    IReadOnlyList<DateOnly> Alternates,
    bool OutOfYear)
{
    /// <summary>True when the entry spans more than one day.</summary>
    public bool IsRange => DateEnd > DateStart;
}

/// <summary>
/// The tolerant multi-matcher of doc 11: an ordered list of matchers where <b>the first one that
/// fires wins</b>, each with a base confidence, followed by the year-inference, chronology and
/// weekday adjustments. Real family journals mix formats freely, so the goal is never to guess — a
/// weak match comes back <em>ambiguous</em> and lands in the Import Report rather than becoming a
/// silently wrong date.
/// <para>Matchers, in order: <c>explicitHeading</c> (1.00 with a year, 0.90 without) ·
/// <c>monthDayPrefix</c> (0.80) · <c>numericDate</c> (0.75) · <c>weekdayOrdinal</c> (0.70) ·
/// <c>ordinalOnly</c> (0.60); any of them expressing a span becomes <c>range</c> at −0.05.</para>
/// </summary>
public static class JournalDateMatchers
{
    /// <summary>Final confidence at or above this is <see cref="PhotoBook.Core.Model.JournalEntryStatus.Matched"/>.</summary>
    public const double MatchedThreshold = 0.70;

    /// <summary>
    /// Below this the token is not treated as a date at all and the paragraph flows into the current
    /// entry; between it and <see cref="MatchedThreshold"/> the entry is
    /// <see cref="PhotoBook.Core.Model.JournalEntryStatus.Ambiguous"/>.
    /// </summary>
    public const double AmbiguousThreshold = 0.40;

    /// <summary>Matcher name for a date found in a heading whose entire text is the date.</summary>
    public const string ExplicitHeading = "explicitHeading";

    /// <summary>Matcher name for a paragraph starting with a month name and day.</summary>
    public const string MonthDayPrefix = "monthDayPrefix";

    /// <summary>Matcher name for a numeric date at paragraph start.</summary>
    public const string NumericDate = "numericDate";

    /// <summary>Matcher name for a weekday plus ordinal with no month.</summary>
    public const string WeekdayOrdinal = "weekdayOrdinal";

    /// <summary>Matcher name for a bare ordinal at paragraph start.</summary>
    public const string OrdinalOnly = "ordinalOnly";

    /// <summary>Matcher name applied when any of the above expresses a span.</summary>
    public const string Range = "range";

    /// <summary>
    /// Runs the ordered matchers over one paragraph and returns the first match, or null when the
    /// paragraph does not start with anything date-shaped.
    /// </summary>
    /// <param name="paragraph">The paragraph to inspect.</param>
    /// <param name="context">Book year and chronology cursor.</param>
    public static JournalDateMatch? TryMatch(ParagraphRecord paragraph, JournalDateContext context)
    {
        ArgumentNullException.ThrowIfNull(paragraph);
        if (paragraph.IsEmpty) return null;

        var token = DateTokenScanner.TryScan(paragraph.Text, context);
        if (token is null) return null;

        var consumed = token.Length;
        var end = token.Date;

        // Ranges: "January 5–7", "Jan 5 – Jan 7", "the 5th through the 7th".
        var range = DateTokenScanner.TryScanRangeEnd(paragraph.Text[consumed..], token.Date, context);
        if (range is { } span)
        {
            end = span.End;
            consumed += span.Length;
        }

        var isRange = end > token.Date;
        var (name, baseConfidence) = Classify(paragraph, token, consumed, isRange);

        var alternates = new List<DateOnly>();
        var confidence = baseConfidence;

        // Ambiguous numeric reading: month/day is kept, day/month is recorded (doc 11 Decision).
        if (token.SwappedReading is { } swapped)
        {
            confidence -= 0.15;
            alternates.Add(swapped);
        }

        // Year inference (R3).
        var outOfYear = token.Date.Year != context.BookYear;
        if (outOfYear) confidence -= 0.25;

        // Chronology prior: journals read forward.
        if (context.Cursor is { } cursor)
        {
            var delta = token.Date.DayNumber - cursor.DayNumber;
            if (delta < -3) confidence -= 0.20;
            else if (delta > 45) confidence -= 0.15;
        }

        // Weekday agreement.
        if (token.StatedWeekday is { } weekday)
        {
            if (token.Date.DayOfWeek == weekday)
            {
                confidence += 0.10;
            }
            else
            {
                confidence -= 0.30;
                var nearest = NearestWeekday(token.Date, weekday);
                if (nearest != token.Date && !alternates.Contains(nearest)) alternates.Add(nearest);
            }
        }

        confidence = Math.Clamp(confidence, 0.0, 1.0);
        return new JournalDateMatch(name, token.Date, end, confidence, consumed, alternates, outOfYear);
    }

    /// <summary>Whether a final confidence means matched, ambiguous, or "not a date at all".</summary>
    /// <param name="confidence">The final confidence.</param>
    public static PhotoBook.Core.Model.JournalEntryStatus StatusFor(double confidence) =>
        confidence >= MatchedThreshold ? PhotoBook.Core.Model.JournalEntryStatus.Matched
        : confidence >= AmbiguousThreshold ? PhotoBook.Core.Model.JournalEntryStatus.Ambiguous
        : PhotoBook.Core.Model.JournalEntryStatus.Unmatched;

    private static (string Name, double Confidence) Classify(
        ParagraphRecord paragraph, DateToken token, int consumed, bool isRange)
    {
        string name;
        double confidence;

        if (paragraph.IsHeadingCandidate && ConsumesWholeParagraph(paragraph.Text, consumed))
        {
            name = ExplicitHeading;
            confidence = token.HadYear ? 1.00 : 0.90;
        }
        else
        {
            (name, confidence) = token.Kind switch
            {
                DateTokenKind.MonthDay or DateTokenKind.DayMonth => (MonthDayPrefix, 0.80),
                DateTokenKind.Iso or DateTokenKind.Numeric => (NumericDate, 0.75),
                DateTokenKind.WeekdayOrdinal => (WeekdayOrdinal, 0.70),
                _ => (OrdinalOnly, 0.60),
            };
        }

        if (isRange)
        {
            name = Range;
            confidence -= 0.05;
        }

        return (name, confidence);
    }

    /// <summary>True when the date token is the paragraph's entire text, bar trailing punctuation.</summary>
    private static bool ConsumesWholeParagraph(string text, int consumed)
    {
        for (var i = consumed; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsWhiteSpace(c) && c is not ('.' or ',' or ':' or ';' or '-' or '–' or '—')) return false;
        }

        return true;
    }

    /// <summary>The nearest date to <paramref name="date"/> falling on <paramref name="weekday"/>.</summary>
    private static DateOnly NearestWeekday(DateOnly date, DayOfWeek weekday)
    {
        for (var offset = 0; offset <= 3; offset++)
        {
            if (date.AddDays(-offset).DayOfWeek == weekday) return date.AddDays(-offset);
            if (date.AddDays(offset).DayOfWeek == weekday) return date.AddDays(offset);
        }

        return date;
    }
}
