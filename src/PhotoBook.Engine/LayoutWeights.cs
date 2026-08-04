using PhotoBook.Core.Model;

namespace PhotoBook.Engine;

/// <summary>
/// Every tunable constant of the auto-layout engine in one record (doc 08 §14). The shipped
/// <see cref="Default"/> instance carries exactly the defaults of doc 08, so tuning the engine
/// never means hunting for magic numbers in phase code: construct a modified copy with
/// <c>with</c> and hand it to the <see cref="LayoutRequest"/>.
/// <para>
/// Names map one-to-one onto the doc: <see cref="TierAreaS"/>…<see cref="TierAreaC"/> are
/// <c>tierArea</c> (§4), <see cref="FitWeight"/> is <c>W_FIT</c> (§5), and so on. Values are pure
/// <see cref="double"/>s used in plain arithmetic — no intrinsics, no fast-math — because layout
/// must be bit-reproducible across machines (§9, doc 13).
/// </para>
/// </summary>
public sealed record LayoutWeights
{
    /// <summary>The shipped defaults — every value in doc 08 §14's summary table.</summary>
    public static LayoutWeights Default { get; } = new();

    // ── §4 Demand model ───────────────────────────────────────────────────────────────────────

    /// <summary><c>tierArea[S]</c> — page units an S-tier photo wants. Default 0.30.</summary>
    public double TierAreaS { get; init; } = 0.30;

    /// <summary><c>tierArea[A]</c> — page units an A-tier photo wants. Default 0.18.</summary>
    public double TierAreaA { get; init; } = 0.18;

    /// <summary><c>tierArea[B]</c> — page units a B-tier photo wants. Default 0.11.</summary>
    public double TierAreaB { get; init; } = 0.11;

    /// <summary><c>tierArea[C]</c> — page units a C-tier photo wants. Default 0.07.</summary>
    public double TierAreaC { get; init; } = 0.07;

    /// <summary>
    /// <c>CHARS_PER_FULL_PAGE</c> — journal characters that fill one page at the default Style
    /// (≈ 60 chars/in² over the ~79 in² safe area). Default 4800; recompute from Style with
    /// <see cref="TextCapacity.CharsPerFullPage"/> when the journal font changes.
    /// </summary>
    public double CharsPerFullPage { get; init; } = 4800;

    /// <summary><c>W_PHOTO</c> — photo-area weight in the demand sum. Default 1.0.</summary>
    public double PhotoWeight { get; init; } = 1.0;

    /// <summary><c>W_TEXT</c> — text-area weight in the demand sum. Default 1.0.</summary>
    public double TextWeight { get; init; } = 1.0;

    /// <summary><c>W_BASE</c> — per-day breathing room (gutters, captions, whitespace). Default 0.05.</summary>
    public double BaseDemand { get; init; } = 0.05;

    /// <summary><c>MIN_DAY_DEMAND</c> — floor for any non-empty day. Default 0.15.</summary>
    public double MinDayDemand { get; init; } = 0.15;

    // ── §5 DP page partitioning ───────────────────────────────────────────────────────────────

    /// <summary><c>W_FIT</c> — how tightly pages hug demand. Default 1.0.</summary>
    public double FitWeight { get; init; } = 1.0;

    /// <summary><c>MERGE_COST</c> — cost per extra day merged onto one page. Default 0.08.</summary>
    public double MergeCost { get; init; } = 0.08;

    /// <summary><c>PARITY_PENALTY</c> — cost of failing to spread-align a long-text day. Default 0.50.</summary>
    public double ParityPenalty { get; init; } = 0.50;

    /// <summary><c>MAX_DAYS_PER_PAGE</c> — matches the multiDay section counts. Default 3.</summary>
    public int MaxDaysPerPage { get; init; } = 3;

    /// <summary><c>MAX_TEXT_PER_PAGE</c> — text demand above which a day needs a whole Spread. Default 0.55.</summary>
    public double MaxTextPerPage { get; init; } = 0.55;

    /// <summary>Total demand ceiling for a merged multiDay page (doc 08 §5). Default 1.15.</summary>
    public double MaxMergeDemand { get; init; } = 1.15;

    /// <summary>A day may only join a merged page with at most this many photos (doc 08 §5). Default 3.</summary>
    public int MaxPhotosPerMergedDay { get; init; } = 3;

    /// <summary>The R20 slot ceiling — the most photos any single page may hold. Default 8.</summary>
    public int MaxSlotsPerPage { get; init; } = 8;

