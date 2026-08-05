using System.Reflection;
using PhotoBook.Core.Model;
using PhotoBook.Core.Templates;

namespace PhotoBook.Tests;

/// <summary>
/// The shipped v1 template library (doc 07) and its linter, run as the build gate doc 13 asks for:
/// every embedded template parses, ids are unique and deterministically ordered, the linter reports
/// no errors library-wide, and mirroring is an involution.
/// </summary>
public class TemplateLibraryTests
{
    private static readonly TemplateLibrary Library = TemplateLibrary.Default;

    [Fact]
    public void EveryEmbeddedTemplateResourceLoads()
    {
        var resources = typeof(Template).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(TemplateLibrary.ResourcePrefix, StringComparison.Ordinal) &&
                        n.EndsWith(".json", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(resources);
        Assert.Equal(resources.Count, Library.Count);
        Assert.True(Library.Count >= 40, $"the v1 library targets ~50 templates; only {Library.Count} loaded");

        foreach (var resource in resources)
        {
            var id = resource[TemplateLibrary.ResourcePrefix.Length..^".json".Length];
            Assert.True(Library.Contains(id), $"embedded resource '{resource}' produced no template with id '{id}'");
        }

        Assert.All(Library.Templates, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Id));
            Assert.False(string.IsNullOrWhiteSpace(t.Name));
            Assert.Equal(1, t.SchemaVersion);
            Assert.Equal(PageGeometry.DefaultPageSizeId, t.PageSize);
            Assert.NotEmpty(t.Slots);
        });
    }

    [Fact]
    public void IdsAreUniqueAndOrderedDeterministically()
    {
        Assert.Equal(Library.Ids.Count, Library.Ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Library.Ids.Order(StringComparer.Ordinal), Library.Ids);

        // Loading again from the same assembly must yield the identical sequence (kernel §7).
        Assert.Equal(Library.Ids, TemplateLibrary.Load(typeof(Template).Assembly).Ids);
    }

    [Fact]
    public void TheLinterReportsNoErrorsAcrossTheShippedLibrary()
    {
        var diagnostics = Library.Lint();
        var errors = diagnostics.Where(d => d.Severity == LintSeverity.Error).ToList();

        Assert.True(errors.Count == 0,
            "the shipped library must lint clean:" + Environment.NewLine +
            string.Join(Environment.NewLine, errors));
        Assert.False(TemplateLinter.HasErrors(diagnostics));
    }

    [Fact]
    public void EveryTemplateLintsCleanOnItsOwn()
    {
        foreach (var template in Library.Templates)
        {
            var errors = TemplateLinter.Lint(template).Where(d => d.Severity == LintSeverity.Error).ToList();
            Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        }
    }

    [Fact]
    public void MirroringIsAnInvolution()
    {
        foreach (var template in Library.Templates)
        {
            var twice = TemplateLibrary.Mirror(TemplateLibrary.Mirror(template));

            Assert.Equal(template.Slots.Count, twice.Slots.Count);
            for (var i = 0; i < template.Slots.Count; i++)
            {
                AssertRectEqual(template.Slots[i].Rect, twice.Slots[i].Rect, template.Id!, template.Slots[i].Id);
                Assert.Equal(template.Slots[i].Id, twice.Slots[i].Id);
                Assert.Equal(template.Slots[i].TierAffinity, twice.Slots[i].TierAffinity);
                Assert.Equal(template.Slots[i].CaptionPolicy, twice.Slots[i].CaptionPolicy);
                Assert.Equal(template.Slots[i].Aspect, twice.Slots[i].Aspect);
            }

            Assert.Equal(template.TextSlots.Count, twice.TextSlots.Count);
            for (var i = 0; i < template.TextSlots.Count; i++)
            {
                AssertRectEqual(template.TextSlots[i].Rect, twice.TextSlots[i].Rect, template.Id!, template.TextSlots[i].Id);
                Assert.Equal(template.TextSlots[i].Align, twice.TextSlots[i].Align); // alignment never flips
                Assert.Equal(template.TextSlots[i].Role, twice.TextSlots[i].Role);
            }
        }
    }

    [Fact]
    public void MirroringLeavesTheLibraryTemplateUntouched()
    {
        var template = Library.Templates.First(t => t.Mirrorable);
        var before = template.Slots.Select(s => s.Rect).ToList();

        var mirrored = TemplateLibrary.Mirror(template);

        Assert.NotSame(template, mirrored);
        Assert.Equal(before, template.Slots.Select(s => s.Rect));
        Assert.Equal(1 - before[0].X - before[0].W, mirrored.Slots[0].Rect.X, 1e-12);
    }

    [Fact]
    public void ForPage_MirrorsLeftPagesOnlyWhenMirrorable()
    {
        var actuallyFlipped = 0;
        foreach (var template in Library.Templates)
        {
            var right = TemplateLibrary.ForPage(template, leftPage: false);
            var left = TemplateLibrary.ForPage(template, leftPage: true);

            Assert.Equal(template.Slots.Select(s => s.Rect), right.Slots.Select(s => s.Rect));

            var expected = template.Mirrorable
                ? template.Slots.Select(s => s.Rect.Mirrored())
                : template.Slots.Select(s => s.Rect);
            Assert.Equal(expected, left.Slots.Select(s => s.Rect));

            if (!left.Slots.Select(s => s.Rect).SequenceEqual(right.Slots.Select(s => s.Rect))) actuallyFlipped++;
        }

        Assert.True(actuallyFlipped > 0, "no template's geometry actually changed on a left page");
    }

    [Fact]
    public void MirroredTextSlotsStayInsideTheLeftPageSafeArea()
    {
        // Doc 07: authored bounds [0.0455, 0.9659] map to [0.0341, 0.9545], exactly the left page's
        // outer safe margin and gutter caution. Verify rather than assume.
        const double outerSafeLeft = 0.0341;
        const double gutterCautionRight = 1 - TemplateLinter.GutterCautionX;

        var mirrorableWithText = Library.Templates.Where(t => t.Mirrorable && t.TextSlots.Count > 0).ToList();
        Assert.NotEmpty(mirrorableWithText);

        foreach (var template in mirrorableWithText)
        {
            foreach (var text in TemplateLibrary.Mirror(template).TextSlots)
            {
                Assert.True(text.Rect.X >= outerSafeLeft - 1e-6,
                    $"{template.Id}/{text.Id} mirrors to x = {text.Rect.X:0.####}, outside the left page safe margin");
                Assert.True(text.Rect.Right <= gutterCautionRight + 1e-6,
                    $"{template.Id}/{text.Id} mirrors to right = {text.Rect.Right:0.####}, into the left page gutter caution zone");
            }
        }
    }

    [Fact]
    public void TheLibraryCoversTheShapesTheEngineNeeds()
    {
        // Doc 13 "Library-level checks".
        for (var count = 1; count <= 8; count++)
        {
            Assert.True(Library.ByPhotoCount(count).Count > 0, $"no template holds {count} photos (R20)");
        }

        Assert.True(Library.ByKind(TemplateKind.MonthTitle).Count >= 4, "R24 wants at least 4 month-title templates");
        Assert.True(Library.ByKind(TemplateKind.MultiDay).Count >= 5, "R28 wants at least 5 multi-day templates");
        Assert.True(Library.ByKind(TemplateKind.FullBleed).Count >= 1, "R18 wants at least one full-bleed template");

        for (var count = 1; count <= 4; count++)
        {
            Assert.True(Library.ByPhotoCount(count).Any(t => t.TextSlots.Count == 0),
                $"no textless {count}-photo template — the negative-space option (R20)");
        }

        // Roughly half the library offers a journal slot (doc 07 "The v1 template library").
        var withJournal = Library.Templates.Count(t => t.HasJournalSlot);
        Assert.InRange(withJournal, Library.Count / 4, Library.Count * 3 / 4);
    }

    [Fact]
    public void SpreadPairsAreCompleteAndNeverMirrorable()
    {
        var pairs = Library.ByKind(TemplateKind.SpreadPair).GroupBy(t => t.Pair!.PairId, StringComparer.Ordinal).ToList();

        foreach (var pair in pairs)
        {
            Assert.Equal(2, pair.Count());
            Assert.Contains(pair, t => t.Pair!.Side == PairSide.Left);
            Assert.Contains(pair, t => t.Pair!.Side == PairSide.Right);
            Assert.All(pair, t => Assert.False(t.Mirrorable));

            var left = pair.Single(t => t.Pair!.Side == PairSide.Left);
            var right = pair.Single(t => t.Pair!.Side == PairSide.Right);
            Assert.Equal(SpanIds(left), SpanIds(right));
            Assert.Equal(2, Library.ByPairId(pair.Key).Count);
        }
    }

    [Fact]
    public void MultiDaySectionsPartitionTheirSlots()
    {
        foreach (var template in Library.ByKind(TemplateKind.MultiDay))
        {
            Assert.NotNull(template.Sections);
            Assert.True(template.Sections!.Count >= 2, $"{template.Id} is multiDay but has {template.Sections.Count} section(s)");
            Assert.Equal(
                template.Slots.Select(s => s.Id).Order(StringComparer.Ordinal),
                template.Sections.SelectMany(s => s.SlotIds).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void UnknownFieldsAndWrongSchemaVersionsAreRejected()
    {
        const string valid =
            """
            {
              "schemaVersion": 1, "id": "t-x", "name": "x", "pageSize": "11x8.5-landscape",
              "kind": "standard", "photoCount": 1, "mirrorable": true,
              "slots": [ { "id": "s1", "rect": { "x": 0, "y": 0, "w": 0.5, "h": 0.5 } } ], "textSlots": []
            }
            """;

        Assert.Equal("t-x", TemplateLibrary.Parse(valid, "t-x.json").Id);

        Assert.Throws<TemplateLoadException>(() => TemplateLibrary.Parse(
            valid.Replace("\"textSlots\": []", "\"textSlots\": [], \"unexpectedField\": 3"), "t-x.json"));

        Assert.Throws<TemplateLoadException>(() => TemplateLibrary.Parse(
            valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"), "t-x.json"));

        Assert.Throws<TemplateLoadException>(() => TemplateLibrary.Parse("{ not json", "t-x.json"));
    }

    [Fact]
    public void DetachingAPageNeverMutatesTheLibraryTemplate()
    {
        var template = Library["t-04-text-a"];
        var before = template.Slots[0].Rect;
        var snapshot = template.ToDetachedSnapshot();

        snapshot.Slots[0].Rect = new Rect(0.2, 0.2, 0.2, 0.2);

        Assert.Null(snapshot.Id);
        Assert.Equal("t-04-text-a", snapshot.BasedOn);
        Assert.True(snapshot.IsDetachedSnapshot);
        Assert.Equal(before, Library["t-04-text-a"].Slots[0].Rect);
        Assert.NotEqual(before, snapshot.Slots[0].Rect);
    }

    // ---- fullness and overlap: the library's shape, guarded so it cannot drift back -----------------

    [Fact]
    public void TheLibraryIsAsFullAsDoc07Says()
    {
        // The user's verdict on the first real month was "lots of white/black space, it should be more
        // full". The library answered by going from a mean coverage of 0.56 to ~0.79; these bounds are
        // what stops a later edit from quietly sparsening it again. Coverage is the union of the slot
        // rects clipped to trim, so overlapping layouts cannot inflate the number (doc 07 L10).
        var coverage = Library.Templates.ToDictionary(t => t.Id!, TemplateLinter.PageCoverage, StringComparer.Ordinal);
        var mean = coverage.Values.Average();

        Assert.InRange(mean, 0.76, 0.86);

        var standard = Library.Templates.Where(t => t.Kind == TemplateKind.Standard).ToList();
        var inBand = standard.Count(t => coverage[t.Id!] is >= 0.75 and <= 0.92);
        Assert.True(inBand >= standard.Count * 2 / 3,
            $"only {inBand} of {standard.Count} standard templates sit in the 0.75–0.92 fullness band");

        // …but not every page is a full grid: R20 wants most of the space, not always. A handful of
        // deliberately airy layouts stay, and the engine rations them (doc 08 S_coverage).
        var airy = standard.Where(t => coverage[t.Id!] < 0.72).ToList();
        Assert.InRange(airy.Count, 4, 10);
        Assert.Contains(standard, t => coverage[t.Id!] < 0.55);

        Assert.All(Library.Templates, t => Assert.InRange(TemplateLinter.PageCoverage(t), 0.0, 1.0 + 1e-9));
    }

    [Fact]
    public void EverySinglePhotoStandardTemplateIsAHero()
    {
        // "When there is only a single picture for a page, it should be large." A lone photo may never
        // render as a stamp in the middle of a black page again.
        var singles = Library.Templates
            .Where(t => t.Kind == TemplateKind.Standard && t.PhotoCount == 1)
            .ToList();

        Assert.NotEmpty(singles);
        Assert.All(singles, t => Assert.True(
            TemplateLinter.PageCoverage(t) >= 0.72,
            $"{t.Id} gives a lone photo only {TemplateLinter.PageCoverage(t):P0} of the page"));
    }

    [Fact]
    public void OverlapIsADesignLanguageAcrossTheLibrary()
    {
        var declaring = Library.Templates.Where(t => t.Overlaps).ToList();
        Assert.InRange(declaring.Count, 12, 26);

        // Both flavours ship: photo over photo, and text on a scrim over a photo.
        Assert.True(declaring.Count(HasSlotOverlap) >= 6, "too few templates overlap one photo with another");
        Assert.True(declaring.Count(t => t.TextSlots.Any(x => x.Scrim)) >= 8, "too few templates set text on a photo");

        foreach (var template in Library.Templates)
        {
            var overlapping = HasSlotOverlap(template);
            var textOnPhoto = template.TextSlots.Any(x => template.Slots.Any(s => s.Rect.Intersects(x.Rect)));

            if (overlapping || textOnPhoto)
            {
                Assert.True(template.Overlaps, $"{template.Id} overlaps but never declared it");
            }

            // Every overlapping pair has a defined z-order, and every text block on a photo has a scrim.
            foreach (var (a, b) in Pairs(template).Where(p => p.A.Rect.IntersectionArea(p.B.Rect) > TemplateLinter.MaxSlotOverlapArea))
            {
                Assert.True(a.Layer != b.Layer, $"{template.Id}: {a.Id} and {b.Id} overlap on layer {a.Layer}");
            }

            foreach (var text in template.TextSlots.Where(x => template.Slots.Any(s => s.Rect.Intersects(x.Rect))))
            {
                Assert.True(text.Scrim, $"{template.Id}/{text.Id} sits on a photo with no scrim");
            }
        }
    }

    [Fact]
    public void PaintOrderFollowsLayerAndThenAuthoredOrder()
    {
        foreach (var template in Library.Templates)
        {
            var painted = template.SlotsInPaintOrder.ToList();

            Assert.Equal(template.Slots.Count, painted.Count);
            Assert.Equal(painted.Select(s => s.Layer).Order(), painted.Select(s => s.Layer));

            // Stable within a layer: ties keep reading order, which is what slot assignment follows.
            foreach (var layer in painted.Select(s => s.Layer).Distinct())
            {
                Assert.Equal(
                    template.Slots.Where(s => s.Layer == layer).Select(s => s.Id),
                    painted.Where(s => s.Layer == layer).Select(s => s.Id));
            }
        }
    }

    [Fact]
    public void TheLinterStillCatchesAccidentalOverlap()
    {
        // L2: the same geometry is an error undeclared, an error unlayered, and clean when it is both
        // declared and layered — that distinction is the whole point of the rule.
        var accident = Overlapping(declares: false, topLayer: 1);
        Assert.Contains(TemplateLinter.Lint(accident), d => d.Rule == "L2" && d.Severity == LintSeverity.Error);

        var flat = Overlapping(declares: true, topLayer: 0);
        Assert.Contains(TemplateLinter.Lint(flat), d => d.Rule == "L2" && d.Severity == LintSeverity.Error);

        var deliberate = Overlapping(declares: true, topLayer: 1);
        Assert.DoesNotContain(TemplateLinter.Lint(deliberate), d => d.Severity == LintSeverity.Error);

        // …and a slot buried under another is still a mistake, however well declared.
        var buried = Overlapping(declares: true, topLayer: 1);
        buried.Slots[1].Rect = new Rect(0.05, 0.05, 0.60, 0.60);
        buried.Slots[1].Aspect = 1.294;
        Assert.Contains(TemplateLinter.Lint(buried), d => d.Rule == "L2" && d.Severity == LintSeverity.Error);
    }

    [Fact]
    public void TheLinterAllowsScrimmedTextOnAPhotoAndRejectsBareText()
    {
        var bare = TextOverPhoto(declares: true, scrim: false);
        Assert.Contains(TemplateLinter.Lint(bare), d => d.Rule == "L3" && d.Severity == LintSeverity.Error);

        var undeclared = TextOverPhoto(declares: false, scrim: true);
        Assert.Contains(TemplateLinter.Lint(undeclared), d => d.Rule == "L3" && d.Severity == LintSeverity.Error);

        var scrimmed = TextOverPhoto(declares: true, scrim: true);
        Assert.DoesNotContain(TemplateLinter.Lint(scrimmed), d => d.Severity == LintSeverity.Error);

        // Half on the photo, half on the page: the scrim would spill onto the background.
        var hanging = TextOverPhoto(declares: true, scrim: true);
        hanging.Slots[0].Rect = new Rect(0.02, 0.02, 0.60, 0.94);
        hanging.Slots[0].Aspect = 0.826;
        Assert.Contains(TemplateLinter.Lint(hanging), d => d.Rule == "L3" && d.Severity == LintSeverity.Error);

        // A block covering the photo is a text box with wallpaper behind it.
        var smothering = TextOverPhoto(declares: true, scrim: true);
        smothering.TextSlots[0].Rect = new Rect(0.08, 0.06, 0.85, 0.72);
        Assert.Contains(TemplateLinter.Lint(smothering), d => d.Rule == "L3" && d.Severity == LintSeverity.Error);
    }

    private static Template Overlapping(bool declares, int topLayer) => new()
    {
        Id = "t-test", Name = "test", Kind = TemplateKind.Standard, PhotoCount = 2,
        Mirrorable = false, Overlaps = declares,
        Slots =
        [
            new ImageSlot { Id = "s1", Rect = new Rect(0.02, 0.02, 0.70, 0.90), Aspect = 1.0068, TierAffinity = TierAffinity.S },
            new ImageSlot { Id = "s2", Rect = new Rect(0.62, 0.55, 0.34, 0.40), Aspect = 1.1, TierAffinity = TierAffinity.B, Layer = topLayer },
        ],
        TextSlots = [],
    };

    private static Template TextOverPhoto(bool declares, bool scrim) => new()
    {
        Id = "t-test", Name = "test", Kind = TemplateKind.Standard, PhotoCount = 1,
        Mirrorable = false, Overlaps = declares,
        Slots = [new ImageSlot { Id = "s1", Rect = new Rect(0.02, 0.02, 0.94, 0.94), Aspect = 1.294, TierAffinity = TierAffinity.S }],
        TextSlots = [new TextSlot { Id = "t1", Rect = new Rect(0.55, 0.60, 0.34, 0.25), Role = TextRole.Journal, Scrim = scrim }],
    };

    private static bool HasSlotOverlap(Template t) =>
        Pairs(t).Any(p => p.A.Rect.IntersectionArea(p.B.Rect) > TemplateLinter.MaxSlotOverlapArea);

    private static IEnumerable<(ImageSlot A, ImageSlot B)> Pairs(Template t)
    {
        for (var i = 0; i < t.Slots.Count; i++)
        {
            for (var j = i + 1; j < t.Slots.Count; j++) yield return (t.Slots[i], t.Slots[j]);
        }
    }

    private static IEnumerable<string> SpanIds(Template t) =>
        t.Slots.Where(s => s.SpanId is not null).Select(s => s.SpanId!).Order(StringComparer.Ordinal);

    private static void AssertRectEqual(Rect expected, Rect actual, string templateId, string slotId)
    {
        // Mirroring twice is 1 − (1 − x − w) − w, which drifts by a few ulps; compare with tolerance.
        Assert.True(Math.Abs(expected.X - actual.X) < 1e-12 && Math.Abs(expected.Y - actual.Y) < 1e-12 &&
                    Math.Abs(expected.W - actual.W) < 1e-12 && Math.Abs(expected.H - actual.H) < 1e-12,
            $"{templateId}/{slotId}: expected {expected} but double-mirroring gave {actual}");
    }
}
