using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;
using PhotoBook.Core.Templates;
using PhotoBook.Engine;
using PhotoBook.Tests.Fixtures;

namespace PhotoBook.Tests;

/// <summary>
/// The Phase 3 seam test for the engine: one realistic month — 61 real photographs analyzed by the
/// shipped analyzer, a real parsed journal, sparse days, a 30-photo birthday, a journal-only day and
/// two excluded frames — laid out by <see cref="LayoutEngine"/>, and then held to the properties
/// doc 08 §9, doc 13 and the requirements list actually promise.
/// <para>
/// Nothing here asserts a golden page-by-page layout: that would break on every legitimate tuning
/// change. It asserts the invariants that must never break — determinism, conservation of photos,
/// R16 pinning, R17 exclusion, R25 focus retention, R26 tier→slot-size — plus the two "does it look
/// good" properties the north star (R27) is about: the book does not repeat one template, and the
/// month's best photographs are not buried in its smallest slots.
/// </para>
/// </summary>
[Collection(SyntheticMonthCollection.Name)]
public sealed class Phase3LayoutTests
{
    private readonly SyntheticMonth _month;

    /// <summary>Receives the shared month.</summary>
    public Phase3LayoutTests(SyntheticMonth month) => _month = month;

    // ---- determinism (doc 08 §9, doc 13) -----------------------------------------------------------

    [Fact]
    public void LayingOutTheSameChapterTwiceProducesTheIdenticalBook()
    {
        var first = LayoutEngine.LayoutChapter(Request());
        var second = LayoutEngine.LayoutChapter(Request());

        Assert.Equal(Serialize(first.Pages), Serialize(second.Pages));
        Assert.Equal(first.UnplacedPhotoIds, second.UnplacedPhotoIds);
        Assert.Equal(first.GeneratedPageCount, second.GeneratedPageCount);
        Assert.Equal(
            first.Diagnostics.Select(d => (d.Kind, d.Severity, d.Message, d.PageNumber, d.PhotoId, d.SlotId, d.Date)),
            second.Diagnostics.Select(d => (d.Kind, d.Severity, d.Message, d.PageNumber, d.PhotoId, d.SlotId, d.Date)));

        // Page ids are content-derived, not clock-derived, or nothing above would hold.
        Assert.Equal(first.Pages.Select(p => p.Id), second.Pages.Select(p => p.Id));
    }

    [Fact]
    public void ShufflingTheInputPhotoListChangesNothing()
    {
        var straight = LayoutEngine.LayoutChapter(Request());

        // A deterministic, content-derived permutation — the engine imposes its own total order, so
        // array position must never reach the output (doc 08 §9).
        var shuffled = _month.Catalog.Photos
            .OrderBy(p => p.ContentHash[^3..], StringComparer.Ordinal)
            .ThenByDescending(p => p.Id, StringComparer.Ordinal)
            .ToList();
        var reordered = LayoutEngine.LayoutChapter(Request(photos: shuffled));

        Assert.Equal(Serialize(straight.Pages), Serialize(reordered.Pages));
        Assert.Equal(straight.UnplacedPhotoIds, reordered.UnplacedPhotoIds);
    }

    [Fact]
    public void ADifferentSeedIsStillACompleteBook()
    {
        var book = LayoutEngine.LayoutChapter(Request(seed: SyntheticMonth.Seed ^ 0xA5A5A5A5A5A5A5A5));

        AssertConservation(book);
        Assert.NotEmpty(book.Pages);
    }

    // ---- conservation and exclusion (R10, R13, R17) -------------------------------------------------

    [Fact]
    public void EveryPhotoOfTheMonthIsPlacedExactlyOnceOrSitsInTheUnplacedBin()
    {
        var book = LayoutEngine.LayoutChapter(Request());

        AssertConservation(book);

        // This month is well within the library's reach, so nothing should actually be stranded.
        Assert.Empty(book.UnplacedPhotoIds);
    }

