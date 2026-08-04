using PhotoBook.Core.Model;

namespace PhotoBook.Core.Templates;

/// <summary>How badly a <see cref="TemplateDiagnostic"/> breaks a template (doc 07 "Template linter").</summary>
public enum LintSeverity
{
    /// <summary>Legal but worth an author's attention; never blocks a build or a save.</summary>
    Warning,

    /// <summary>Unprintable. Fails the library build gate; shows as a red badge on a detached page.</summary>
    Error,
}

/// <summary>
/// One linter finding. The same structured diagnostic serves both callers: the xUnit gate over the
/// embedded library fails the build on any <see cref="LintSeverity.Error"/>, and the editor turns
/// findings on a Detached snapshot into badges that never block saving (doc 07, doc 09, doc 13).
/// </summary>
/// <param name="Rule">The doc 07 rule id — <c>L1</c>…<c>L12</c>.</param>
/// <param name="Severity">How badly it breaks.</param>
/// <param name="TemplateId">The template's id, or its <c>basedOn</c> id for a detached snapshot.</param>
/// <param name="TargetId">The offending slot / text slot / section id, when the rule has one.</param>
/// <param name="Message">What is wrong, in the author's terms.</param>
public sealed record TemplateDiagnostic(
    string Rule,
    LintSeverity Severity,
    string TemplateId,
    string? TargetId,
    string Message)
{
    /// <inheritdoc/>
    public override string ToString() =>
        $"{Severity.ToString().ToUpperInvariant()} {Rule} {TemplateId}" +
        $"{(TargetId is null ? string.Empty : "/" + TargetId)}: {Message}";
}

/// <summary>
/// The doc 07 template linter: pure, allocation-light, and identical in both contexts it runs in.
/// Rules L1–L12 are implemented one method each and reported as <see cref="TemplateDiagnostic"/>s.
/// </summary>
public static class TemplateLinter
{
    // ---- normalized constants for the default 11 x 8.5 landscape page (doc 07, kernel §3) ----

    /// <summary>Bleed 0.125 in on the x axis (0.125 ÷ 11).</summary>
    public const double BleedX = 0.0114;

    /// <summary>Bleed 0.125 in on the y axis (0.125 ÷ 8.5).</summary>
    public const double BleedY = 0.0147;

    /// <summary>Gutter caution 0.5 in — the left limit for text on an authored (right) page.</summary>
    public const double GutterCautionX = 0.0455;

    /// <summary>Safe margin 0.375 in from the outer edge: text must end by here.</summary>
    public const double SafeRight = 0.9659;

    /// <summary>Safe margin 0.375 in from the top.</summary>
    public const double SafeTop = 0.0441;

    /// <summary>Safe margin 0.375 in from the bottom: text must end by here.</summary>
    public const double SafeBottom = 0.9559;

    /// <summary>Minimum printable slot width — 1.5 in.</summary>
    public const double MinSlotWidth = 0.1364;

    /// <summary>Minimum printable slot height — 1.5 in.</summary>
    public const double MinSlotHeight = 0.1765;

    /// <summary>The reserved below-caption band — 0.30 in.</summary>
    public const double CaptionBandHeight = 0.0353;

    /// <summary>L2's tolerated pairwise slot intersection — rounding slop only.</summary>
    public const double MaxSlotOverlapArea = 0.002;

    /// <summary>L10's lower coverage bound.</summary>
    public const double MinCoverage = 0.15;

    /// <summary>L10's upper coverage bound.</summary>
    public const double MaxCoverage = 0.95;

    /// <summary>Geometric slop; rects are authored to four decimals.</summary>
    private const double Eps = 1e-6;

    /// <summary>Slop for "reaches the bleed edge exactly".</summary>
    private const double BleedEps = 1e-4;

    private const int MaxPhotoCount = 8;

    private static readonly Dictionary<string, (double W, double H)> PageInches =
        new(StringComparer.Ordinal) { ["11x8.5-landscape"] = (11.0, 8.5) };

