using PhotoBook.Core.Model;
using PhotoBook.Core.Templates;

namespace PhotoBook.Engine;

/// <summary>
/// The chapter-prefix memory behind <c>S_variety</c> and <c>S_pacing</c> (doc 08 §6). It is fed in
/// final page order — including Pinned anchor pages, whose templates absolutely count towards
/// rhythm — so a relayout of the tail of a chapter still paces against what came before.
/// </summary>
public sealed class PacingMemory
{
    private readonly List<string> _templateIds = [];
    private readonly List<TemplateKind> _kinds = [];
    private readonly List<double> _coverages = [];

    /// <summary>How many pages the memory has seen.</summary>
    public int PageCount => _templateIds.Count;

    /// <summary>Records an emitted page.</summary>
    /// <param name="templateId">The library template id, or null for a detached snapshot.</param>
    /// <param name="kind">The template kind.</param>
    /// <param name="coverage">
    /// The page's slot coverage (<see cref="TemplateCatalog.SlotCoverage"/>), which is what the airy
    /// half of <c>S_pacing</c> rations. Pages whose coverage is unknown count as well-filled, so an
    /// unresolvable anchor never hands out a free airy slot.
    /// </param>
    public void Record(string? templateId, TemplateKind kind, double coverage = 1.0)
    {
        _templateIds.Add(templateId ?? string.Empty);
        _kinds.Add(kind);
        _coverages.Add(coverage);
    }

    /// <summary>
    /// <c>S_variety</c>: 0 if the previous page used the same template, 0.5 if it appeared within the
    /// last 3 pages, 0.75 if the same family appeared within the last 2, else 1.0.
    /// </summary>
    public double Variety(Template template, LayoutWeights weights)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(weights);

        var id = template.Id ?? string.Empty;
        if (_templateIds.Count > 0 && string.Equals(_templateIds[^1], id, StringComparison.Ordinal))
        {
            return weights.VarietySameTemplateScore;
        }

        for (var back = 1; back <= weights.VarietyRecentWindow && back <= _templateIds.Count; back++)
        {
            if (string.Equals(_templateIds[^back], id, StringComparison.Ordinal))
            {
                return weights.VarietyRecentScore;
            }
        }

        var family = TemplateCatalog.FamilyOf(id);
        for (var back = 1; back <= weights.VarietyFamilyWindow && back <= _templateIds.Count; back++)
        {
            if (string.Equals(TemplateCatalog.FamilyOf(_templateIds[^back]), family, StringComparison.Ordinal))
            {
                return weights.VarietyFamilyScore;
            }
        }

