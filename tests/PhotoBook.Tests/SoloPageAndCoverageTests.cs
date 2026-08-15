using PhotoBook.Core.Model;
using PhotoBook.Core.Templates;
using PhotoBook.Engine;

namespace PhotoBook.Tests;

/// <summary>
/// The two rules doc 08 gained after the first real month was laid out and looked at (§4b and
/// <c>S_coverage</c> in §6), stated as the user stated them:
/// <list type="number">
/// <item><description>"When there is only a single picture for a page, it should be large. However,
/// if the picture is lame include it on another page."</description></item>
/// <item><description>"The page designs … have lots of white/black space. It should be more
/// full."</description></item>
/// </list>
/// <para>
/// Everything here runs on hand-built <see cref="Photo"/> records with
/// <see cref="Photo.UserTierOverride"/> set — the tier the engine must obey absolutely (kernel §4,
/// R26) — so the assertions are about the engine's choices and nothing else. No pixels are involved.
/// </para>
/// </summary>
public sealed class SoloPageAndCoverageTests
{
    private const int Year = 2024;
    private const int Month = 9;
    private const ulong Seed = 0xB0A7C0DEUL;

    // ---- §4b: a lame photo does not get a page to itself -------------------------------------------

    [Fact]
    public void ALoneBottomTierPhotoOnABusyRunSharesAPageInsteadOfGettingOne()
    {
        // Two busy days with a single C-tier straggler between them. R28's merge caps rule out a
        // multiDay page (its neighbours have five photos each), so before §4b the DP had no choice but
        // to hand that one weak frame a page of its own.
        var photos = new List<Photo>();
        photos.AddRange(PhotosOn(new DateOnly(Year, Month, 5), 5, Tier.B, offset: 0));
        photos.AddRange(PhotosOn(new DateOnly(Year, Month, 6), 1, Tier.C, offset: 10));
        photos.AddRange(PhotosOn(new DateOnly(Year, Month, 7), 5, Tier.B, offset: 20));

        var book = Layout(photos, []);

        Assert.Empty(book.UnplacedPhotoIds);

        var straggler = photos.Single(p => p.EffectiveTier == Tier.C);
        var host = Assert.Single(book.Pages, p => p.Placements.Any(x => x.PhotoId == straggler.Id));

        Assert.True(
            host.Placements.Count > 1,
            "the C-tier straggler was left alone on a page — doc 08 §4b says it must ride along instead");

        // …and the fix is the absorb transition, not a page that quietly lost the photo.
        Assert.Contains(book.Diagnostics, d => d.Kind == LayoutDiagnosticKind.StragglerAbsorbed);
    }

    [Fact]
    public void NoPageInABottomTierMonthEverHoldsASinglePhoto()
    {
        // A whole month of C-tier days of one and two photos: every page must hold company.
        var photos = new List<Photo>();
        for (var day = 2; day <= 26; day += 2)
        {
            var count = day % 4 == 0 ? 2 : 1;
            photos.AddRange(PhotosOn(new DateOnly(Year, Month, day), count, Tier.C, offset: day * 10));
        }

        var book = Layout(photos, []);

        Assert.Empty(book.UnplacedPhotoIds);
        Assert.All(book.Pages, page => Assert.True(
            page.Placements.Count != 1,
            $"page {page.Id} strands one C-tier photo on its own"));
    }

    [Fact]
    public void ADayWhoseOnlyPhotoIsMidTierStillEarnsItsOwnPage()
    {
        // The other half of §4b: a day that genuinely has one photo, and it is not bottom-tier, keeps
        // its page. The rule removes stragglers, not single-photo pages.
        var photos = new List<Photo>();
        photos.AddRange(PhotosOn(new DateOnly(Year, Month, 5), 5, Tier.B, offset: 0));
        photos.AddRange(PhotosOn(new DateOnly(Year, Month, 6), 1, Tier.B, offset: 10));
        photos.AddRange(PhotosOn(new DateOnly(Year, Month, 7), 5, Tier.B, offset: 20));

        var book = Layout(photos, []);

        var lone = photos.Single(p => p.OriginalFileName.Contains("-010.", StringComparison.Ordinal));
        var page = Assert.Single(book.Pages, p => p.Placements.Any(x => x.PhotoId == lone.Id));
        Assert.Single(page.Placements);
    }