    /// <summary>
    /// Lints one template against every rule that does not need the rest of the library, using the
    /// page dimensions registered for its <see cref="Template.PageSize"/>.
    /// </summary>
    public static IReadOnlyList<TemplateDiagnostic> Lint(Template template)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (!PageInches.TryGetValue(template.PageSize, out var page))
        {
            return
            [
                new TemplateDiagnostic("L12", LintSeverity.Error, IdOf(template), null,
                    $"unknown pageSize '{template.PageSize}'; the template cannot be validated."),
            ];
        }

        return Lint(template, page.W, page.H);
    }

    /// <summary>
    /// Lints one template against every rule that does not need the rest of the library, for an
    /// explicit physical page size in inches. Rects are normalized, so the page size only enters
    /// through the aspect check (L5).
    /// </summary>
    public static IReadOnlyList<TemplateDiagnostic> Lint(Template template, double pageWidthIn, double pageHeightIn)
    {
        ArgumentNullException.ThrowIfNull(template);

        var id = IdOf(template);
        var issues = new List<TemplateDiagnostic>();

        WellFormedness(template, id, issues);          // L12
        SlotsInsideTrim(template, id, issues);         // L1
        NoSlotOverlaps(template, id, issues);          // L2
        TextImageSeparation(template, id, issues);     // L3
        TextSlotsInsideSafeArea(template, id, issues); // L4
        AspectSanity(template, id, pageWidthIn, pageHeightIn, issues); // L5
        PhotoCount(template, id, issues);              // L6
        PrintableSlotSize(template, id, issues);       // L7
        StructuralIntegrity(template, id, issues);     // L8
        SpreadPairShape(template, id, issues);         // L9 (single-template half)
        CoverageSanity(template, id, issues);          // L10
        TrimTouchingSlots(template, id, issues);       // L11

        return Order(issues);
    }

    /// <summary>
    /// Lints a whole library: every per-template rule, plus the rules that only make sense across
    /// templates — library-wide unique ids and spread-pair completeness (L8, L9).
    /// </summary>
    public static IReadOnlyList<TemplateDiagnostic> LintLibrary(IEnumerable<Template> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);

        var all = templates.ToList();
        var issues = new List<TemplateDiagnostic>();

        foreach (var template in all) issues.AddRange(Lint(template));

        foreach (var group in all.Where(t => t.Id is not null)
                     .GroupBy(t => t.Id!, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            issues.Add(new TemplateDiagnostic("L8", LintSeverity.Error, group.Key, null,
                $"id is used by {group.Count()} templates; library ids must be unique."));
        }

        foreach (var pair in all.Where(t => t.Pair is not null)
                     .GroupBy(t => t.Pair!.PairId, StringComparer.Ordinal))
        {
            var sides = pair.Select(t => t.Pair!.Side).ToList();
            var reportedOn = IdOf(pair.First());

            foreach (var side in new[] { PairSide.Left, PairSide.Right })
            {
                if (!sides.Contains(side))
                {
                    issues.Add(new TemplateDiagnostic("L9", LintSeverity.Error, reportedOn, null,
                        $"spread pair '{pair.Key}' has no {side.ToString().ToLowerInvariant()} side."));
                }
            }

            if (sides.Count != sides.Distinct().Count())
            {
                issues.Add(new TemplateDiagnostic("L9", LintSeverity.Error, reportedOn, null,
                    $"spread pair '{pair.Key}' has more than one template on a side."));
            }

            var left = pair.FirstOrDefault(t => t.Pair!.Side == PairSide.Left);
            var right = pair.FirstOrDefault(t => t.Pair!.Side == PairSide.Right);
            if (left is null || right is null) continue;

            var leftSpans = SpanIds(left);
            var rightSpans = SpanIds(right);
            if (!leftSpans.SetEquals(rightSpans))
            {
                issues.Add(new TemplateDiagnostic("L9", LintSeverity.Error, IdOf(right), null,
                    $"spread pair '{pair.Key}' spanId sets differ: left [{string.Join(", ", leftSpans.Order(StringComparer.Ordinal))}] " +
                    $"vs right [{string.Join(", ", rightSpans.Order(StringComparer.Ordinal))}]; " +
                    "a gutter-spanning photo needs exactly one slot on each side."));
            }
        }

        return Order(issues);
    }

    /// <summary>True when any diagnostic is an error — the build gate's predicate.</summary>
    public static bool HasErrors(IEnumerable<TemplateDiagnostic> diagnostics) =>
        diagnostics.Any(d => d.Severity == LintSeverity.Error);

    // ------------------------------------------------------------------ rules

    /// <summary>L12 — all rect values finite, extents positive, schema version known.</summary>
    private static void WellFormedness(Template t, string id, List<TemplateDiagnostic> issues)
    {
        if (t.SchemaVersion != TemplateLibrary.SupportedSchemaVersion)
        {
            issues.Add(Error("L12", id, null,
                $"schemaVersion {t.SchemaVersion} is not the supported version " +
                $"{TemplateLibrary.SupportedSchemaVersion}."));
        }

        if (t.Slots.Count == 0)
        {
            issues.Add(Error("L12", id, null, "has no image slots; a template needs at least one."));
        }

        foreach (var slot in t.Slots)
        {
            if (!slot.Rect.IsWellFormed)
            {
                issues.Add(Error("L12", id, slot.Id, $"rect {slot.Rect} is not well formed (finite, w > 0, h > 0)."));
            }
        }

        foreach (var text in t.TextSlots)
        {
            if (!text.Rect.IsWellFormed)
            {
                issues.Add(Error("L12", id, text.Id, $"rect {text.Rect} is not well formed (finite, w > 0, h > 0)."));
            }
        }
    }

    /// <summary>
    /// L1 — non-bleed slots stay inside the trim box; a bleed slot stays inside the bleed box and
    /// reaches the bleed edge exactly on every side it crosses (no partial-bleed slivers).
    /// </summary>
    private static void SlotsInsideTrim(Template t, string id, List<TemplateDiagnostic> issues)
    {
        foreach (var slot in t.Slots)
        {
            var r = slot.Rect;
            if (!r.IsWellFormed) continue;

            if (!slot.Bleed)
            {
                if (r.X < -Eps || r.Y < -Eps || r.Right > 1 + Eps || r.Bottom > 1 + Eps)
                {
                    issues.Add(Error("L1", id, slot.Id,
                        $"rect {r} leaves the trim box; crossing trim requires \"bleed\": true."));
                }

                continue;
            }

            if (r.X < -BleedX - Eps || r.Y < -BleedY - Eps ||
                r.Right > 1 + BleedX + Eps || r.Bottom > 1 + BleedY + Eps)
            {
                issues.Add(Error("L1", id, slot.Id,
                    $"bleed rect {r} leaves the bleed box (x ∈ [{-BleedX}, {1 + BleedX}], y ∈ [{-BleedY}, {1 + BleedY}])."));
            }

            CheckBleedEdge(r.X < -Eps, r.X, -BleedX, "left");
            CheckBleedEdge(r.Y < -Eps, r.Y, -BleedY, "top");
            CheckBleedEdge(r.Right > 1 + Eps, r.Right, 1 + BleedX, "right");
            CheckBleedEdge(r.Bottom > 1 + Eps, r.Bottom, 1 + BleedY, "bottom");

            void CheckBleedEdge(bool crossesTrim, double actual, double required, string side)
            {
                if (crossesTrim && Math.Abs(actual - required) > BleedEps)
                {
                    issues.Add(Error("L1", id, slot.Id,
                        $"crosses trim on the {side} but stops at {actual:0.####} instead of the bleed edge " +
                        $"{required:0.####} — partial-bleed slivers do not print."));
                }
            }
        }
    }

    /// <summary>L2 — pairwise image-slot intersection is rounding slop at most; v1 has no intentional overlaps.</summary>
    private static void NoSlotOverlaps(Template t, string id, List<TemplateDiagnostic> issues)
    {
        for (var i = 0; i < t.Slots.Count; i++)
        {
            for (var j = i + 1; j < t.Slots.Count; j++)
            {
                var overlap = t.Slots[i].Rect.IntersectionArea(t.Slots[j].Rect);
                if (overlap > MaxSlotOverlapArea)
                {
                    issues.Add(Error("L2", id, t.Slots[i].Id,
                        $"overlaps {t.Slots[j].Id} by {overlap:0.####} of the page " +
                        $"(limit {MaxSlotOverlapArea})."));
                }
            }
        }
    }

    /// <summary>L3 — journal and caption text never sits on a photo; only monthTitle may (R24).</summary>
    private static void TextImageSeparation(Template t, string id, List<TemplateDiagnostic> issues)
    {
        foreach (var text in t.TextSlots.Where(x => x.Role != TextRole.MonthTitle))
        {
            foreach (var slot in t.Slots.Where(s => s.Rect.Intersects(text.Rect)))
            {
                issues.Add(Error("L3", id, text.Id,
                    $"{text.Role.ToString().ToLowerInvariant()} text intersects image slot {slot.Id}; " +
                    "only monthTitle text may overlap a photo."));
            }
        }
    }

    /// <summary>
    /// L4 — every text slot clears the safe margin and the gutter caution zone <b>as authored</b>,
    /// i.e. as a right page with the gutter at <c>x = 0</c>.
    /// <para>
    /// Do not re-run this against a mirrored template: mirroring swaps which bound is the gutter and
    /// which is the outer edge, and the authored bounds already imply the mirrored ones. A rect
    /// inside <c>[0.0455, 0.9659]</c> maps to <c>[0.0341, 0.9545]</c>, which is exactly the outer
    /// safe margin and gutter caution of a left page. Left pages are safe by construction.
    /// </para>
    /// </summary>
    private static void TextSlotsInsideSafeArea(Template t, string id, List<TemplateDiagnostic> issues)
    {
        foreach (var text in t.TextSlots)
        {
            var r = text.Rect;
            if (!r.IsWellFormed) continue;

            if (r.X < GutterCautionX - Eps)
            {
                issues.Add(Error("L4", id, text.Id,
                    $"starts at x = {r.X:0.####}, inside the {GutterCautionX} gutter caution zone."));
            }

            if (r.Right > SafeRight + Eps)
            {
                issues.Add(Error("L4", id, text.Id, $"ends at x = {r.Right:0.####}, past the safe margin {SafeRight}."));
            }

            if (r.Y < SafeTop - Eps)
            {
                issues.Add(Error("L4", id, text.Id, $"starts at y = {r.Y:0.####}, above the safe margin {SafeTop}."));
            }

            if (r.Bottom > SafeBottom + Eps)
            {
                issues.Add(Error("L4", id, text.Id, $"ends at y = {r.Bottom:0.####}, below the safe margin {SafeBottom}."));
            }
        }
    }

    /// <summary>L5 — the declared aspect agrees with the rect, and both aspect and tolerance are sane.</summary>
    private static void AspectSanity(Template t, string id, double pageW, double pageH, List<TemplateDiagnostic> issues)
    {
        foreach (var slot in t.Slots)
        {
            var r = slot.Rect;
            if (!r.IsWellFormed) continue;

            var derived = r.W * pageW / (r.H * pageH);
            if (Math.Abs(slot.Aspect - derived) / derived > 0.03)
            {
                issues.Add(Error("L5", id, slot.Id,
                    $"declares aspect {slot.Aspect:0.####} but the rect derives {derived:0.####} " +
                    "(more than 3% apart)."));
            }

            if (slot.Aspect is < 0.3 or > 3.5)
            {
                issues.Add(Error("L5", id, slot.Id,
                    $"aspect {slot.Aspect:0.####} is outside [0.3, 3.5]; no photo crops to that shape well."));
            }

            if (slot.AspectTolerance is < 0 or > 0.6)
            {
                issues.Add(Error("L5", id, slot.Id,
                    $"aspectTolerance {slot.AspectTolerance:0.####} is outside [0, 0.6]."));
            }
        }
    }

    /// <summary>L6 — photoCount matches the slots and stays within 1..8 (R20).</summary>
    private static void PhotoCount(Template t, string id, List<TemplateDiagnostic> issues)
    {
        if (t.PhotoCount != t.Slots.Count)
        {
            issues.Add(Error("L6", id, null,
                $"photoCount {t.PhotoCount} does not equal the {t.Slots.Count} declared slots."));
        }

        if (t.PhotoCount is < 1 or > MaxPhotoCount)
        {
            issues.Add(Error("L6", id, null, $"photoCount {t.PhotoCount} is outside 1..{MaxPhotoCount} (R20)."));
        }

        if (t.Kind == TemplateKind.MultiDay && t.Sections is { Count: > 0 })
        {
            var sectioned = t.Sections.Sum(s => s.SlotIds.Count);
            if (sectioned != t.PhotoCount)
            {
                issues.Add(Error("L6", id, null,
                    $"photoCount {t.PhotoCount} does not equal the {sectioned} slots summed over sections."));
            }
        }
    }

    /// <summary>L7 — every slot prints at 1.5 in minimum edge, with room for a caption band when it takes one.</summary>
    private static void PrintableSlotSize(Template t, string id, List<TemplateDiagnostic> issues)
    {
        foreach (var slot in t.Slots)
        {
            var r = slot.Rect;
            if (!r.IsWellFormed) continue;

            if (r.W < MinSlotWidth - Eps || r.H < MinSlotHeight - Eps)
            {
                issues.Add(Error("L7", id, slot.Id,
                    $"is {r.W:0.####} × {r.H:0.####}, under the 1.5 in minimum edge " +
                    $"({MinSlotWidth} × {MinSlotHeight})."));
            }

            if (slot.CaptionPolicy == CaptionPolicy.Below && r.H < MinSlotHeight + CaptionBandHeight - Eps)
            {
                issues.Add(Error("L7", id, slot.Id,
                    $"reserves a below-caption band but is only {r.H:0.####} tall; " +
                    $"a captioned slot needs {MinSlotHeight + CaptionBandHeight:0.####}."));
            }
        }
    }

    /// <summary>L8 — ids unique inside the template, and sections/pair present exactly for their kind.</summary>
    private static void StructuralIntegrity(Template t, string id, List<TemplateDiagnostic> issues)
    {
        foreach (var duplicate in t.Slots.Select(s => s.Id)
                     .Concat(t.TextSlots.Select(x => x.Id))
                     .GroupBy(x => x, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            issues.Add(Error("L8", id, duplicate.Key, "id is used more than once in this template."));
        }

        if ((t.Sections is not null) != (t.Kind == TemplateKind.MultiDay))
        {
            issues.Add(Error("L8", id, null,
                t.Sections is null
                    ? "kind is multiDay but no sections are declared."
                    : $"declares sections but kind is {t.Kind}; sections belong to multiDay only."));
        }

        if ((t.Pair is not null) != (t.Kind == TemplateKind.SpreadPair))
        {
            issues.Add(Error("L8", id, null,
                t.Pair is null
                    ? "kind is spreadPair but no pair metadata is declared."
                    : $"declares pair metadata but kind is {t.Kind}; pair belongs to spreadPair only."));
        }

        if (t.Sections is null) return;

        foreach (var duplicate in t.Sections.Select(s => s.Id)
                     .GroupBy(x => x, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            issues.Add(Error("L8", id, duplicate.Key, "section id is used more than once."));
        }

        Partitions("slot", t.Sections.SelectMany(s => s.SlotIds).ToList(), t.Slots.Select(s => s.Id).ToList());
        Partitions("text slot", t.Sections.SelectMany(s => s.TextSlotIds).ToList(), t.TextSlots.Select(x => x.Id).ToList());

        foreach (var section in t.Sections)
        {
            if (section.SlotIds.Count == 0)
            {
                issues.Add(Error("L8", id, section.Id, "section has no image slots; a day needs at least one photo."));
            }

            if (section.TextSlotIds.Count > 1)
            {
                issues.Add(Error("L8", id, section.Id,
                    $"section binds {section.TextSlotIds.Count} text slots; a day's journal takes 0 or 1."));
            }

            foreach (var textSlotId in section.TextSlotIds)
            {
                var text = t.FindTextSlot(textSlotId);
                if (text is not null && text.Role != TextRole.Journal)
                {
                    issues.Add(Error("L8", id, section.Id,
                        $"section binds text slot '{textSlotId}' whose role is {text.Role}; sections take journal text."));
                }
            }
        }

        void Partitions(string what, List<string> assigned, List<string> declared)
        {
            foreach (var missing in declared.Except(assigned, StringComparer.Ordinal))
            {
                issues.Add(Error("L8", id, missing, $"{what} belongs to no section; sections must partition every {what}."));
            }

            foreach (var unknown in assigned.Except(declared, StringComparer.Ordinal))
            {
                issues.Add(Error("L8", id, unknown, $"a section references {what} '{unknown}', which the template does not declare."));
            }

            foreach (var twice in assigned.GroupBy(x => x, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                issues.Add(Error("L8", id, twice.Key, $"{what} belongs to {twice.Count()} sections; it must belong to exactly one."));
            }
        }
    }

    /// <summary>L9 — the half of spread-pair integrity a single template can answer for.</summary>
    private static void SpreadPairShape(Template t, string id, List<TemplateDiagnostic> issues)
    {
        if (t.Kind == TemplateKind.SpreadPair && t.Mirrorable)
        {
            issues.Add(Error("L9", id, null, "spreadPair templates must not be mirrorable; each side is authored explicitly."));
        }

        if (t.Kind != TemplateKind.SpreadPair)
        {
            foreach (var slot in t.Slots.Where(s => s.SpanId is not null))
            {
                issues.Add(Error("L9", id, slot.Id, "declares a spanId, but only spreadPair templates span the gutter."));
            }
        }

        foreach (var twice in t.Slots.Where(s => s.SpanId is not null)
                     .GroupBy(s => s.SpanId!, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            issues.Add(Error("L9", id, twice.Key, $"spanId is used by {twice.Count()} slots on this side; a span takes one slot per side."));
        }
    }

    /// <summary>
    /// L10 — coverage sanity. Negative space is legitimate (R20), so this is a warning, and pages
    /// carrying a bleed slot are exempt: full-bleed coverage is over 1.0 by construction.
    /// </summary>
    private static void CoverageSanity(Template t, string id, List<TemplateDiagnostic> issues)
    {
        if (t.Slots.Any(s => s.Bleed)) return;

        var coverage = t.Slots.Where(s => s.Rect.IsWellFormed).Sum(s => s.Rect.Area);
        if (coverage < MinCoverage || coverage > MaxCoverage)
        {
            issues.Add(new TemplateDiagnostic("L10", LintSeverity.Warning, id, null,
                $"slots cover {coverage:0.###} of the page, outside the sane band " +
                $"[{MinCoverage}, {MaxCoverage}]."));
        }
    }

    /// <summary>L11 — a non-bleed slot sitting exactly on trim may show a hairline at trim variance.</summary>
    private static void TrimTouchingSlots(Template t, string id, List<TemplateDiagnostic> issues)
    {
        foreach (var slot in t.Slots.Where(s => !s.Bleed && s.Rect.IsWellFormed))
        {
            var r = slot.Rect;
            var edges = new List<string>();
            if (Math.Abs(r.X) < Eps) edges.Add("left");
            if (Math.Abs(r.Y) < Eps) edges.Add("top");
            if (Math.Abs(r.Right - 1) < Eps) edges.Add("right");
            if (Math.Abs(r.Bottom - 1) < Eps) edges.Add("bottom");
            if (edges.Count == 0) continue;

            issues.Add(new TemplateDiagnostic("L11", LintSeverity.Warning, id, slot.Id,
                $"sits exactly on the {string.Join(" and ", edges)} trim edge; " +
                "trim variance may show a hairline (the black background hides it)."));
        }
    }

    // ----------------------------------------------------------------- helpers

    private static HashSet<string> SpanIds(Template t) =>
        t.Slots.Where(s => s.SpanId is not null).Select(s => s.SpanId!).ToHashSet(StringComparer.Ordinal);

    private static string IdOf(Template t) => t.Id ?? t.BasedOn ?? "<detached>";

    private static TemplateDiagnostic Error(string rule, string id, string? target, string message) =>
        new(rule, LintSeverity.Error, id, target, message);

    private static IReadOnlyList<TemplateDiagnostic> Order(List<TemplateDiagnostic> issues) =>
        issues
            .OrderByDescending(d => d.Severity)
            .ThenBy(d => d.TemplateId, StringComparer.Ordinal)
            .ThenBy(d => d.Rule, StringComparer.Ordinal)
            .ThenBy(d => d.TargetId ?? string.Empty, StringComparer.Ordinal)
            .ToList();
}