    [Fact]
    public void ExcludedPhotosAreNeverPlacedAndNeverEvenReachTheBin()
    {
        var book = LayoutEngine.LayoutChapter(Request());
        var placed = PlacedIds(book.Pages);

        Assert.Equal(2, _month.ExcludedPhotos.Count);
        foreach (var excluded in _month.ExcludedPhotos)
        {
            Assert.DoesNotContain(excluded.Id, placed);
            Assert.DoesNotContain(excluded.Id, book.UnplacedPhotoIds);
        }
    }

    [Fact]
    public void PhotosTheUserUnplacedStayUnplaced()
    {
        var victim = _month.MonthPhotos.First(p => p.TakenOn == SyntheticMonth.HeavyDay);
        var book = LayoutEngine.LayoutChapter(Request(unplaced: new HashSet<string>(StringComparer.Ordinal) { victim.Id }));

        Assert.DoesNotContain(victim.Id, PlacedIds(book.Pages));
        Assert.Contains(victim.Id, book.UnplacedPhotoIds);
    }

    // ---- R16: pinned pages survive "auto-layout rest of chapter" ------------------------------------

    [Fact]
    public void PinnedPagesAreByteIdenticalAfterAutoLayoutRestOfChapter()
    {
        var initial = LayoutEngine.LayoutChapter(Request());
        Assert.True(initial.Pages.Count >= 6, "the fixture month should not collapse to a handful of pages");

        // The user hand-tweaks page 6 and, later, asks for the rest of the chapter from page 3.
        var pages = initial.Pages.ToList();
        var pinned = pages[5];
        pinned.Pinned = true;
        var pinnedBefore = Serialize([pinned]);
        var untouchedBefore = Serialize(pages.Take(2));

        var relaid = LayoutEngine.LayoutChapter(Request(existing: pages, scope: LayoutScope.From(3)));

        var kept = relaid.Pages.SingleOrDefault(p => p.Id == pinned.Id);
        Assert.True(kept is not null, "the pinned page vanished from the relaid chapter");
        Assert.Equal(pinnedBefore, Serialize([kept!]));
        Assert.Contains(6, relaid.PinnedPagesKept);

        // Pages before the requested start are equally untouched, and the warning modal lists exactly
        // the pages that were replaced.
        Assert.Equal(untouchedBefore, Serialize(relaid.Pages.Take(2)));
        Assert.DoesNotContain(1, relaid.AffectedPages);
        Assert.DoesNotContain(2, relaid.AffectedPages);
        Assert.DoesNotContain(6, relaid.AffectedPages);
        Assert.Contains(3, relaid.AffectedPages);

        // …and the whole chapter is still conserved after the splice.
        AssertConservation(relaid);
    }

    [Fact]
    public void TheDryRunPredictsTheSamePagesTheRealRunReplaces()
    {
        var initial = LayoutEngine.LayoutChapter(Request());
        var pages = initial.Pages.ToList();

        var dry = LayoutEngine.DryRun(Request(existing: pages, scope: LayoutScope.From(4)));
        var real = LayoutEngine.LayoutChapter(Request(existing: pages, scope: LayoutScope.From(4)));

        Assert.True(dry.IsDryRun);
        Assert.Equal(real.AffectedPages, dry.AffectedPages);
        Assert.Equal(real.GeneratedPageCount, dry.GeneratedPageCount);
    }

    // ---- the shape of the month (R28, doc 08 §4/§12) ------------------------------------------------