        return 1.0;
    }

    /// <summary>Pages emitted since the last page of this kind; <see cref="int.MaxValue"/> if never.</summary>
    public int PagesSince(TemplateKind kind)
    {
        for (var back = 1; back <= _kinds.Count; back++)
        {
            if (_kinds[^back] == kind) return back - 1;
        }

        return int.MaxValue;
    }

    /// <summary>Pages emitted since the last airy page; <see cref="int.MaxValue"/> if never.</summary>
    /// <param name="weights">Supplies <see cref="LayoutWeights.AiryCoverageMax"/>.</param>
    public int PagesSinceAiry(LayoutWeights weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        for (var back = 1; back <= _coverages.Count; back++)
        {
            if (_coverages[^back] < weights.AiryCoverageMax) return back - 1;
        }

        return int.MaxValue;
    }

    /// <summary>
    /// <c>S_pacing</c> — <b>rhythm only</b> (doc 08 §6). It answers "is this the right page for a
    /// gesture?", never "is this template full enough?"; that second question is <c>S_coverage</c>'s,
    /// and conflating the two is what used to make sparse templates win.
    /// <list type="bullet">
    /// <item><description>A <c>fullBleed</c> page scores 1.0 only when it holds an S-tier photo and no
    /// full bleed appeared in the last 4 pages (R18's "not the norm").</description></item>
    /// <item><description>An <b>airy</b> template — coverage below
    /// <see cref="LayoutWeights.AiryCoverageMax"/>, deliberate negative space per R20 — scores 1.0
    /// when its cooldown has elapsed and <see cref="LayoutWeights.PacingAiryOutOfRhythm"/> when it
    /// has not. Airy pages are rationed, not banned: they still have to out-score a fuller template on
    /// <c>S_coverage</c>, so they turn up occasionally, for rhythm.</description></item>
    /// <item><description>Everything else takes the flat <see cref="LayoutWeights.PacingBaseScore"/>
    /// baseline — a well-filled page is always in rhythm.</description></item>
    /// </list>
    /// </summary>
    public double Pacing(Template template, PagePlan plan, LayoutWeights weights)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(weights);

        if (template.Kind == TemplateKind.FullBleed)
        {
            var since = PagesSince(TemplateKind.FullBleed);
            var inRhythm = plan.HasHeroPhoto && since >= weights.FullBleedCooldownPages;
            return inRhythm ? 1.0 : weights.PacingFullBleedOutOfRhythm;
        }

        var coverage = TemplateCatalog.SlotCoverage(template);
        if (coverage >= weights.AiryCoverageMax) return weights.PacingBaseScore;

        return PagesSinceAiry(weights) >= weights.AiryCooldownPages
            ? weights.PacingAiryInRhythm
            : weights.PacingAiryOutOfRhythm;
    }

    /// <summary>
    /// <c>S_coverage</c> (doc 08 §6) — how much of the page the template's slots actually cover,
    /// against the target the page deserves: <c>min(1, coverage / target)</c>, with
    /// <c>target = SOLO_COVERAGE_TARGET</c> for a page holding one photo and
    /// <c>COVERAGE_TARGET</c> otherwise.
    /// <para>
    /// This term is monotone in coverage, which is the whole point. Its predecessor rewarded coverage
    /// "within ±10% of page demand", so a page whose demand was 0.15 — one weak photo — scored best on
    /// the emptiest template in the library, and the book came out full of white space. Now fuller
    /// wins by default at every demand, a photo that earned a page to itself gets a near-full-page
    /// composition, and airiness is bought back deliberately through <see cref="Pacing"/>.
    /// </para>
    /// </summary>
    public static double Coverage(Template template, PagePlan plan, LayoutWeights weights)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(weights);

        var target = plan.Photos.Count <= 1 ? weights.SoloCoverageTarget : weights.CoverageTarget;
        if (!(target > 0)) return 1.0;

        return Math.Clamp(TemplateCatalog.SlotCoverage(template) / target, 0, 1);
    }
}

/// <summary>A scored template candidate — the winner's assignment is kept, phase 5 is not re-run.</summary>
public sealed record TemplateChoice
{
    /// <summary>The library template that won.</summary>
    public required Template Library { get; init; }

    /// <summary>The same template oriented for the page side (mirrored on left pages when allowed).</summary>
    public required Template Oriented { get; init; }

    /// <summary>True when the oriented template is the mirrored variant — stored on <see cref="Page.Mirrored"/>.</summary>
    public bool Mirrored { get; init; }

    /// <summary>The winning Hungarian assignment for a single-day page.</summary>
    public SlotAssignment Assignment { get; init; } = SlotAssignment.Empty;

    /// <summary>The per-section assignments for a multiDay page, in section order.</summary>
    public IReadOnlyList<SlotAssignment> SectionAssignments { get; init; } = [];

    /// <summary>The soft score that won.</summary>
    public double Score { get; init; }

    /// <summary>False when the template was accepted despite not holding the day's text (§12 ladder step 4).</summary>
    public bool TextFits { get; init; } = true;
}

/// <summary>
/// Phase 4 of doc 08 — template scoring. Hard filters first (photo count, kind, text fit,
/// mirroring), then the weighted soft score
/// <c>0.26·S_aspect + 0.17·S_tier + 0.13·S_text + 0.17·S_variety + 0.10·S_pacing +
/// 0.17·S_coverage</c> plus seeded jitter. Scoring runs the <em>real</em> phase-5 Hungarian for every
/// surviving candidate — with n ≤ 8 that is trivial — and the winner's assignment is kept.
/// </summary>
public static class TemplateSelector
{
    /// <summary>
    /// Chooses a template for <paramref name="plan"/>, or null when no candidate passes the hard
    /// filters. <paramref name="allowTextOverflow"/> is the last rung of the §12 escalation ladder:
    /// it keeps the roomiest journal template instead of failing.
    /// </summary>
    public static TemplateChoice? Select(
        PagePlan plan, LayoutContext context, PacingMemory memory, bool allowTextOverflow = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(memory);

        var candidates = Candidates(plan, context);
        TemplateChoice? best = null;
        var bestScore = double.NegativeInfinity;
        var bestTieBreak = 0.0;

        var overflowFallback = (Template?)null;
        var overflowCapacity = -1.0;

        foreach (var template in candidates)
        {
            var isLeft = plan.Side == PageSide.Left;
            var oriented = TemplateLibrary.ForPage(template, isLeft);

            if (!PassesTextFilter(plan, oriented, context))
            {
                if (!allowTextOverflow) continue;

                var capacity = context.CapacityChars(TemplateCatalog.JournalChain(oriented));
                if (capacity > overflowCapacity)
                {
                    overflowCapacity = capacity;
                    overflowFallback = template;
                }

                continue;
            }

            var scored = Score(plan, template, oriented, isLeft, context, memory);
            var tieBreak = LayoutRandom.Unit(context.Seed, "tie|" + (template.Id ?? string.Empty) + "|" + plan.StableKey);

            if (scored.Score > bestScore || (scored.Score == bestScore && tieBreak > bestTieBreak))
            {
                best = scored;
                bestScore = scored.Score;
                bestTieBreak = tieBreak;
            }
        }

        if (best is not null) return best;
        if (overflowFallback is null) return null;

        // §12 ladder step 4: place with the roomiest journal template and let preflight raise
        // TextOverflow. Never auto-shrink the font (kernel §9).
        var fallbackLeft = plan.Side == PageSide.Left;
        var fallbackOriented = TemplateLibrary.ForPage(overflowFallback, fallbackLeft);
        var fallbackChoice = Score(plan, overflowFallback, fallbackOriented, fallbackLeft, context, memory);
        return fallbackChoice with { TextFits = false };
    }