    [Fact]
    public void ABurstDayIsNeverCutSoThatOneWeakFrameEndsUpAlone()
    {
        // The same rule inside a single day: the cut may not shed one unearned photo onto its own
        // page just because there is a big time gap in front of it.
        var date = new DateOnly(Year, Month, 11);
        var photos = new List<Photo>(PhotosOn(date, 9, Tier.B, offset: 0));

        // Plant a four-hour gap before the last frame — by far the day's largest.
        photos[8] = photos[8] with { TakenAt = photos[8].TakenAt.AddHours(4) };

        var book = Layout(photos, []);

        Assert.Empty(book.UnplacedPhotoIds);
        Assert.All(book.Pages, page => Assert.True(
            page.Placements.Count != 1,
            $"the day's cut stranded one photo on page {page.Id}"));
    }

    [Fact]
    public void TheSoloRuleIsExactlyWhatTheDocSays()
    {
        var weights = LayoutWeights.Default;
        var s = PhotosOn(new DateOnly(Year, Month, 1), 1, Tier.S)[0];
        var a = PhotosOn(new DateOnly(Year, Month, 2), 1, Tier.A)[0];
        var b = PhotosOn(new DateOnly(Year, Month, 3), 1, Tier.B)[0];
        var c = PhotosOn(new DateOnly(Year, Month, 4), 1, Tier.C)[0];

        // S and A earn a page to themselves however many siblings the day has.
        Assert.True(DemandModel.EarnsSoloPage(s, 12, weights));
        Assert.True(DemandModel.EarnsSoloPage(a, 12, weights));

        // B only when it genuinely is the day's only photo; C never.
        Assert.True(DemandModel.EarnsSoloPage(b, 1, weights));
        Assert.False(DemandModel.EarnsSoloPage(b, 2, weights));
        Assert.False(DemandModel.EarnsSoloPage(c, 1, weights));

        // The cost is charged only for the single-photo pages a cut cannot avoid.
        Assert.Equal(0, DemandModel.ForcedSoloPages(6, 1));
        Assert.Equal(0, DemandModel.ForcedSoloPages(6, 3));
        Assert.Equal(1, DemandModel.ForcedSoloPages(1, 1));
        Assert.Equal(2, DemandModel.ForcedSoloPages(2, 2));

        Assert.Equal(0.0, DemandModel.SoloPageCost([c], 1, weights with { SoloPageCost = 0 }), 9);
        Assert.True(DemandModel.SoloPageCost([c], 1, weights) > 0);
        Assert.Equal(0.0, DemandModel.SoloPageCost([s], 1, weights), 9);
    }

    // ---- §6: a solo photo renders large, and pages are fuller by default ---------------------------

    [Fact]
    public void ASoloHeroPhotoLandsOnANearFullPageTemplate()
    {
        var photos = new List<Photo>();
        photos.AddRange(PhotosOn(new DateOnly(Year, Month, 5), 4, Tier.B, offset: 0));
        photos.AddRange(PhotosOn(new DateOnly(Year, Month, 12), 1, Tier.S, offset: 10));
        photos.AddRange(PhotosOn(new DateOnly(Year, Month, 20), 4, Tier.B, offset: 20));

        var book = Layout(photos, []);

        var hero = photos.Single(p => p.EffectiveTier == Tier.S);
        var page = Assert.Single(book.Pages, p => p.Placements.Any(x => x.PhotoId == hero.Id));
        Assert.Single(page.Placements);

        // Stated against the library rather than as a bare number, so re-authoring templates cannot
        // silently invalidate it: the hero must land within 10 points of the fullest composition the
        // library offers for one photo, and cover most of the page outright.
        var best = TemplateLibrary.Default.Templates
            .Where(t => t.PhotoCount == 1 && t.Kind is TemplateKind.Standard or TemplateKind.FullBleed)
            .Max(TemplateCatalog.SlotCoverage);

        var coverage = TemplateCatalog.SlotCoverage(TemplateOf(page));
        Assert.True(
            coverage >= 0.75 && coverage >= best - 0.10,
            $"the hero got a page to itself and then rendered small — its template covers {coverage:P0} " +
            $"where the library offers {best:P0}");
    }

