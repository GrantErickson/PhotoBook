namespace PhotoBook.Core.Model;

/// <summary>
/// A page layout (kernel §6, doc 07). Library templates are immutable data shipped with the app and
/// referenced by <see cref="Page.TemplateRef"/>; hand-editing a page's geometry copies the template
/// inline as a <see cref="Page.DetachedTemplate"/> snapshot which drops <see cref="Id"/> and gains
/// <see cref="BasedOn"/> (R15). All rects are normalized over the single-page trim box (kernel §3).
/// </summary>
public sealed record Template
{
    /// <summary>Schema version of the template document; currently 1.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Stable library id, e.g. <c>t-04-text-a</c>. Null on a detached snapshot.</summary>
    public string? Id { get; set; }

    /// <summary>The library id this snapshot was detached from; null for library templates (R15).</summary>
    public string? BasedOn { get; set; }

    /// <summary>Human display name shown in the layout picker.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The page-size id this template is authored for; it applies only to pages of exactly this size (R19).</summary>
    public string PageSize { get; set; } = PageGeometry.DefaultPageSizeId;

    /// <summary>What the template is for.</summary>
    public TemplateKind Kind { get; set; } = TemplateKind.Standard;

    /// <summary>Number of photos the layout holds, 1..8 (R20); equals the total slot count.</summary>
    public int PhotoCount { get; set; }

    /// <summary>
    /// Whether the engine may mirror this template horizontally for a left page. Templates are
    /// authored as right pages (gutter at <c>x = 0</c>); mirroring is automatic and never persisted.
    /// Must be false for spread pairs.
    /// </summary>
    public bool Mirrorable { get; set; }

    /// <summary>
    /// The template's opt-in to deliberate overlap (doc 07 "Deliberate overlap"): image slots that
    /// intersect each other, or text placed on a photo. Overlap in a template that does not declare
    /// this is an authoring accident and a linter error (L2, L3).
    /// </summary>
    public bool Overlaps { get; set; }

    /// <summary>The image slots, in reading order.</summary>
    public IList<ImageSlot> Slots { get; set; } = new List<ImageSlot>();

    /// <summary>The text slots; may be empty — textless layouts are the deliberate negative-space option (R20).</summary>
    public IList<TextSlot> TextSlots { get; set; } = new List<TextSlot>();

    /// <summary>Spread-pair link; non-null exactly when <see cref="Kind"/> is <see cref="TemplateKind.SpreadPair"/>.</summary>
    public TemplatePair? Pair { get; set; }

    /// <summary>Per-day sections; non-null exactly when <see cref="Kind"/> is <see cref="TemplateKind.MultiDay"/> (R28).</summary>
    public IList<TemplateSection>? Sections { get; set; }

    /// <summary>True when this instance is an inline page snapshot rather than a library template (R15).</summary>
    public bool IsDetachedSnapshot => Id is null && BasedOn is not null;

    /// <summary>
    /// The image slots in paint order — ascending <see cref="ImageSlot.Layer"/>, ties in authored
    /// order (<see cref="Enumerable.OrderBy{T,TKey}(IEnumerable{T}, Func{T,TKey})"/> is stable). The
    /// renderer draws in this order, so a slot on a higher layer sits on top of the ones below it;
    /// <see cref="Slots"/> itself stays in reading order because that is what slot assignment, the
    /// Unplaced-bin fill order and the empty-slot flags follow (doc 07).
    /// </summary>
    public IEnumerable<ImageSlot> SlotsInPaintOrder => Slots.OrderBy(s => s.Layer);

    /// <summary>The journal-role text slots in authored order — the chain a day's atomic text flows into (doc 11).</summary>
    public IEnumerable<TextSlot> JournalSlots => TextSlots.Where(t => t.Role == TextRole.Journal);

    /// <summary>True when the layout offers somewhere for a day's journal text to go.</summary>
    public bool HasJournalSlot => TextSlots.Any(t => t.Role == TextRole.Journal);

    /// <summary>Finds an image slot by id, or null.</summary>
    public ImageSlot? FindSlot(string slotId) =>
        Slots.FirstOrDefault(s => string.Equals(s.Id, slotId, StringComparison.Ordinal));

    /// <summary>Finds a text slot by id, or null.</summary>
    public TextSlot? FindTextSlot(string textSlotId) =>
        TextSlots.FirstOrDefault(t => string.Equals(t.Id, textSlotId, StringComparison.Ordinal));

    /// <summary>
    /// This template mirrored for a left page: every slot and text-slot rect flipped about the page
    /// centerline (doc 07). Ids, ordering, aspects, tier affinities, caption policies and text
    /// alignment are unchanged. Returns an equivalent deep copy when <see cref="Mirrorable"/> is false.
    /// </summary>
    public Template Mirrored()
    {
        var copy = DeepCopy();
        if (!Mirrorable) return copy;

        foreach (var slot in copy.Slots) slot.Rect = slot.Rect.Mirrored();
        foreach (var text in copy.TextSlots) text.Rect = text.Rect.Mirrored();
        return copy;
    }

    /// <summary>
    /// A deep copy of this template as a detached page snapshot: <see cref="Id"/> is dropped and
    /// <see cref="BasedOn"/> records where it came from (R15, doc 07). The library template itself is
    /// never mutated, so other pages using it are unaffected.
    /// </summary>
    public Template ToDetachedSnapshot()
    {
        var copy = DeepCopy();
        copy.BasedOn = Id ?? BasedOn;
        copy.Id = null;
        return copy;
    }

    private Template DeepCopy() => this with
    {
        Slots = Slots.Select(s => s with { }).ToList(),
        TextSlots = TextSlots.Select(t => t with { }).ToList(),
        Sections = Sections?.Select(s => s with
        {
            SlotIds = new List<string>(s.SlotIds),
            TextSlotIds = new List<string>(s.TextSlotIds),
        }).ToList(),
        Pair = Pair is null ? null : Pair with { },
    };
}
