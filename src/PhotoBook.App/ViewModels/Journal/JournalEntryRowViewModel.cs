using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoBook.Core.Model;

namespace PhotoBook.App.ViewModels.Journal;

/// <summary>
/// One date the matcher considered defensible for an entry, offered as a one-click chip (doc 11
/// §"Import Report UI"). The common ambiguous case — a date the matcher read but was not sure of, plus
/// the reading it rejected — is then a single click rather than a trip through a calendar.
/// </summary>
/// <param name="Date">The date the chip assigns.</param>
/// <param name="Label">How the date reads, e.g. "Fri 5 Jan 2024".</param>
/// <param name="Reason">Where the candidate came from, shown under the label.</param>
/// <param name="IsCurrent">True when the entry already carries this date.</param>
/// <param name="IsOutOfYear">True when the candidate falls outside the book's year (R3).</param>
public sealed record JournalDateCandidate(
    DateOnly Date, string Label, string Reason, bool IsCurrent, bool IsOutOfYear);

/// <summary>
/// One journal entry as the Import Report and the day map show it: which review group it falls in,
/// what the matcher made of it, its text, and the dates it could plausibly take.
///
/// <para>
/// The row is a projection, never a writer — every edit goes through
/// <see cref="JournalImportReportViewModel"/> so it lands on the undo stack — and
/// <see cref="Refresh"/> re-reads the entry after one.
/// </para>
/// </summary>
public sealed partial class JournalEntryRowViewModel : ObservableObject
{
    private readonly int _bookYear;

