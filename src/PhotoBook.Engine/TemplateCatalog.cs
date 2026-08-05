using PhotoBook.Core.Model;
using PhotoBook.Core.Templates;

namespace PhotoBook.Engine;

/// <summary>
/// A read-only index over the shipped <see cref="TemplateLibrary"/> for one page size, built once
/// per engine run. It exists so the hard filters of doc 08 §6 are O(candidates) instead of
/// O(library) and so phase 3 can ask structural questions ("is there a multiDay template shaped
/// like these three days?") before it commits to a merge.
/// </summary>
public sealed class TemplateCatalog
{
    private readonly Dictionary<int, List<Template>> _byPhotoCount = [];
    private readonly List<Template> _multiDay = [];
    private readonly List<Template> _monthTitle = [];
    private readonly List<KeyValuePair<string, (Template Left, Template Right)>> _spreadPairs = [];
    private readonly Dictionary<string, Template> _byId = new(StringComparer.Ordinal);

    /// <summary>Indexes every template of <paramref name="library"/> authored for <paramref name="pageSize"/>.</summary>
    public TemplateCatalog(TemplateLibrary library, string pageSize)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(pageSize);

        var pairLeft = new Dictionary<string, Template>(StringComparer.Ordinal);
        var pairRight = new Dictionary<string, Template>(StringComparer.Ordinal);

        foreach (var template in library.Templates)
        {
            if (!string.Equals(template.PageSize, pageSize, StringComparison.Ordinal)) continue;
            if (template.Id is { } id) _byId[id] = template;

            switch (template.Kind)
            {
                case TemplateKind.MultiDay:
                    _multiDay.Add(template);
                    break;
                case TemplateKind.MonthTitle:
                    _monthTitle.Add(template);
                    break;
                case TemplateKind.SpreadPair:
                    if (template.Pair is { } pair)
                    {
                        if (pair.Side == PairSide.Left) pairLeft[pair.PairId] = template;
                        else pairRight[pair.PairId] = template;
                    }

                    break;
                default:
                    if (!_byPhotoCount.TryGetValue(template.PhotoCount, out var bucket))
                    {
                        bucket = [];
                        _byPhotoCount[template.PhotoCount] = bucket;
                    }

                    bucket.Add(template);
                    break;
            }
        }

        foreach (var id in pairLeft.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (pairRight.TryGetValue(id, out var right))
            {
                _spreadPairs.Add(new KeyValuePair<string, (Template, Template)>(id, (pairLeft[id], right)));
            }
        }

        SupportsTextOnlySections = _multiDay.Any(t => t.Sections?.Any(s => s.SlotIds.Count == 0) == true);

        var withJournal = 0;
        foreach (var bucket in _byPhotoCount)
        {
            foreach (var template in bucket.Value)
            {
                if (template.HasJournalSlot && bucket.Key > withJournal) withJournal = bucket.Key;
            }
        }