    [Fact]
    public void TheSparseDaysShareOnePageAndTheBirthdayGetsSeveral()
    {
        // R24's title page borrows hero photos out of the day pool, which can change the sparse run's
        // shape; R28 is about the day partitioning, so this asks the engine the R28 question alone.
        var book = LayoutEngine.LayoutChapter(Request(titlePage: false));
        var pagesOf = DaysByPage(book.Pages);

        var sparsePages = pagesOf
            .Where(p => p.Days.Any(d => SyntheticMonth.SparseDays.Contains(d)))
            .ToList();

        // Three days of 3, 2 and 2 photos take at most two pages instead of three, and at least one
        // of them is a genuine multiDay page carrying more than one day (R28).
        //
        // Not one page: merging is only offered when a multiDay template of exactly that section
        // shape exists, and the shipped library (doc 07) authors its three-section layouts as
        // (1,1,1) and (2,2,2) only — there is no (3,2,2). The engine is right to refuse a merge
        // phase 4 could not satisfy; the gap is in the library, and a (3,2,2) sibling of t-md-e
        // would close it. Doc 08 §4's own (2,2,2) example does merge onto one page — see
        // Doc08CalibrationTests.
        Assert.True(sparsePages.Count <= 2, $"the sparse run sprawled over {sparsePages.Count} pages");

        var merged = sparsePages.Where(p => p.Days.Count > 1).ToList();
        Assert.Single(merged);

        var template = Resolve(merged[0].Page);
        Assert.Equal(TemplateKind.MultiDay, template.Kind);
        Assert.Equal(merged[0].Days.Count, template.Sections!.Count);

        // The 30-photo birthday cannot fit on fewer than four pages (R20's 8-slot ceiling) and should
        // not sprawl past the doc's demand-driven band either.
        var heavyPages = pagesOf.Count(p => p.Days.Contains(SyntheticMonth.HeavyDay));
        Assert.InRange(heavyPages, 4, 7);
    }

    [Fact]
    public void TheJournalOnlyDaysTextIsPlacedSomewhere()
    {
        var book = LayoutEngine.LayoutChapter(Request());
        var entry = _month.Journal.Entries.Single(e => e.EffectiveDate == SyntheticMonth.JournalOnlyDay);

        var assigned = book.Pages
            .SelectMany(p => p.JournalAssignments)
            .SelectMany(a => a.EntryIds)
            .ToList();

        Assert.Contains(entry.Id, assigned);

        // Every entry of the month is placed, and none is placed twice — a day's text is atomic (R5).
        foreach (var monthEntry in _month.Journal.EntriesIn(SyntheticMonth.Year, SyntheticMonth.Month))
        {
            Assert.Equal(1, assigned.Count(id => id == monthEntry.Id));
        }
    }

    [Fact]
    public void TheChapterOpensWithItsMonthTitlePage()
    {
        var book = LayoutEngine.LayoutChapter(Request());

        Assert.Equal(TemplateKind.MonthTitle, Resolve(book.Pages[0]).Kind);
        Assert.Equal(1, book.Pages.Count(p => Resolve(p).Kind == TemplateKind.MonthTitle));
    }

    // ---- "does it look good" (R27) ------------------------------------------------------------------

    [Fact]
    public void TheBookDoesNotLayEveryPageOutTheSameWay()
    {
        var book = LayoutEngine.LayoutChapter(Request());
        var refs = book.Pages.Select(p => p.TemplateRef).ToList();

        var distinct = refs.Distinct().Count();
        Assert.True(
            distinct >= (int)Math.Ceiling(refs.Count * 0.6),
            $"only {distinct} distinct templates across {refs.Count} pages — the variety score is not doing its job");

        var busiest = refs.GroupBy(r => r).Max(g => g.Count());
        Assert.True(busiest <= 3, $"one template was used {busiest} times in an {refs.Count}-page chapter");
    }