    /// <param name="entry">The entry this row stands for.</param>
    /// <param name="bookYear">The book's year, which decides the out-of-year group (R3).</param>
    public JournalEntryRowViewModel(JournalEntry entry, int bookYear)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entry = entry;
        _bookYear = bookYear;
        Candidates = BuildCandidates();
    }

    /// <summary>The underlying entry.</summary>
    public JournalEntry Entry { get; }

    /// <summary>The entry's stable content-derived id.</summary>
    public string Id => Entry.Id;

    /// <summary>Which review group the entry is in right now.</summary>
    public JournalReviewBucket Bucket => JournalEdits.Bucket(Entry, _bookYear);

    /// <summary>The effective date, or an em dash when the entry has none (doc 11's "—").</summary>
    public string DateLabel => Entry.Status == JournalEntryStatus.Unmatched && Entry.UserDate is null
        ? "—"
        : Entry.EffectiveDate.ToString("ddd d MMM", CultureInfo.CurrentCulture);

    /// <summary>The year alone, so an out-of-year row says which year without a second line.</summary>
    public string YearLabel => Entry.Status == JournalEntryStatus.Unmatched && Entry.UserDate is null
        ? string.Empty
        : Entry.EffectiveDate.Year.ToString(CultureInfo.CurrentCulture);

    /// <summary>A short sentence naming the state: "No date", "Unsure", "Dated by you"…</summary>
    public string StatusLabel => Bucket switch
    {
        JournalReviewBucket.Undated => "No date could be read",
        JournalReviewBucket.Ambiguous => "The date is a guess",
        JournalReviewBucket.OutOfYear => $"Dated outside {_bookYear.ToString(CultureInfo.CurrentCulture)}",
        JournalReviewBucket.Discarded => "Discarded",
        _ => Entry.Status == JournalEntryStatus.UserAssigned ? "Dated by you" : "Dated",
    };

    /// <summary>The matcher that fired and how sure it was — the provenance of the date.</summary>
    public string MatcherLabel
    {
        get
        {
            if (Entry.Status == JournalEntryStatus.UserAssigned)
            {
                return "You set this date by hand.";
            }

            if (Entry.MatchedBy is not { Length: > 0 } matcher)
            {
                return "No date token was found anywhere in this entry.";
            }

            var percent = (Entry.Confidence * 100).ToString("F0", CultureInfo.CurrentCulture);
            return $"Read by the {Humanize(matcher)} matcher, {percent}% confident.";
        }
    }

    /// <summary>The heading the date came from, or a stand-in for an entry that had none.</summary>
    public string HeadingLabel => Entry.HeadingText is { Length: > 0 } heading
        ? heading
        : Entry.Paragraphs.Count == 0 ? "(empty entry)" : "(no heading — the text opens the entry)";

    /// <summary>True when the entry actually has a heading, so the stand-in can be dimmed.</summary>
    public bool HasHeading => Entry.HeadingText is { Length: > 0 };

    /// <summary>The one-line preview shown in the list.</summary>
    public string Preview
    {
        get
        {
            var text = Entry.Paragraphs.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))
                       ?? Entry.HeadingText
                       ?? string.Empty;
            return text.Length <= 140 ? text : text[..140] + "…";
        }
    }

    /// <summary>The whole entry as one block, blank line between paragraphs — the detail pane's text.</summary>
    public string BodyText => string.Join(Environment.NewLine + Environment.NewLine, Entry.Paragraphs);

    /// <summary>"3 paragraphs · 412 characters" — enough to judge whether text went missing.</summary>
    public string SizeLabel
    {
        get
        {
            var paragraphs = Entry.Paragraphs.Count;
            var characters = Entry.CharacterCount;
            return $"{paragraphs} paragraph{(paragraphs == 1 ? string.Empty : "s")} · " +
                   $"{characters.ToString("N0", CultureInfo.CurrentCulture)} characters";
        }
    }

    /// <summary>True when the entry spans several days, so the detail pane can say so.</summary>
    public bool IsRange => Entry.IsRange;

    /// <summary>The span, for a range entry.</summary>
    public string RangeLabel => Entry.IsRange
        ? $"Covers {Entry.DateStart.ToString("d MMM", CultureInfo.CurrentCulture)} – " +
          $"{Entry.DateEnd.ToString("d MMM", CultureInfo.CurrentCulture)}"
        : string.Empty;

    /// <summary>True when the entry is in the book nowhere until the user dates it.</summary>
    public bool NeedsDate => Bucket == JournalReviewBucket.Undated;

    /// <summary>True when the matcher was unsure — the chips are the fast path.</summary>
    public bool IsAmbiguous => Bucket == JournalReviewBucket.Ambiguous;

    /// <summary>True when the entry's date falls outside the book's year (R3).</summary>
    public bool IsOutOfYear => Bucket == JournalReviewBucket.OutOfYear;

    /// <summary>True when the user discarded the entry.</summary>
    public bool IsDiscarded => Entry.Excluded;

    /// <summary>True when the user set this date by hand.</summary>
    public bool IsUserAssigned => Entry.Status == JournalEntryStatus.UserAssigned;

    /// <summary>True when the row is in one of the three groups that want the user's attention.</summary>
    public bool NeedsAttention => Bucket is JournalReviewBucket.Undated
        or JournalReviewBucket.Ambiguous or JournalReviewBucket.OutOfYear;

    /// <summary>The dates offered as one-click chips, best first.</summary>
    public IReadOnlyList<JournalDateCandidate> Candidates { get; private set; }

    /// <summary>True when there is at least one chip to click.</summary>
    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>True while this row is the one the detail pane is showing.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Re-reads the entry after an edit. Everything on this row is derived, so one blanket
    /// notification is both correct and cheaper than twenty named ones.
    /// </summary>
    public void Refresh()
    {
        Candidates = BuildCandidates();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>
    /// The candidate dates, ranked so the first chip is almost always the right one: the reading the
    /// matcher actually made, then the readings it recorded as defensible alternates, with anything
    /// outside the book's year pushed below anything inside it (R3 — a book covers one year, so an
    /// in-year candidate is nearly always what was meant).
    /// </summary>
    private IReadOnlyList<JournalDateCandidate> BuildCandidates()
    {
        var candidates = new List<(int Rank, JournalDateCandidate Candidate)>();
        var seen = new HashSet<DateOnly>();

        void Add(DateOnly date, string reason, int baseRank)
        {
            if (!seen.Add(date))
            {
                return;
            }

            var outOfYear = date.Year != _bookYear;
            candidates.Add((baseRank + (outOfYear ? 10 : 0), new JournalDateCandidate(
                date,
                date.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture),
                reason,
                date == Entry.EffectiveDate && Entry.Status != JournalEntryStatus.Unmatched,
                outOfYear)));
        }

        if (Entry.Status != JournalEntryStatus.Unmatched && Entry.DateStart != default)
        {
            Add(Entry.DateStart, "What the document reads as", 0);
        }

        foreach (var alternate in Entry.Alternates)
        {
            Add(alternate, "Another defensible reading", 1);
        }

        if (Entry.UserDate is { } assigned)
        {
            Add(assigned, "Your assignment", 0);
        }

        return [.. candidates.OrderBy(c => c.Rank).ThenBy(c => c.Candidate.Date).Select(c => c.Candidate)];
    }

    /// <summary>Turns a camelCase matcher name into the words doc 11 uses for it.</summary>
    private static string Humanize(string matcher) => matcher switch
    {
        "explicitHeading" => "date heading",
        "monthDayPrefix" => "month-and-day",
        "numericDate" => "numeric date",
        "weekdayOrdinal" => "weekday-and-ordinal",
        "ordinalOnly" => "bare ordinal",
        "range" => "date range",
        _ => matcher,
    };
}
