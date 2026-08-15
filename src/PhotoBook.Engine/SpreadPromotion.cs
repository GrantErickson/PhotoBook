using PhotoBook.Core.Model;

namespace PhotoBook.Engine;

/// <summary>
/// The matched spread-pair pass (R22, doc 07 "Spread pairs"). Pairing is a <b>soft</b> feature, so
/// it runs after the DP has already decided page counts and only ever rewrites which template two
/// facing pages of the <em>same day</em> use:
/// <list type="bullet">
/// <item><description><b>Non-spanning pairs</b> (matched left/right compositions) redistribute the
/// two pages' photos across the pair's slots. Page count does not change.</description></item>
/// <item><description><b>Spanning pairs</b> (one photo across the 22 × 8.5 in Spread, R18) apply
/// only to a lone panorama and add the facing page, because the pair holds one photo in total. The
/// photo is counted once in placed/unplaced accounting.</description></item>
/// </list>
/// A cooldown plus the S-tier requirement keeps spreads rare — R18: "should not be the norm, but
/// should be supported".
/// </summary>
public static class SpreadPromotion
{
    /// <summary>Returns the run's plans with any spread pairs promoted; page sides are recomputed.</summary>
    /// <param name="plans">The run's page plans, in reading order.</param>
    /// <param name="context">The run environment.</param>
    /// <param name="runStartPageIndex">0-based index of the run's first page within the Chapter.</param>
    /// <param name="pagesSinceLastSpread">Pages emitted since the last spread pair, from the chapter prefix.</param>
    public static IReadOnlyList<PagePlan> Apply(
        IReadOnlyList<PagePlan> plans, LayoutContext context, int runStartPageIndex, int pagesSinceLastSpread)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(context);

        var weights = context.Weights;
        if (!weights.EnableSpreadPairs || context.Catalog.SpreadPairs.Count == 0) return plans;

        var result = new List<PagePlan>(plans.Count + 1);
        var since = pagesSinceLastSpread;
        var index = 0;

        while (index < plans.Count)
        {
            var plan = plans[index];
            var side = Spreads.SideOf(runStartPageIndex + result.Count);
            var consumed = 0;

            if (side == PageSide.Left && plan.ForcedTemplateId is null && since >= weights.SpreadPairCooldownPages)
            {
                consumed = TryPromote(plans, index, plan, context, result);
            }

            if (consumed > 0)
            {
                since = 0;
                index += consumed;
                continue;
            }

            result.Add(plan.Side == side ? plan : plan with { Side = side });
            since++;
            index++;
        }

        return result;
    }

    /// <summary>Returns how many source plans were consumed, or 0 when no pair applies.</summary>
    private static int TryPromote(
        IReadOnlyList<PagePlan> plans, int index, PagePlan plan, LayoutContext context, List<PagePlan> result)
    {
        var weights = context.Weights;

        // ── Spanning panorama: one photo across the whole Spread (R18). Adds the facing page. ──
        if (plan.Photos.Count == 1 && plan.JournalChars == 0)
        {
            var photo = plan.Photos[0];
            var aspect = SmartCrop.AspectOf(photo);
            if (aspect >= weights.SpreadSpanMinAspect &&
                (!weights.SpreadPairRequiresHero || photo.EffectiveTier is Tier.S or Tier.A))
            {
                foreach (var pair in context.Catalog.SpreadPairs)
                {
                    var (left, right) = pair.Value;
                    if (!IsSpanning(left) || !IsSpanning(right)) continue;
                    if (left.PhotoCount != 1 || right.PhotoCount != 1) continue;

                    result.Add(plan with
                    {
                        Side = PageSide.Left,
                        ForcedTemplateId = left.Id,
                        SpanWidthFactor = 2.0,
                        SharesPhotoWithFacingPage = true,
                        StableKey = plan.StableKey + "|sp-left",
                    });
                    result.Add(plan with
                    {
                        Side = PageSide.Right,
                        ForcedTemplateId = right.Id,
                        SpanWidthFactor = 2.0,
                        SharesPhotoWithFacingPage = true,
                        Entries = [],
                        StableKey = plan.StableKey + "|sp-right",
                    });
                    return 1;
                }
            }
        }

        // ── Non-spanning matched pair: two facing pages of the same day share a composition. ──
        if (index + 1 >= plans.Count) return 0;

        var next = plans[index + 1];
        if (plan.Kind != PagePlanKind.Standard || next.Kind != PagePlanKind.Standard) return 0;
        if (next.PrimaryDate != plan.PrimaryDate) return 0;
        if (next.ForcedTemplateId is not null) return 0;
        if (next.JournalChars > 0) return 0;                     // text already flows on the first page

        var combined = new List<Photo>(plan.Photos.Count + next.Photos.Count);
        combined.AddRange(plan.Photos);
        combined.AddRange(next.Photos);

        if (weights.SpreadPairRequiresHero && !combined.Any(p => p.EffectiveTier == Tier.S)) return 0;

        foreach (var pair in context.Catalog.SpreadPairs)
        {
            var (left, right) = pair.Value;
            if (IsSpanning(left) || IsSpanning(right)) continue;
            if (left.PhotoCount + right.PhotoCount != combined.Count) continue;

            if (plan.JournalChars > 0 &&
                !context.FitsText(plan.Paragraphs, TemplateCatalog.JournalChain(left)))
            {
                continue;
            }

            result.Add(plan with
            {
                Photos = combined.Take(left.PhotoCount).ToList(),
                Side = PageSide.Left,
                ForcedTemplateId = left.Id,
                StableKey = plan.StableKey + "|sp-left",
            });
            result.Add(next with
            {
                Photos = combined.Skip(left.PhotoCount).ToList(),
                Side = PageSide.Right,
                ForcedTemplateId = right.Id,
                Entries = [],
                StableKey = next.StableKey + "|sp-right",
            });
            return 2;
        }

        return 0;
    }

    private static bool IsSpanning(Template template) => template.Slots.Any(s => s.SpanId is not null);
}