    [Fact]
    public void BetterPhotosGetBiggerSlots()
    {
        var book = LayoutEngine.LayoutChapter(Request());

        var areas = new Dictionary<Tier, List<double>>();
        var pairs = 0;
        var violations = 0;

        foreach (var page in book.Pages)
        {
            var template = Resolve(page);
            var onPage = page.Placements
                .Select(pl => (Photo: PhotoOf(pl.PhotoId), Slot: template.FindSlot(pl.SlotId)!))
                .ToList();

            foreach (var item in onPage)
            {
                if (!areas.TryGetValue(item.Photo.EffectiveTier, out var list)) areas[item.Photo.EffectiveTier] = list = [];
                list.Add(item.Slot.Rect.Area);

                // Within one page the tier→slot-size pressure of doc 08 §7 is the only thing deciding
                // who gets the hero slot, so this is where R26 has to show up.
                foreach (var other in onPage)
                {
                    if (DemandModel.TierRank(item.Photo.EffectiveTier) >= DemandModel.TierRank(other.Photo.EffectiveTier)) continue;
                    pairs++;
                    if (item.Slot.Rect.Area < other.Slot.Rect.Area) violations++;
                }
            }
        }

        Assert.True(areas.ContainsKey(Tier.S) && areas.ContainsKey(Tier.C), "the fixture month should span tiers");
        Assert.True(
            areas[Tier.S].Average() > areas[Tier.C].Average(),
            $"S-tier photos averaged {areas[Tier.S].Average():F4} page units of slot, C-tier {areas[Tier.C].Average():F4}");

        Assert.True(pairs > 20, "not enough mixed-tier pages to judge");
        Assert.True(
            violations <= pairs / 10,
            $"{violations} of {pairs} same-page tier pairs put the better photo in the smaller slot");
    }

    // ---- R25: smart crop keeps the subject ----------------------------------------------------------

    [Fact]
    public void APlantedOffCenterSubjectSurvivesASlotOfAVeryDifferentAspect()
    {
        var photo = _month.SubjectPhoto;
        Assert.Equal(1800, photo.Width);
        Assert.Equal(1350, photo.Height);

        // The analyzer found the planted checkerboard rather than the middle of the frame.
        var focus = SmartCrop.PrimaryFocus(photo);
        Assert.True(
            focus.CenterX > 0.5 && focus.CenterY > 0.5,
            $"the detected focus {focus} is not in the lower-right quadrant where the subject was planted");

        // A tall, narrow slot on a 4:3 frame: the cover crop throws away nearly half the width, so a
        // centered crop would cut the subject off entirely. This is the whole of R25.
        var slot = new ImageSlot { Id = "s1", Rect = new Rect(0.10, 0.05, 0.24, 0.90), Aspect = (0.24 * 11.0) / (0.90 * 8.5) };
        var crop = SmartCrop.Crop(photo, slot, PageSide.Right, PageGeometry.TrimWidthIn, PageGeometry.TrimHeightIn, LayoutWeights.Default);

        var (slotW, slotH) = PageGeometry.PhysicalSize(slot.Rect);
        var visible = CropMath.SourceRectNormalized(crop.Crop, photo.Width, photo.Height, slotW, slotH);

        Assert.True(visible.W < 0.5, $"the test slot should crop hard; it only removed {(1 - visible.W) * 100:F0}% of the width");
        Assert.True(
            visible.Contains(focus.CenterX, focus.CenterY),
            $"the crop {visible} lost the subject at ({focus.CenterX:F3}, {focus.CenterY:F3})");
        Assert.True(
            visible.IntersectionArea(SyntheticMonth.PlantedSubject) / SyntheticMonth.PlantedSubject.Area > 0.75,
            $"the crop {visible} kept less than three quarters of the planted subject {SyntheticMonth.PlantedSubject}");

        // The engine emits the minimal-crop cover fit and never a letterbox (doc 08 §8).
        Assert.Equal(1.0, crop.Crop.Zoom, 6);
    }

