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
        var snapshot = template.ToDetachedSnapshot();

        snapshot.Slots[0].Rect = new Rect(0.2, 0.2, 0.2, 0.2);

        Assert.Null(snapshot.Id);
        Assert.Equal("t-04-text-a", snapshot.BasedOn);
        Assert.True(snapshot.IsDetachedSnapshot);
        Assert.Equal(new Rect(0.0, 0.0, 0.5, 0.66), Library["t-04-text-a"].Slots[0].Rect);
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