    /// <summary>The hard filters of doc 08 §6, applied in order of cheapness.</summary>
    private static IEnumerable<Template> Candidates(PagePlan plan, LayoutContext context)
    {
        if (plan.ForcedTemplateId is not null)
        {
            // A spread-pair page: the promotion pass already chose both sides together (R22).
            var forced = context.Catalog.Find(plan.ForcedTemplateId);
            if (forced is not null) yield return forced;
            yield break;
        }

        switch (plan.Kind)
        {
            case PagePlanKind.MonthTitle:
                foreach (var template in context.Catalog.MonthTitle)
                {
                    if (template.PhotoCount == plan.Photos.Count) yield return template;
                }

                break;

            case PagePlanKind.MultiDay:
                foreach (var template in context.Catalog.MultiDay)
                {
                    if (template.PhotoCount != plan.Photos.Count) continue;
                    if (!SectionsMatch(template, plan)) continue;
                    yield return template;
                }

                break;

            default:
                foreach (var template in context.Catalog.SinglePage(plan.Photos.Count))
                {
                    // spreadPair templates are only reachable through the paired promotion pass, and
                    // monthTitle/multiDay are excluded by construction of the catalog buckets.
                    if (template.Kind is not (TemplateKind.Standard or TemplateKind.FullBleed)) continue;
                    yield return template;
                }

                break;
        }
    }

    private static bool SectionsMatch(Template template, PagePlan plan)
    {
        var sections = template.Sections;
        if (sections is null || sections.Count != plan.Sections.Count) return false;

        for (var i = 0; i < sections.Count; i++)
        {
            if (sections[i].SlotIds.Count != plan.Sections[i].Photos.Count) return false;
            if (plan.Sections[i].Day.HasJournal && sections[i].TextSlotIds.Count == 0) return false;
        }

        return true;
    }

    private static bool PassesTextFilter(PagePlan plan, Template oriented, LayoutContext context)
    {
        if (plan.Kind == PagePlanKind.MultiDay)
        {
            var sections = oriented.Sections;
            if (sections is null) return false;
            for (var i = 0; i < sections.Count; i++)
            {
                var day = plan.Sections[i].Day;
                if (!day.HasJournal) continue;
                var chain = TemplateCatalog.SectionJournalChain(oriented, sections[i]);
                if (!context.FitsText(day.Paragraphs, chain)) return false;
            }

            return true;
        }

        if (plan.JournalChars == 0) return true;                 // textless pages may use any template
        return context.FitsText(plan.Paragraphs, TemplateCatalog.JournalChain(oriented));
    }