        MaxPhotoCountWithJournalSlot = withJournal;
    }

    /// <summary>
    /// The largest photo count for which a single-page template with a journal slot exists — the cap
    /// the partitioner puts on the first page of a day that carries text, so the escalation ladder of
    /// doc 08 §12 rarely has to fire.
    /// </summary>
    public int MaxPhotoCountWithJournalSlot { get; }

    /// <summary>
    /// True when at least one multiDay template offers a section with zero image slots — the shape
    /// doc 08 §12 wants for a journal-only day. The v1 library has none, so journal-only days are
    /// carried onto a neighbouring day's page instead (see <see cref="LayoutEngine"/>).
    /// </summary>
    public bool SupportsTextOnlySections { get; }

    /// <summary>The template with this id among those authored for this page size, or null.</summary>
    public Template? Find(string? id) => id is null ? null : _byId.GetValueOrDefault(id);

    /// <summary>Standard and full-bleed templates holding exactly <paramref name="photoCount"/> photos, in library order.</summary>
    public IReadOnlyList<Template> SinglePage(int photoCount) =>
        _byPhotoCount.TryGetValue(photoCount, out var bucket) ? bucket : [];

    /// <summary>Every multiDay template, in library order.</summary>
    public IReadOnlyList<Template> MultiDay => _multiDay;

    /// <summary>Every month-title template, in library order.</summary>
    public IReadOnlyList<Template> MonthTitle => _monthTitle;

    /// <summary>Every complete spread pair, ordered by pair id.</summary>
    public IReadOnlyList<KeyValuePair<string, (Template Left, Template Right)>> SpreadPairs => _spreadPairs;

    /// <summary>
    /// The multiDay templates whose section structure matches this run of days: one section per day
    /// in order, each section's image-slot count equal to that day's photo count, and a journal-role
    /// text slot present for every day that has journal text (doc 07 "Multi-day section templates").
    /// </summary>
    public IReadOnlyList<Template> MatchingMultiDay(IReadOnlyList<LayoutDay> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        var matches = new List<Template>();
        foreach (var template in _multiDay)
        {
            if (SectionsMatch(template, days)) matches.Add(template);
        }

        return matches;
    }

    /// <summary>True when <paramref name="template"/>'s sections line up with <paramref name="days"/> one for one.</summary>
    public static bool SectionsMatch(Template template, IReadOnlyList<LayoutDay> days)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(days);

        var sections = template.Sections;
        if (sections is null || sections.Count != days.Count) return false;

        for (var i = 0; i < days.Count; i++)
        {
            var section = sections[i];
            if (section.SlotIds.Count != days[i].Photos.Count) return false;

            var hasJournalSlot = section.TextSlotIds.Count > 0;
            if (days[i].HasJournal && !hasJournalSlot) return false;
        }

        return true;
    }

    /// <summary>
    /// The "family" of a template id for the <c>S_variety</c> memory (doc 08 §6): the id with a
    /// trailing single-letter variant removed, so <c>t-04-text-a</c> and <c>t-04-text-c</c> share
    /// family <c>t-04-text</c> while <c>t-04-notext-a</c> does not.
    /// </summary>
    public static string FamilyOf(string? templateId)
    {
        if (string.IsNullOrEmpty(templateId)) return string.Empty;

        var parts = templateId.Split('-');
        if (parts.Length <= 2) return templateId;

        // t-sp-a-left / t-sp-a-right → t-sp-a; t-04-text-a → t-04-text; t-md-a → t-md.
        var last = parts[^1];
        var cut = last.Length == 1 || string.Equals(last, "left", StringComparison.Ordinal) ||
                  string.Equals(last, "right", StringComparison.Ordinal)
            ? parts.Length - 1
            : parts.Length;
        return cut <= 1 ? templateId : string.Join('-', parts, 0, cut);
    }

    /// <summary>The template's journal-role text slots in authored (flow) order.</summary>
    public static IReadOnlyList<TextSlot> JournalChain(Template template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var chain = new List<TextSlot>();
        foreach (var slot in template.TextSlots)
        {
            if (slot.Role == TextRole.Journal) chain.Add(slot);
        }

        return chain;
    }

    /// <summary>The journal-role text slots belonging to one multiDay section, in authored order.</summary>
    public static IReadOnlyList<TextSlot> SectionJournalChain(Template template, TemplateSection section)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(section);
        var chain = new List<TextSlot>();
        foreach (var id in section.TextSlotIds)
        {
            var slot = template.FindTextSlot(id);
            if (slot is { Role: TextRole.Journal }) chain.Add(slot);
        }

        return chain;
    }

    /// <summary>The image slots of one multiDay section, in authored order.</summary>
    public static IReadOnlyList<ImageSlot> SectionSlots(Template template, TemplateSection section)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(section);
        var slots = new List<ImageSlot>();
        foreach (var id in section.SlotIds)
        {
            var slot = template.FindSlot(id);
            if (slot is not null) slots.Add(slot);
        }

        return slots;
    }

    /// <summary>
    /// Slot coverage as a fraction of the trim box: the area of the <b>union</b> of the template's
    /// image slots, clipped to the page. This is the input to <c>S_coverage</c> (doc 08 §6) and it
    /// answers the only question that term cares about — how much of the page is picture rather than
    /// background. The union, not the sum, is what makes that true once templates overlap slots
    /// deliberately (doc 07): two slots sharing a corner cover the page once, not twice, and a summed
    /// figure would score an overlapping layout as fuller than it looks.
    /// <para>
    /// Computed by coordinate compression over the slots' own edges — exact, allocation-light at
    /// n ≤ 8, and free of any floating-point ordering surprises, so it stays bit-reproducible (§9).
    /// </para>
    /// </summary>
    public static double SlotCoverage(Template template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var rects = new List<Rect>(8);
        foreach (var slot in template.Slots)
        {
            var left = Math.Max(0.0, slot.Rect.X);
            var top = Math.Max(0.0, slot.Rect.Y);
            var right = Math.Min(1.0, slot.Rect.Right);
            var bottom = Math.Min(1.0, slot.Rect.Bottom);
            if (right > left && bottom > top) rects.Add(Rect.FromEdges(left, top, right, bottom));
        }

        if (rects.Count == 0) return 0;
        if (rects.Count == 1) return rects[0].Area;

        var xs = new List<double>(rects.Count * 2);
        var ys = new List<double>(rects.Count * 2);
        foreach (var rect in rects)
        {
            xs.Add(rect.X);
            xs.Add(rect.Right);
            ys.Add(rect.Y);
            ys.Add(rect.Bottom);
        }

        xs.Sort();
        ys.Sort();

        var area = 0.0;
        for (var i = 0; i < xs.Count - 1; i++)
        {
            var width = xs[i + 1] - xs[i];
            if (width <= 0) continue;

            for (var j = 0; j < ys.Count - 1; j++)
            {
                var height = ys[j + 1] - ys[j];
                if (height <= 0) continue;

                foreach (var rect in rects)
                {
                    if (rect.X <= xs[i] && rect.Right >= xs[i + 1] &&
                        rect.Y <= ys[j] && rect.Bottom >= ys[j + 1])
                    {
                        area += width * height;
                        break;
                    }
                }
            }
        }

        return area;
    }
}