    [Fact]
    public void TheCoverageTermPrefersTheFullerOfTwoTemplates()
    {
        var weights = LayoutWeights.Default;
        var plan = SoloPlan();

        var full = TemplateLibrary.Default.Templates
            .Where(t => t.PhotoCount == 1 && t.Kind == TemplateKind.Standard)
            .OrderByDescending(TemplateCatalog.SlotCoverage)
            .First();
        var sparse = TemplateLibrary.Default.Templates
            .Where(t => t.PhotoCount == 1 && t.Kind == TemplateKind.Standard)
            .OrderBy(TemplateCatalog.SlotCoverage)
            .First();

        Assert.True(
            PacingMemory.Coverage(full, plan, weights) > PacingMemory.Coverage(sparse, plan, weights),
            "S_coverage must be monotone in slot coverage — that is the whole reason it exists");

        // And it is measured as a union, so overlapping slots (doc 07) cannot inflate it past the page.
        Assert.All(
            TemplateLibrary.Default.Templates,
            t => Assert.InRange(TemplateCatalog.SlotCoverage(t), 0.0, 1.0 + 1e-9));
    }

    [Fact]
    public void AnAiryTemplateIsRationedRatherThanBanned()
    {
        var weights = LayoutWeights.Default;
        var airy = TemplateLibrary.Default.Templates
            .Where(t => t.Kind == TemplateKind.Standard)
            .OrderBy(TemplateCatalog.SlotCoverage)
            .First();
        Assert.True(TemplateCatalog.SlotCoverage(airy) < weights.AiryCoverageMax, "pick a genuinely airy template");

        var plan = SoloPlan();

        // Cold memory: its turn has come round.
        var fresh = new PacingMemory();
        Assert.Equal(weights.PacingAiryInRhythm, fresh.Pacing(airy, plan, weights), 9);

        // Straight after another airy page it is out of rhythm — negative space, not a habit (R20).
        var used = new PacingMemory();
        used.Record(airy.Id, airy.Kind, TemplateCatalog.SlotCoverage(airy));
        Assert.Equal(weights.PacingAiryOutOfRhythm, used.Pacing(airy, plan, weights), 9);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static PagePlan SoloPlan() => new()
    {
        Kind = PagePlanKind.Standard,
        Photos = PhotosOn(new DateOnly(Year, Month, 12), 1, Tier.S),
        Side = PageSide.Right,
        PrimaryDate = new DateOnly(Year, Month, 12),
        StableKey = "solo",
        Demand = 0.35,
    };

    private static LayoutResult Layout(IReadOnlyList<Photo> photos, IReadOnlyList<JournalEntry> entries) =>
        LayoutEngine.LayoutChapter(new LayoutRequest
        {
            Chapter = new ChapterInput
            {
                Year = Year,
                Month = Month,
                Photos = photos,
                JournalEntries = entries,
            },
            Seed = Seed,
            // R24's title page would borrow a photo out of the day pool and blur exactly the page
            // shapes these tests are about.
            GenerateMonthTitlePage = false,
        });

    private static Template TemplateOf(Page page)
    {
        var template = page.ResolveTemplate(id => TemplateLibrary.Default.Find(id));
        Assert.NotNull(template);
        return template!;
    }

    private static List<Photo> PhotosOn(DateOnly date, int count, Tier tier, int offset = 0)
    {
        var photos = new List<Photo>(count);
        for (var i = 0; i < count; i++)
        {
            var index = offset + i;
            var hash = $"{date:yyyyMMdd}{index:D4}".PadRight(64, 'b');
            photos.Add(new Photo
            {
                Id = Ids.PhotoId(hash),
                ContentHash = hash,
                OriginalFileName = $"{date:yyyyMMdd}-{index:D3}.jpg",
                OriginalPath = $"originals/{date:yyyyMMdd}-{index:D3}.jpg",
                TakenAt = date.ToDateTime(new TimeOnly(9, 0)).AddMinutes(index * 13),
                DateSource = DateSource.Exif,
                Width = index % 3 == 1 ? 1200 : 1600,
                Height = index % 3 == 1 ? 1600 : 1200,
                UserTierOverride = tier,
                FocusRegions =
                [
                    new FocusRegion
                    {
                        Rect = new Rect(0.30 + 0.01 * (index % 5), 0.28, 0.30, 0.32),
                        Weight = 0.8,
                        Kind = FocusKind.Saliency,
                    },
                ],
            });
        }

        return photos;
    }
}