    private static TemplateChoice Score(
        PagePlan plan, Template library, Template oriented, bool mirrored,
        LayoutContext context, PacingMemory memory)
    {
        var weights = context.Weights;

        double meanCost;
        double meanTier;
        SlotAssignment assignment = SlotAssignment.Empty;
        var sectionAssignments = new List<SlotAssignment>();

        if (plan.Kind == PagePlanKind.MultiDay && oriented.Sections is { } sections)
        {
            var totalCost = 0.0;
            var totalTier = 0.0;
            var count = 0;
            for (var i = 0; i < sections.Count; i++)
            {
                var slots = TemplateCatalog.SectionSlots(oriented, sections[i]);
                var photos = plan.Sections[i].Photos;
                var reading = AssignmentCost.ReadingOrder(slots, weights);
                var section = AssignmentCost.Assign(photos, slots, reading, weights);
                sectionAssignments.Add(section);
                totalCost += section.MeanCost * photos.Count;
                totalTier += section.MeanTierDistance * photos.Count;
                count += photos.Count;
            }

            meanCost = count == 0 ? 0 : totalCost / count;
            meanTier = count == 0 ? 0 : totalTier / count;
        }
        else
        {
            var slots = oriented.Slots as IReadOnlyList<ImageSlot> ?? oriented.Slots.ToList();
            var reading = AssignmentCost.ReadingOrder(slots, weights);
            assignment = AssignmentCost.Assign(plan.Photos, slots, reading, weights);
            meanCost = assignment.MeanCost;
            meanTier = assignment.MeanTierDistance;
        }

        var aspectScore = Math.Clamp(1.0 - meanCost, 0, 1);
        var tierScore = Math.Clamp(1.0 - meanTier, 0, 1);
        var textScore = TextScore(plan, oriented, context);
        var varietyScore = memory.Variety(library, weights);
        var pacingScore = memory.Pacing(library, plan, weights);
        var coverageScore = PacingMemory.Coverage(library, plan, weights);

        var jitter = LayoutRandom.Signed(
            context.Seed, "jitter|" + (library.Id ?? string.Empty) + "|" + plan.StableKey, weights.JitterBound);

        var score = weights.AspectScoreWeight * aspectScore
                    + weights.TierScoreWeight * tierScore
                    + weights.TextScoreWeight * textScore
                    + weights.VarietyScoreWeight * varietyScore
                    + weights.PacingScoreWeight * pacingScore
                    + weights.CoverageScoreWeight * coverageScore
                    + jitter;

        return new TemplateChoice
        {
            Library = library,
            Oriented = oriented,
            Mirrored = mirrored && library.Mirrorable,
            Assignment = assignment,
            SectionAssignments = sectionAssignments,
            Score = score,
        };
    }

    /// <summary>
    /// <c>S_text</c> of doc 08 §6: 1.0 for a fill of 0.50–0.85, falling linearly to 0.3 at 0.10 and
    /// to 0 at 1.0. Pages with no journal text score by whether the template's journal slot would sit
    /// empty — legitimate negative space (R20), but not the first choice.
    /// </summary>
    private static double TextScore(PagePlan plan, Template oriented, LayoutContext context)
    {
        var weights = context.Weights;

        if (plan.Kind == PagePlanKind.MultiDay && oriented.Sections is { } sections)
        {
            var total = 0.0;
            var count = 0;
            for (var i = 0; i < sections.Count; i++)
            {
                var day = plan.Sections[i].Day;
                var chain = TemplateCatalog.SectionJournalChain(oriented, sections[i]);
                total += FillScore(day.JournalChars, context.CapacityChars(chain), chain.Count > 0, weights);
                count++;
            }

            return count == 0 ? 1.0 : total / count;
        }

        var pageChain = TemplateCatalog.JournalChain(oriented);
        return FillScore(plan.JournalChars, context.CapacityChars(pageChain), pageChain.Count > 0, weights);
    }

    private static double FillScore(int chars, double capacity, bool hasJournalSlot, LayoutWeights weights)
    {
        if (chars <= 0)
        {
            return hasJournalSlot ? weights.TextlessPageWithJournalSlotScore : weights.TextlessPageScore;
        }

        if (!hasJournalSlot || capacity <= 0) return 0;

        var fill = chars / capacity;
        if (fill >= weights.TextFillIdealLow && fill <= weights.TextFillIdealHigh) return 1.0;

        if (fill < weights.TextFillIdealLow)
        {
            if (fill <= weights.TextFillCavernous) return weights.TextFillCavernousScore;
            var span = weights.TextFillIdealLow - weights.TextFillCavernous;
            var t = span <= 0 ? 1 : (fill - weights.TextFillCavernous) / span;
            return weights.TextFillCavernousScore + t * (1.0 - weights.TextFillCavernousScore);
        }

        if (fill >= 1.0) return 0;
        var upperSpan = 1.0 - weights.TextFillIdealHigh;
        return upperSpan <= 0 ? 0 : 1.0 - (fill - weights.TextFillIdealHigh) / upperSpan;
    }
}
