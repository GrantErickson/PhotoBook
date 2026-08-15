namespace PhotoBook.Core.Model;

/// <summary>
/// A place for one photo on a page (kernel §6, doc 07). The placed photo always cover-fills the rect
/// through a <see cref="CropState"/>; the slot itself never crops.
/// </summary>
public sealed record ImageSlot
{
    /// <summary>Unique within the template; conventionally <c>s1..sN</c> in reading order.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The slot box in page space, normalized over the trim box. Inside <c>[0,1]²</c> unless <see cref="Bleed"/>.</summary>
    public Rect Rect { get; set; }

    /// <summary>The slot's <b>physical</b> width/height ratio, declared redundantly so scorers never re-derive it.</summary>
    public double Aspect { get; set; } = 1.0;

    /// <summary>How far a photo's native aspect may differ before crop-loss cost ramps up; <c>0..0.6</c>, default 0.35.</summary>
    public double AspectTolerance { get; set; } = 0.35;

    /// <summary>The tier this slot prefers — a soft scoring preference, never a filter (R26).</summary>
    public TierAffinity TierAffinity { get; set; } = TierAffinity.Any;

    /// <summary>Whether and where a caption may render for the photo placed here (R5).</summary>
    public CaptionPolicy CaptionPolicy { get; set; } = CaptionPolicy.None;

    /// <summary>True when the rect crosses the trim edge and must reach the bleed box on every crossed side.</summary>
    public bool Bleed { get; set; }

    /// <summary>
    /// Paint order within the page, <c>0..9</c>, default <c>0</c> (doc 07 "Deliberate overlap"). Slots
    /// are drawn in ascending layer, ties broken by authored order, so a higher layer sits <em>on
    /// top</em>. Two slots may only overlap when they sit on different layers and their template
    /// declares <see cref="Template.Overlaps"/> — that pairing is what separates an intentional inset
    /// from an authoring accident (linter rule L2).
    /// </summary>
    public int Layer { get; set; }

    /// <summary>
    /// Spread-pair templates only: slots on the two sides sharing a span id hold the <em>same</em>
    /// photo, rendered continuously across the gutter (R18).
    /// </summary>
    public string? SpanId { get; set; }
}