    /// <summary><c>kMax = ceil(SplitPageFactor · D)</c> in the split transition (doc 08 §5). Default 1.6.</summary>
    public double SplitPageFactor { get; init; } = 1.6;

    /// <summary>
    /// How strongly a split day's pages are pulled toward equal photo counts, relative to the
    /// normalized time gaps they are cut at (doc 08 §5). A genuine cluster boundary scores ~1.0 and
    /// still wins outright; this only decides the cuts the gaps are indifferent about — a day shot as
    /// one even burst. 0 restores the doc's literal "largest gaps, ties by seeded hash". Default 0.35.
    /// </summary>
    public double SplitBalanceWeight { get; init; } = 0.35;

    // ── §6 Template scoring ───────────────────────────────────────────────────────────────────

    /// <summary><c>S_aspect</c> weight. Default 0.30.</summary>
    public double AspectScoreWeight { get; init; } = 0.30;

    /// <summary><c>S_tier</c> weight. Default 0.20.</summary>
    public double TierScoreWeight { get; init; } = 0.20;

    /// <summary><c>S_text</c> weight. Default 0.15.</summary>
    public double TextScoreWeight { get; init; } = 0.15;

    /// <summary><c>S_variety</c> weight. Default 0.20.</summary>
    public double VarietyScoreWeight { get; init; } = 0.20;

    /// <summary><c>S_pacing</c> weight. Default 0.15.</summary>
    public double PacingScoreWeight { get; init; } = 0.15;

    /// <summary>Bound of the seeded template-score jitter (doc 08 §6, §9). Default 0.02.</summary>
    public double JitterBound { get; init; } = 0.02;

    /// <summary>Pages that must pass before another <c>fullBleed</c> page is well paced. Default 4.</summary>
    public int FullBleedCooldownPages { get; init; } = 4;

    /// <summary>"Used within the last N pages" window of <c>S_variety</c>. Default 3.</summary>
    public int VarietyRecentWindow { get; init; } = 3;

    /// <summary>"Same family within the last N pages" window of <c>S_variety</c>. Default 2.</summary>
    public int VarietyFamilyWindow { get; init; } = 2;

    /// <summary><c>S_variety</c> when the previous page used the very same template. Default 0.</summary>
    public double VarietySameTemplateScore { get; init; } = 0.0;

    /// <summary><c>S_variety</c> when the template appeared within <see cref="VarietyRecentWindow"/>. Default 0.5.</summary>
    public double VarietyRecentScore { get; init; } = 0.5;

    /// <summary><c>S_variety</c> when the template's family appeared within <see cref="VarietyFamilyWindow"/>. Default 0.75.</summary>
    public double VarietyFamilyScore { get; init; } = 0.75;

    /// <summary>Slot-coverage tolerance around <c>min(1, pageDemand)</c> in <c>S_pacing</c>. Default 0.10.</summary>
    public double PacingCoverageTolerance { get; init; } = 0.10;

    /// <summary>Baseline <c>S_pacing</c> of a non-full-bleed template. Default 0.70.</summary>
    public double PacingBaseScore { get; init; } = 0.70;

    /// <summary>Bonus added when slot coverage matches page demand. Default 0.30.</summary>
    public double PacingCoverageBonus { get; init; } = 0.30;

    /// <summary><c>S_pacing</c> of a <c>fullBleed</c> template that is out of rhythm. Default 0.20.</summary>
    public double PacingFullBleedOutOfRhythm { get; init; } = 0.20;

    /// <summary>Lower end of the ideal text fill band. Default 0.50.</summary>
    public double TextFillIdealLow { get; init; } = 0.50;

    /// <summary>Upper end of the ideal text fill band. Default 0.85.</summary>
    public double TextFillIdealHigh { get; init; } = 0.85;

    /// <summary>Fill at which the slot reads as cavernous. Default 0.10.</summary>
    public double TextFillCavernous { get; init; } = 0.10;

    /// <summary><c>S_text</c> at <see cref="TextFillCavernous"/>. Default 0.30.</summary>
    public double TextFillCavernousScore { get; init; } = 0.30;

    /// <summary><c>S_text</c> for a page with no journal text on a template with no journal slot. Default 1.0.</summary>
    public double TextlessPageScore { get; init; } = 1.0;

