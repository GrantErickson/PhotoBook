using PhotoBook.Core.Model;
using PhotoBook.Engine;

namespace PhotoBook.Tests;

/// <summary>
/// The three worked examples of doc 08 §4, which the doc itself nominates as calibration targets and
/// unit-test fixtures. They pin the Demand Model's arithmetic <em>and</em> the page counts the DP is
/// supposed to fall out with, so a well-meaning tweak to a weight cannot silently re-shape every
/// book in the suite.
/// <para>
/// These run on hand-built <see cref="Photo"/> records with <see cref="Photo.UserTierOverride"/> set:
/// the doc's examples are stated in tiers, and the user's override is the tier the engine must obey
/// absolutely (kernel §4, R26). No pixels are involved — the engine never touches any.
/// </para>
/// </summary>
public sealed class Doc08CalibrationTests
{
    private const int Year = 2024;
    private const int Month = 6;
    private const ulong Seed = 0xC0FFEE12345UL;

    [Fact]
    public void ThreeSparseDaysOfTwoCPhotosAndAShortEntryMergeOntoOnePage()
    {
        // doc 08 §4: "3 consecutive days, each 2 C-tier photos + a 250-char entry → demand ≈ 0.24
        //             each → merge to one page (R28)."
        var photos = new List<Photo>();
        var entries = new List<JournalEntry>();
        for (var d = 0; d < 3; d++)
        {
            var date = new DateOnly(Year, Month, 10 + d);
            photos.AddRange(PhotosOn(date, 2, Tier.C));
            entries.Add(Entry(date, 250));
        }

        var days = DayGrouping.GroupDays(photos, entries);
        Assert.Equal(3, days.Count);
        foreach (var day in days)
        {
            Assert.Equal(0.242, DemandModel.Demand(day, LayoutWeights.Default), 3);
        }

        var result = Layout(photos, entries);

        Assert.Single(result.Pages);
        Assert.Empty(result.UnplacedPhotoIds);

        // …and it is genuinely one multi-day page carrying all three days, not one day with the other
        // two thrown away.
        var template = TemplateOf(result.Pages[0]);
        Assert.Equal(TemplateKind.MultiDay, template.Kind);
        Assert.Equal(3, template.Sections!.Count);
        Assert.Equal(6, result.Pages[0].Placements.Count);
        Assert.Equal(3, result.Pages[0].JournalAssignments.Count);
    }

    [Fact]
    public void EightBPhotosWithNoJournalFillExactlyOnePage()
    {
        // doc 08 §4: "One day, 8 B photos, no journal → 0.93 → one full page."
        var date = new DateOnly(Year, Month, 12);
        var photos = PhotosOn(date, 8, Tier.B);

        var day = Assert.Single(DayGrouping.GroupDays(photos, []));
        Assert.Equal(0.93, DemandModel.Demand(day, LayoutWeights.Default), 3);

        var result = Layout(photos, []);

        Assert.Single(result.Pages);
        Assert.Equal(8, result.Pages[0].Placements.Count);
        Assert.Empty(result.UnplacedPhotoIds);
    }

    [Fact]
    public void TheFortyPhotoBirthdayLandsInTheDocsPageRange()
    {
        // doc 08 §4: "Birthday, 40 photos (6 S, 12 A, 22 B) + 1900-char entry → 6.78 → 6–7 pages."
        var date = new DateOnly(Year, Month, 20);
        var photos = new List<Photo>();
        photos.AddRange(PhotosOn(date, 6, Tier.S, offset: 0));
        photos.AddRange(PhotosOn(date, 12, Tier.A, offset: 6));
        photos.AddRange(PhotosOn(date, 22, Tier.B, offset: 18));
        var entries = new List<JournalEntry> { Entry(date, 1900) };

        var day = Assert.Single(DayGrouping.GroupDays(photos, entries));

        // The doc quotes 6.78, which is the sum without W_BASE; its other two examples include it, so
        // the implemented formula — photo + text + base — is the one the spec text actually defines.
        Assert.Equal(6.78, DemandModel.Demand(day, LayoutWeights.Default) - LayoutWeights.Default.BaseDemand, 2);

        var result = Layout(photos, entries);

        Assert.InRange(result.Pages.Count, 6, 7);
        Assert.Empty(result.UnplacedPhotoIds);
        Assert.Equal(40, result.Pages.Sum(p => p.Placements.Count));

        // The day's atomic journal text is placed, and placed once (R5, kernel §9).
        var assignments = result.Pages.SelectMany(p => p.JournalAssignments).ToList();
        Assert.NotEmpty(assignments);
        Assert.Single(assignments.SelectMany(a => a.EntryIds).Distinct());
    }

    [Fact]
    public void TheFitCostIsMinimalWhenPagesMatchDemand()
    {
        // The DP's whole objective, spot-checked: fitCost(k, D) = ((k − D) / max(k, D))².
        Assert.Equal(0.0, DemandModel.FitCost(1, 1.0), 9);
        Assert.Equal(0.0, DemandModel.FitCost(7, 7.0), 9);
        Assert.True(DemandModel.FitCost(1, 0.93) < DemandModel.FitCost(2, 0.93));
        Assert.True(DemandModel.FitCost(7, 6.83) < DemandModel.FitCost(4, 6.83));
    }

    // ---- helpers -----------------------------------------------------------------------------------

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
            // The month title page is R24's, not phase 3's: it would borrow photos out of the day pool
            // and blur exactly the page counts these examples are calibrating.
            GenerateMonthTitlePage = false,
        });

    private static Template TemplateOf(Page page)
    {
        var template = page.ResolveTemplate(id => PhotoBook.Core.Templates.TemplateLibrary.Default.Find(id));
        Assert.NotNull(template);
        return template!;
    }

    private static List<Photo> PhotosOn(DateOnly date, int count, Tier tier, int offset = 0)
    {
        var photos = new List<Photo>(count);
        for (var i = 0; i < count; i++)
        {
            var index = offset + i;
            var hash = $"{date:yyyyMMdd}{index:D4}".PadRight(64, 'a');
            photos.Add(new Photo
            {
                Id = Ids.PhotoId(hash),
                ContentHash = hash,
                OriginalFileName = $"{date:yyyyMMdd}-{index:D3}.jpg",
                OriginalPath = $"originals/{date:yyyyMMdd}-{index:D3}.jpg",
                TakenAt = date.ToDateTime(new TimeOnly(9, 0)).AddMinutes(index * 11),
                DateSource = DateSource.Exif,
                Width = index % 3 == 1 ? 1200 : 1600,
                Height = index % 3 == 1 ? 1600 : 1200,
                UserTierOverride = tier,
                FocusRegions =
                [
                    new FocusRegion
                    {
                        Rect = new Rect(0.32 + 0.01 * (index % 5), 0.30, 0.28, 0.30),
                        Weight = 0.8,
                        Kind = FocusKind.Saliency,
                    },
                ],
            });
        }

        return photos;
    }

    private static JournalEntry Entry(DateOnly date, int characters)
    {
        var sourceKey = $"entry-{date:yyyyMMdd}";
        return new JournalEntry
        {
            Id = Ids.JournalEntryId(sourceKey, 0),
            SourceKey = sourceKey,
            DateStart = date,
            DateEnd = date,
            Status = JournalEntryStatus.Matched,
            MatchedBy = "explicitHeading",
            Confidence = 1.0,
            Paragraphs = [Fixtures.SyntheticMonth.Filler(characters)],
        };
    }
}