    [Fact]
    public void EveryPlacementInTheBookKeepsItsFocusRegionVisible()
    {
        var book = LayoutEngine.LayoutChapter(Request());
        var checkedPlacements = 0;

        foreach (var page in book.Pages)
        {
            var template = Resolve(page);
            foreach (var placement in page.Placements)
            {
                var photo = PhotoOf(placement.PhotoId);
                var slot = template.FindSlot(placement.SlotId);
                Assert.True(slot is not null, $"page {page.Id} placed a photo in unknown slot '{placement.SlotId}'");

                var (slotW, slotH) = PageGeometry.PhysicalSize(slot!.Rect);
                var visible = CropMath.SourceRectNormalized(placement.Crop, photo.Width, photo.Height, slotW, slotH);
                var focus = SmartCrop.PrimaryFocus(photo);
                checkedPlacements++;

                Assert.True(
                    visible.Contains(focus.CenterX, focus.CenterY),
                    $"'{photo.OriginalFileName}' in slot '{slot.Id}' crops away the middle of its focus region");

                // When the region does fit the slot's maximal window, doc 08 §8 requires it to be kept
                // whole — not merely mostly visible.
                var (windowW, windowH) = CropMath.MaximalWindow(slotW / slotH, (double)photo.Width / photo.Height);
                if (focus.W > windowW || focus.H > windowH) continue;

                var retained = visible.IntersectionArea(focus) / focus.Area;
                Assert.True(retained > 0.999, $"'{photo.OriginalFileName}' kept only {retained:P0} of a focus region that fits");
            }
        }

        Assert.True(checkedPlacements >= 50, $"only {checkedPlacements} placements were checked");
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private LayoutRequest Request(
        IReadOnlyList<Photo>? photos = null,
        IReadOnlyList<Page>? existing = null,
        LayoutScope? scope = null,
        ulong? seed = null,
        bool titlePage = true,
        IReadOnlySet<string>? unplaced = null) => new()
        {
            Chapter = new ChapterInput
            {
                Year = SyntheticMonth.Year,
                Month = SyntheticMonth.Month,
                Photos = photos ?? [.. _month.Catalog.Photos],
                JournalEntries = [.. _month.Journal.EntriesIn(SyntheticMonth.Year, SyntheticMonth.Month)],
                ExistingPages = existing ?? [],
                UnplacedPhotoIds = unplaced ?? new HashSet<string>(StringComparer.Ordinal),
            },
            Style = StyleResolver.Resolve(_month.Book, null, null),
            Seed = seed ?? SyntheticMonth.Seed,
            Scope = scope ?? LayoutScope.WholeChapter,
            GenerateMonthTitlePage = titlePage,
        };

    private void AssertConservation(LayoutResult book)
    {
        var placed = new List<string>();
        foreach (var page in book.Pages)
        {
            foreach (var placement in page.Placements) placed.Add(placement.PhotoId);
        }

        // No photo is placed twice — one photo, at most one placement, anywhere in the book.
        Assert.Equal(placed.Count, placed.Distinct().Count());

        // Placed ∪ Unplaced = the month, and the two are disjoint (doc 08 §1).
        var month = _month.MonthPhotos.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(placed, id => Assert.Contains(id, month));
        Assert.All(book.UnplacedPhotoIds, id => Assert.Contains(id, month));
        Assert.Empty(placed.Intersect(book.UnplacedPhotoIds, StringComparer.Ordinal).ToList());
        Assert.Equal(month.Count, placed.Distinct().Count() + book.UnplacedPhotoIds.Distinct().Count());
    }

    private static HashSet<string> PlacedIds(IEnumerable<Page> pages) =>
        [.. pages.SelectMany(p => p.Placements).Select(p => p.PhotoId)];

    private Photo PhotoOf(string photoId) =>
        _month.Catalog.Find(photoId) ?? throw new InvalidOperationException($"unknown photo '{photoId}'");

    private static Template Resolve(Page page) =>
        page.ResolveTemplate(id => TemplateLibrary.Default.Find(id))
        ?? throw new InvalidOperationException($"page '{page.Id}' references unknown template '{page.TemplateRef}'");

    private IReadOnlyList<(Page Page, IReadOnlyList<DateOnly> Days)> DaysByPage(IEnumerable<Page> pages) =>
    [
        .. pages.Select(page => (page, (IReadOnlyList<DateOnly>)
            [.. page.Placements.Select(pl => PhotoOf(pl.PhotoId).TakenOn).Distinct().OrderBy(d => d)])),
    ];

    private static string Serialize(IEnumerable<Page> pages) =>
        ProjectJson.Serialize(new Chapter
        {
            Year = SyntheticMonth.Year,
            Month = SyntheticMonth.Month,
            Pages = [.. pages],
        });
}