    /// <summary>
    /// <c>S_text</c> for a page with no journal text on a template that has a journal slot — the slot
    /// renders as deliberate negative space (R20), which is fine but not preferred. Default 0.75.
    /// </summary>
    public double TextlessPageWithJournalSlotScore { get; init; } = 0.75;

    // ── §7 Hungarian slot assignment ──────────────────────────────────────────────────────────

    /// <summary><c>aspectCrop</c> weight in the assignment cost. Default 0.45.</summary>
    public double AspectCropWeight { get; init; } = 0.45;

    /// <summary><c>focusRisk</c> weight in the assignment cost. Default 0.20.</summary>
    public double FocusRiskWeight { get; init; } = 0.20;

    /// <summary><c>tierDist</c> weight in the assignment cost. Default 0.20.</summary>
    public double TierDistanceWeight { get; init; } = 0.20;

    /// <summary><c>chronoDisp</c> weight in the assignment cost. Default 0.15.</summary>
    public double ChronoWeight { get; init; } = 0.15;

    /// <summary><c>captionAdj</c> — added when a captioned photo lands in a caption-less slot. Default 0.15.</summary>
    public double CaptionPenalty { get; init; } = 0.15;

    /// <summary>
    /// When true, <c>aspectCrop</c> gets a dead zone of <see cref="ImageSlot.AspectTolerance"/>
    /// (doc 07's "zero aspect penalty inside the tolerance" reading) instead of doc 08 §7's plain
    /// log-ratio. <b>Off by default</b> so the shipped behavior is exactly doc 08 §7.
    /// </summary>
    public bool UseAspectTolerance { get; init; } = false;

    /// <summary>
    /// When true, <c>readingIndex(s)</c> is derived from the slot's <em>oriented</em> geometry
    /// (top-to-bottom, then left-to-right), so a mirrored left page reads correctly; when false the
    /// authored slot order is used verbatim. Default true.
    /// </summary>
    public bool UseGeometricReadingOrder { get; init; } = true;

    /// <summary>Row-band height, in normalized page units, used to group slots into reading rows. Default 0.12.</summary>
    public double ReadingRowBand { get; init; } = 0.12;

    // ── §8 Smart crop ─────────────────────────────────────────────────────────────────────────

    /// <summary>The zoom the engine emits — exactly the minimal-crop cover fit (doc 08 §8). Default 1.0.</summary>
    public double EmittedZoom { get; init; } = 1.0;

    /// <summary>Hard ceiling on any zoom the engine may ever emit; headroom for a future tight-crop heuristic. Default 1.25.</summary>
    public double MaxEmittedZoom { get; init; } = 1.25;

    // ── Spread pairs (R18/R22 — doc 07 "Spread pairs", ships in M5) ───────────────────────────

    /// <summary>
    /// Whether the engine may promote two facing single-day pages into a matched
    /// <see cref="TemplateKind.SpreadPair"/> (R22). Rare by construction — see
    /// <see cref="SpreadPairCooldownPages"/> and <see cref="SpreadPairRequiresHero"/> — because R18
    /// says full-page spreads "should not be the norm, but should be supported". Default true.
    /// </summary>
    public bool EnableSpreadPairs { get; init; } = true;

    /// <summary>Pages that must pass between two spread pairs. Default 6.</summary>
    public int SpreadPairCooldownPages { get; init; } = 6;

    /// <summary>A spread pair is only offered when the two pages hold an S-tier photo. Default true.</summary>
    public bool SpreadPairRequiresHero { get; init; } = true;

    /// <summary>
    /// A gutter-spanning pair (one photo across 22 × 8.5 in, R18) is only offered to photos at least
    /// this wide relative to their height. Default 2.0.
    /// </summary>
    public double SpreadSpanMinAspect { get; init; } = 2.0;

    // ── Diagnostics thresholds ────────────────────────────────────────────────────────────────

    /// <summary>Page demand above which the page is reported as crowded. Default 1.30.</summary>
    public double CrowdingDemand { get; init; } = 1.30;

    /// <summary>How many <c>k + 1</c> re-partition attempts the text escalation ladder may make (doc 08 §12). Default 2.</summary>
    public int TextEscalationRetries { get; init; } = 2;

    /// <summary>Page units a photo of the given effective tier wants (doc 08 §4).</summary>
    /// <param name="tier">The photo's effective tier (user override wins, R26).</param>
    public double TierArea(Tier tier) => tier switch
    {
        Tier.S => TierAreaS,
        Tier.A => TierAreaA,
        Tier.B => TierAreaB,
        _ => TierAreaC,
    };
}
